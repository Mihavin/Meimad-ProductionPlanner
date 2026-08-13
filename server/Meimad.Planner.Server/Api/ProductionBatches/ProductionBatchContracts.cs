using System.Text.Json.Serialization;
using Meimad.Planner.Server.Application.ProductionBatches;
using Meimad.Planner.Server.Domain.ProductionBatches;

namespace Meimad.Planner.Server.Api.ProductionBatches;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateProductionBatchRequest(
    string? CaseId,
    string? BatchNumber,
    string? Status,
    int PlannedQuantity,
    IReadOnlyList<CreateBatchAllocationRequest>? Allocations)
{
    internal CreateProductionBatchCommand ToCommand() => new(
        CaseId,
        BatchNumber,
        Status,
        PlannedQuantity,
        Allocations?.Select(allocation => allocation.ToCommand()).ToArray());
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateBatchAllocationRequest(
    string? AllocationType,
    string? OrderId,
    int Quantity)
{
    internal CreateBatchAllocationCommand ToCommand() => new(
        AllocationType,
        OrderId,
        Quantity);
}

internal sealed record ProductionBatchResponse(
    string BatchId,
    string CaseId,
    string BatchNumber,
    string Status,
    int PlannedQuantity,
    int? RouteRevision,
    IReadOnlyList<BatchAllocationResponse> Allocations,
    int BatchOperationCount,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static ProductionBatchResponse FromDomain(ProductionBatch batch) => new(
        batch.BatchId,
        batch.CaseId,
        batch.BatchNumber,
        batch.Status,
        batch.PlannedQuantity,
        batch.RouteRevision,
        batch.Allocations.Select(BatchAllocationResponse.FromDomain).ToArray(),
        batch.Operations.Count,
        batch.Version,
        batch.CreatedAt,
        batch.UpdatedAt);
}

internal sealed record ProductionBatchListResponse(
    IReadOnlyList<ProductionBatchResponse> Items,
    string? NextCursor);

internal sealed record BatchAllocationResponse(
    string AllocationId,
    string AllocationType,
    string? OrderId,
    int Quantity)
{
    internal static BatchAllocationResponse FromDomain(BatchAllocation allocation) => new(
        allocation.AllocationId,
        allocation.AllocationType.ToContractToken(),
        allocation.OrderId,
        allocation.Quantity);
}

internal sealed record BatchOperationResponse(
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
    DateTimeOffset UpdatedAt,
    int QaTimeAfterSetupSeconds,
    int LoadUnloadTimeSeconds,
    bool LoadUnloadRequiresWorker,
    bool AutomaticLoading,
    int? LoadUnloadEveryNParts,
    bool DayShiftOnly,
    DateTimeOffset? ActualStart,
    DateTimeOffset? ActualEnd,
    string? ActualMachineId)
{
    internal static BatchOperationResponse FromDomain(BatchOperation operation) => new(
        operation.BatchOperationId,
        operation.BatchId,
        operation.SourceCaseOperationId,
        operation.OperationNumber,
        operation.RoutePosition,
        operation.Name,
        operation.RequiredMachineType,
        operation.SetupTimeSeconds,
        operation.CycleTimePerPartSeconds,
        operation.Status,
        operation.Version,
        operation.CreatedAt,
        operation.UpdatedAt,
        operation.QaTimeAfterSetupSeconds,
        operation.LoadUnloadTimeSeconds,
        operation.LoadUnloadRequiresWorker,
        operation.AutomaticLoading,
        operation.LoadUnloadEveryNParts,
        operation.DayShiftOnly,
        operation.ActualStart,
        operation.ActualEnd,
        operation.ActualMachineId);
}

internal sealed record BatchOperationListResponse(
    IReadOnlyList<BatchOperationResponse> Items,
    string? NextCursor);
