using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Domain.ProductionBatches;

namespace Meimad.Planner.Server.Application.ProductionBatches;

internal sealed class ProductionBatchService
{
    private readonly IProductionBatchRepository repository;
    private readonly ICaseComponentRepository? componentRepository;
    private readonly TimeProvider timeProvider;
    private readonly DerivedCaseOrderService? derivedOrderService;

    public ProductionBatchService(
        IProductionBatchRepository repository,
        TimeProvider timeProvider,
        DerivedCaseOrderService? derivedOrderService = null,
        ICaseComponentRepository? componentRepository = null)
    {
        this.repository = repository;
        this.componentRepository = componentRepository;
        this.timeProvider = timeProvider;
        this.derivedOrderService = derivedOrderService;
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
                allocation.Quantity,
                allocation.DerivedOrderKey)).ToArray()));
        await EnsureCaseCanOwnBatchesAsync(values.CaseId, cancellationToken);
        await ValidateDerivedAllocationsAsync(values.CaseId, values.Allocations, null, cancellationToken);
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
            now,
            allocation.DerivedOrderKey)).ToArray();
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
                allocation.Quantity,
                allocation.DerivedOrderKey)).ToArray()));
        await EnsureCaseCanOwnBatchesAsync(current.CaseId, cancellationToken);
        await ValidateDerivedAllocationsAsync(current.CaseId, values.Allocations, current, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var allocations = values.Allocations.Select(allocation => new BatchAllocation(
            Guid.NewGuid().ToString("N"),
            batchId,
            allocation.AllocationType,
            allocation.OrderId,
            allocation.Quantity,
            1,
            now,
            now,
            allocation.DerivedOrderKey)).ToArray();
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

    private async Task EnsureCaseCanOwnBatchesAsync(string caseId, CancellationToken cancellationToken)
    {
        if (componentRepository is not null
            && (await componentRepository.ListComponentsAsync(caseId, cancellationToken)).Any(item => item.IsActive))
            throw new ProductionBatchParentCaseForbiddenException();
    }

    private async Task ValidateDerivedAllocationsAsync(
        string caseId, IReadOnlyList<ValidatedBatchAllocationValue> allocations,
        ProductionBatch? current, CancellationToken cancellationToken)
    {
        var requested = allocations.Where(item => item.AllocationType == BatchAllocationType.DerivedOrder).ToArray();
        if (requested.Length == 0) return;
        if (derivedOrderService is null)
            throw new InvalidOperationException("Derived Order validation service is not configured.");
        var rows = (await derivedOrderService.ListAsync(caseId, cancellationToken))
            .ToDictionary(item => item.DerivedOrderKey, StringComparer.Ordinal);
        var prior = current?.Allocations
            .Where(item => item.AllocationType == BatchAllocationType.DerivedOrder && item.DerivedOrderKey is not null)
            .GroupBy(item => item.DerivedOrderKey!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity), StringComparer.Ordinal)
            ?? new Dictionary<string, int>(StringComparer.Ordinal);
        var issues = new List<ProductionBatchValidationIssue>();
        foreach (var allocation in requested)
        {
            if (!rows.TryGetValue(allocation.DerivedOrderKey!, out var row))
            {
                issues.Add(new("allocations.derivedOrderKey", "invalid_reference",
                    "The derived Order does not belong to this child Case."));
                continue;
            }
            if (StringComparer.Ordinal.Equals(row.Status, "cancelled"))
                issues.Add(new("allocations.derivedOrderKey", "cancelled_order",
                    $"Cancelled source Order '{row.SourceOrderNumber}' cannot be allocated."));
            prior.TryGetValue(row.DerivedOrderKey, out var priorQuantity);
            if (allocation.Quantity > row.RemainingQuantity + priorQuantity + 0.0000001)
                issues.Add(new("allocations.quantity", "derived_order_overallocated",
                    $"Allocation exceeds the remaining derived demand for Order '{row.SourceOrderNumber}'."));
        }
        if (issues.Count > 0) throw new ProductionBatchValidationException(issues);
    }
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

internal sealed class ProductionBatchParentCaseForbiddenException : Exception
{
    internal ProductionBatchParentCaseForbiddenException()
        : base("A parent Case cannot own Production Batches. Production Batches belong to standalone or child Cases.") { }
}

internal sealed class ProductionBatchNotFoundException : Exception
{
    internal ProductionBatchNotFoundException(string batchId) : base($"Production Batch '{batchId}' was not found.") { }
}

internal sealed class ProductionBatchVersionConflictException : Exception
{
    internal ProductionBatchVersionConflictException(string batchId) : base($"Production Batch '{batchId}' changed after it was read.") { }
}
