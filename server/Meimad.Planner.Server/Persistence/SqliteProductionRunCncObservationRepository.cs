using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.ProductionRuns;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteProductionRunCncObservationRepository(
    SqliteDatabase database, TimeProvider timeProvider)
    : IProductionRunCncObservationRepository
{
    public async Task<CncCycleObservationResult> ConsumeCycleEventAsync(
        CncCycleObservation observation, CancellationToken token)
    {
        var source = $"HAAS_DPRINT:{observation.MachineId}".ToUpperInvariant();
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);

        await using (var duplicate = connection.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText = """
                SELECT event_type,production_run_id,
                       json_extract(metadata_json,'$.productionRunProgramId'),
                       (SELECT completed_cycle_count FROM production_run_cycle_events
                        WHERE source=$source AND source_event_id=$eventId)
                FROM production_run_workflow_events
                WHERE source=$source AND source_event_id=$eventId;
                """;
            duplicate.Parameters.AddWithValue("$source", source);
            duplicate.Parameters.AddWithValue("$eventId", observation.SourceEventId);
            await using var reader = await duplicate.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                var sameType = reader.GetString(0) == observation.EventType;
                int? completedCycleCount = reader.IsDBNull(3) ? null : reader.GetInt32(3);
                var result = new CncCycleObservationResult(
                    sameType, sameType, sameType && completedCycleCount.HasValue,
                    sameType ? "duplicate" : "source_event_conflict",
                    reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    completedCycleCount);
                await transaction.CommitAsync(token);
                return result;
            }
        }

        var candidates = new List<CycleTarget>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT run.id,program.id,
                       COALESCE(program.production_gcode_release_id,program.selected_gcode_release_id),
                       hook.nc_identity_token,release.original_file_name
                FROM machine_assignments assignment
                JOIN production_runs run ON run.id=assignment.production_run_id
                JOIN production_run_programs program ON program.production_run_id=run.id
                LEFT JOIN gcode_releases release ON release.id=COALESCE(
                    program.production_gcode_release_id,program.selected_gcode_release_id)
                LEFT JOIN gcode_release_verification_hooks hook
                    ON hook.gcode_release_id=release.id
                WHERE assignment.machine_id=$machineId
                  AND run.status='IN_PROGRESS' AND program.status='ACTIVE'
                  AND ($runIdentity IS NULL OR upper(run.id)=upper($runIdentity))
                  AND ($programIdentity IS NULL
                       OR upper(program.id)=upper($programIdentity)
                       OR CAST(hook.nc_identity_token AS TEXT)=$programIdentity
                       OR upper(COALESCE(release.original_file_name,''))=upper($programIdentity));
                """;
            query.Parameters.AddWithValue("$machineId", observation.MachineId);
            query.Parameters.AddWithValue("$runIdentity", Db(observation.ProductionRunIdentity));
            query.Parameters.AddWithValue("$programIdentity", Db(observation.ProgramIdentity));
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                candidates.Add(new(reader.GetString(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        if (candidates.Count != 1)
        {
            await transaction.RollbackAsync(token);
            return new(false, false, false,
                candidates.Count == 0 ? "cycle_target_unresolved" : "cycle_target_ambiguous");
        }

        var target = candidates[0];
        LatestCycleWorkflow? latest = null;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT id,event_type,source,source_event_id,source_sequence,server_received_at,
                       json_extract(metadata_json,'$.productionRunProgramId')
                FROM production_run_workflow_events
                WHERE production_run_id=$runId
                ORDER BY server_received_at DESC,id DESC LIMIT 1;
                """;
            query.Parameters.AddWithValue("$runId", target.RunId);
            await using var reader = await query.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
                latest = new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind).ToUniversalTime(),
                    reader.IsDBNull(6) ? null : reader.GetString(6));
        }

        var interruptedAttempt = observation.EventType == "CYCLE_START"
            && latest is { EventType: "CYCLE_START" };
        var validStart = observation.EventType == "CYCLE_START"
            && (latest?.EventType is "QC_PASS" or "CYCLE_END" || interruptedAttempt);
        var validEnd = observation.EventType == "CYCLE_END"
            && latest is { EventType: "CYCLE_START" }
            && latest.Source == source
            && latest.ProgramId == target.ProgramId
            && latest.Sequence.HasValue
            && observation.Sequence == latest.Sequence.Value + 1;
        if (observation.EventType == "CYCLE_START" && !validStart)
        {
            await transaction.RollbackAsync(token);
            return new(false, false, false, "cycle_start_requires_qc_pass_or_completed_cycle",
                target.RunId, target.ProgramId);
        }
        var unmatchedEnd = observation.EventType == "CYCLE_END" && !validEnd;

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var receivedAt = latest is not null && now <= latest.ReceivedAt
            ? latest.ReceivedAt.AddTicks(1)
            : now;
        long? previousSourceSequence = null;
        await using (var previous = connection.CreateCommand())
        {
            previous.Transaction = transaction;
            previous.CommandText = """
                SELECT MAX(source_sequence) FROM production_run_workflow_events
                WHERE machine_id=$machineId AND source=$source
                  AND source_sequence IS NOT NULL;
                """;
            previous.Parameters.AddWithValue("$machineId", observation.MachineId);
            previous.Parameters.AddWithValue("$source", source);
            var scalar = await previous.ExecuteScalarAsync(token);
            previousSourceSequence = scalar is null or DBNull
                ? null
                : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }
        if (interruptedAttempt)
        {
            var interruptedAt = receivedAt;
            await using var interrupted = connection.CreateCommand();
            interrupted.Transaction = transaction;
            interrupted.CommandText = """
                INSERT INTO production_run_workflow_events(
                    id,production_run_id,machine_id,event_type,source,source_event_id,
                    server_received_at,nc_release_id,metadata_json)
                VALUES($id,$runId,$machineId,'CYCLE_INTERRUPTED','SERVER_CYCLE',
                       $sourceEventId,$receivedAt,$releaseId,$metadata);
                """;
            interrupted.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            interrupted.Parameters.AddWithValue("$runId", target.RunId);
            interrupted.Parameters.AddWithValue("$machineId", observation.MachineId);
            interrupted.Parameters.AddWithValue("$sourceEventId",
                $"INTERRUPTED:{source}:{observation.SourceEventId}");
            interrupted.Parameters.AddWithValue("$receivedAt", Format(interruptedAt));
            interrupted.Parameters.AddWithValue("$releaseId", Db(target.NcReleaseId));
            interrupted.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(new
            {
                productionRunProgramId = latest!.ProgramId,
                interruptedWorkflowEventId = latest.EventId,
                interruptedSourceEventId = latest.SourceEventId,
                interruptedBySourceEventId = observation.SourceEventId,
                interruptedBySequence = observation.Sequence
            }));
            await interrupted.ExecuteNonQueryAsync(token);
            receivedAt = interruptedAt.AddTicks(1);
        }
        var workflowEventId = Guid.NewGuid().ToString("N");
        var metadata = JsonSerializer.Serialize(new
        {
            productionRunProgramId = target.ProgramId,
            suppliedRunIdentity = observation.ProductionRunIdentity,
            suppliedProgramIdentity = observation.ProgramIdentity,
            macroVersion = observation.MacroVersion,
            rawLine = observation.RawLine
        });
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO production_run_workflow_events(
                    id,production_run_id,machine_id,event_type,source,source_event_id,
                    source_sequence,server_received_at,nc_release_id,metadata_json)
                VALUES($id,$runId,$machineId,$eventType,$source,$sourceEventId,
                       $sequence,$receivedAt,$releaseId,$metadata);
                """;
            insert.Parameters.AddWithValue("$id", workflowEventId);
            insert.Parameters.AddWithValue("$runId", target.RunId);
            insert.Parameters.AddWithValue("$machineId", observation.MachineId);
            insert.Parameters.AddWithValue("$eventType", observation.EventType);
            insert.Parameters.AddWithValue("$source", source);
            insert.Parameters.AddWithValue("$sourceEventId", observation.SourceEventId);
            insert.Parameters.AddWithValue("$sequence", observation.Sequence);
            insert.Parameters.AddWithValue("$receivedAt", Format(receivedAt));
            insert.Parameters.AddWithValue("$releaseId", Db(target.NcReleaseId));
            insert.Parameters.AddWithValue("$metadata", metadata);
            await insert.ExecuteNonQueryAsync(token);
        }
        if (previousSourceSequence.HasValue
            && observation.Sequence != previousSourceSequence.Value + 1)
        {
            var anomalyType = observation.Sequence <= previousSourceSequence.Value
                ? "EVENT_SEQUENCE_OUT_OF_ORDER"
                : "EVENT_SEQUENCE_GAP";
            await using var anomaly = connection.CreateCommand();
            anomaly.Transaction = transaction;
            anomaly.CommandText = """
                INSERT INTO production_run_workflow_anomalies(
                    id,production_run_id,machine_id,source,source_event_id,
                    anomaly_type,previous_sequence,expected_sequence,received_sequence,
                    workflow_event_id,detected_at,details_json)
                VALUES($id,$runId,$machineId,$source,$sourceEventId,$type,$previous,
                       $expected,$received,$workflowEventId,$at,$details);
                """;
            anomaly.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            anomaly.Parameters.AddWithValue("$runId", target.RunId);
            anomaly.Parameters.AddWithValue("$machineId", observation.MachineId);
            anomaly.Parameters.AddWithValue("$source", source);
            anomaly.Parameters.AddWithValue("$sourceEventId", observation.SourceEventId);
            anomaly.Parameters.AddWithValue("$type", anomalyType);
            anomaly.Parameters.AddWithValue("$previous", previousSourceSequence.Value);
            anomaly.Parameters.AddWithValue("$expected", previousSourceSequence.Value + 1);
            anomaly.Parameters.AddWithValue("$received", observation.Sequence);
            anomaly.Parameters.AddWithValue("$workflowEventId", workflowEventId);
            anomaly.Parameters.AddWithValue("$at", Format(receivedAt));
            anomaly.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
            {
                previousSequence = previousSourceSequence.Value,
                expectedSequence = previousSourceSequence.Value + 1,
                receivedSequence = observation.Sequence
            }));
            await anomaly.ExecuteNonQueryAsync(token);
        }

        if (unmatchedEnd)
        {
            var anomalyType = latest?.EventType == "CYCLE_START"
                ? "CYCLE_END_SEQUENCE_MISMATCH"
                : "CYCLE_END_WITHOUT_START";
            await InsertCycleAnomalyAsync(connection, transaction, target, observation,
                source, workflowEventId, receivedAt, anomalyType, latest, token);
            await transaction.CommitAsync(token);
            return new(true, false, false, "cycle_end_unmatched", target.RunId,
                target.ProgramId);
        }

        var completed = default(int?);
        if (validEnd)
            completed = await RecordResolvedCycleAsync(connection, transaction, target,
                source, observation.SourceEventId, receivedAt, observation.MachineId, token);

        await transaction.CommitAsync(token);
        return new(true, false, completed.HasValue, "accepted", target.RunId,
            target.ProgramId, completed);
    }

    private static async Task<int> RecordResolvedCycleAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        CycleTarget target, string source, string sourceEventId,
        DateTimeOffset observedAt, string machineId, CancellationToken token)
    {
        var result = await SqliteProductionRunCycleAccounting.RecordAsync(
            connection, transaction, new(
                target.RunId, target.ProgramId, source, sourceEventId,
                observedAt, observedAt, "cnc-system", "CNC_OBSERVATION", machineId), token);
        return result.CompletedCycleCount;
    }

    private static async Task InsertCycleAnomalyAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        CycleTarget target,
        CncCycleObservation observation,
        string source,
        string workflowEventId,
        DateTimeOffset detectedAt,
        string anomalyType,
        LatestCycleWorkflow? latest,
        CancellationToken token)
    {
        await using var anomaly = connection.CreateCommand();
        anomaly.Transaction = transaction;
        anomaly.CommandText = """
            INSERT INTO production_run_workflow_anomalies(
                id,production_run_id,machine_id,source,source_event_id,
                anomaly_type,previous_sequence,expected_sequence,received_sequence,
                workflow_event_id,detected_at,details_json)
            VALUES($id,$runId,$machineId,$source,$sourceEventId,$type,$previous,
                   $expected,$received,$workflowEventId,$at,$details);
            """;
        anomaly.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        anomaly.Parameters.AddWithValue("$runId", target.RunId);
        anomaly.Parameters.AddWithValue("$machineId", observation.MachineId);
        anomaly.Parameters.AddWithValue("$source", source);
        anomaly.Parameters.AddWithValue("$sourceEventId", observation.SourceEventId);
        anomaly.Parameters.AddWithValue("$type", anomalyType);
        anomaly.Parameters.AddWithValue("$previous", Db(latest?.Sequence));
        anomaly.Parameters.AddWithValue("$expected",
            Db(latest?.Sequence is long sequence ? sequence + 1 : null));
        anomaly.Parameters.AddWithValue("$received", observation.Sequence);
        anomaly.Parameters.AddWithValue("$workflowEventId", workflowEventId);
        anomaly.Parameters.AddWithValue("$at", Format(detectedAt));
        anomaly.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new
        {
            latestWorkflowEventId = latest?.EventId,
            latestEventType = latest?.EventType,
            latestSourceEventId = latest?.SourceEventId,
            latestProgramId = latest?.ProgramId,
            resolvedProgramId = target.ProgramId,
            receivedSequence = observation.Sequence
        }));
        await anomaly.ExecuteNonQueryAsync(token);
    }

    private static object Db(object? value) => value ?? DBNull.Value;
    private static string Format(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);

    private sealed record CycleTarget(string RunId, string ProgramId, string? NcReleaseId);
    private sealed record LatestCycleWorkflow(
        string EventId, string EventType, string Source, string? SourceEventId,
        long? Sequence, DateTimeOffset ReceivedAt, string? ProgramId);
}
