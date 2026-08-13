using Meimad.Planner.Server.Application.PlanningBoard;

namespace Meimad.Planner.Server.Api.PlanningBoard;

internal sealed record PlanningBoardResponse(
    DateTimeOffset ReadAt,
    string ConflictCalculationStatus,
    string ConflictCalculationMessage,
    IReadOnlyList<PlanningBoardConflictResponse> Conflicts,
    IReadOnlyList<PlanningBoardOperationResponse> Pool,
    IReadOnlyList<PlanningBoardMachineResponse> Machines)
{
    internal static PlanningBoardResponse FromApplication(PlanningBoardSnapshot snapshot) => new(
        snapshot.ReadAt,
        "unavailable",
        "The pure time engine is not connected to the planning-board projection yet.",
        [],
        snapshot.Pool.Select(PlanningBoardOperationResponse.FromApplication).ToArray(),
        snapshot.Machines.Select(PlanningBoardMachineResponse.FromApplication).ToArray());
}

internal sealed record PlanningBoardConflictResponse(
    string ConflictId,
    string Code,
    string Severity,
    string Title,
    string Message);

internal sealed record PlanningBoardOperationResponse(
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
    string? CaseName,
    DateTimeOffset? ActualStart,
    DateTimeOffset? ActualEnd,
    string? ActualMachineId)
{
    internal static PlanningBoardOperationResponse FromApplication(
        PlanningBoardOperation operation) => new(
        operation.BatchOperationId,
        operation.BatchId,
        operation.BatchNumber,
        operation.CaseId,
        operation.PartNumber,
        operation.OperationNumber,
        operation.OperationName,
        operation.RequiredMachineType,
        operation.SetupTimeSeconds,
        operation.CycleTimePerPartSeconds,
        operation.Status,
        operation.MachineId,
        operation.BacklogPosition,
        operation.PlannedQuantity,
        operation.OrderReferences,
        operation.EstimatedTimeSeconds,
        operation.QaTimeAfterSetupSeconds,
        operation.LoadUnloadTimeSeconds,
        operation.LoadUnloadRequiresWorker,
        operation.AutomaticLoading,
        operation.LoadUnloadEveryNParts,
        operation.DayShiftOnly,
        operation.ActivePauseReason,
        operation.PausedBy,
        operation.PauseStartedAt,
        operation.CaseName,
        operation.ActualStart,
        operation.ActualEnd,
        operation.ActualMachineId);
}

internal sealed record PlanningBoardMachineResponse(
    string MachineId,
    string Number,
    string Name,
    string ProcessType,
    string? AxisType,
    IReadOnlyList<string> Capabilities,
    bool IsActive,
    IReadOnlyList<PlanningBoardOperationResponse> Backlog)
{
    internal static PlanningBoardMachineResponse FromApplication(
        PlanningBoardMachine machine) => new(
        machine.MachineId,
        machine.Number,
        machine.Name,
        machine.ProcessType,
        machine.AxisType,
        machine.Capabilities,
        machine.IsActive,
        machine.Backlog.Select(PlanningBoardOperationResponse.FromApplication).ToArray());
}
