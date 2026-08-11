using System.Globalization;
using Meimad.Planner.Server.Application.Timeline;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteTimelineSourceRepository : ITimelineSourceRepository
{
    private readonly SqliteDatabase database;
    private readonly TimeProvider timeProvider;

    public SqliteTimelineSourceRepository(SqliteDatabase database, TimeProvider timeProvider)
    {
        this.database = database;
        this.timeProvider = timeProvider;
    }

    public async Task<TimelineSourceSnapshot> ReadAsync(
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var machines = await ReadMachinesAsync(connection, transaction, cancellationToken);
        var operations = await ReadOperationsAsync(connection, transaction, cancellationToken);
        var downtimes = await ReadDowntimesAsync(
            connection,
            transaction,
            horizonStart,
            horizonEnd,
            cancellationToken);
        var setupCalendar = await ReadSettingAsync(
            connection,
            transaction,
            "timeline.setup_calendar_json",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TimelineSourceSnapshot(
            timeProvider.GetUtcNow(),
            machines,
            operations,
            downtimes,
            setupCalendar);
    }

    private static async Task<IReadOnlyList<TimelineSourceMachine>> ReadMachinesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT machines.id, machines.number, machines.name,
                   working_calendars.time_zone_id, working_calendars.calendar_json
            FROM machines
            JOIN working_calendars
              ON working_calendars.id = machines.working_calendar_id
            WHERE machines.is_active = 1
            ORDER BY machines.number COLLATE NOCASE, machines.id;
            """;
        var values = new List<TimelineSourceMachine>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new TimelineSourceMachine(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4)));
        }

        return values;
    }

    private static async Task<IReadOnlyList<TimelineSourceOperation>> ReadOperationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT batch_operations.id, production_batches.id,
                   production_batches.batch_number, cases.id, cases.part_number,
                   batch_operations.operation_number, batch_operations.name,
                   batch_operations.status, production_batches.planned_quantity,
                   batch_operations.setup_seconds, batch_operations.cycle_seconds,
                   batch_operations.source_case_operation_id,
                   case_operations.dependency_type,
                   case_operations.predecessor_case_operation_id,
                   case_operations.simultaneous_group_key,
                   machine_assignments.machine_id,
                   machine_assignments.backlog_position
            FROM batch_operations
            JOIN production_batches
              ON production_batches.id = batch_operations.production_batch_id
            JOIN cases ON cases.id = production_batches.case_id
            JOIN case_operations
              ON case_operations.id = batch_operations.source_case_operation_id
            LEFT JOIN machine_assignments
              ON machine_assignments.batch_operation_id = batch_operations.id
            ORDER BY production_batches.id, batch_operations.route_position;
            """;
        var values = new List<TimelineSourceOperation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new TimelineSourceOperation(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5), reader.GetString(6), reader.GetString(7),
                reader.GetInt32(8), NullableInt(reader, 9), NullableInt(reader, 10),
                reader.GetString(11), reader.GetString(12), NullableString(reader, 13),
                NullableString(reader, 14), NullableString(reader, 15), NullableInt(reader, 16)));
        }

        return values;
    }

    private static async Task<IReadOnlyList<TimelineSourceDowntime>> ReadDowntimesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, machine_id, starts_at, ends_at, reason
            FROM downtimes
            ORDER BY starts_at, id;
            """;
        var values = new List<TimelineSourceDowntime>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var startsAt = Parse(reader.GetString(2));
            var endsAt = Parse(reader.GetString(3));
            if (startsAt < horizonEnd && endsAt > horizonStart)
            {
                values.Add(new TimelineSourceDowntime(
                    reader.GetString(0), reader.GetString(1), startsAt,
                    endsAt, reader.GetString(4)));
            }
        }

        return values;
    }

    private static async Task<string?> ReadSettingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM application_settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? NullableInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
