using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// A newer G-code release reaches running work when a planner refreshes the Work Order from its
/// Case (owner decisions 2026-09-29). A started operation of the Work Order whose production release
/// has a newer local version for the same Postprocessor goes back to setup like a new one:
/// <list type="bullet">
/// <item>its Production Run gets <c>SETUP_RESTARTED</c>, so the tablet shows "ready for setup" and
/// cycles count again only after setup verification and first-part QC;</item>
/// <item>the current Offset Loader is revoked and its verification superseded, so the Machine's
/// loaded program is refused at its next verified start;</item>
/// <item>the current Production Package is retired, and the operation chooses its release again
/// until a new package pins the new one;</item>
/// <item>the parts already made stay counted.</item>
/// </list>
/// A new process revision is reported but not switched: a started Production Run's process
/// revision is part of its immutable structure (AGENTS.md rule 27).
/// </summary>
internal static class SqliteSetupRestart
{
    /// <summary>A started operation whose production G-code has a newer release.</summary>
    internal sealed record Candidate(
        string BatchOperationId,
        int OperationNumber,
        string ProductionRunId,
        string MachineId,
        string MachineName,
        string ProductionGCodeReleaseId,
        string ProductionRelease,
        string? NewerRelease,
        bool NewProcessRevision);

    internal sealed record Outcome(IReadOnlyList<Candidate> Restarted, IReadOnlyList<Candidate> ProcessRevisionNotSwitched);

