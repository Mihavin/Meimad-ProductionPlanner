using System.Globalization;
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
        await transaction.CommitAsync(token);
        return new(value, false, anomalies);
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
                SET state='SUPERSEDED', resolved_at=$resolvedAt
                WHERE machine_id=$machineId AND state IN ('PENDING','SUCCEEDED');
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
                nonce,macro_version,response_code_digits,state,created_at,expires_at,
                resolved_at,source_workflow_event_id,resolution_workflow_event_id)
            SELECT $id,$runId,$machineId,$ncReleaseId,$offsetReleaseId,
                   $nonce,$macroVersion,$digits,'PENDING',$createdAt,$expiresAt,
                   NULL,$workflowEventId,NULL
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
        insert.Parameters.AddWithValue("$expiresAt", Format(workflowEvent.ServerReceivedAt.AddSeconds(seed.TimeoutSeconds)));
        insert.Parameters.AddWithValue("$workflowEventId", workflowEvent.EventId);
        if (await insert.ExecuteNonQueryAsync(token) != 1)
            throw new ProductionRunWorkflowTargetException(
                "The current Offset Loader and enabled Machine verification settings no longer match the session request.");
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
}
