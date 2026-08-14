namespace Meimad.Planner.Server.Application.PlanningBoard;

internal interface IPlanningBoardRepository
{
    Task<PlanningBoardSnapshot> ReadAsync(CancellationToken cancellationToken);
}

internal sealed record PlanningBoardSnapshot(
    DateTimeOffset ReadAt,
    IReadOnlyList<PlanningBoardOperation> Pool,
    IReadOnlyList<PlanningBoardMachine> Machines);

internal sealed record PlanningBoardOperation(
    string BatchOperationId,
    string BatchId,
    string BatchNumber,
    string CaseId,
    string PartNumber,
    int OperationNumber,
    string OperationName,
    string? RequiredMachineType,
    int? SetupTimeSeconds,
    int? CycleTimePerPartSeconds,
    string Status,
    string? MachineId,
    int? BacklogPosition,
    int PlannedQuantity,
    IReadOnlyList<string> OrderReferences,
    long? EstimatedTimeSeconds,
    int QaTimeAfterSetupSeconds,
    int LoadUnloadTimeSeconds,
    bool LoadUnloadRequiresWorker,
    bool AutomaticLoading,
    int? LoadUnloadEveryNParts,
    bool DayShiftOnly,
    string? ActivePauseReason,
    string? PausedBy,
    DateTimeOffset? PauseStartedAt,
    string? CaseName = null,
    DateTimeOffset? ActualStart = null,
    DateTimeOffset? ActualEnd = null,
    string? ActualMachineId = null,
    string? MachineAssignmentId = null,
    int? AssignmentVersion = null,
    string PlanningMode = "manual");

internal sealed record PlanningBoardMachine(
    string MachineId,
    string Number,
    string Name,
    string ProcessType,
    string? AxisType,
    IReadOnlyList<string> Capabilities,
    bool IsActive,
    IReadOnlyList<PlanningBoardOperation> Backlog);
