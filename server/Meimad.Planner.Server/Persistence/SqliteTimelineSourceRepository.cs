using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using Meimad.Planner.Server.Application.Timeline;
using Meimad.Planner.Server.Domain.Timeline;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteTimelineSourceRepository : ITimelineSourceRepository
{
    private readonly SqliteDatabase database;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SqliteTimelineSourceRepository> logger;

    public SqliteTimelineSourceRepository(
        SqliteDatabase database,
        TimeProvider timeProvider,
        ILogger<SqliteTimelineSourceRepository> logger)
    {
        this.database = database;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<TimelineSourceSnapshot> ReadAsync(
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var phase = Stopwatch.StartNew();
        var machines = await ReadMachinesAsync(connection, transaction, cancellationToken);
        var machinesElapsed = phase.ElapsedMilliseconds;
        phase.Restart();
        var operations = await ReadOperationsAsync(connection, transaction, cancellationToken);
        var operationsElapsed = phase.ElapsedMilliseconds;
        phase.Restart();
        var downtimes = await ReadDowntimesAsync(
            connection,
            transaction,
            horizonStart,
            horizonEnd,
            cancellationToken);
        var downtimesElapsed = phase.ElapsedMilliseconds;
        phase.Restart();
        var setupCalendar = await ReadSetupCalendarAsync(
            connection,
            transaction,
            cancellationToken);
        var setupCalendarElapsed = phase.ElapsedMilliseconds;
        phase.Restart();
        var holidays = await ReadHolidaysAsync(connection, transaction, horizonStart, horizonEnd, cancellationToken);
        var holidaysElapsed = phase.ElapsedMilliseconds;
        phase.Restart();
        var resources = await ReadResourcesAsync(connection, transaction, horizonStart, horizonEnd, cancellationToken);
        var resourcesElapsed = phase.ElapsedMilliseconds;
        await transaction.CommitAsync(cancellationToken);
        total.Stop();
        logger.LogInformation(
            "Timeline database read completed in {TotalMilliseconds} ms: machines {MachinesMilliseconds} ms ({MachineCount}), operations {OperationsMilliseconds} ms ({OperationCount}), downtimes {DowntimesMilliseconds} ms ({DowntimeCount}), setup calendar {SetupCalendarMilliseconds} ms, holidays {HolidaysMilliseconds} ms ({HolidayCount}), resources and exceptions {ResourcesMilliseconds} ms ({ResourceCount}).",
            total.ElapsedMilliseconds, machinesElapsed, machines.Count, operationsElapsed, operations.Count,
            downtimesElapsed, downtimes.Count, setupCalendarElapsed, holidaysElapsed, holidays.Count,
            resourcesElapsed, resources.Count);
        return new TimelineSourceSnapshot(
            timeProvider.GetUtcNow(),
            machines,
            operations,
            downtimes,
            setupCalendar.Json,
            setupCalendar.TimeZoneId,
            holidays,
            resources);
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
                   working_calendars.time_zone_id, working_calendars.calendar_json,
                   machines.machine_type, machines.axis_type, machines.capabilities_json,
                   machine_types.capabilities_json
            FROM machines
            JOIN working_calendars
              ON working_calendars.id = machines.working_calendar_id
            LEFT JOIN machine_types ON machine_types.id = machines.machine_type_id
            WHERE machines.is_active = 1
               OR EXISTS (
                    SELECT 1 FROM batch_operations
                    WHERE batch_operations.actual_machine_id = machines.id)
            ORDER BY machines.number COLLATE NOCASE, machines.id;
            """;
        var values = new List<TimelineSourceMachine>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new TimelineSourceMachine(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4),
                MachineSkillTokens(reader)));
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
                   batch_operations.dependency_type,
                   batch_operations.predecessor_source_case_operation_id,
                   batch_operations.simultaneous_group_key,
                   machine_assignments.id,
                   machine_assignments.machine_id,
                   machine_assignments.backlog_position,
                   machine_assignments.planning_mode,
                   batch_operations.qa_seconds,
                   batch_operations.load_unload_seconds,
                   batch_operations.load_unload_requires_worker,
                   batch_operations.automatic_loading,
                   batch_operations.load_unload_every_n_parts,
                   batch_operations.day_shift_only,
                   (SELECT MIN(orders.work_finish_date)
                    FROM batch_allocations
                    JOIN orders ON orders.id = batch_allocations.order_id
                    WHERE batch_allocations.production_batch_id = production_batches.id
                      AND batch_allocations.allocation_type = 'order') AS priority_due_date,
                   COALESCE((SELECT json_group_array(order_reference)
                    FROM (SELECT DISTINCT orders.order_reference
                          FROM batch_allocations
                          JOIN orders ON orders.id = batch_allocations.order_id
                          WHERE batch_allocations.production_batch_id = production_batches.id
                            AND batch_allocations.allocation_type = 'order'
                            AND orders.work_finish_date = (
                                SELECT MIN(priority_orders.work_finish_date)
                                FROM batch_allocations AS priority_allocations
                                JOIN orders AS priority_orders ON priority_orders.id = priority_allocations.order_id
                                WHERE priority_allocations.production_batch_id = production_batches.id
                                  AND priority_allocations.allocation_type = 'order'))), '[]'),
                   operation_pause_events.reason_type,
                   operation_pause_events.paused_by,
                   operation_pause_events.pause_started_at,
                   COALESCE(operation_pause_events.problem_description,
                            operation_pause_events.tooling_item_description,
                            operation_pause_events.request_description,
                            operation_pause_events.comment),
                   batch_operations.actual_start,
                   batch_operations.actual_end,
                   batch_operations.actual_machine_id
            FROM batch_operations
            JOIN production_batches
              ON production_batches.id = batch_operations.production_batch_id
            JOIN cases ON cases.id = production_batches.case_id
            LEFT JOIN machine_assignments
              ON machine_assignments.batch_operation_id = batch_operations.id
            LEFT JOIN operation_pause_events
              ON operation_pause_events.batch_operation_id = batch_operations.id
             AND operation_pause_events.status = 'active'
            ORDER BY production_batches.id, batch_operations.route_position;
            """;
        var values = new List<TimelineSourceOperation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            DateOnly? priorityDate = NullableString(reader, 25) is { } date
                ? DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null;
            var priorityOrder = (JsonSerializer.Deserialize<string[]>(reader.GetString(26)) ?? [])
                .OrderBy(value => value, Comparer<string>.Create(TimelinePriorityComparer.CompareOrderNumbers))
                .FirstOrDefault();
            values.Add(new TimelineSourceOperation(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5), reader.GetString(6), reader.GetString(7),
                reader.GetInt32(8), NullableInt(reader, 9), NullableInt(reader, 10),
                reader.GetString(11), reader.GetString(12), NullableString(reader, 13),
                NullableString(reader, 14), NullableString(reader, 15), NullableString(reader, 16), NullableInt(reader, 17),
                NullableString(reader, 18), reader.GetInt32(19), reader.GetInt32(20), reader.GetInt32(21) == 1,
                reader.GetInt32(22) == 1, NullableInt(reader, 23), reader.GetInt32(24) == 1,
                priorityDate, priorityOrder,
                reader.IsDBNull(27) ? null : $"{reader.GetString(27).Replace('_', ' ')}: {reader.GetString(30)}",
                NullableString(reader, 28),
                NullableString(reader, 29) is { } pauseAt ? Parse(pauseAt) : null,
                NullableString(reader, 31) is { } actualStart ? Parse(actualStart) : null,
                NullableString(reader, 32) is { } actualEnd ? Parse(actualEnd) : null,
                NullableString(reader, 33)));
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
            SELECT id, machine_id, starts_at, COALESCE(ends_at, $horizonEnd),
                   downtime_type, reason, planned_by, repair_note, reported_by, status
            FROM downtimes
            WHERE status IN ('planned', 'active', 'restored')
            ORDER BY starts_at, id;
            """;
        command.Parameters.AddWithValue("$horizonEnd", horizonEnd.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
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
                    endsAt, DowntimeDetail(reader)));
            }
        }

        return values;
    }

    private static async Task<SetupCalendarSource> ReadSetupCalendarAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT working_calendars.calendar_json, working_calendars.time_zone_id
            FROM setup_calendar_settings
            JOIN working_calendars
              ON working_calendars.id = setup_calendar_settings.working_calendar_id
            WHERE setup_calendar_settings.id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new SetupCalendarSource(reader.GetString(0), reader.GetString(1));
        }

        await reader.DisposeAsync();
        command.CommandText = """
            SELECT legacy_fallback_enabled
            FROM setup_calendar_settings
            WHERE id = 1;
            """;
        command.Parameters.Clear();
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
        {
            return new SetupCalendarSource(null, null);
        }

        command.CommandText = """
            SELECT value
            FROM application_settings
            WHERE key = 'timeline.setup_calendar_json';
            """;
        command.Parameters.Clear();
        return new SetupCalendarSource(
            await command.ExecuteScalarAsync(cancellationToken) as string,
            null);
    }

    private static async Task<IReadOnlyList<TimelineSourceHoliday>> ReadHolidaysAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        DateTimeOffset horizonStart, DateTimeOffset horizonEnd, CancellationToken cancellationToken)
    {
        await using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="SELECT holiday_date,name,holiday_status,starts_at_local,ends_at_local FROM israeli_holidays WHERE holiday_date >= $from AND holiday_date <= $to ORDER BY holiday_date;";
        command.Parameters.AddWithValue("$from",horizonStart.UtcDateTime.AddDays(-2).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to",horizonEnd.UtcDateTime.AddDays(2).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
        var values=new List<TimelineSourceHoliday>();await using var reader=await command.ExecuteReaderAsync(cancellationToken);
        while(await reader.ReadAsync(cancellationToken))values.Add(new(DateOnly.ParseExact(reader.GetString(0),"yyyy-MM-dd",CultureInfo.InvariantCulture),reader.GetString(1),reader.GetString(2),NullableString(reader,3),NullableString(reader,4)));
        return values;
    }

    private static string DowntimeDetail(SqliteDataReader reader)
    {
        var type = reader.GetString(4);
        var reason = reader.GetString(5);
        if (type == "planned_maintenance")
            return reader.IsDBNull(6) ? $"Planned maintenance: {reason}" : $"Planned maintenance: {reason} (planned by {reader.GetString(6)})";
        var reported = reader.IsDBNull(8) ? string.Empty : $" (reported by {reader.GetString(8)})";
        var repair = reader.IsDBNull(7) ? string.Empty : $" Repair: {reader.GetString(7)}";
        return $"Breakdown: {reason}{reported}.{repair}".TrimEnd();
    }

    private static async Task<IReadOnlyList<TimelineSourceResource>> ReadResourcesAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        DateTimeOffset horizonStart, DateTimeOffset horizonEnd, CancellationToken cancellationToken)
    {
        var resources = new List<TimelineSourceResource>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT employee_resources.id, employee_resources.resource_type,
                   working_calendars.time_zone_id, working_calendars.calendar_json,
                   employee_resources.skills_json
            FROM employee_resources
            JOIN working_calendars ON working_calendars.id = employee_resources.assigned_calendar_id
            WHERE employee_resources.is_active = 1
            ORDER BY employee_resources.id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            resources.Add(new TimelineSourceResource(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), [],
                JsonSerializer.Deserialize<string[]>(reader.GetString(4)) ?? []));
        }
        await reader.DisposeAsync();

        // Read every active resource's relevant exceptions in one indexed query. The
        // previous per-resource query was an N+1 pattern that made Timeline loading
        // slower as the employee directory grew.
        var exceptionsByResource = new Dictionary<string, List<TimelineSourceResourceException>>(
            StringComparer.Ordinal);
        await using var exceptions = connection.CreateCommand();
        exceptions.Transaction = transaction;
        exceptions.CommandText = """
            SELECT employee_calendar_exceptions.resource_id,
                   employee_calendar_exceptions.exception_date,
                   employee_calendar_exceptions.is_full_day,
                   employee_calendar_exceptions.starts_at_local,
                   employee_calendar_exceptions.ends_at_local
            FROM employee_calendar_exceptions
            JOIN employee_resources
              ON employee_resources.id = employee_calendar_exceptions.resource_id
            WHERE employee_resources.is_active = 1
              AND employee_calendar_exceptions.exception_date >= $from
              AND employee_calendar_exceptions.exception_date <= $to
            ORDER BY employee_calendar_exceptions.resource_id,
                     employee_calendar_exceptions.exception_date,
                     employee_calendar_exceptions.starts_at_local,
                     employee_calendar_exceptions.id;
            """;
        exceptions.Parameters.AddWithValue("$from", horizonStart.UtcDateTime.AddDays(-2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        exceptions.Parameters.AddWithValue("$to", horizonEnd.UtcDateTime.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await using var exceptionReader = await exceptions.ExecuteReaderAsync(cancellationToken);
        while (await exceptionReader.ReadAsync(cancellationToken))
        {
            var resourceId = exceptionReader.GetString(0);
            if (!exceptionsByResource.TryGetValue(resourceId, out var values))
            {
                values = [];
                exceptionsByResource.Add(resourceId, values);
            }

            values.Add(new TimelineSourceResourceException(
                DateOnly.ParseExact(exceptionReader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                exceptionReader.GetInt32(2) == 1,
                NullableString(exceptionReader, 3), NullableString(exceptionReader, 4)));
        }

        for (var index = 0; index < resources.Count; index++)
        {
            resources[index] = resources[index] with
            {
                Exceptions = exceptionsByResource.GetValueOrDefault(resources[index].ResourceId) ?? []
            };
        }
        return resources;
    }

    private static IReadOnlyList<string> MachineSkillTokens(SqliteDataReader reader)
    {
        var machineCapabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(7)) ?? [];
        var typeCapabilities = reader.IsDBNull(8)
            ? []
            : JsonSerializer.Deserialize<string[]>(reader.GetString(8)) ?? [];
        return new[] { reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(5) }
            .Concat(reader.IsDBNull(6) ? [] : [reader.GetString(6)])
            .Concat(machineCapabilities)
            .Concat(typeCapabilities)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? NullableInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private sealed record SetupCalendarSource(string? Json, string? TimeZoneId);
}
