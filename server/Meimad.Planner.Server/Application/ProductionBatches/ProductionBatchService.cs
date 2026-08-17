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

    internal async Task<ProductionBatch> UpdateAsync(
        string batchId,
        int expectedVersion,
        UpdateProductionBatchCommand command,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        var current = await repository.GetByIdAsync(batchId, cancellationToken)
            ?? throw new ProductionBatchNotFoundException(batchId);
        var values = ProductionBatchValidator.ValidateAndNormalize(new ProductionBatchValues(
            current.CaseId,
            command.BatchNumber,
            ProductionBatchValidator.WaitingStatus,
            command.PlannedQuantity,
            command.Allocations?.Select(allocation => new BatchAllocationValue(
                allocation.AllocationType,
                allocation.OrderId,
                allocation.Quantity)).ToArray()));
        var now = timeProvider.GetUtcNow();
        var allocations = values.Allocations.Select(allocation => new BatchAllocation(
            Guid.NewGuid().ToString("N"),
            batchId,
            allocation.AllocationType,
            allocation.OrderId,
            allocation.Quantity,
            1,
            now,
            now)).ToArray();
        var candidate = current with
        {
            BatchNumber = values.BatchNumber,
            PlannedQuantity = values.PlannedQuantity,
            Allocations = allocations,
            Version = expectedVersion + 1,
            UpdatedAt = now
        };
        return await repository.UpdateAsync(candidate, expectedVersion, editAuthority, cancellationToken)
            ?? throw new ProductionBatchVersionConflictException(batchId);
    }

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

internal sealed class ProductionBatchRouteRequiredException : Exception
{
    internal ProductionBatchRouteRequiredException()
        : base("Cannot generate Production Batch because this Case has no defined operations. Create operations first.")
    {
    }
}

internal sealed class ProductionBatchNotFoundException : Exception
{
    internal ProductionBatchNotFoundException(string batchId) : base($"Production Batch '{batchId}' was not found.") { }
}

internal sealed class ProductionBatchVersionConflictException : Exception
{
    internal ProductionBatchVersionConflictException(string batchId) : base($"Production Batch '{batchId}' changed after it was read.") { }
}
