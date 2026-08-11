using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.ProductionBatches;

namespace Meimad.Planner.Server.Application.ProductionBatches;

internal sealed class ProductionBatchService
{
    private readonly IProductionBatchRepository repository;
    private readonly TimeProvider timeProvider;

    public ProductionBatchService(
        IProductionBatchRepository repository,
        TimeProvider timeProvider)
    {
        this.repository = repository;
        this.timeProvider = timeProvider;
    }

    internal async Task<ProductionBatch> CreateAsync(
        CreateProductionBatchCommand command,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        var values = ProductionBatchValidator.ValidateAndNormalize(new ProductionBatchValues(
            command.CaseId,
            command.BatchNumber,
            command.Status,
            command.PlannedQuantity,
            command.Allocations?.Select(allocation => new BatchAllocationValue(
                allocation.AllocationType,
                allocation.OrderId,
                allocation.Quantity)).ToArray()));
        var now = timeProvider.GetUtcNow();
        var batchId = Guid.NewGuid().ToString("N");
        var allocations = values.Allocations.Select(allocation => new BatchAllocation(
            Guid.NewGuid().ToString("N"),
            batchId,
            allocation.AllocationType,
            allocation.OrderId,
            allocation.Quantity,
            1,
            now,
            now)).ToArray();
        var batch = new ProductionBatch(
            batchId,
            values.CaseId,
            values.BatchNumber,
            values.Status,
            values.PlannedQuantity,
            null,
            allocations,
            [],
            1,
            now,
            now);

        return await repository.CreateAsync(batch, editAuthority, cancellationToken);
    }

    internal Task<ProductionBatch?> GetByIdAsync(
        string batchId,
        CancellationToken cancellationToken = default) =>
        repository.GetByIdAsync(batchId, cancellationToken);

    internal Task<IReadOnlyList<ProductionBatch>> ListByCaseAsync(
        string caseId,
        CancellationToken cancellationToken = default) =>
        repository.ListByCaseAsync(caseId, cancellationToken);

    internal Task<IReadOnlyList<BatchOperation>> ListOperationsAsync(
        string batchId,
        CancellationToken cancellationToken = default) =>
        repository.ListOperationsAsync(batchId, cancellationToken);
}

internal sealed class ProductionBatchCaseNotFoundException : Exception
{
    internal ProductionBatchCaseNotFoundException(string caseId)
        : base($"Case '{caseId}' was not found.")
    {
    }
}

internal sealed class ProductionBatchNumberConflictException : Exception
{
    internal ProductionBatchNumberConflictException(string caseId, string batchNumber)
        : base($"Batch Number '{batchNumber}' already exists for Case '{caseId}'.")
    {
    }
}
