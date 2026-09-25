using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Kitaron;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteKitaronStationRepository(SqliteDatabase database) : IKitaronStationRepository
{
    private const string Columns = """
        kitaron_station_id, station_name, station_type, retired, route_rows, planned_rows, supplier_rows,
        suggested_role, import_role, machine_type, workstation_type_id, external_resource_id,
        default_minutes_per_part, default_minutes_per_batch, capacity_required, notes,
        first_seen_at, last_seen_at, decided_at, decided_by, version, updated_at
        """;

    public async Task<IReadOnlyList<KitaronStationRecord>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, null, cancellationToken);
    }

    public async Task<KitaronStationRecord?> GetAsync(int kitaronStationId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return (await ReadAsync(connection, null, kitaronStationId, cancellationToken)).FirstOrDefault();
    }

    public async Task<KitaronStationRecord> DecideAsync(
        int kitaronStationId,
        KitaronStationDecision decision,
        int expectedVersion,
        EditAuthority authority,
        string? userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(connection, transaction, DateTimeOffset.UtcNow, cancellationToken);
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT holder_client_id, generation FROM edit_tokens WHERE id = 1;";
            await using var reader = await check.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
                throw new EditModeMutationException("edit_mode_required", "No Windows client currently holds Edit Mode.");
            if (!string.Equals(reader.GetString(0), authority.ClientId, StringComparison.Ordinal)
                || reader.GetInt64(1) != authority.Generation)
                throw new EditModeMutationException("edit_generation_stale", "This client does not hold the active Edit Mode generation.");
        }
        var current = (await ReadAsync(connection, transaction, kitaronStationId, cancellationToken)).FirstOrDefault()
            ?? throw new KitaronStationNotFoundException(kitaronStationId);
        if (current.Version != expectedVersion) throw new KitaronStationVersionConflictException();

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE kitaron_stations
                SET import_role = $role, machine_type = $machineType, workstation_type_id = $workstationType,
                    external_resource_id = $external, default_minutes_per_part = $perPart,
                    default_minutes_per_batch = $perBatch, capacity_required = $capacity, notes = $notes,
                    decided_at = $now, decided_by = $by, version = version + 1, updated_at = $now
                WHERE kitaron_station_id = $id AND version = $version;
                """;
            update.Parameters.AddWithValue("$role", decision.ImportRole);
            update.Parameters.AddWithValue("$machineType", (object?)decision.MachineType ?? DBNull.Value);
            update.Parameters.AddWithValue("$workstationType", (object?)decision.WorkstationTypeId ?? DBNull.Value);
            update.Parameters.AddWithValue("$external", (object?)decision.ExternalResourceId ?? DBNull.Value);
            update.Parameters.AddWithValue("$perPart", decision.DefaultMinutesPerPart);
            update.Parameters.AddWithValue("$perBatch", decision.DefaultMinutesPerBatch);
            update.Parameters.AddWithValue("$capacity", decision.CapacityRequired);
            update.Parameters.AddWithValue("$notes", (object?)decision.Notes ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$by", (object?)userId ?? authority.ClientId);
            update.Parameters.AddWithValue("$id", kitaronStationId);
            update.Parameters.AddWithValue("$version", expectedVersion);
            try
            {
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) throw new KitaronStationVersionConflictException();
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                throw new KitaronStationValidationException(
                    "target", "The Workstation type or External Resource does not exist.");
            }
        }
        var updated = (await ReadAsync(connection, transaction, kitaronStationId, cancellationToken)).Single();
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    /// <summary>
    /// Records the stations a synchronization saw. New stations start undecided with a suggested
    /// role; known stations keep their decision and only refresh the Kitaron-owned facts.
    /// </summary>
    internal static async Task UpsertDiscoveredAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<KitaronDiscoveredStation> stations,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var station in stations)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO kitaron_stations (
                    kitaron_station_id, station_name, station_type, retired, route_rows, planned_rows, supplier_rows,
                    suggested_role, import_role, first_seen_at, last_seen_at, version, updated_at)
                VALUES ($id, $name, $type, $retired, $rows, $planned, $supplier, $suggested, 'UNDECIDED', $now, $now, 1, $now)
                ON CONFLICT (kitaron_station_id) DO UPDATE SET
                    station_name = excluded.station_name,
                    station_type = excluded.station_type,
                    retired = excluded.retired,
                    route_rows = excluded.route_rows,
                    planned_rows = excluded.planned_rows,
                    supplier_rows = excluded.supplier_rows,
                    suggested_role = excluded.suggested_role,
                    last_seen_at = excluded.last_seen_at,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$id", station.KitaronStationId);
            command.Parameters.AddWithValue("$name", station.StationName);
            command.Parameters.AddWithValue("$type", (object?)station.StationType ?? DBNull.Value);
            command.Parameters.AddWithValue("$retired", station.Retired ? 1 : 0);
            command.Parameters.AddWithValue("$rows", station.RouteRows);
            command.Parameters.AddWithValue("$planned", station.PlannedRows);
            command.Parameters.AddWithValue("$supplier", station.SupplierRows);
            command.Parameters.AddWithValue("$suggested", KitaronStationService.SuggestRole(
                station.RouteRows, station.PlannedRows, station.SupplierRows));
            command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    internal static async Task<IReadOnlyList<KitaronStationRecord>> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int? kitaronStationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {Columns}
            FROM kitaron_stations
            WHERE $id IS NULL OR kitaron_station_id = $id
            ORDER BY CASE import_role WHEN 'UNDECIDED' THEN 0 ELSE 1 END, route_rows DESC, station_name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$id", (object?)kitaronStationId ?? DBNull.Value);
        var values = new List<KitaronStationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new KitaronStationRecord(
                reader.GetInt32(0), reader.GetString(1), Text(reader, 2), reader.GetInt32(3) == 1,
                reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetString(7), reader.GetString(8),
                Text(reader, 9), Text(reader, 10), Text(reader, 11), reader.GetDouble(12), reader.GetDouble(13),
                reader.GetInt32(14), Text(reader, 15), Date(reader, 16)!.Value, Date(reader, 17)!.Value,
                Date(reader, 18), Text(reader, 19), reader.GetInt32(20), Date(reader, 21)!.Value));
        }
        return values;
    }

    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? Date(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null
        : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
