using Meimad.Planner.Server.Application.Accounts;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// User accounts, user types and their permissions, and sign-in sessions (owner decision
/// 2026-09-27). The built-in Administrator type holds every permission and cannot be changed or
/// removed; the five types the owner named start with their default permissions and are editable.
/// No account is created: the first Windows client to connect creates the first administrator.
/// </summary>
internal sealed class SchemaV86UserAccountsMigration : IDatabaseMigration
{
    internal const string AdministratorTypeId = "user-type-administrator";

    public int Version => 86;

    public string Name => "user_accounts";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE user_accounts (
                    id TEXT PRIMARY KEY,
                    user_name TEXT NOT NULL COLLATE NOCASE UNIQUE CHECK (length(trim(user_name)) BETWEEN 1 AND 64),
                    display_name TEXT NOT NULL CHECK (length(trim(display_name)) BETWEEN 1 AND 120),
                    password_hash TEXT NOT NULL,
                    is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
                    must_change_password INTEGER NOT NULL DEFAULT 0 CHECK (must_change_password IN (0, 1)),
                    failed_sign_ins INTEGER NOT NULL DEFAULT 0 CHECK (failed_sign_ins >= 0),
                    locked_until TEXT NULL,
                    last_sign_in_at TEXT NULL,
                    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    updated_by TEXT NULL
                );

                CREATE TABLE user_types (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL COLLATE NOCASE UNIQUE CHECK (length(trim(name)) BETWEEN 1 AND 80),
                    description TEXT NULL CHECK (description IS NULL OR length(description) <= 500),
                    is_administrator INTEGER NOT NULL DEFAULT 0 CHECK (is_administrator IN (0, 1)),
                    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    updated_by TEXT NULL
                );

                CREATE TABLE user_type_permissions (
                    user_type_id TEXT NOT NULL REFERENCES user_types(id) ON DELETE CASCADE,
                    permission TEXT NOT NULL CHECK (length(trim(permission)) BETWEEN 1 AND 80),
                    PRIMARY KEY (user_type_id, permission)
                );

                CREATE TABLE user_account_types (
                    user_id TEXT NOT NULL REFERENCES user_accounts(id) ON DELETE CASCADE,
                    user_type_id TEXT NOT NULL REFERENCES user_types(id) ON DELETE RESTRICT,
                    PRIMARY KEY (user_id, user_type_id)
                );

                CREATE TABLE user_sessions (
                    id TEXT PRIMARY KEY,
                    token_hash TEXT NOT NULL UNIQUE CHECK (length(token_hash) = 64),
                    user_id TEXT NOT NULL REFERENCES user_accounts(id) ON DELETE CASCADE,
                    client_id TEXT NULL,
                    created_at TEXT NOT NULL,
                    last_seen_at TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    revoked_at TEXT NULL
                );

                CREATE INDEX ix_user_sessions_user ON user_sessions (user_id, revoked_at);

                -- The last manual change of each Machine backlog, to name who changed an order
                -- another planner's move was based on.
                CREATE TABLE machine_backlog_changes (
                    machine_id TEXT PRIMARY KEY REFERENCES machines(id) ON DELETE CASCADE,
                    stamp_after TEXT NOT NULL,
                    changed_by TEXT NOT NULL,
                    changed_at TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        await InsertTypeAsync(connection, transaction, AdministratorTypeId, "Administrator",
            "Can do anything, including managing users.", isAdministrator: true, [], now, cancellationToken);
        foreach (var (name, description, permissions) in Permissions.DefaultTypes)
        {
            await InsertTypeAsync(connection, transaction, "user-type-" + name.ToLowerInvariant().Replace(' ', '-'),
                name, description, isAdministrator: false, permissions, now, cancellationToken);
        }
    }

    private static async Task InsertTypeAsync(
        SqliteConnection connection, SqliteTransaction transaction, string id, string name, string description,
        bool isAdministrator, IReadOnlyList<string> permissions, string now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO user_types (id, name, description, is_administrator, created_at, updated_at)
            VALUES ($id, $name, $description, $administrator, $now, $now);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$administrator", isAdministrator ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        foreach (var permission in permissions)
        {
            await using var grant = connection.CreateCommand();
            grant.Transaction = transaction;
            grant.CommandText = "INSERT INTO user_type_permissions (user_type_id, permission) VALUES ($id, $permission);";
            grant.Parameters.AddWithValue("$id", id);
            grant.Parameters.AddWithValue("$permission", permission);
            await grant.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
