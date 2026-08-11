using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteEditModeRepository : IEditModeRepository
{
    private readonly SqliteDatabase database;

    public SqliteEditModeRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task ProcessTimeoutAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await ApplyExpiredRequestAsync(connection, transaction, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<EditModeSnapshot> GetStatusAsync(
        string callerClientId,
        DateTimeOffset now,
        int transferTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await ApplyExpiredRequestAsync(connection, transaction, now, cancellationToken);
        var snapshot = await ReadSnapshotAsync(
            connection,
            transaction,
            callerClientId,
            now,
            transferTimeoutSeconds,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    public async Task<EditModeSnapshot> RequestEditAsync(
        string requesterClientId,
        string requesterUserId,
        DateTimeOffset now,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await ApplyExpiredRequestAsync(connection, transaction, now, cancellationToken);

        var token = await ReadTokenAsync(connection, transaction, cancellationToken);
        var pending = await ReadPendingRequestAsync(connection, transaction, cancellationToken);
        if (string.Equals(token.HolderClientId, requesterClientId, StringComparison.Ordinal))
        {
            var current = await ReadSnapshotAsync(
                connection,
                transaction,
                requesterClientId,
                now,
                checked((int)timeout.TotalSeconds),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return current;
        }

        if (pending is not null)
        {
            if (string.Equals(pending.RequesterClientId, requesterClientId, StringComparison.Ordinal))
            {
                var existing = await ReadSnapshotAsync(
                    connection,
                    transaction,
                    requesterClientId,
                    now,
                    checked((int)timeout.TotalSeconds),
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }

            throw new EditModeCommandException(
                "edit_request_pending",
                "Another Windows client already has a pending Edit Mode request.");
        }

        if (token.HolderClientId is null)
        {
            await UpdateTokenHolderAsync(
                connection,
                transaction,
                requesterClientId,
                requesterUserId,
                now,
                cancellationToken);
        }
        else
        {
            var deadline = now.Add(timeout);
            await InsertPendingRequestAsync(
                connection,
                transaction,
                requesterClientId,
                requesterUserId,
                token.Generation,
                now,
                deadline,
                cancellationToken);
            await SetLeaseDeadlineAsync(connection, transaction, deadline, now, cancellationToken);
        }

        var snapshot = await ReadSnapshotAsync(
            connection,
            transaction,
            requesterClientId,
            now,
            checked((int)timeout.TotalSeconds),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    public async Task<EditTransferRequest?> GetRequestAsync(
        string requestId,
        string callerClientId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await ApplyExpiredRequestAsync(connection, transaction, now, cancellationToken);
        var request = await ReadRequestAsync(
            connection,
            transaction,
            requestId,
            cancellationToken);
        if (request is not null)
        {
            var token = await ReadTokenAsync(connection, transaction, cancellationToken);
            if (!string.Equals(request.RequesterClientId, callerClientId, StringComparison.Ordinal)
                && !string.Equals(token.HolderClientId, callerClientId, StringComparison.Ordinal))
            {
                throw new EditModeCommandException(
                    "edit_request_forbidden",
                    "This client cannot read the requested Edit Mode transfer.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return request;
    }

    public async Task<EditModeSnapshot> DecideAsync(
        string requestId,
        EditAuthority authority,
        EditDecision decision,
        DateTimeOffset now,
        int transferTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await ApplyExpiredRequestAsync(connection, transaction, now, cancellationToken);

        var request = await ReadRequestAsync(connection, transaction, requestId, cancellationToken)
            ?? throw new EditModeCommandException(
                "edit_request_not_found",
                "The Edit Mode request was not found.");

        if (request.Status != EditRequestStatus.Pending)
        {
            var isSameOutcome = decision == EditDecision.Reject
                ? request.Status == EditRequestStatus.Rejected
                : request.Status is EditRequestStatus.Transferred or EditRequestStatus.AutoTransferred;
            if (!isSameOutcome)
            {
                throw new EditModeCommandException(
                    "edit_request_already_decided",
                    "The Edit Mode request already has a different final outcome.");
            }

            var finalSnapshot = await ReadSnapshotAsync(
                connection,
                transaction,
                authority.ClientId,
                now,
                transferTimeoutSeconds,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return finalSnapshot;
        }

        await EnsureAuthorityAsync(connection, transaction, authority, cancellationToken);
        if (request.HolderGenerationAtRequest != authority.Generation)
        {
            throw new EditModeCommandException(
                "edit_generation_stale",
                "The request was created for a different Edit Mode generation.");
        }

        if (decision == EditDecision.Release)
        {
            var generation = await UpdateTokenHolderAsync(
                connection,
                transaction,
                request.RequesterClientId,
                request.RequesterUserId,
                now,
                cancellationToken);
            await FinalizeRequestAsync(
                connection,
                transaction,
                request.RequestId,
                EditRequestStatus.Transferred,
                now,
                generation,
                cancellationToken);
        }
        else
        {
            await ClearLeaseDeadlineAsync(connection, transaction, now, cancellationToken);
            await FinalizeRequestAsync(
                connection,
                transaction,
                request.RequestId,
                EditRequestStatus.Rejected,
                now,
                null,
                cancellationToken);
        }

        var snapshot = await ReadSnapshotAsync(
            connection,
            transaction,
            authority.ClientId,
            now,
            transferTimeoutSeconds,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    public async Task<EditModeSnapshot> ReleaseAsync(
        EditAuthority authority,
        DateTimeOffset now,
        int transferTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await ApplyExpiredRequestAsync(connection, transaction, now, cancellationToken);
        await EnsureAuthorityAsync(connection, transaction, authority, cancellationToken);

        var pending = await ReadPendingRequestAsync(connection, transaction, cancellationToken);
        if (pending is null)
        {
            await ClearTokenHolderAsync(connection, transaction, now, cancellationToken);
        }
        else
        {
            var generation = await UpdateTokenHolderAsync(
                connection,
                transaction,
                pending.RequesterClientId,
                pending.RequesterUserId,
                now,
                cancellationToken);
            await FinalizeRequestAsync(
                connection,
                transaction,
                pending.RequestId,
                EditRequestStatus.Transferred,
                now,
                generation,
                cancellationToken);
        }

        var snapshot = await ReadSnapshotAsync(
            connection,
            transaction,
            authority.ClientId,
            now,
            transferTimeoutSeconds,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    internal static async Task ApplyExpiredRequestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pending = await ReadPendingRequestAsync(connection, transaction, cancellationToken);
        if (pending is null || pending.DecisionDeadline > now)
        {
            return;
        }

        await using var transfer = connection.CreateCommand();
        transfer.Transaction = transaction;
        transfer.CommandText = """
            UPDATE edit_tokens
            SET holder_client_id = $client_id,
                holder_user_id = $user_id,
                generation = generation + 1,
                acquired_at = $now,
                lease_expires_at = NULL,
                version = version + 1,
                updated_at = $now
            WHERE id = 1
              AND holder_client_id IS NOT NULL
              AND generation = $expected_generation;
            """;
        transfer.Parameters.AddWithValue("$client_id", pending.RequesterClientId);
        transfer.Parameters.AddWithValue("$user_id", pending.RequesterUserId);
        transfer.Parameters.AddWithValue("$now", Format(now));
        transfer.Parameters.AddWithValue("$expected_generation", pending.HolderGenerationAtRequest);
        if (await transfer.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "The pending Edit Mode request no longer matches the authoritative edit token.");
        }

        var token = await ReadTokenAsync(connection, transaction, cancellationToken);
        await FinalizeRequestAsync(
            connection,
            transaction,
            pending.RequestId,
            EditRequestStatus.AutoTransferred,
            now,
            token.Generation,
            cancellationToken);
    }

    private static async Task<EditModeSnapshot> ReadSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string callerClientId,
        DateTimeOffset now,
        int transferTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var token = await ReadTokenAsync(connection, transaction, cancellationToken);
        var pending = await ReadPendingRequestAsync(connection, transaction, cancellationToken);
        var state = string.Equals(token.HolderClientId, callerClientId, StringComparison.Ordinal)
            ? EditClientState.Editor
            : pending is not null
                && string.Equals(pending.RequesterClientId, callerClientId, StringComparison.Ordinal)
                    ? EditClientState.RequestingEdit
                    : EditClientState.Viewer;
        var holder = token.HolderClientId is null
            ? null
            : new EditModeHolder(
                token.HolderClientId,
                token.HolderUserId!,
                token.Generation,
                token.AcquiredAt!.Value);
        return new EditModeSnapshot(
            state,
            token.Generation,
            holder,
            pending,
            now,
            transferTimeoutSeconds);
    }

    private static async Task<EditTokenRow> ReadTokenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT holder_client_id, holder_user_id, generation, acquired_at
            FROM edit_tokens
            WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The singleton Edit Mode token row is missing.");
        }

        return new EditTokenRow(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? null : Parse(reader.GetString(3)));
    }

    private static async Task<EditTransferRequest?> ReadPendingRequestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, requester_client_id, requester_user_id,
                   holder_generation_at_request, status, requested_at,
                   decision_deadline, decided_at, granted_generation
            FROM edit_requests
            WHERE status = 'pending'
            LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRequest(reader) : null;
    }

    private static async Task<EditTransferRequest?> ReadRequestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, requester_client_id, requester_user_id,
                   holder_generation_at_request, status, requested_at,
                   decision_deadline, decided_at, granted_generation
            FROM edit_requests
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRequest(reader) : null;
    }

    private static EditTransferRequest ReadRequest(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt64(3),
        ParseStatus(reader.GetString(4)),
        Parse(reader.GetString(5)),
        Parse(reader.GetString(6)),
        reader.IsDBNull(7) ? null : Parse(reader.GetString(7)),
        reader.IsDBNull(8) ? null : reader.GetInt64(8));

    private static async Task InsertPendingRequestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string clientId,
        string userId,
        long holderGeneration,
        DateTimeOffset now,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO edit_requests (
                id, requester_client_id, requester_user_id,
                holder_generation_at_request, status, requested_at,
                decision_deadline, created_at, updated_at)
            VALUES (
                $id, $client_id, $user_id,
                $holder_generation, 'pending', $requested_at,
                $deadline, $requested_at, $requested_at);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$client_id", clientId);
        command.Parameters.AddWithValue("$user_id", userId);
        command.Parameters.AddWithValue("$holder_generation", holderGeneration);
        command.Parameters.AddWithValue("$requested_at", Format(now));
        command.Parameters.AddWithValue("$deadline", Format(deadline));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> UpdateTokenHolderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string clientId,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE edit_tokens
            SET holder_client_id = $client_id,
                holder_user_id = $user_id,
                generation = generation + 1,
                acquired_at = $now,
                lease_expires_at = NULL,
                version = version + 1,
                updated_at = $now
            WHERE id = 1
            RETURNING generation;
            """;
        command.Parameters.AddWithValue("$client_id", clientId);
        command.Parameters.AddWithValue("$user_id", userId);
        command.Parameters.AddWithValue("$now", Format(now));
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task ClearTokenHolderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE edit_tokens
            SET holder_client_id = NULL,
                holder_user_id = NULL,
                generation = generation + 1,
                acquired_at = NULL,
                lease_expires_at = NULL,
                version = version + 1,
                updated_at = $now
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$now", Format(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SetLeaseDeadlineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset deadline,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE edit_tokens
            SET lease_expires_at = $deadline,
                version = version + 1,
                updated_at = $now
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$deadline", Format(deadline));
        command.Parameters.AddWithValue("$now", Format(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ClearLeaseDeadlineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE edit_tokens
            SET lease_expires_at = NULL,
                version = version + 1,
                updated_at = $now
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$now", Format(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task FinalizeRequestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string requestId,
        EditRequestStatus status,
        DateTimeOffset now,
        long? generation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE edit_requests
            SET status = $status,
                decided_at = $now,
                granted_generation = $generation,
                version = version + 1,
                updated_at = $now
            WHERE id = $id AND status = 'pending';
            """;
        command.Parameters.AddWithValue("$status", ToStorage(status));
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$generation", (object?)generation ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", requestId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The pending Edit Mode request changed unexpectedly.");
        }
    }

    private static async Task EnsureAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EditAuthority authority,
        CancellationToken cancellationToken)
    {
        var token = await ReadTokenAsync(connection, transaction, cancellationToken);
        if (token.HolderClientId is null)
        {
            throw new EditModeMutationException(
                "edit_mode_required",
                "No Windows client currently holds Edit Mode.");
        }

        if (!string.Equals(token.HolderClientId, authority.ClientId, StringComparison.Ordinal)
            || token.Generation != authority.Generation)
        {
            throw new EditModeMutationException(
                "edit_generation_stale",
                "This client does not hold the active Edit Mode generation.");
        }
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static EditRequestStatus ParseStatus(string value) => value switch
    {
        "pending" => EditRequestStatus.Pending,
        "transferred" => EditRequestStatus.Transferred,
        "rejected" => EditRequestStatus.Rejected,
        "auto_transferred" => EditRequestStatus.AutoTransferred,
        _ => throw new InvalidOperationException($"Unknown Edit Mode request status '{value}'.")
    };

    private static string ToStorage(EditRequestStatus value) => value switch
    {
        EditRequestStatus.Pending => "pending",
        EditRequestStatus.Transferred => "transferred",
        EditRequestStatus.Rejected => "rejected",
        EditRequestStatus.AutoTransferred => "auto_transferred",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    private sealed record EditTokenRow(
        string? HolderClientId,
        string? HolderUserId,
        long Generation,
        DateTimeOffset? AcquiredAt);
}