    /// <summary>The started operations of a Work Order that a refresh would send back to setup, or report.</summary>
    internal static async Task<IReadOnlyList<Candidate>> FindAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string batchId, CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT operation.id, operation.operation_number, run.id, machine.id, machine.name,
                   pinned.id, postprocessor.name, pinned.post_specific_revision,
                   (SELECT MAX(newer.post_specific_revision) FROM gcode_releases newer
                    WHERE newer.process_revision_id = pinned.process_revision_id
                      AND newer.postprocessor_id = pinned.postprocessor_id),
                   CASE WHEN active.id IS NOT NULL AND active.id <> pinned.process_revision_id THEN 1 ELSE 0 END
            FROM batch_operations operation
            JOIN production_runs run
              ON run.legacy_batch_operation_id = operation.id AND run.status IN ('IN_PROGRESS', 'SUSPENDED')
            JOIN machine_assignments assignment
              ON assignment.batch_operation_id = operation.id AND assignment.released_at IS NULL
            JOIN machines machine ON machine.id = assignment.machine_id
            JOIN gcode_releases pinned ON pinned.id = operation.production_gcode_release_id
            JOIN postprocessors postprocessor ON postprocessor.id = pinned.postprocessor_id
            LEFT JOIN process_revisions active
              ON active.case_operation_id = operation.source_case_operation_id AND active.is_active = 1
            WHERE operation.production_batch_id = $batchId
              AND operation.status IN ('in_progress', 'suspended')
              AND NOT EXISTS (
                  SELECT 1 FROM batch_operation_setup_restarts restart
                  WHERE restart.batch_operation_id = operation.id AND restart.resolved_at IS NULL)
              AND ((active.id IS NOT NULL AND active.id <> pinned.process_revision_id)
                   OR EXISTS (
                       SELECT 1 FROM gcode_releases newer
                       WHERE newer.process_revision_id = pinned.process_revision_id
                         AND newer.postprocessor_id = pinned.postprocessor_id
                         AND newer.post_specific_revision > pinned.post_specific_revision))
            ORDER BY operation.route_position, operation.operation_number, operation.id;
            """;
        command.Parameters.AddWithValue("$batchId", batchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var postprocessor = reader.GetString(6);
            var revision = reader.GetInt32(7);
            var newest = reader.GetInt32(8);
            candidates.Add(new Candidate(
                reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5),
                $"{postprocessor} r{revision.ToString(CultureInfo.InvariantCulture)}",
                newest > revision ? $"{postprocessor} r{newest.ToString(CultureInfo.InvariantCulture)}" : null,
                reader.GetInt64(9) == 1));
        }
        return candidates;
    }

    /// <summary>Sends the Work Order's operations with a newer local G-code version back to setup.</summary>
    internal static async Task<Outcome> ApplyAsync(
        SqliteConnection connection, SqliteTransaction transaction, string batchId, string actor,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var candidates = await FindAsync(connection, transaction, batchId, cancellationToken);
        var restarted = new List<Candidate>();
        foreach (var candidate in candidates.Where(candidate => !candidate.NewProcessRevision))
        {
            await RestartAsync(connection, transaction, batchId, candidate, actor, now, cancellationToken);
            restarted.Add(candidate);
        }
        return new Outcome(restarted, candidates.Where(candidate => candidate.NewProcessRevision).ToArray());
    }

    private static async Task RestartAsync(
        SqliteConnection connection, SqliteTransaction transaction, string batchId, Candidate candidate,
        string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The event must be the run's latest, so it follows its last event by at least one tick.
        DateTimeOffset receivedAt = now.ToUniversalTime();
        await using (var latest = connection.CreateCommand())
        {
            latest.Transaction = transaction;
            latest.CommandText = "SELECT MAX(server_received_at) FROM production_run_workflow_events WHERE production_run_id = $runId;";
            latest.Parameters.AddWithValue("$runId", candidate.ProductionRunId);
            if (await latest.ExecuteScalarAsync(cancellationToken) is string text)
            {
                var last = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
                if (receivedAt <= last) receivedAt = last.AddTicks(1);
            }
        }

        string? packageId;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT production_package_id FROM production_package_current WHERE batch_operation_id = $operationId;";
            current.Parameters.AddWithValue("$operationId", candidate.BatchOperationId);
            packageId = await current.ExecuteScalarAsync(cancellationToken) as string;
        }

        var restartId = Guid.NewGuid().ToString("N");
        var eventId = Guid.NewGuid().ToString("N");
        var at = Format(now);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO production_run_workflow_events (
                    id, production_run_id, machine_id, event_type, source, source_event_id,
                    server_received_at, nc_release_id, user_id, metadata_json)
                VALUES ($eventId, $runId, $machineId, 'SETUP_RESTARTED', 'PLANNER', $sourceEventId,
                        $receivedAt, $releaseId, $actor, $metadata);

                -- The loaded program is refused at its next verified start.
                DELETE FROM production_run_current_offset_loaders WHERE production_run_id = $runId;
                UPDATE cnc_setup_verification_sessions
                SET state = 'SUPERSEDED', resolved_at = $at, resolution_workflow_event_id = NULL
                WHERE production_run_id = $runId AND state IN ('ARMED', 'PENDING', 'SUCCEEDED');

                -- The package with the old program is retired; a new one is built for the new release.
                INSERT OR IGNORE INTO production_package_invalidations (
                    id, production_package_id, replacement_package_id, reason, invalidated_at)
                SELECT $invalidationId, $packageId, NULL, 'NC_RELEASE_REPLACED', $at WHERE $packageId IS NOT NULL;
                DELETE FROM production_package_current WHERE batch_operation_id = $operationId;

                -- The release is chosen again: the newest compatible one, or the planner's selection.
                UPDATE machine_assignments
                SET selected_gcode_release_id = NULL, version = version + 1, updated_at = $at
                WHERE batch_operation_id = $operationId AND released_at IS NULL
                  AND selected_gcode_release_id IS NOT NULL;

                INSERT INTO batch_operation_setup_restarts (
                    id, batch_operation_id, production_run_id, machine_id, replaced_gcode_release_id,
                    workflow_event_id, invalidated_package_id, requested_at, requested_by)
                VALUES ($restartId, $operationId, $runId, $machineId, $releaseId,
                        $eventId, $packageId, $at, $actor);

                UPDATE batch_operations SET version = version + 1, updated_at = $at WHERE id = $operationId;
                UPDATE production_runs SET version = version + 1, updated_at = $at WHERE id = $runId;
                """;
            command.Parameters.AddWithValue("$eventId", eventId);
            command.Parameters.AddWithValue("$runId", candidate.ProductionRunId);
            command.Parameters.AddWithValue("$machineId", candidate.MachineId);
            command.Parameters.AddWithValue("$sourceEventId", $"SETUP_RESTARTED:{restartId}");
            command.Parameters.AddWithValue("$receivedAt", Format(receivedAt));
            command.Parameters.AddWithValue("$releaseId", candidate.ProductionGCodeReleaseId);
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(new
            {
                reason = "NC_RELEASE_REPLACED",
                productionBatchId = batchId,
                batchOperationId = candidate.BatchOperationId,
                replacedRelease = candidate.ProductionRelease,
                newerRelease = candidate.NewerRelease,
                retiredProductionPackageId = packageId
            }));
            command.Parameters.AddWithValue("$at", at);
            command.Parameters.AddWithValue("$invalidationId", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$packageId", (object?)packageId ?? DBNull.Value);
            command.Parameters.AddWithValue("$operationId", candidate.BatchOperationId);
            command.Parameters.AddWithValue("$restartId", restartId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await SqliteStructuredEventLogRepository.AppendAsync(
            connection,
            transaction,
            new(
                "operation_setup_restarted",
                now,
                actor,
                new Dictionary<string, string>
                {
                    ["productionBatchId"] = batchId,
                    ["batchOperationId"] = candidate.BatchOperationId,
                    ["productionRunId"] = candidate.ProductionRunId,
                    ["machineId"] = candidate.MachineId
                },
                "NC_RELEASE_REPLACED",
                null,
                new { gCodeReleaseId = candidate.ProductionGCodeReleaseId, release = candidate.ProductionRelease, productionPackageId = packageId },
                new { newerRelease = candidate.NewerRelease, workflowEventId = eventId }),
            cancellationToken);
    }

    /// <summary>
    /// A new Production Package for an operation back in setup pins the package's release as the one in
    /// production and ends the restart; the process revision and tool table stay as they were.
    /// </summary>
    internal static async Task PinNewPackageAsync(
        SqliteConnection connection, SqliteTransaction transaction, string batchOperationId,
        string machineAssignmentId, string productionPackageId, string gcodeReleaseId, DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT production_run_id FROM batch_operation_setup_restarts
            WHERE batch_operation_id = $operationId AND resolved_at IS NULL;
            """;
        command.Parameters.AddWithValue("$operationId", batchOperationId);
        if (await command.ExecuteScalarAsync(cancellationToken) is not string runId) return;

        command.CommandText = """
            UPDATE batch_operations
            SET production_gcode_release_id = $releaseId,
                production_gcode_file_hash = (SELECT file_hash FROM gcode_releases WHERE id = $releaseId),
                version = version + 1, updated_at = $at
            WHERE id = $operationId;
            UPDATE production_run_programs
            SET production_gcode_release_id = $releaseId,
                production_gcode_file_hash = (SELECT file_hash FROM gcode_releases WHERE id = $releaseId),
                selected_gcode_release_id = $releaseId,
                version = version + 1, updated_at = $at
            WHERE production_run_id = $runId;
            UPDATE machine_assignments
            SET selected_gcode_release_id = $releaseId, version = version + 1, updated_at = $at
            WHERE id = $assignmentId AND selected_gcode_release_id IS NOT $releaseId;
            UPDATE batch_operation_setup_restarts
            SET resolved_at = $at, resolution = 'NEW_PACKAGE', resolved_package_id = $packageId
            WHERE batch_operation_id = $operationId AND resolved_at IS NULL;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$releaseId", gcodeReleaseId);
        command.Parameters.AddWithValue("$assignmentId", machineAssignmentId);
        command.Parameters.AddWithValue("$packageId", productionPackageId);
        command.Parameters.AddWithValue("$at", Format(createdAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
