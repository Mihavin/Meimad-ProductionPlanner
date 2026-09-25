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
    DateTimeOffset UpdatedAt)
{
    /// <summary>Kitaron's material verdict for the imported batch: available, on_order, missing
    /// or unknown; null for a batch Kitaron does not own. Advisory - the ERP owns stock.</summary>
    internal string? KitaronMaterialState { get; init; }

    internal string? KitaronMaterialDetail { get; init; }

    /// <summary>True when the batch is linked to a Kitaron work order.</summary>
    internal bool IsKitaronManaged { get; init; }
}

internal sealed record BatchAllocation(
    string AllocationId,
    string BatchId,
    BatchAllocationType AllocationType,
    string? OrderId,
    int Quantity,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? DerivedOrderKey = null);

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
    DateTimeOffset UpdatedAt,
    int QaTimeAfterSetupSeconds = 0,
    int LoadUnloadTimeSeconds = 0,
    bool LoadUnloadRequiresWorker = false,
    bool AutomaticLoading = false,
    int? LoadUnloadEveryNParts = null,
    bool DayShiftOnly = false,
    DateTimeOffset? ActualStart = null,
    DateTimeOffset? ActualEnd = null,
    string? ActualMachineId = null,
    bool HasExternalDelay = false,
    string? ExternalDelayDescription = null,
    double ExternalDelayDuration = 0,
    string ExternalDelayDurationUnit = "hours",
    string? ExternalDelayCalendarId = null,
    bool RespectMasterCalendar = true);
