using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>A time kind the operation statistics measure and the planner may apply.</summary>
internal static class OperationTimeKinds
{
    internal const string Cycle = "cycle";
    internal const string Setup = "setup";
    internal const string Qa = "qa";
    internal const string LoadUnload = "load_unload";

    internal static readonly IReadOnlyList<string> All = [Cycle, Setup, Qa, LoadUnload];
}

/// <summary>One measured time of a Case Operation on a Machine (seconds, with when and where it was measured).</summary>
internal sealed record OperationTimeSample(
    string Kind,
    string CaseOperationId,
    string MachineId,
    double Seconds,
    DateTimeOffset MeasuredAt,
    string Source,
    string? BatchNumber);

/// <summary>The median of the last measurements of one time kind.</summary>
internal sealed record MeasuredTime(double MedianSeconds, int SampleCount, DateTimeOffset LastMeasuredAt);

/// <summary>What was measured for one Case Operation on one Machine; a null kind has no measurement.</summary>
internal sealed record MeasuredOperationTimes(
    MeasuredTime? Cycle,
    MeasuredTime? Setup,
    MeasuredTime? Qa,
    MeasuredTime? LoadUnload)
{
    internal MeasuredTime? Of(string kind) => kind switch
    {
        OperationTimeKinds.Cycle => Cycle,
        OperationTimeKinds.Setup => Setup,
        OperationTimeKinds.Qa => Qa,
        OperationTimeKinds.LoadUnload => LoadUnload,
        _ => null
    };
}

/// <summary>
/// The real times of Case Operations on their Machines (owner decisions 2026-09-29): only times of
/// the same Case Operation on the same Machine count, and each kind is the median of its last
/// <see cref="SampleLimit"/> measurements. Measurements come from the immutable Production Run
/// events and the manual reports:
/// <list type="bullet">
/// <item>cycle: completed CNC cycles (start to end, Machine timestamps when both are present) and
/// manual part-time reports;</item>
/// <item>setup: from an Offset Loader run (or a setup run reported by hand) to the first
/// SEND_TO_QC after it, or a manual setup start to its setup end;</item>
/// <item>cycle, on a Machine without DPRNT: a production session reported by hand, from its
/// opening to the operation's finish, over the parts it made;</item>
/// <item>QC: from SEND_TO_QC to the QC decision;</item>
/// <item>load/unload: the gap between a cycle end and the next cycle start of the run, when it is
/// at most <see cref="MaximumLoadGapSeconds"/> (longer gaps are breaks, not loading).</item>
/// </list>
/// </summary>
internal static class SqliteOperationTimeMeasurements
{
    internal const int SampleLimit = 10;
    internal const double MaximumLoadGapSeconds = 3600;

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, (string Signature, IReadOnlyDictionary<(string, string), MeasuredOperationTimes> Medians)> Cache = new();

    /// <summary>
    /// A measured gap between cycles is the loading time only when a worker loads every part; with
    /// automatic loading or loading every N parts the gap is not the Operation's load/unload time.
    /// </summary>
    internal static bool LoadingIsPerPart(bool automaticLoading, int? everyNParts) =>
        !automaticLoading && everyNParts is null or <= 1;

