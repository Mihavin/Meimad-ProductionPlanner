using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.ProductionRuns;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteProductionRunWorkflowEventRepository(SqliteDatabase database)
    : IProductionRunWorkflowEventRepository
{
    public async Task<ProductionRunWorkflowAppendResult> AppendAsync(
        AppendProductionRunWorkflowEvent command, DateTimeOffset serverReceivedAt,
        CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var source = command.Source.Trim().ToUpperInvariant();
        var duplicate = await ReadBySourceAsync(connection, transaction,
            source, command.SourceEventId, token);
        if (duplicate is not null)
        {
            await transaction.CommitAsync(token);
            return new(duplicate, true, []);
        }

        await using (var target = connection.CreateCommand())
        {
            target.Transaction = transaction;
            target.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM production_runs run
                    JOIN machine_assignments assignment ON assignment.production_run_id = run.id
                    WHERE run.id = $runId AND assignment.machine_id = $machineId);
                """;
            target.Parameters.AddWithValue("$runId", command.ProductionRunId.Trim());
            target.Parameters.AddWithValue("$machineId", command.MachineId.Trim());
            if (Convert.ToInt32(await target.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 1)
                throw new ProductionRunWorkflowTargetException(
                    "The Production Run is not assigned to the supplied Machine.");
        }

        long? previousSequence = null;
        if (command.SourceSequence.HasValue)
        {
            await using var previous = connection.CreateCommand();
            previous.Transaction = transaction;
            previous.CommandText = """
                SELECT MAX(source_sequence)
                FROM production_run_workflow_events
                WHERE machine_id = $machineId AND source = $source
                  AND source_sequence IS NOT NULL;
                """;
            previous.Parameters.AddWithValue("$machineId", command.MachineId.Trim());
            previous.Parameters.AddWithValue("$source", source);
            var scalar = await previous.ExecuteScalarAsync(token);
            previousSequence = scalar is null or DBNull ? null : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }

        var value = new ProductionRunWorkflowEvent(
            Guid.NewGuid().ToString("N"), command.ProductionRunId.Trim(), command.MachineId.Trim(),
            command.EventType, source, command.SourceEventId.Trim(),
            command.SourceSequence, serverReceivedAt.ToUniversalTime(),
            command.MachineTimestamp?.ToUniversalTime(), Optional(command.NcReleaseId),
            Optional(command.OffsetLoaderReleaseId), Optional(command.TabletDeviceId),
            Optional(command.UserId), command.MetadataJson);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO production_run_workflow_events (
                    id, production_run_id, machine_id, event_type, source,
                    source_event_id, source_sequence, server_received_at,
                    machine_timestamp, nc_release_id, offset_loader_release_id,
                    tablet_device_id, user_id, metadata_json)
                VALUES ($id, $runId, $machineId, $eventType, $source,
                    $sourceEventId, $sourceSequence, $receivedAt, $machineAt,
                    $ncReleaseId, $offsetReleaseId, $tabletDeviceId, $userId, $metadata);
                """;
            insert.Parameters.AddWithValue("$id", value.EventId);
            insert.Parameters.AddWithValue("$runId", value.ProductionRunId);
            insert.Parameters.AddWithValue("$machineId", value.MachineId);
            insert.Parameters.AddWithValue("$eventType", value.EventType);
            insert.Parameters.AddWithValue("$source", value.Source);
            insert.Parameters.AddWithValue("$sourceEventId", value.SourceEventId);
            insert.Parameters.AddWithValue("$sourceSequence", Db(value.SourceSequence));
            insert.Parameters.AddWithValue("$receivedAt", Format(value.ServerReceivedAt));
            insert.Parameters.AddWithValue("$machineAt", Db(value.MachineTimestamp is null ? null : Format(value.MachineTimestamp.Value)));
            insert.Parameters.AddWithValue("$ncReleaseId", Db(value.NcReleaseId));
            insert.Parameters.AddWithValue("$offsetReleaseId", Db(value.OffsetLoaderReleaseId));
            insert.Parameters.AddWithValue("$tabletDeviceId", Db(value.TabletDeviceId));
            insert.Parameters.AddWithValue("$userId", Db(value.UserId));
            insert.Parameters.AddWithValue("$metadata", value.MetadataJson);
            await insert.ExecuteNonQueryAsync(token);
        }
        if (value.EventType == "OFFSET_LOADER_COMPLETED")
            await ClosePriorProductionSessionAsync(
                connection, transaction, value, token);
        var anomalies = new List<ProductionRunWorkflowAnomaly>();
        if (previousSequence.HasValue && command.SourceSequence.HasValue
            && command.SourceSequence.Value != previousSequence.Value + 1)
        {
            var anomalyType = command.SourceSequence.Value <= previousSequence.Value
                ? "EVENT_SEQUENCE_OUT_OF_ORDER" : "EVENT_SEQUENCE_GAP";
            var anomaly = new ProductionRunWorkflowAnomaly(
                Guid.NewGuid().ToString("N"), value.ProductionRunId, value.MachineId,
                value.Source, value.SourceEventId, anomalyType, previousSequence.Value,
                previousSequence.Value + 1, command.SourceSequence.Value, value.EventId,
                value.ServerReceivedAt,
                $"{{\"previousSequence\":{previousSequence.Value},\"expectedSequence\":{previousSequence.Value + 1},\"receivedSequence\":{command.SourceSequence.Value}}}");
            await InsertAnomalyAsync(connection, transaction, anomaly, token);
            anomalies.Add(anomaly);
        }
        if (command.VerificationSession is not null)
            await StartVerificationSessionAsync(connection, transaction, value,
                command.VerificationSession, token);
        if (command.VerificationActivation is not null)
            await ActivateVerificationSessionAsync(connection, transaction, value,
                command.VerificationActivation, token);
        if (command.VerificationResolution is not null)
            await ResolveVerificationSessionAsync(connection, transaction, value,
                command.VerificationResolution, token);
        await transaction.CommitAsync(token);
        return new(value, false, anomalies);
    }

    private static async Task ClosePriorProductionSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionRunWorkflowEvent triggeringEvent,
        CancellationToken cancellationToken)
    {
        string? priorRunId = null;
        await using (var candidate = connection.CreateCommand())
        {
            candidate.Transaction = transaction;
            candidate.CommandText = """
                SELECT event.production_run_id
                FROM production_run_workflow_events event
                WHERE event.machine_id=$machineId
                  AND event.production_run_id<>$triggeringRunId
                  AND event.event_type='CYCLE_START'
                  AND NOT EXISTS(
                      SELECT 1 FROM production_run_session_closures closure
                      WHERE closure.production_run_id=event.production_run_id)
                  AND NOT EXISTS(
                      SELECT 1 FROM production_run_workflow_events closed
                      WHERE closed.production_run_id=event.production_run_id
                        AND closed.event_type='PRODUCTION_SESSION_CLOSED')
                ORDER BY event.server_received_at DESC,event.id DESC
                LIMIT 1;
                """;
            candidate.Parameters.AddWithValue("$machineId", triggeringEvent.MachineId);
            candidate.Parameters.AddWithValue("$triggeringRunId", triggeringEvent.ProductionRunId);
            priorRunId = await candidate.ExecuteScalarAsync(cancellationToken) as string;
        }
        if (priorRunId is null) return;

        var rows = new List<SessionCycleTiming>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT event.id,event.event_type,event.source,event.source_event_id,
                       event.source_sequence,event.server_received_at,event.machine_timestamp,
                       json_extract(event.metadata_json,'$.productionRunProgramId'),
                       EXISTS(
                           SELECT 1 FROM production_run_cycle_events cycle
                           WHERE cycle.source=event.source
                             AND cycle.source_event_id=event.source_event_id)
                FROM production_run_workflow_events event
                WHERE event.production_run_id=$runId
                  AND event.machine_id=$machineId
                  AND event.event_type IN('CYCLE_START','CYCLE_END')
                ORDER BY event.server_received_at,event.id;
                """;
            query.Parameters.AddWithValue("$runId", priorRunId);
            query.Parameters.AddWithValue("$machineId", triggeringEvent.MachineId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(new(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    Parse(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : Parse(reader.GetString(6)),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt32(8) == 1));
        }
        var last = rows.LastOrDefault(row =>
            row.EventType == "CYCLE_START" || row.IsCompletedEnd);
        if (last is null) return;

        DateTimeOffset? observedEndAt = null;
        DateTimeOffset? effectiveEndAt = null;
        var endTimeInferred = false;
        object inferenceBasis;
        if (last.IsCompletedEnd)
        {
            observedEndAt = last.MachineTimestamp ?? last.ServerReceivedAt;
            effectiveEndAt = observedEndAt;
            inferenceBasis = new
            {
                kind = "OBSERVED_CYCLE_END",
                workflowEventId = last.EventId,
                clock = last.MachineTimestamp.HasValue ? "MACHINE" : "SERVER_RECEIPT"
            };
        }
        else
        {
            var minimum = MinimumValidatedCycleDuration(rows);
            if (minimum.HasValue)
            {
                effectiveEndAt = (last.MachineTimestamp ?? last.ServerReceivedAt)
                    .Add(minimum.Value);
                endTimeInferred = true;
            }
            inferenceBasis = new
            {
                kind = minimum.HasValue
                    ? "LAST_START_PLUS_MINIMUM_VALIDATED_CYCLE"
                    : "UNAVAILABLE_NO_VALIDATED_CYCLE_DURATION",
                lastStartWorkflowEventId = last.EventId,
                minimumValidatedCycleSeconds = minimum?.TotalSeconds,
                startClock = last.MachineTimestamp.HasValue ? "MACHINE" : "SERVER_RECEIPT"
            };
        }

        var closureEventId = Guid.NewGuid().ToString("N");
        var metadata = JsonSerializer.Serialize(new
        {
            triggeringProductionRunId = triggeringEvent.ProductionRunId,
            triggeringWorkflowEventId = triggeringEvent.EventId,
            observedEndAt,
            effectiveEndAt,
            endTimeInferred,
            inferenceBasis
        });
        await using (var workflow = connection.CreateCommand())
        {
            workflow.Transaction = transaction;
            workflow.CommandText = """
                INSERT INTO production_run_workflow_events(
                    id,production_run_id,machine_id,event_type,source,source_event_id,
                    server_received_at,metadata_json)
                VALUES($id,$runId,$machineId,'PRODUCTION_SESSION_CLOSED',
                       'SERVER_SESSION',$sourceEventId,$closedAt,$metadata);
                """;
            workflow.Parameters.AddWithValue("$id", closureEventId);
            workflow.Parameters.AddWithValue("$runId", priorRunId);
            workflow.Parameters.AddWithValue("$machineId", triggeringEvent.MachineId);
            workflow.Parameters.AddWithValue("$sourceEventId", $"NEXT_SETUP:{triggeringEvent.EventId}");
            workflow.Parameters.AddWithValue("$closedAt", Format(triggeringEvent.ServerReceivedAt));
            workflow.Parameters.AddWithValue("$metadata", metadata);
            await workflow.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var closure = connection.CreateCommand())
        {
            closure.Transaction = transaction;
            closure.CommandText = """
                INSERT INTO production_run_session_closures(
                    id,production_run_id,machine_id,triggering_production_run_id,
                    triggering_workflow_event_id,closure_workflow_event_id,
                    observed_end_at,effective_end_at,end_time_inferred,
                    inference_basis_json,closed_at)
                VALUES($id,$runId,$machineId,$triggeringRunId,$triggeringEventId,
                       $closureEventId,$observedEndAt,$effectiveEndAt,$inferred,$basis,$closedAt);
                """;
            closure.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            closure.Parameters.AddWithValue("$runId", priorRunId);
            closure.Parameters.AddWithValue("$machineId", triggeringEvent.MachineId);
            closure.Parameters.AddWithValue("$triggeringRunId", triggeringEvent.ProductionRunId);
            closure.Parameters.AddWithValue("$triggeringEventId", triggeringEvent.EventId);
            closure.Parameters.AddWithValue("$closureEventId", closureEventId);
            closure.Parameters.AddWithValue("$observedEndAt",
                Db(observedEndAt.HasValue ? Format(observedEndAt.Value) : null));
            closure.Parameters.AddWithValue("$effectiveEndAt",
                Db(effectiveEndAt.HasValue ? Format(effectiveEndAt.Value) : null));
            closure.Parameters.AddWithValue("$inferred", endTimeInferred ? 1 : 0);
            closure.Parameters.AddWithValue("$basis", JsonSerializer.Serialize(inferenceBasis));
            closure.Parameters.AddWithValue("$closedAt", Format(triggeringEvent.ServerReceivedAt));
            await closure.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static TimeSpan? MinimumValidatedCycleDuration(
        IReadOnlyList<SessionCycleTiming> rows)
    {
        var starts = rows
            .Where(row => row.EventType == "CYCLE_START" && row.Sequence.HasValue)
            .GroupBy(row => (row.Source, row.Sequence!.Value, row.ProgramId))
            .ToDictionary(
                group => group.Key,
                group => group.Last());
        TimeSpan? minimum = null;
        foreach (var end in rows.Where(row => row.IsCompletedEnd && row.Sequence.HasValue))
        {
            if (!starts.TryGetValue(
                    (end.Source, end.Sequence!.Value - 1, end.ProgramId), out var start))
                continue;
            var duration = end.MachineTimestamp.HasValue && start.MachineTimestamp.HasValue
                ? end.MachineTimestamp.Value - start.MachineTimestamp.Value
                : end.ServerReceivedAt - start.ServerReceivedAt;
            if (duration <= TimeSpan.Zero) continue;
            if (!minimum.HasValue || duration < minimum.Value) minimum = duration;
        }
        return minimum;
    }

    private static async Task StartVerificationSessionAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        ProductionRunWorkflowEvent workflowEvent, SetupVerificationSessionSeed seed,
        CancellationToken token)
    {
        await using (var supersede = connection.CreateCommand())
        {
            supersede.Transaction = transaction;
            supersede.CommandText = """
                UPDATE cnc_setup_verification_sessions
                SET state='SUPERSEDED', resolved_at=$resolvedAt,resolution_workflow_event_id=NULL
                WHERE machine_id=$machineId AND state IN ('ARMED','PENDING','SUCCEEDED');
                """;
            supersede.Parameters.AddWithValue("$resolvedAt", Format(workflowEvent.ServerReceivedAt));
            supersede.Parameters.AddWithValue("$machineId", workflowEvent.MachineId);
            await supersede.ExecuteNonQueryAsync(token);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO cnc_setup_verification_sessions (
                id,production_run_id,machine_id,nc_release_id,offset_loader_release_id,
                nonce,macro_version,response_code_digits,state,created_at,
                pending_started_at,expires_at,resolved_at,source_workflow_event_id,
                pending_workflow_event_id,resolution_workflow_event_id)
            SELECT $id,$runId,$machineId,$ncReleaseId,$offsetReleaseId,
                   $nonce,$macroVersion,$digits,'ARMED',$createdAt,
                   NULL,NULL,NULL,$workflowEventId,NULL,NULL
            FROM production_run_current_offset_loaders current
            JOIN offset_loader_releases release ON release.id=current.offset_loader_release_id
            JOIN cnc_verification_settings settings ON settings.machine_id=release.machine_id
            WHERE current.production_run_id=$runId
              AND current.machine_id=$machineId
              AND current.offset_loader_release_id=$offsetReleaseId
              AND release.nc_release_id=$ncReleaseId
              AND settings.enabled=1
              AND settings.expected_macro_version=$macroVersion
              AND settings.response_code_digits=$digits
              AND settings.verification_timeout_seconds=$timeout;
            """;
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        insert.Parameters.AddWithValue("$runId", workflowEvent.ProductionRunId);
        insert.Parameters.AddWithValue("$machineId", workflowEvent.MachineId);
        insert.Parameters.AddWithValue("$ncReleaseId", workflowEvent.NcReleaseId!);
        insert.Parameters.AddWithValue("$offsetReleaseId", workflowEvent.OffsetLoaderReleaseId!);
        insert.Parameters.AddWithValue("$nonce", seed.Nonce);
        insert.Parameters.AddWithValue("$macroVersion", seed.MacroVersion);
        insert.Parameters.AddWithValue("$digits", seed.ResponseCodeDigits);
        insert.Parameters.AddWithValue("$timeout", seed.TimeoutSeconds);
        insert.Parameters.AddWithValue("$createdAt", Format(workflowEvent.ServerReceivedAt));
        insert.Parameters.AddWithValue("$workflowEventId", workflowEvent.EventId);
        if (await insert.ExecuteNonQueryAsync(token) != 1)
            throw new ProductionRunWorkflowTargetException(
                "The current Offset Loader and enabled Machine verification settings no longer match the session request.");
    }

    private static async Task ActivateVerificationSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionRunWorkflowEvent workflowEvent,
        SetupVerificationActivationSeed activation,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE cnc_setup_verification_sessions
            SET state='PENDING',pending_started_at=$at,expires_at=$expiresAt,
                pending_workflow_event_id=$eventId
            WHERE id=$sessionId AND machine_id=$machineId AND production_run_id=$runId
              AND state='ARMED';
            """;
        command.Parameters.AddWithValue("$at", Format(workflowEvent.ServerReceivedAt));
        command.Parameters.AddWithValue("$expiresAt", Format(
            workflowEvent.ServerReceivedAt.AddSeconds(activation.TimeoutSeconds)));
        command.Parameters.AddWithValue("$eventId", workflowEvent.EventId);
        command.Parameters.AddWithValue("$sessionId", activation.SessionId);
        command.Parameters.AddWithValue("$machineId", workflowEvent.MachineId);
        command.Parameters.AddWithValue("$runId", workflowEvent.ProductionRunId);
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new ProductionRunWorkflowTargetException(
                "The setup-verification session is no longer armed for this NC start.");
    }

    private static async Task ResolveVerificationSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionRunWorkflowEvent workflowEvent,
        SetupVerificationResolutionSeed resolution,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = resolution.Succeeded ? """
            UPDATE cnc_setup_verification_sessions
            SET state=$state,resolved_at=$at,resolution_workflow_event_id=$eventId
            WHERE id=$sessionId AND machine_id=$machineId AND production_run_id=$runId
              AND state='PENDING' AND expires_at>$at;
            """ : """
            UPDATE cnc_setup_verification_sessions
            SET state=$state,resolved_at=$at,resolution_workflow_event_id=$eventId
            WHERE id=$sessionId AND machine_id=$machineId AND production_run_id=$runId
              AND state IN ('PENDING','EXPIRED');
            """;
        command.Parameters.AddWithValue("$state", resolution.Succeeded ? "SUCCEEDED" : "FAILED");
        command.Parameters.AddWithValue("$at", Format(workflowEvent.ServerReceivedAt));
        command.Parameters.AddWithValue("$eventId", workflowEvent.EventId);
        command.Parameters.AddWithValue("$sessionId", resolution.SessionId);
        command.Parameters.AddWithValue("$machineId", workflowEvent.MachineId);
        command.Parameters.AddWithValue("$runId", workflowEvent.ProductionRunId);
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new ProductionRunWorkflowTargetException(
                "The setup-verification session is no longer pending and current.");
    }

    private static async Task InsertAnomalyAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        ProductionRunWorkflowAnomaly value, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO production_run_workflow_anomalies (
                id, production_run_id, machine_id, source, source_event_id,
                anomaly_type, previous_sequence, expected_sequence,
                received_sequence, workflow_event_id, detected_at, details_json)
            VALUES ($id,$runId,$machineId,$source,$sourceEventId,$type,$previous,
                    $expected,$received,$workflowEventId,$detectedAt,$details);
            """;
        command.Parameters.AddWithValue("$id", value.AnomalyId);
        command.Parameters.AddWithValue("$runId", value.ProductionRunId);
        command.Parameters.AddWithValue("$machineId", value.MachineId);
        command.Parameters.AddWithValue("$source", value.Source);
        command.Parameters.AddWithValue("$sourceEventId", value.SourceEventId);
        command.Parameters.AddWithValue("$type", value.AnomalyType);
        command.Parameters.AddWithValue("$previous", value.PreviousSequence);
        command.Parameters.AddWithValue("$expected", value.ExpectedSequence);
        command.Parameters.AddWithValue("$received", value.ReceivedSequence);
        command.Parameters.AddWithValue("$workflowEventId", value.WorkflowEventId);
        command.Parameters.AddWithValue("$detectedAt", Format(value.DetectedAt));
        command.Parameters.AddWithValue("$details", value.DetailsJson);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<ProductionRunWorkflowEvent?> ReadBySourceAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        string source, string sourceEventId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, production_run_id, machine_id, event_type, source,
                   source_event_id, source_sequence, server_received_at,
                   machine_timestamp, nc_release_id, offset_loader_release_id,
                   tablet_device_id, user_id, metadata_json
            FROM production_run_workflow_events
            WHERE source = $source AND source_event_id = $sourceEventId;
            """;
        command.Parameters.AddWithValue("$source", source.Trim());
        command.Parameters.AddWithValue("$sourceEventId", sourceEventId.Trim());
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6), Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : Parse(reader.GetString(8)), Optional(reader, 9),
            Optional(reader, 10), Optional(reader, 11), Optional(reader, 12), reader.GetString(13));
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Optional(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static object Db(object? value) => value ?? DBNull.Value;
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private sealed record SessionCycleTiming(
        string EventId,
        string EventType,
        string Source,
        string? SourceEventId,
        long? Sequence,
        DateTimeOffset ServerReceivedAt,
        DateTimeOffset? MachineTimestamp,
        string? ProgramId,
        bool IsCompletedEnd);
}
