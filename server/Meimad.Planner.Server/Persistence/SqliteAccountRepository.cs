using System.Globalization;
using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Application.Concurrency;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteAccountRepository(SqliteDatabase database) : IAccountRepository
{
    public async Task<bool> AnyAccountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ScalarLongAsync(connection, null, "SELECT EXISTS(SELECT 1 FROM user_accounts);", cancellationToken) == 1;
    }

    public async Task<StoredAccount?> FindForSignInAsync(string userName, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, user_name, display_name, password_hash, is_active, must_change_password, failed_sign_ins, locked_until
            FROM user_accounts WHERE user_name = $name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$name", userName.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new StoredAccount(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt64(4) == 1, reader.GetInt64(5) == 1, reader.GetInt32(6),
            reader.IsDBNull(7) ? null : Parse(reader.GetString(7)));
    }

    public async Task RecordSignInAsync(
        string userId, bool succeeded, DateTimeOffset? lockedUntil, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = succeeded
            ? "UPDATE user_accounts SET failed_sign_ins = 0, locked_until = NULL, last_sign_in_at = $now WHERE id = $id;"
            : "UPDATE user_accounts SET failed_sign_ins = CASE WHEN $locked IS NULL THEN failed_sign_ins + 1 ELSE 0 END, locked_until = $locked WHERE id = $id;";
        command.Parameters.AddWithValue("$id", userId);
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$locked", lockedUntil is { } locked ? Format(locked) : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CreateSessionAsync(
        string sessionId, string tokenHash, string userId, string? clientId, DateTimeOffset now, DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO user_sessions (id, token_hash, user_id, client_id, created_at, last_seen_at, expires_at)
            VALUES ($id, $hash, $user, $client, $now, $now, $expires);
            DELETE FROM user_sessions WHERE expires_at < $cleanup OR (revoked_at IS NOT NULL AND revoked_at < $cleanup);
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$hash", tokenHash);
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$client", (object?)clientId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$expires", Format(expiresAt));
        command.Parameters.AddWithValue("$cleanup", Format(now.AddDays(-30)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SignedInUser?> ResolveSessionAsync(
        string tokenHash, DateTimeOffset now, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        string sessionId, userId, userName, displayName, lastSeen;
        bool mustChange;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT session.id, account.id, account.user_name, account.display_name, account.must_change_password,
                       session.last_seen_at
                FROM user_sessions session
                JOIN user_accounts account ON account.id = session.user_id
                WHERE session.token_hash = $hash AND session.revoked_at IS NULL AND session.expires_at > $now
                  AND account.is_active = 1;
                """;
            command.Parameters.AddWithValue("$hash", tokenHash);
            command.Parameters.AddWithValue("$now", Format(now));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            (sessionId, userId, userName, displayName, mustChange, lastSeen) =
                (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4) == 1, reader.GetString(5));
        }

        if (now - Parse(lastSeen) > TimeSpan.FromMinutes(1))
        {
            await using var touch = connection.CreateCommand();
            touch.CommandText = "UPDATE user_sessions SET last_seen_at = $now, expires_at = $expires WHERE id = $id;";
            touch.Parameters.AddWithValue("$id", sessionId);
            touch.Parameters.AddWithValue("$now", Format(now));
            touch.Parameters.AddWithValue("$expires", Format(now + idleTimeout));
            await touch.ExecuteNonQueryAsync(cancellationToken);
        }

        var (isAdministrator, permissions) = await ReadPermissionsAsync(connection, userId, cancellationToken);
        return new SignedInUser(userId, userName, displayName, isAdministrator, permissions, mustChange, sessionId);
    }

    public async Task RevokeSessionAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE user_sessions SET revoked_at = $now WHERE token_hash = $hash AND revoked_at IS NULL;";
        command.Parameters.AddWithValue("$hash", tokenHash);
        command.Parameters.AddWithValue("$now", Format(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> CreateFirstAdministratorAsync(
        string userId, string userName, string displayName, string passwordHash, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (await ScalarLongAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM user_accounts);", cancellationToken) == 1)
            return false;
        await InsertAccountAsync(connection, transaction, userId, userName, displayName, passwordHash, true, false, userName, now, cancellationToken);
        await SetTypesAsync(connection, transaction, userId, [SchemaV86UserAccountsMigration.AdministratorTypeId], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<string?> FindDisplayNameAsync(string userName, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT display_name FROM user_accounts WHERE user_name = $name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$name", userName);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<IReadOnlyList<UserAccount>> ListUsersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ReadUsersAsync(connection, null, null, cancellationToken);
    }

    public async Task<UserAccount?> GetUserAsync(string userId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return (await ReadUsersAsync(connection, null, userId, cancellationToken)).SingleOrDefault();
    }

    public async Task<UserAccount> CreateUserAsync(
        string userId, string userName, UserAccountChange change, string passwordHash, string actor, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT EXISTS(SELECT 1 FROM user_accounts WHERE user_name = $name COLLATE NOCASE);";
            exists.Parameters.AddWithValue("$name", userName);
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1)
                throw new AccountException(StatusCodes.Status409Conflict, "user_name_in_use",
                    $"The user name '{userName}' is already taken. Choose another name, or edit the existing account.");
        }
        await EnsureTypesExistAsync(connection, transaction, change.UserTypeIds, cancellationToken);
        await InsertAccountAsync(connection, transaction, userId, userName, change.DisplayName, passwordHash,
            change.IsActive, mustChangePassword: true, actor, now, cancellationToken);
        await SetTypesAsync(connection, transaction, userId, change.UserTypeIds, cancellationToken);
        var created = (await ReadUsersAsync(connection, transaction, userId, cancellationToken)).Single();
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    public async Task<UserAccount> UpdateUserAsync(
        string userId, int expectedVersion, UserAccountChange change, string actor, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = (await ReadUsersAsync(connection, transaction, userId, cancellationToken)).SingleOrDefault()
            ?? throw new AccountException(StatusCodes.Status404NotFound, "resource_not_found", "The user account was not found.");
        if (current.Version != expectedVersion)
            throw new EditConflictException("user account",
                $"The account '{current.UserName}' was changed by {current.UpdatedBy ?? "another administrator"} after you opened it.",
                "Refresh the user list to see the current account, then apply your change again.",
                current.UpdatedBy, current.UpdatedAt);
        await EnsureTypesExistAsync(connection, transaction, change.UserTypeIds, cancellationToken);

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE user_accounts
                SET display_name = $display, is_active = $active, version = version + 1, updated_at = $now, updated_by = $actor
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$id", userId);
            update.Parameters.AddWithValue("$display", change.DisplayName);
            update.Parameters.AddWithValue("$active", change.IsActive ? 1 : 0);
            update.Parameters.AddWithValue("$now", Format(now));
            update.Parameters.AddWithValue("$actor", actor);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await SetTypesAsync(connection, transaction, userId, change.UserTypeIds, cancellationToken);
        if (!change.IsActive)
        {
            await ExecuteAsync(connection, transaction, "UPDATE user_sessions SET revoked_at = $now WHERE user_id = $id AND revoked_at IS NULL;",
                cancellationToken, ("$id", userId), ("$now", Format(now)));
        }
        await EnsureAnAdministratorRemainsAsync(connection, transaction, cancellationToken);
        var updated = (await ReadUsersAsync(connection, transaction, userId, cancellationToken)).Single();
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task SetPasswordAsync(
        string userId, string passwordHash, bool mustChange, bool endSessions, string actor, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var changed = await ExecuteAsync(connection, transaction, """
            UPDATE user_accounts
            SET password_hash = $hash, must_change_password = $must, failed_sign_ins = 0, locked_until = NULL,
                version = version + 1, updated_at = $now, updated_by = $actor
            WHERE id = $id;
            """, cancellationToken, ("$id", userId), ("$hash", passwordHash), ("$must", mustChange ? 1 : 0),
            ("$now", Format(now)), ("$actor", actor));
        if (changed == 0)
            throw new AccountException(StatusCodes.Status404NotFound, "resource_not_found", "The user account was not found.");
        if (endSessions)
        {
            await ExecuteAsync(connection, transaction, "UPDATE user_sessions SET revoked_at = $now WHERE user_id = $id AND revoked_at IS NULL;",
                cancellationToken, ("$id", userId), ("$now", Format(now)));
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserType>> ListTypesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ReadTypesAsync(connection, null, null, cancellationToken);
    }

    public async Task<UserType> CreateTypeAsync(
        string userTypeId, UserTypeChange change, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureTypeNameFreeAsync(connection, transaction, change.Name, null, cancellationToken);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO user_types (id, name, description, is_administrator, created_at, updated_at, updated_by)
            VALUES ($id, $name, $description, 0, $now, $now, $actor);
            """, cancellationToken, ("$id", userTypeId), ("$name", change.Name), ("$description", (object?)change.Description ?? DBNull.Value),
            ("$now", Format(now)), ("$actor", actor));
        await SetPermissionsAsync(connection, transaction, userTypeId, change.Permissions, cancellationToken);
        var created = (await ReadTypesAsync(connection, transaction, userTypeId, cancellationToken)).Single();
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    public async Task<UserType> UpdateTypeAsync(
        string userTypeId, int expectedVersion, UserTypeChange change, string actor, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await RequireEditableTypeAsync(connection, transaction, userTypeId, expectedVersion, cancellationToken);
        await EnsureTypeNameFreeAsync(connection, transaction, change.Name, userTypeId, cancellationToken);
        await ExecuteAsync(connection, transaction, """
            UPDATE user_types
            SET name = $name, description = $description, version = version + 1, updated_at = $now, updated_by = $actor
            WHERE id = $id;
            """, cancellationToken, ("$id", current.UserTypeId), ("$name", change.Name),
            ("$description", (object?)change.Description ?? DBNull.Value), ("$now", Format(now)), ("$actor", actor));
        await SetPermissionsAsync(connection, transaction, userTypeId, change.Permissions, cancellationToken);
        var updated = (await ReadTypesAsync(connection, transaction, userTypeId, cancellationToken)).Single();
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task DeleteTypeAsync(string userTypeId, int expectedVersion, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await RequireEditableTypeAsync(connection, transaction, userTypeId, expectedVersion, cancellationToken);
        if (current.UserCount > 0)
            throw new AccountException(StatusCodes.Status409Conflict, "user_type_in_use",
                $"The user type '{current.Name}' is still given to {current.UserCount} user(s). Take it away from them first.");
        await ExecuteAsync(connection, transaction, "DELETE FROM user_types WHERE id = $id;", cancellationToken, ("$id", userTypeId));
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<UserType> RequireEditableTypeAsync(
        SqliteConnection connection, SqliteTransaction transaction, string userTypeId, int expectedVersion,
        CancellationToken cancellationToken)
    {
        var current = (await ReadTypesAsync(connection, transaction, userTypeId, cancellationToken)).SingleOrDefault()
            ?? throw new AccountException(StatusCodes.Status404NotFound, "resource_not_found", "The user type was not found.");
        if (current.IsAdministrator)
            throw new AccountException(StatusCodes.Status409Conflict, "administrator_type_fixed",
                "The Administrator type always has every permission and cannot be changed or removed.");
        if (current.Version != expectedVersion)
            throw new EditConflictException("user type",
                $"The user type '{current.Name}' was changed by {current.UpdatedBy ?? "another administrator"} after you opened it.",
                "Refresh the user types to see its current permissions, then apply your change again.",
                current.UpdatedBy, current.UpdatedAt);
        return current;
    }

    private static async Task EnsureTypeNameFreeAsync(
        SqliteConnection connection, SqliteTransaction transaction, string name, string? exceptId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM user_types WHERE name = $name COLLATE NOCASE AND id IS NOT $except);";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$except", (object?)exceptId ?? DBNull.Value);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1)
            throw new AccountException(StatusCodes.Status409Conflict, "user_type_name_in_use",
                $"A user type named '{name}' already exists. Choose another name.");
    }

    private static async Task EnsureTypesExistAsync(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<string> typeIds, CancellationToken cancellationToken)
    {
        foreach (var typeId in typeIds)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM user_types WHERE id = $id);";
            command.Parameters.AddWithValue("$id", typeId);
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
                throw new AccountException(StatusCodes.Status422UnprocessableEntity, "user_type_unknown",
                    "A chosen user type no longer exists. Refresh the user types and choose again.");
        }
    }

    /// <summary>The Server always keeps at least one active administrator, so nobody locks everyone out.</summary>
    private static async Task EnsureAnAdministratorRemainsAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var administrators = await ScalarLongAsync(connection, transaction, """
            SELECT COUNT(DISTINCT account.id) FROM user_accounts account
            JOIN user_account_types assigned ON assigned.user_id = account.id
            JOIN user_types type ON type.id = assigned.user_type_id
            WHERE account.is_active = 1 AND type.is_administrator = 1;
            """, cancellationToken);
        if (administrators == 0)
            throw new AccountException(StatusCodes.Status409Conflict, "last_administrator",
                "This would leave no active administrator. Make another user an administrator first.");
    }

    private static async Task InsertAccountAsync(
        SqliteConnection connection, SqliteTransaction transaction, string userId, string userName, string displayName,
        string passwordHash, bool isActive, bool mustChangePassword, string actor, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(connection, transaction, """
            INSERT INTO user_accounts (id, user_name, display_name, password_hash, is_active, must_change_password,
                created_at, updated_at, updated_by)
            VALUES ($id, $name, $display, $hash, $active, $must, $now, $now, $actor);
            """, cancellationToken, ("$id", userId), ("$name", userName), ("$display", displayName), ("$hash", passwordHash),
            ("$active", isActive ? 1 : 0), ("$must", mustChangePassword ? 1 : 0), ("$now", Format(now)), ("$actor", actor));

    private static async Task SetTypesAsync(
        SqliteConnection connection, SqliteTransaction transaction, string userId, IReadOnlyList<string> typeIds,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, "DELETE FROM user_account_types WHERE user_id = $id;", cancellationToken, ("$id", userId));
        foreach (var typeId in typeIds.Distinct(StringComparer.Ordinal))
        {
            await ExecuteAsync(connection, transaction, "INSERT INTO user_account_types (user_id, user_type_id) VALUES ($id, $type);",
                cancellationToken, ("$id", userId), ("$type", typeId));
        }
    }

    private static async Task SetPermissionsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string typeId, IReadOnlyList<string> permissions,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, "DELETE FROM user_type_permissions WHERE user_type_id = $id;", cancellationToken, ("$id", typeId));
        foreach (var permission in permissions.Distinct(StringComparer.Ordinal))
        {
            await ExecuteAsync(connection, transaction, "INSERT INTO user_type_permissions (user_type_id, permission) VALUES ($id, $permission);",
                cancellationToken, ("$id", typeId), ("$permission", permission));
        }
    }

    private static async Task<(bool IsAdministrator, IReadOnlySet<string> Permissions)> ReadPermissionsAsync(
        SqliteConnection connection, string userId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type.is_administrator, permission.permission
            FROM user_account_types assigned
            JOIN user_types type ON type.id = assigned.user_type_id
            LEFT JOIN user_type_permissions permission ON permission.user_type_id = type.id
            WHERE assigned.user_id = $id;
            """;
        command.Parameters.AddWithValue("$id", userId);
        var isAdministrator = false;
        var permissions = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            isAdministrator |= reader.GetInt64(0) == 1;
            if (!reader.IsDBNull(1)) permissions.Add(reader.GetString(1));
        }
        return (isAdministrator, permissions);
    }

    private static async Task<IReadOnlyList<UserAccount>> ReadUsersAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string? userId, CancellationToken cancellationToken)
    {
        var types = new Dictionary<string, List<UserTypeSummary>>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT assigned.user_id, type.id, type.name, type.is_administrator
                FROM user_account_types assigned JOIN user_types type ON type.id = assigned.user_type_id
                WHERE $id IS NULL OR assigned.user_id = $id
                ORDER BY type.name COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$id", (object?)userId ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!types.TryGetValue(reader.GetString(0), out var list)) types[reader.GetString(0)] = list = [];
                list.Add(new UserTypeSummary(reader.GetString(1), reader.GetString(2), reader.GetInt64(3) == 1));
            }
        }

        var users = new List<UserAccount>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id, user_name, display_name, is_active, must_change_password, last_sign_in_at, version, updated_at, updated_by
                FROM user_accounts WHERE $id IS NULL OR id = $id
                ORDER BY display_name COLLATE NOCASE, user_name COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$id", (object?)userId ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                users.Add(new UserAccount(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) == 1,
                    reader.GetInt64(4) == 1, reader.IsDBNull(5) ? null : Parse(reader.GetString(5)),
                    types.GetValueOrDefault(reader.GetString(0)) ?? [], reader.GetInt32(6), Parse(reader.GetString(7)),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
        }
        return users;
    }

    private static async Task<IReadOnlyList<UserType>> ReadTypesAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string? typeId, CancellationToken cancellationToken)
    {
        var permissions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT user_type_id, permission FROM user_type_permissions WHERE $id IS NULL OR user_type_id = $id ORDER BY permission;";
            command.Parameters.AddWithValue("$id", (object?)typeId ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!permissions.TryGetValue(reader.GetString(0), out var list)) permissions[reader.GetString(0)] = list = [];
                list.Add(reader.GetString(1));
            }
        }

        var types = new List<UserType>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT type.id, type.name, type.description, type.is_administrator,
                       (SELECT COUNT(*) FROM user_account_types assigned WHERE assigned.user_type_id = type.id),
                       type.version, type.updated_at, type.updated_by
                FROM user_types type WHERE $id IS NULL OR type.id = $id
                ORDER BY type.is_administrator DESC, type.name COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$id", (object?)typeId ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var isAdministrator = reader.GetInt64(3) == 1;
                types.Add(new UserType(
                    reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), isAdministrator,
                    isAdministrator ? Permissions.All.Select(definition => definition.Code).ToArray()
                        : permissions.GetValueOrDefault(reader.GetString(0)) ?? [],
                    reader.GetInt32(4), reader.GetInt32(5), Parse(reader.GetString(6)),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }
        return types;
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
