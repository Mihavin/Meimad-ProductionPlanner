using System.Globalization;
using Meimad.Planner.Server.Application.Reports;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>Reads what the machine usage report needs in one consistent snapshot.</summary>
internal sealed class SqliteMachineUsageRepository(SqliteDatabase database) : IMachineUsageRepository
{
    /// <summary>Workflow events after which a setup that started earlier is no longer in progress.</summary>
    private const string SetupEndingEvents =
        "'SEND_TO_QC', 'PRODUCTION_SESSION_OPENED', 'PRODUCTION_SESSION_CLOSED', 'SETUP_RESTARTED', " +
        "'MANUAL_READY_FOR_SETUP', 'OFFSET_LOADER_COMPLETED', 'MANUAL_SETUP_RUN'";

    public async Task<MachineUsageSource> ReadAsync(
        DateTimeOffset from, DateTimeOffset to, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var machines = await SqliteTimelineSourceRepository.ReadMachinesAsync(connection, transaction, cancellationToken);
        var cnc = await ReadCncStatusAsync(connection, transaction, cancellationToken);
        var downtimes = (await SqliteTimelineSourceRepository.ReadDowntimesAsync(connection, transaction, from, to, cancellationToken))
            .Select(downtime => downtime.EndsAt > now ? downtime with { EndsAt = now } : downtime)
            .Where(downtime => downtime.EndsAt > downtime.StartsAt)
            .ToArray();
        var holidays = await SqliteTimelineSourceRepository.ReadHolidaysAsync(connection, transaction, from, to, cancellationToken);
        var master = await SqliteTimelineSourceRepository.ReadMasterCalendarAsync(connection, transaction, cancellationToken);
        var changes = await ReadStateChangesAsync(connection, transaction, from, to, cancellationToken);
        var sessions = await ReadProductionSessionsAsync(connection, transaction, to, now, cancellationToken);
        var setups = await ReadSetupsAsync(connection, transaction, from, to, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MachineUsageSource(
            machines.Select(machine => cnc.TryGetValue(machine.MachineId, out var status)
                    ? new MachineUsageSourceMachine(machine, status.Enabled, status.LastPolledAt)
                    : new MachineUsageSourceMachine(machine, false, null))
                .ToArray(),
            master.Json, master.TimeZoneId, holidays, downtimes, changes, sessions, setups);
    }

    private static async Task<IReadOnlyDictionary<string, (bool Enabled, DateTimeOffset? LastPolledAt)>> ReadCncStatusAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT machines.id,
                   EXISTS (SELECT 1 FROM machine_connections
                           WHERE machine_connections.machine_id = machines.id AND machine_connections.enabled = 1),
                   (SELECT observed_at FROM machine_current_state WHERE machine_current_state.machine_id = machines.id)
            FROM machines;
            """;
        var values = new Dictionary<string, (bool, DateTimeOffset?)>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            values[reader.GetString(0)] = (reader.GetInt64(1) == 1, reader.IsDBNull(2) ? null : Parse(reader.GetString(2)));
        return values;
    }

    /// <summary>The CNC changes in [from, to) and, per Machine, the last change before <paramref name="from"/>.</summary>
    private static async Task<IReadOnlyList<MachineStateChange>> ReadStateChangesAsync(
        SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // observed_at is always written as UTC round-trip text, so text order is time order.
        command.CommandText = """
            SELECT history.machine_id, history.observed_at,
                   json_extract(history.snapshot_json, '$.connectionStatus'),
                   json_extract(history.snapshot_json, '$.machineState.value')
            FROM machine_state_history history
            WHERE history.observed_at < $to
              AND (history.observed_at >= $from
                   OR history.observed_at = (
                       SELECT MAX(earlier.observed_at) FROM machine_state_history earlier
                       WHERE earlier.machine_id = history.machine_id AND earlier.observed_at < $from))
            ORDER BY history.machine_id, history.observed_at;
            """;
        command.Parameters.AddWithValue("$from", Format(from));
        command.Parameters.AddWithValue("$to", Format(to));
        var values = new List<MachineStateChange>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new MachineStateChange(
                reader.GetString(0), Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return values;
    }

    /// <summary>
    /// Production sessions: each PRODUCTION_SESSION_OPENED of a Production Run on a Machine to its
    /// next PRODUCTION_SESSION_CLOSED there, to the run's first later event on another Machine (the
    /// run moved), or to now while it is open.
    /// </summary>
    private static async Task<IReadOnlyList<MachineTimeSpan>> ReadProductionSessionsAsync(
        SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset to, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var events = new List<(string RunId, string MachineId, string Type, DateTimeOffset At)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT production_run_id, machine_id, event_type, server_received_at
                FROM production_run_workflow_events
                WHERE julianday(server_received_at) < julianday($to);
                """;
            command.Parameters.AddWithValue("$to", Format(to));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                events.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), Parse(reader.GetString(3))));
        }
        var spans = new List<MachineTimeSpan>();
        foreach (var run in events.GroupBy(value => value.RunId))
        {
            (string MachineId, DateTimeOffset At)? open = null;
            foreach (var value in run.OrderBy(value => value.At))
            {
                if (open is { } session && (value.MachineId != session.MachineId || value.Type == "PRODUCTION_SESSION_CLOSED"))
                {
                    spans.Add(new MachineTimeSpan(session.MachineId, session.At, value.At));
                    open = null;
                }
                if (value.Type == "PRODUCTION_SESSION_OPENED") open ??= (value.MachineId, value.At);
            }
            if (open is { } last && now > last.At) spans.Add(new MachineTimeSpan(last.MachineId, last.At, now));
        }
        return spans;
    }

    /// <summary>
    /// The measured setups (the same definition as the operation time statistics) that touch the
    /// period, and every setup of a Production Run still in progress (no later setup-ending event,
    /// and the run not since reported on another Machine), until now.
    /// </summary>
    private static async Task<IReadOnlyList<MachineTimeSpan>> ReadSetupsAsync(
        SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset from, DateTimeOffset to,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var spans = (await SqliteOperationTimeMeasurements.ReadSamplesAsync(connection, transaction, null, cancellationToken))
            .Where(sample => sample.Kind == OperationTimeKinds.Setup && sample.Seconds > 0 && double.IsFinite(sample.Seconds))
            .Select(sample => new MachineTimeSpan(sample.MachineId, sample.MeasuredAt.AddSeconds(-sample.Seconds), sample.MeasuredAt))
            .Where(span => span.StartsAt < to && span.EndsAt > from)
            .ToList();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT started.machine_id, started.server_received_at
            FROM production_run_workflow_events started
            WHERE started.event_type IN ('OFFSET_LOADER_COMPLETED', 'MANUAL_SETUP_RUN')
              AND julianday(started.server_received_at) < julianday($to)
              AND NOT EXISTS (
                  SELECT 1 FROM production_run_workflow_events later
                  WHERE later.production_run_id = started.production_run_id
                    AND (later.machine_id <> started.machine_id OR later.event_type IN ({SetupEndingEvents}))
                    AND julianday(later.server_received_at) > julianday(started.server_received_at));
            """;
        command.Parameters.AddWithValue("$to", Format(to));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var startedAt = Parse(reader.GetString(1));
            if (now > startedAt) spans.Add(new MachineTimeSpan(reader.GetString(0), startedAt, now));
        }
        return spans;
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal)
            .ToUniversalTime();
}
