namespace Meimad.Planner.Server.Domain.ProductionBatches;

internal sealed record ProductionBatch(
    string BatchId,
    string CaseId,
    string BatchNumber,
    string Status,
    int PlannedQuantity,
    int? RouteRevision,
    IReadOnlyList<BatchAllocation> Allocations,
    IReadOnlyList<BatchOperation> Operations,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record BatchAllocation(
    string AllocationId,
    string BatchId,
    BatchAllocationType AllocationType,
    string? OrderId,
    int Quantity,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record BatchOperation(
    string BatchOperationId,
    string BatchId,
    string SourceCaseOperationId,
    int OperationNumber,
    int RoutePosition,
    string Name,
    string? RequiredMachineType,
    int? SetupTimeSeconds,
    int? CycleTimePerPartSeconds,
    string Status,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
