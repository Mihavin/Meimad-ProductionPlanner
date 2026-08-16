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
