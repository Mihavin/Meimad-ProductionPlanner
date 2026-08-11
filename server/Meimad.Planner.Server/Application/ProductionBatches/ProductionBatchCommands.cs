namespace Meimad.Planner.Server.Application.ProductionBatches;

internal sealed record CreateProductionBatchCommand(
    string? CaseId,
    string? BatchNumber,
    string? Status,
    int PlannedQuantity,
    IReadOnlyList<CreateBatchAllocationCommand>? Allocations);

internal sealed record CreateBatchAllocationCommand(
    string? AllocationType,
    string? OrderId,
    int Quantity);
