using Meimad.Planner.Server.Application.Preparation;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqlitePreparationQueueRepository(SqliteDatabase database)
    : IPreparationQueueRepository
{
    public async Task<IReadOnlyList<PreparationQueueSource>> ReadSourcesAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var metadata = await ReadMetadataAsync(connection, transaction, cancellationToken);
        var result = new List<PreparationQueueSource>(metadata.Count);

        foreach (var row in metadata)
        {
            var context = await SqliteProductionReadinessContextReader.ReadAsync(
                connection, transaction, row.BatchOperationId, cancellationToken);
            if (context is null) continue;
            var hasPackage = await HasCurrentValidPackageAsync(
                connection, transaction, row.BatchOperationId, cancellationToken);
            result.Add(new(
                row.BatchOperationId,
                row.ProductionRunId,
                row.MachineAssignmentId,
                row.MachineId,
                row.MachineNumber,
                row.MachineName,
                row.PartNumber,
                row.PartName,
                row.BatchNumber,
                row.OperationNumber,
                row.OperationName,
                row.LatestWorkflowEventType,
                context,
                hasPackage,
                row.CaseId,
                row.CaseOperationId));
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<IReadOnlyList<MetadataRow>> ReadMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rows = new List<MetadataRow>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT operation.id, assignment.id, machine.id, machine.number, machine.name,
                   cases.part_number, cases.name, batch.batch_number,
                   operation.operation_number, operation.name,
                   run.id,cases.id,operation.source_case_operation_id,
                   (SELECT event.event_type
                    FROM production_run_workflow_events event
                    WHERE event.production_run_id=run.id
                    ORDER BY event.server_received_at DESC,event.id DESC
                    LIMIT 1)
            FROM batch_operations operation
            JOIN production_batches batch ON batch.id=operation.production_batch_id
            JOIN cases ON cases.id=batch.case_id
            JOIN machine_assignments assignment ON assignment.batch_operation_id=operation.id
            JOIN machines machine ON machine.id=assignment.machine_id
            LEFT JOIN production_runs run ON run.id=(
                SELECT program.production_run_id
                FROM production_run_programs program
                JOIN production_run_outputs output
                  ON output.production_run_program_id=program.id
                WHERE output.batch_operation_id=operation.id
                ORDER BY program.sequence_position,program.id
                LIMIT 1)
            WHERE operation.status <> 'completed';
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.GetInt32(8),
                reader.GetString(9), Nullable(reader, 10), Nullable(reader, 13),
                reader.GetString(11), reader.GetString(12)));
        }
        return rows;
    }

    private static string? Nullable(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static async Task<bool> HasCurrentValidPackageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM production_package_current current
                JOIN production_packages package ON package.id=current.production_package_id
                JOIN machine_assignments assignment
                  ON assignment.id=package.machine_assignment_id
                 AND assignment.batch_operation_id=package.batch_operation_id
                 AND assignment.machine_id=package.machine_id
                JOIN batch_operations operation ON operation.id=package.batch_operation_id
                JOIN process_revisions process
                  ON process.case_operation_id=operation.source_case_operation_id
                 AND process.is_active=1
                 AND process.tool_table_release_id=package.tool_table_release_id
                LEFT JOIN cnc_verification_settings settings ON settings.machine_id=package.machine_id
                WHERE current.batch_operation_id=$operationId
                  AND ((package.execution_mode='MANUAL' AND package.gcode_release_id IS NULL)
                       OR (package.execution_mode='CNC_GCODE'
                           AND package.gcode_release_id=COALESCE(
                               assignment.selected_gcode_release_id,
                               (SELECT release.id FROM gcode_releases release
                                JOIN machine_supported_postprocessors supported
                                  ON supported.machine_id=package.machine_id
                                 AND supported.postprocessor_id=release.postprocessor_id
                                WHERE release.process_revision_id=process.id
                                  AND release.post_specific_revision=(
                                      SELECT MAX(latest.post_specific_revision)
                                      FROM gcode_releases latest
                                      WHERE latest.process_revision_id=release.process_revision_id
                                        AND latest.postprocessor_id=release.postprocessor_id)
                                ORDER BY release.id LIMIT 1))))
                  AND ((package.verification_enabled=0 AND COALESCE(settings.enabled,0)=0)
                       OR (package.verification_enabled=1 AND settings.enabled=1
                           AND settings.version=package.verification_configuration_version
                           AND settings.expected_macro_version=package.verification_macro_version)));
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private sealed record MetadataRow(
        string BatchOperationId,
        string MachineAssignmentId,
        string MachineId,
        string MachineNumber,
        string MachineName,
        string PartNumber,
        string PartName,
        string BatchNumber,
        int OperationNumber,
        string OperationName,
        string? ProductionRunId,
        string? LatestWorkflowEventType,
        string CaseId,
        string CaseOperationId);
}
