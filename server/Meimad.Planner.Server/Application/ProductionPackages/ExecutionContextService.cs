using Meimad.Planner.Server.Persistence;
namespace Meimad.Planner.Server.Application.ProductionPackages;
internal sealed record WorkbenchAllocation(string AllocationId, string Type, string? OrderId, string? OrderReference,
    long Quantity, int Version, string? DueDate);
internal sealed record OperationWorkbenchDetails(string CaseId, string PartNumber, string CaseName, string BatchId,
    string BatchNumber, long BatchQuantity, int BatchVersion, string BatchOperationId, int OperationNumber, string OperationName,
    int OperationVersion, string RecipeCaseId, string RecipeOperationId, string? ProcessRevisionId, string? MaterialType,
    IReadOnlyList<WorkbenchAllocation> Allocations, bool AllocationsTruncated, DateTimeOffset SampledAt);
internal sealed class ExecutionContextService(SqliteDatabase database)
{
    internal async Task<OperationWorkbenchDetails?> ReadWorkbenchAsync(string operationId, ProductionPackageSelection? selection, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 256) throw new ArgumentException("Supply a Batch Operation ID.");
        await using var connection = await database.OpenConnectionAsync(token);
        await using var tx = connection.BeginTransaction(deferred: true);
        var exact = selection is null ? null : await SqliteProductionPackageContext.ResolveAsync(connection, tx, operationId, selection, token);
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            SELECT c.id,c.part_number,c.name,b.id,b.batch_number,b.planned_quantity,b.version,
                bo.operation_number,bo.name,bo.version,recipe.case_id,recipe.id,c.material_type
            FROM batch_operations bo JOIN production_batches b ON b.id=bo.production_batch_id
            JOIN cases c ON c.id=b.case_id
            LEFT JOIN process_revisions process ON process.id=$process
            JOIN case_operations recipe ON recipe.id=COALESCE(process.case_operation_id,bo.source_case_operation_id)
            WHERE bo.id=$operation;
            """;
        command.Parameters.AddWithValue("$operation", operationId); command.Parameters.AddWithValue("$process", (object?)exact?.ProcessRevisionId ?? DBNull.Value);
        OperationWorkbenchDetails result;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) return null;
            result = new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetInt64(5), reader.GetInt32(6), operationId, reader.GetInt32(7), reader.GetString(8), reader.GetInt32(9),
                reader.GetString(10), reader.GetString(11), exact?.ProcessRevisionId, reader.IsDBNull(12) ? null : reader.GetString(12), [], false, DateTimeOffset.UtcNow);
        }
        command.Parameters.Clear(); command.CommandText = """
            SELECT a.id,a.allocation_type,a.order_id,o.order_reference,a.quantity,a.version,o.work_finish_date
            FROM batch_allocations a LEFT JOIN orders o ON o.id=a.order_id
            WHERE a.production_batch_id=$batch ORDER BY a.id LIMIT 201;
            """;
        command.Parameters.AddWithValue("$batch", result.BatchId);
        var allocations = new List<WorkbenchAllocation>();
        await using var allocationReader = await command.ExecuteReaderAsync(token);
        while (await allocationReader.ReadAsync(token)) allocations.Add(new(allocationReader.GetString(0), allocationReader.GetString(1),
            allocationReader.IsDBNull(2) ? null : allocationReader.GetString(2), allocationReader.IsDBNull(3) ? null : allocationReader.GetString(3),
            allocationReader.GetInt64(4), allocationReader.GetInt32(5), allocationReader.IsDBNull(6) ? null : allocationReader.GetString(6)));
        return result with { Allocations = allocations.Take(200).ToArray(), AllocationsTruncated = allocations.Count > 200 };
    }
    internal async Task<IReadOnlyList<ProductionPackageContext>> ListAsync(string operationId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 256) throw new ArgumentException("Supply a Batch Operation ID.");
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: true);
        return await SqliteProductionPackageContext.ListAsync(connection, transaction, operationId, token);
    }
}
