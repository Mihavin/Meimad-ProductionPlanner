using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.Maintenance;
using Meimad.Planner.Server.Application.Projections;

namespace Meimad.Planner.Server.Application.LocalAi;

internal sealed partial class LocalEvidenceService
{
    private static DateTimeOffset? Instant(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind, out var instant) ? instant.ToUniversalTime() : null;
    private static void Append(List<EvidenceFact> facts, List<EvidenceClaim> claims, string kind, string code,
        object value, string? unit, string entity, string entityId, string validity, string description,
        string? version = null, DateTimeOffset? observed = null, DateTimeOffset? received = null, bool claim = true)
    {
        var id = "E" + (facts.Count + 1).ToString(CultureInfo.InvariantCulture);
        facts.Add(new(id, kind, code, JsonSerializer.SerializeToElement(value), unit, new(entity, entityId), version,
            observed, received, validity, description));
        if (claim) claims.Add(new(code, description, [id]));
    }

    private async Task AppendRunPlanningAsync(string runId, HashSet<string> operations, Func<CancellationToken, Task<Timeline.TimelineProjection>> readForecast,
        List<EvidenceFact> facts, List<EvidenceClaim> claims, List<string> limitations, CancellationToken token)
    {
        // Use precisely the Board's current 30-day horizon and authoritative engine. No persisted
        // assignment, backlog or forecast is changed and no diagnostic writes are requested.
        try
        {
            var projection = await readForecast(token);
            var conflicts = projection.Conflicts.Where(x => x.OperationIds.Any(operations.Contains))
                .OrderBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.ConflictId, StringComparer.Ordinal).ToArray();
            foreach (var conflict in conflicts.Take(100))
                Append(facts, claims, "TimelineConflict", conflict.Code,
                    new { conflict.Code, conflict.Severity, conflict.OperationIds, conflict.MachineIds }, null,
                    "productionRun", runId, "FORECAST", conflict.Message);
            if (conflicts.Length > 100) limitations.Add($"Showing 100 of {conflicts.Length} scoped Timeline conflicts; inspect Timeline for the full set.");
            limitations.Add("Timeline conflicts use the same current 30-day horizon as Board. They are forecasts, not execution authorization or historical production facts.");
        }
        catch (LocalForecastBudgetExceededException exception)
        {
            Append(facts, claims, "MissingEvidence", "timeline.budget_exceeded", false, null, "productionRun", runId,
                "UNKNOWN", exception.Message + " Readiness and recorded facts remain available; open Timeline for a separate full calculation. No conflict-free forecast is inferred.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ProjectionChangedException)
        {
            logger.LogWarning(exception, "Local explanation could not calculate Timeline evidence for {RunId}.", runId);
            Append(facts, claims, "MissingEvidence", "timeline.unavailable", false, null, "productionRun", runId,
                "UNKNOWN", "Timeline evidence is unavailable; absence of a reported conflict does not prove a feasible plan.");
        }
        limitations.Add("Responsible employee/role is not resolved by this explanation. Inspect the cited readiness action and existing planning or preparation screen; no employee assignment is inferred.");

        await using var connection = await database.OpenConnectionAsync(token);
        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT package.id,package.machine_id,package.gcode_release_id,package.tool_table_release_id,
                package.offset_loader_release_id,package.manifest_hash,package.created_at,
                current.production_run_program_id,current.production_run_output_id,package.supersedes_package_id
            FROM production_package_context_current current
            JOIN production_packages package ON package.id=current.production_package_id
            WHERE package.production_run_id=$run ORDER BY package.id LIMIT 101;
            """;
        query.Parameters.AddWithValue("$run", runId);
        var count = 0;
        await using var reader = await query.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (++count > 100) { limitations.Add("Package pointer evidence is limited to 100 contexts; use the Operation workbench for the full Run."); break; }
            string? Text(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
            Append(facts, claims, "Package", "package.recorded_pointer", new
            {
                MachineId = Text(1), NcReleaseId = Text(2), ToolTableReleaseId = Text(3), OffsetLoaderReleaseId = Text(4),
                ManifestHash = Text(5), ProgramId = Text(7), OutputId = Text(8), SupersedesPackageId = Text(9)
            }, null, "productionPackage", reader.GetString(0), "RECORDED_POINTER",
                $"Recorded package {reader.GetString(0)} for output {reader.GetString(8)}. Readiness separately decides whether this context is usable.",
                Text(5), DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture));
        }
        if (count == 0) limitations.Add("No exact-context package pointer is recorded for this Run. Historical compatibility packages are not inferred to be current.");
        limitations.Add("Package metadata does not rehash files or prove physical transfer, loader execution or successful NC verification.");
    }

    private async Task AppendOperationalAsync(ExplanationRequest request, string? machineId, DateTimeOffset now,
        List<EvidenceFact> facts, List<EvidenceClaim> claims, List<string> limitations, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.Parameters.AddWithValue("$machine", (object?)machineId ?? DBNull.Value);
        query.Parameters.AddWithValue("$factory", request.Intent == "SummarizeShift");
        query.CommandText = """
            SELECT machine.id,connection.enabled,connection.connection_status,connection.polling_interval_ms,
                connection.version,state.observed_at,state.machine_state
            FROM machines machine LEFT JOIN machine_connections connection ON connection.machine_id=machine.id
            LEFT JOIN machine_current_state state ON state.machine_id=machine.id
            WHERE ($machine IS NOT NULL AND machine.id=$machine) OR ($machine IS NULL AND $factory AND machine.is_active=1)
            ORDER BY machine.id LIMIT 101;
            """;
        var healthCount = 0;
        await using (var reader = await query.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                if (++healthCount > 100) { limitations.Add("Current CNC health shows 100 Machines; select one Machine or open Integrations for additional devices."); break; }
                var observed = reader.IsDBNull(5) ? null : Instant(reader.GetString(5));
                var status = reader.IsDBNull(1) ? "NOT_CONFIGURED" : !reader.GetBoolean(1) ? "DISABLED" : reader.GetString(2);
                var freshness = OperationalHealthService.Freshness(now, observed,
                    TimeSpan.FromMilliseconds(reader.IsDBNull(3) ? 60000 : Math.Max(60000, reader.GetInt32(3) * 3)));
                var state = reader.IsDBNull(6) ? null : reader.GetString(6);
                Append(facts, claims, "MachineHealth", "cnc.observation", new { Status = status, Freshness = freshness, State = state }, null,
                    "machine", reader.GetString(0), "CURRENT_SAMPLE",
                    $"Machine {reader.GetString(0)}: connection {status}, observation {freshness}, recorded state {state ?? "unknown"}.",
                    reader.IsDBNull(4) ? null : reader.GetInt64(4).ToString(CultureInfo.InvariantCulture), observed);
            }
        limitations.Add("Health is sampled now, independently of the requested shift interval. Connection/freshness does not prove NC or Offset Loader verification; unknown receipt times stay unknown.");
        if (request.Intent != "SummarizeShift") return;
        query.Parameters.AddWithValue("$from", request.From!.Value.ToUniversalTime().ToString("O"));
        query.Parameters.AddWithValue("$to", request.To!.Value.ToUniversalTime().ToString("O"));
        var scope = machineId is null ? "factory" : "machine";
        var scopeId = machineId ?? "local";
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timelineOptions.TimeZoneId);
        Append(facts, claims, "TimeScope", "shift.interval", new
        {
            FromUtc = request.From.Value.ToUniversalTime(), ToUtc = request.To.Value.ToUniversalTime(),
            FactoryTimeZoneId = zone.Id, LocalFrom = TimeZoneInfo.ConvertTime(request.From.Value, zone),
            LocalTo = TimeZoneInfo.ConvertTime(request.To.Value, zone)
        }, null, scope, scopeId, "REQUESTED_SCOPE",
            $"Selected interval {request.From.Value.ToUniversalTime():O} to {request.To.Value.ToUniversalTime():O}; factory display timezone {zone.Id}. Shift boundaries are explicitly selected, not inferred.");
        query.CommandText = """
            SELECT event_type,COUNT(*) FROM production_run_workflow_events
            WHERE server_received_at >= $from AND server_received_at < $to AND ($machine IS NULL OR machine_id=$machine)
            GROUP BY event_type ORDER BY event_type;
            """;
        await using (var reader = await query.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                Append(facts, claims, "WorkflowCount", "workflow." + reader.GetString(0), reader.GetInt64(1), "events", scope, scopeId,
                    "AGGREGATED", $"{reader.GetString(0)}: {reader.GetInt64(1)} immutable Run workflow events received in the selected interval.");
        query.CommandText = """
            SELECT id,event_type,server_received_at,machine_timestamp FROM production_run_workflow_events
            WHERE server_received_at >= $from AND server_received_at < $to AND ($machine IS NULL OR machine_id=$machine)
            ORDER BY server_received_at DESC,id DESC LIMIT 20;
            """;
        await using (var reader = await query.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                Append(facts, claims, "WorkflowEvent", reader.GetString(1), reader.GetString(1), null, "workflowEvent", reader.GetString(0),
                    "RECORDED", reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture), claim: false);
        query.CommandText = """
            SELECT COUNT(*),SUM(CASE WHEN julianday(end_server_received_at)>julianday(start_server_received_at) THEN 1 ELSE 0 END),
                SUM(CASE WHEN julianday(end_server_received_at)>julianday(start_server_received_at)
                    THEN (julianday(end_server_received_at)-julianday(start_server_received_at))*86400.0 END),
                AVG(CASE WHEN julianday(end_server_received_at)>julianday(start_server_received_at)
                    THEN (julianday(end_server_received_at)-julianday(start_server_received_at))*86400.0 END)
            FROM production_run_cycle_attempt_timing WHERE completion_state='COMPLETED'
                AND end_server_received_at >= $from AND end_server_received_at < $to AND ($machine IS NULL OR machine_id=$machine);
            """;
        await using (var reader = await query.ExecuteReaderAsync(token))
        {
            await reader.ReadAsync(token);
            var completed = reader.GetInt64(0); var valid = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            var total = reader.IsDBNull(2) ? (double?)null : reader.GetDouble(2);
            var average = reader.IsDBNull(3) ? (double?)null : reader.GetDouble(3);
            Append(facts, claims, "CycleTiming", "cycle.completed_samples", new { CompletedCycles = completed, ValidDurationSamples = valid,
                TotalSeconds = total, AverageSeconds = average }, "seconds", scope, scopeId, "AGGREGATED",
                $"{completed} completed CNC cycle attempts; {valid} positive Server-time duration samples. " +
                (average is null ? "Average duration is unknown." : $"Average duration: {average.Value.ToString("0.###", CultureInfo.InvariantCulture)} seconds."));
        }
        query.CommandText = """
            SELECT COUNT(*) FROM production_packages WHERE supersedes_package_id IS NOT NULL
                AND created_at >= $from AND created_at < $to AND ($machine IS NULL OR machine_id=$machine);
            """;
        var supersessions = Convert.ToInt64(await query.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
        Append(facts, claims, "PackageCount", "package.supersessions", supersessions, "packages", scope, scopeId, "AGGREGATED",
            $"{supersessions} superseding Production Packages were created in the selected interval.");
        query.CommandText = """
            SELECT id,is_enabled,COALESCE(last_server_contact_at,last_seen_at),version FROM device_registry
            WHERE device_type='eink' AND ($machine IS NULL OR machine_id=$machine) ORDER BY id LIMIT 101;
            """;
        var tablets = 0;
        await using (var reader = await query.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                if (++tablets > 100) { limitations.Add("Tablet health is limited to 100 devices; inspect Integrations for the rest."); break; }
                var contact = reader.IsDBNull(2) ? null : Instant(reader.GetString(2));
                var freshness = OperationalHealthService.Freshness(now, contact, TimeSpan.FromHours(24));
                var enabled = reader.GetBoolean(1);
                Append(facts, claims, "TabletHealth", "tablet.contact", new { Enabled = enabled, Freshness = freshness }, null,
                    "tablet", reader.GetString(0), "CURRENT_SAMPLE", $"Tablet {reader.GetString(0)}: {(enabled ? "registered" : "disabled")}, contact {freshness}.",
                    reader.GetInt64(3).ToString(CultureInfo.InvariantCulture), received: contact);
            }
        query.CommandText = "SELECT COUNT(*) FROM kitaron_push_runs WHERE lifecycle_state IN ('OutcomeUnknown','Writing','Prepared','Preparing')";
        var unresolved = Convert.ToInt64(await query.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
        Append(facts, claims, "IntegrationCount", "erp.unresolved_pushes", unresolved, "pushes", "factory", "kitaron", "CURRENT_SAMPLE",
            $"Factory integration currently has {unresolved} unresolved ERP push attempts; this is not a count of changes for the selected Machine or shift.");
        query.CommandText = """
            SELECT id,lifecycle_state,started_at,intent_version FROM kitaron_push_runs
            WHERE lifecycle_state IN ('OutcomeUnknown','Writing','Prepared','Preparing') ORDER BY started_at,id LIMIT 20;
            """;
        await using (var reader = await query.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                Append(facts, claims, "IntegrationOutcome", "erp.push_outcome", reader.GetString(1), null,
                    "erpPush", reader.GetString(0), "CURRENT_SAMPLE", $"ERP push {reader.GetString(0)} is {reader.GetString(1)}; no ERP commit is inferred.",
                    reader.IsDBNull(3) ? null : Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture), Instant(reader.GetString(2)));
        if (unresolved > 20) limitations.Add("Showing 20 unresolved ERP attempt references; the total covers all recorded unresolved attempts.");
        limitations.Add("Workflow totals cover the complete Server-received [from,to) interval; the 20 event references are representative, not the aggregation input. Source-event deduplication remains owned by ingestion.");
        limitations.Add("Cycle duration uses positive Server-received end minus start, matching Timeline's CNC samples. Full completed-cycle durations are assigned to the interval containing completion; they are not clipped shift occupancy, spindle time, parts produced, or plan-versus-actual history. Open/interrupted attempts and invalid durations contribute no duration. No samples means unknown, not zero duration.");
    }
}
