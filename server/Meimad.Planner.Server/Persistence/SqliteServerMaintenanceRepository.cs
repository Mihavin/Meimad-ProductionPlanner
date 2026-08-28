using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Maintenance;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteServerMaintenanceRepository : IServerMaintenanceRepository
{
    private sealed record DataTable(string Type, string DisplayName, string TableName, string TimestampColumn);

    private static readonly IReadOnlyDictionary<string, DataTable> Tables =
        new Dictionary<string, DataTable>(StringComparer.Ordinal)
        {
            [CollectedDataTypes.CncRawTelemetry] = new(
                CollectedDataTypes.CncRawTelemetry,
                "Raw CNC telemetry",
                "machine_telemetry_raw",
                "observed_at"),
            [CollectedDataTypes.CncStateHistory] = new(
                CollectedDataTypes.CncStateHistory,
                "Machine state history",
                "machine_state_history",
                "observed_at"),
            [CollectedDataTypes.CncConnectionEvents] = new(
                CollectedDataTypes.CncConnectionEvents,
                "CNC connection events",
                "machine_connection_events",
                "occurred_at")
        };

    private readonly SqliteDatabase database;
    private readonly TimeProvider timeProvider;

    public SqliteServerMaintenanceRepository(SqliteDatabase database, TimeProvider timeProvider)
    {
        this.database = database;
        this.timeProvider = timeProvider;
    }

    public async Task<DatabaseStorageStatus> ReadStatusAsync(CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        var pageSize = await ReadPragmaLongAsync(connection, "page_size", token);
        var pageCount = await ReadPragmaLongAsync(connection, "page_count", token);
        var freePageCount = await ReadPragmaLongAsync(connection, "freelist_count", token);
        var schemaVersion = checked((int)await ReadPragmaLongAsync(connection, "user_version", token));
        var collected = await ReadAllCountsAsync(connection, transaction: null, token);
        var databaseBytes = FileLength(database.DatabasePath);
        var walBytes = FileLength(database.DatabasePath + "-wal");
        var sharedMemoryBytes = FileLength(database.DatabasePath + "-shm");
        return new(
            timeProvider.GetUtcNow(),
            databaseBytes,
            walBytes,
            sharedMemoryBytes,
            checked(databaseBytes + walBytes + sharedMemoryBytes),
            pageSize,
            pageCount,
            freePageCount,
            checked(Math.Max(0, pageCount - freePageCount) * pageSize),
            checked(freePageCount * pageSize),
            schemaVersion,
            collected);
    }

    public async Task<CollectedDataPreview> PreviewAsync(
        CollectedDataFilter filter,
        CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        var values = new List<CollectedDataTypeSummary>(filter.Types.Count);
        foreach (var type in filter.Types)
        {
            values.Add(await ReadCountAsync(connection, transaction: null, Tables[type], filter, token));
        }
        return new(filter, values, values.Sum(item => item.RowCount), timeProvider.GetUtcNow());
    }

    public async Task ValidateEditAuthorityAsync(EditAuthority authority, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        await transaction.CommitAsync(token);
    }

    public async Task RecordBackupAsync(
        MaintenanceBackupInfo backup,
        string userId,
        EditAuthority authority,
        DateTimeOffset occurredAt,
        CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        var detail = JsonSerializer.Serialize(new
        {
            backup.FileName,
            backup.CreatedAt,
            backup.ByteLength,
            backup.Sha256,
            backup.IntegrityVerified,
            backup.RestoreVerified,
            Transport = "HTTP"
        });
        await InsertAuditAsync(
            connection,
            transaction,
            "DATABASE_BACKUP_CREATED_HTTP",
            occurredAt,
            userId,
            "server_maintenance_backup",
            "Verified database backup created for Edit-Mode-authorized HTTP download.",
            beforeJson: null,
            afterJson: detail,
            token);
        await transaction.CommitAsync(token);
    }

    public async Task<IReadOnlyList<CollectedDataTypeSummary>> PurgeAsync(
        CollectedDataFilter filter,
        long expectedTotalRows,
        string reason,
        string userId,
        EditAuthority authority,
        MaintenanceBackupInfo backup,
        DateTimeOffset occurredAt,
        CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, token);

        var before = new List<CollectedDataTypeSummary>(filter.Types.Count);
        foreach (var type in filter.Types)
        {
            before.Add(await ReadCountAsync(connection, transaction, Tables[type], filter, token));
        }
        var actualRows = before.Sum(item => item.RowCount);
        if (actualRows != expectedTotalRows)
        {
            throw new CollectedDataPreviewChangedException(expectedTotalRows, actualRows);
        }

        var deleted = new List<CollectedDataTypeSummary>(filter.Types.Count);
        foreach (var item in before)
        {
            var table = Tables[item.Type];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                DELETE FROM {table.TableName}
                WHERE julianday({table.TimestampColumn}) >= julianday($from)
                  AND julianday({table.TimestampColumn}) < julianday($to)
                  AND ($machineId IS NULL OR machine_id = $machineId);
                """;
            AddFilterParameters(command, filter);
            var count = await command.ExecuteNonQueryAsync(token);
            if (count != item.RowCount)
            {
                throw new InvalidOperationException(
                    $"Collected-data delete count changed for {item.Type}; expected {item.RowCount}, deleted {count}.");
            }
            deleted.Add(item with { RowCount = count });
        }

        var beforeJson = JsonSerializer.Serialize(new
        {
            FromInclusive = filter.FromInclusive,
            ToExclusive = filter.ToExclusive,
            filter.Types,
            filter.MachineId,
            Items = before,
            ExpectedTotalRows = expectedTotalRows,
            Backup = backup
        });
        var afterJson = JsonSerializer.Serialize(new
        {
            Deleted = deleted,
            TotalDeletedRows = deleted.Sum(item => item.RowCount)
        });
        await InsertAuditAsync(
            connection,
            transaction,
            "COLLECTED_DATA_PURGED",
            occurredAt,
            userId,
            "collected_data_retention",
            reason,
            beforeJson,
            afterJson,
            token);
        await transaction.CommitAsync(token);
        return deleted;
    }

    private async Task<IReadOnlyList<CollectedDataTypeSummary>> ReadAllCountsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken token)
    {
        var values = new List<CollectedDataTypeSummary>(Tables.Count);
        foreach (var type in CollectedDataTypes.All)
        {
            var table = Tables[type];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT COUNT(*), MIN({table.TimestampColumn}), MAX({table.TimestampColumn})
                FROM {table.TableName};
                """;
            values.Add(await ReadSummaryAsync(command, table, token));
        }
        return values;
    }

    private static async Task<CollectedDataTypeSummary> ReadCountAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DataTable table,
        CollectedDataFilter filter,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT COUNT(*), MIN({table.TimestampColumn}), MAX({table.TimestampColumn})
            FROM {table.TableName}
            WHERE julianday({table.TimestampColumn}) >= julianday($from)
              AND julianday({table.TimestampColumn}) < julianday($to)
              AND ($machineId IS NULL OR machine_id = $machineId);
            """;
        AddFilterParameters(command, filter);
        return await ReadSummaryAsync(command, table, token);
    }

    private static async Task<CollectedDataTypeSummary> ReadSummaryAsync(
        SqliteCommand command,
        DataTable table,
        CancellationToken token)
    {
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            throw new InvalidOperationException($"Could not read collected-data summary for {table.Type}.");
        }
        return new(
            table.Type,
            table.DisplayName,
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : ParseTimestamp(reader.GetString(1)),
            reader.IsDBNull(2) ? null : ParseTimestamp(reader.GetString(2)));
    }

    private static void AddFilterParameters(SqliteCommand command, CollectedDataFilter filter)
    {
        command.Parameters.AddWithValue("$from", FormatTimestamp(filter.FromInclusive));
        command.Parameters.AddWithValue("$to", FormatTimestamp(filter.ToExclusive));
        command.Parameters.AddWithValue("$machineId", (object?)filter.MachineId ?? DBNull.Value);
    }

    private static async Task EnsureEditAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EditAuthority authority,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0))
        {
            throw new EditModeMutationException(
                "edit_mode_required",
                "No Windows client currently holds Edit Mode.");
        }
        if (!string.Equals(reader.GetString(0), authority.ClientId, StringComparison.Ordinal)
            || reader.GetInt64(1) != authority.Generation)
        {
            throw new EditModeMutationException(
                "edit_generation_stale",
                "This client does not hold the active Edit Mode generation.");
        }
    }

    private static async Task InsertAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventType,
        DateTimeOffset occurredAt,
        string userId,
        string reasonCode,
        string comment,
        string? beforeJson,
        string afterJson,
        CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO structured_event_log (
                id, event_type, occurred_at, user_id, related_entity_ids_json,
                reason_code, comment, before_data_json, after_data_json, event_key)
            VALUES (
                $id, $type, $at, $user, '{}', $reason, $comment, $before, $after, $key);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$type", eventType);
        command.Parameters.AddWithValue("$at", FormatTimestamp(occurredAt));
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$reason", reasonCode);
        command.Parameters.AddWithValue("$comment", comment);
        command.Parameters.AddWithValue("$before", (object?)beforeJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$after", afterJson);
        command.Parameters.AddWithValue("$key", $"server-maintenance:{eventType}:{id}");
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<long> ReadPragmaLongAsync(
        SqliteConnection connection,
        string pragma,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        var value = await command.ExecuteScalarAsync(token);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static long FileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
