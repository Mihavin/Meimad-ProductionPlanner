using System.Globalization;
using Meimad.Planner.Server.Application.Downtimes;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.EventLogging;
using Meimad.Planner.Server.Domain.Downtimes;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteMachineDowntimeRepository : IMachineDowntimeRepository
{
    private const string Projection = "id, machine_id, downtime_type, starts_at, ends_at, reason, planned_by, repair_note, reported_by, status, version, created_at, updated_at";
    private readonly SqliteDatabase database;

    public SqliteMachineDowntimeRepository(SqliteDatabase database) => this.database = database;

    public async Task<IReadOnlyList<MachineDowntime>> ListAsync(string? machineId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Projection} FROM downtimes WHERE ($machineId IS NULL OR machine_id = $machineId) ORDER BY starts_at DESC, id;";
        command.Parameters.AddWithValue("$machineId", machineId is null ? DBNull.Value : machineId);
        var values = new List<MachineDowntime>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) values.Add(Read(reader));
        return values;
    }

    public async Task<MachineDowntime?> GetAsync(string id, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Projection} FROM downtimes WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Read(reader) : null;
    }

    public async Task<MachineDowntime> CreateAsync(MachineDowntime value, EditAuthority authority, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var user = await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        await EnsureMachineAsync(connection, transaction, value.MachineId, token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO downtimes (
                id, machine_id, downtime_type, starts_at, ends_at, reason,
                planned_by, repair_note, reported_by, status, version, created_at, updated_at)
            VALUES (
                $id, $machineId, $type, $startsAt, $endsAt, $reason,
                $plannedBy, $repairNote, $reportedBy, $status, $version, $createdAt, $updatedAt);
            """;
        Add(command, value);
        await command.ExecuteNonQueryAsync(token);
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction, new(
            value.DowntimeType == MachineDowntimeType.Breakdown ? "breakdown_reported" : "maintenance_created",
            value.CreatedAt, user,
            new Dictionary<string,string> { ["downtimeId"]=value.DowntimeId,["machineId"]=value.MachineId },
            value.DowntimeType, value.Reason, null,
            new { value.StartsAt,value.EndsAt,value.Status,value.PlannedBy,value.ReportedBy }), token);
        await transaction.CommitAsync(token);
        return value;
    }

    public async Task<MachineDowntime?> UpdateAsync(MachineDowntime value, int expectedVersion, EditAuthority authority, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var user = await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        await EnsureMachineAsync(connection, transaction, value.MachineId, token);
        var before = await ReadAsync(connection, transaction, value.DowntimeId, token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE downtimes
            SET machine_id = $machineId, starts_at = $startsAt, ends_at = $endsAt,
                reason = $reason, planned_by = $plannedBy, repair_note = $repairNote,
                reported_by = $reportedBy, status = $status, version = $version,
                updated_at = $updatedAt
            WHERE id = $id AND version = $expectedVersion;
            """;
        Add(command, value);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        var updated = await command.ExecuteNonQueryAsync(token) == 1;
        if (updated)
            await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction, new(
                value.DowntimeType == MachineDowntimeType.Breakdown && value.Status == MachineDowntimeStatus.Restored
                    ? "breakdown_restored" : "maintenance_updated",
                value.UpdatedAt, user,
                new Dictionary<string,string> { ["downtimeId"]=value.DowntimeId,["machineId"]=value.MachineId },
                value.DowntimeType, value.RepairNote ?? value.Reason, before, value), token);
        await transaction.CommitAsync(token);
        return updated ? value : null;
    }

    private static MachineDowntime Read(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), Parse(reader.GetString(3)),
        reader.IsDBNull(4) ? null : Parse(reader.GetString(4)), reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9), reader.GetInt32(10),
        Parse(reader.GetString(11)), Parse(reader.GetString(12)));

    private static void Add(SqliteCommand command, MachineDowntime value)
    {
        command.Parameters.AddWithValue("$id", value.DowntimeId);
        command.Parameters.AddWithValue("$machineId", value.MachineId);
        command.Parameters.AddWithValue("$type", value.DowntimeType);
        command.Parameters.AddWithValue("$startsAt", Format(value.StartsAt));
        command.Parameters.AddWithValue("$endsAt", value.EndsAt.HasValue ? Format(value.EndsAt.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$reason", value.Reason);
        command.Parameters.AddWithValue("$plannedBy", value.PlannedBy is null ? DBNull.Value : value.PlannedBy);
        command.Parameters.AddWithValue("$repairNote", value.RepairNote is null ? DBNull.Value : value.RepairNote);
        command.Parameters.AddWithValue("$reportedBy", value.ReportedBy is null ? DBNull.Value : value.ReportedBy);
        command.Parameters.AddWithValue("$status", value.Status);
        command.Parameters.AddWithValue("$version", value.Version);
        command.Parameters.AddWithValue("$createdAt", Format(value.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", Format(value.UpdatedAt));
    }

    private static async Task EnsureMachineAsync(SqliteConnection connection, SqliteTransaction transaction, string id, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM machines WHERE id = $id);";
        command.Parameters.AddWithValue("$id", id);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 1)
            throw new MachineDowntimeMachineException(id);
    }

    private static async Task<string> EnsureEditAuthorityAsync(SqliteConnection connection, SqliteTransaction transaction, EditAuthority authority, CancellationToken token)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(connection, transaction, DateTimeOffset.UtcNow, token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, holder_user_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0))
            throw new EditModeMutationException("edit_mode_required", "No Windows client currently holds Edit Mode.");
        if (!string.Equals(reader.GetString(0), authority.ClientId, StringComparison.Ordinal) || reader.GetInt64(2) != authority.Generation)
            throw new EditModeMutationException("edit_generation_stale", "This client does not hold the active Edit Mode generation.");
        return reader.GetString(1);
    }

    private static async Task<MachineDowntime?> ReadAsync(
        SqliteConnection connection, SqliteTransaction transaction, string id, CancellationToken token)
    {
        await using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText=$"SELECT {Projection} FROM downtimes WHERE id=$id;";command.Parameters.AddWithValue("$id",id);
        await using var reader=await command.ExecuteReaderAsync(token);return await reader.ReadAsync(token)?Read(reader):null;
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