    /// <summary>The medians of every Case Operation and Machine; cached until a new event or report is recorded.</summary>
    internal static async Task<IReadOnlyDictionary<(string CaseOperationId, string MachineId), MeasuredOperationTimes>> ReadMediansAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var signature = await SignatureAsync(connection, transaction, cancellationToken);
        var key = connection.DataSource ?? string.Empty;
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached) && cached.Signature == signature) return cached.Medians;
        }
        var samples = await ReadSamplesAsync(connection, transaction, null, cancellationToken);
        var medians = Medians(samples);
        lock (CacheLock) Cache[key] = (signature, medians);
        return medians;
    }

    internal static IReadOnlyDictionary<(string CaseOperationId, string MachineId), MeasuredOperationTimes> Medians(
        IReadOnlyList<OperationTimeSample> samples) =>
        samples
            .GroupBy(sample => (sample.CaseOperationId, sample.MachineId))
            .ToDictionary(group => group.Key, group => new MeasuredOperationTimes(
                Median(group, OperationTimeKinds.Cycle), Median(group, OperationTimeKinds.Setup),
                Median(group, OperationTimeKinds.Qa), Median(group, OperationTimeKinds.LoadUnload)));

    /// <summary>The median of the last <see cref="SampleLimit"/> measurements of one kind.</summary>
    internal static MeasuredTime? Median(IEnumerable<OperationTimeSample> samples, string kind)
    {
        var recent = samples.Where(sample => sample.Kind == kind && sample.Seconds > 0 && double.IsFinite(sample.Seconds))
            .OrderByDescending(sample => sample.MeasuredAt).Take(SampleLimit).ToArray();
        if (recent.Length == 0) return null;
        var sorted = recent.Select(sample => sample.Seconds).Order().ToArray();
        var middle = sorted.Length / 2;
        var median = sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        return new MeasuredTime(Math.Round(median, 3), recent.Length, recent[0].MeasuredAt);
    }

    /// <summary>Every measurement, optionally of one Case Operation only.</summary>
    internal static async Task<IReadOnlyList<OperationTimeSample>> ReadSamplesAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string? caseOperationId, CancellationToken cancellationToken)
    {
        var samples = new List<OperationTimeSample>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$caseOperationId", (object?)caseOperationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$maximumGap", MaximumLoadGapSeconds);
        command.CommandText = """
            WITH run_operation AS (
                SELECT run.id AS run_id, operation.source_case_operation_id AS case_operation_id, batch.batch_number
                FROM production_runs run
                JOIN batch_operations operation ON operation.id = run.legacy_batch_operation_id
                JOIN production_batches batch ON batch.id = operation.production_batch_id
                WHERE $caseOperationId IS NULL OR operation.source_case_operation_id = $caseOperationId
            ),
            events AS (
                SELECT event.production_run_id AS run_id, event.machine_id, event.event_type, event.server_received_at AS at,
                       run_operation.case_operation_id, run_operation.batch_number,
                       CASE WHEN event.source = 'PLANNER_MANUAL' THEN 'MANUAL' ELSE 'CNC' END AS origin,
                       json_extract(event.metadata_json, '$.producedQuantity') AS produced_quantity
                FROM production_run_workflow_events event
                JOIN run_operation ON run_operation.run_id = event.production_run_id
            ),
            marked AS (
                SELECT events.*,
                       MAX(CASE WHEN event_type IN ('OFFSET_LOADER_COMPLETED', 'MANUAL_SETUP_RUN') THEN at END) OVER (
                           PARTITION BY run_id, machine_id ORDER BY at ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) AS last_loader_at,
                       MAX(CASE WHEN event_type = 'PRODUCTION_SESSION_OPENED' THEN at END) OVER (
                           PARTITION BY run_id, machine_id ORDER BY at ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) AS last_opened_at,
                       MAX(CASE WHEN event_type = 'SEND_TO_QC' THEN at END) OVER (
                           PARTITION BY run_id, machine_id ORDER BY at ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) AS last_sent_at,
                       MIN(CASE WHEN event_type IN ('QC_PASS', 'QC_FAIL') THEN at END) OVER (
                           PARTITION BY run_id, machine_id ORDER BY at ROWS BETWEEN 1 FOLLOWING AND UNBOUNDED FOLLOWING) AS next_decision_at,
                       MIN(CASE WHEN event_type = 'SEND_TO_QC' THEN at END) OVER (
                           PARTITION BY run_id, machine_id ORDER BY at ROWS BETWEEN 1 FOLLOWING AND UNBOUNDED FOLLOWING) AS next_sent_at
                FROM events
            ),
            sequenced AS (
                SELECT events.*,
                       LAG(event_type) OVER (PARTITION BY run_id, machine_id ORDER BY at) AS previous_type,
                       LAG(at) OVER (PARTITION BY run_id, machine_id ORDER BY at) AS previous_at
                FROM events
                WHERE event_type IN ('CYCLE_START', 'CYCLE_END')
            )
            SELECT 'cycle', run_operation.case_operation_id, timing.machine_id,
                   CASE WHEN timing.start_machine_timestamp IS NOT NULL AND timing.end_machine_timestamp IS NOT NULL
                         AND julianday(timing.end_machine_timestamp) > julianday(timing.start_machine_timestamp)
                        THEN (julianday(timing.end_machine_timestamp) - julianday(timing.start_machine_timestamp)) * 86400.0
                        ELSE (julianday(timing.end_server_received_at) - julianday(timing.start_server_received_at)) * 86400.0 END,
                   timing.end_server_received_at, 'CNC', run_operation.batch_number
            FROM production_run_cycle_attempt_timing timing
            JOIN run_operation ON run_operation.run_id = timing.production_run_id
            WHERE timing.completion_state = 'COMPLETED'
              AND julianday(timing.end_server_received_at) > julianday(timing.start_server_received_at)
            UNION ALL
            -- Setup: an Offset Loader run to the first SEND_TO_QC after it.
            SELECT 'setup', case_operation_id, machine_id,
                   (julianday(at) - julianday(last_loader_at)) * 86400.0, at, origin, batch_number
            FROM marked
            WHERE event_type = 'SEND_TO_QC' AND last_loader_at IS NOT NULL
              AND (last_sent_at IS NULL OR last_sent_at < last_loader_at)
            UNION ALL
            -- A production session reported by hand: its time over the parts it made.
            SELECT 'cycle', case_operation_id, machine_id,
                   (julianday(at) - julianday(last_opened_at)) * 86400.0 / produced_quantity, at, 'MANUAL', batch_number
            FROM marked
            WHERE event_type = 'PRODUCTION_SESSION_CLOSED' AND origin = 'MANUAL'
              AND last_opened_at IS NOT NULL AND produced_quantity > 0
            UNION ALL
            -- QC: SEND_TO_QC to the next QC decision, unless the part was sent again first.
            SELECT 'qa', case_operation_id, machine_id,
                   (julianday(next_decision_at) - julianday(at)) * 86400.0, next_decision_at, origin, batch_number
            FROM marked
            WHERE event_type = 'SEND_TO_QC' AND next_decision_at IS NOT NULL
              AND (next_sent_at IS NULL OR next_decision_at < next_sent_at)
            UNION ALL
            SELECT 'load_unload', case_operation_id, machine_id,
                   (julianday(at) - julianday(previous_at)) * 86400.0, at, 'CNC', batch_number
            FROM sequenced
            WHERE event_type = 'CYCLE_START' AND previous_type = 'CYCLE_END'
              AND (julianday(at) - julianday(previous_at)) * 86400.0 BETWEEN 0 AND $maximumGap;
            """;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                samples.Add(new OperationTimeSample(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3),
                    Parse(reader.GetString(4)), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
            }
        }
        samples.AddRange(await ReadManualReportsAsync(connection, transaction, caseOperationId, cancellationToken));
        return samples;
    }

    /// <summary>Manual Machines: part-time reports are cycles, and a setup start to its setup end is a setup.</summary>
    private static async Task<IReadOnlyList<OperationTimeSample>> ReadManualReportsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string? caseOperationId, CancellationToken cancellationToken)
    {
        var reports = new List<(string OperationId, string CaseOperationId, string MachineId, string Type, DateTimeOffset At, double? PartSeconds, string BatchNumber)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT operation.id, operation.source_case_operation_id,
                       json_extract(log.related_entity_ids_json, '$.machineId'), log.reason_code, log.occurred_at,
                       json_extract(log.after_data_json, '$.partTimeSeconds'), batch.batch_number
                FROM structured_event_log log
                JOIN batch_operations operation ON operation.id = json_extract(log.related_entity_ids_json, '$.batchOperationId')
                JOIN production_batches batch ON batch.id = operation.production_batch_id
                WHERE log.event_type = 'manual_operation_reported'
                  AND json_extract(log.related_entity_ids_json, '$.machineId') IS NOT NULL
                  AND ($caseOperationId IS NULL OR operation.source_case_operation_id = $caseOperationId)
                ORDER BY log.occurred_at;
                """;
            command.Parameters.AddWithValue("$caseOperationId", (object?)caseOperationId ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                reports.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    Parse(reader.GetString(4)), reader.IsDBNull(5) ? null : reader.GetDouble(5), reader.GetString(6)));
            }
        }
        var samples = new List<OperationTimeSample>();
        foreach (var report in reports.Where(report => report.Type == "partTimeUpdate" && report.PartSeconds is > 0))
        {
            samples.Add(new OperationTimeSample(OperationTimeKinds.Cycle, report.CaseOperationId, report.MachineId,
                report.PartSeconds!.Value, report.At, "MANUAL", report.BatchNumber));
        }
        foreach (var group in reports.GroupBy(report => (report.OperationId, report.MachineId)))
        {
            DateTimeOffset? start = null;
            foreach (var report in group)
            {
                if (report.Type == "setupStart") start = report.At;
                else if (report.Type == "setupEnd" && start is { } began && report.At > began)
                {
                    samples.Add(new OperationTimeSample(OperationTimeKinds.Setup, report.CaseOperationId, report.MachineId,
                        (report.At - began).TotalSeconds, report.At, "MANUAL", report.BatchNumber));
                    start = null;
                }
            }
        }
        return samples;
    }

    private static async Task<string> SignatureAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE((SELECT MAX(rowid) FROM production_run_workflow_events), 0) || ':' ||
                   COALESCE((SELECT MAX(rowid) FROM production_run_cycle_attempt_outcomes), 0) || ':' ||
                   COALESCE((SELECT MAX(occurred_at) FROM structured_event_log WHERE event_type = 'manual_operation_reported'), '');
            """;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal);
}
