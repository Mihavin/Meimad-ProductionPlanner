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
        var contexts = (await SqliteProductionPackageContext.ListAsync(connection,transaction,null,cancellationToken))
            .ToDictionary(value=>value.ProductionRunOutputId,StringComparer.Ordinal);

        foreach (var row in metadata)
        {
            if (!contexts.TryGetValue(row.OutputId,out var exact)) continue;
            var context=await SqliteProductionReadinessContextReader.ReadForPackageAsync(connection,transaction,exact,cancellationToken);
            exact=exact with { ProcessRevisionId=context.ActiveProcessRevisionId };
            var hasPackage=await SqliteProductionPackageRepository.ReadCurrentAsync(connection,transaction,exact,cancellationToken) is not null;
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
                row.CaseOperationId, exact, row.RecipeCaseId, row.RecipeCaseOperationId));
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
                    WHERE event.production_run_id=run.id AND event.machine_id=machine.id
                    ORDER BY event.server_received_at DESC,event.id DESC
                    LIMIT 1), output.id, recipe.case_id, recipe.id
            FROM batch_operations operation
            JOIN production_batches batch ON batch.id=operation.production_batch_id
            JOIN cases ON cases.id=batch.case_id
            JOIN production_run_outputs output ON output.batch_operation_id=operation.id
            JOIN production_run_programs program ON program.id=output.production_run_program_id
            LEFT JOIN process_revisions process ON process.id=COALESCE(program.production_process_revision_id,program.process_revision_id)
            LEFT JOIN case_operations recipe ON recipe.id=process.case_operation_id
            JOIN production_runs run ON run.id=program.production_run_id
            JOIN machine_assignments assignment ON assignment.production_run_id=run.id AND assignment.released_at IS NULL
            JOIN machines machine ON machine.id=assignment.machine_id
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
                reader.GetString(11), reader.GetString(12), reader.GetString(14), Nullable(reader, 15), Nullable(reader, 16)));
        }
        return rows;
    }

    private static string? Nullable(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

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
        string CaseOperationId, string OutputId, string? RecipeCaseId, string? RecipeCaseOperationId);
}
