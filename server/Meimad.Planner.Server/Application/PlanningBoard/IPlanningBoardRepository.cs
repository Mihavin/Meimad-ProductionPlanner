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
    int? BacklogPosition);

internal sealed record PlanningBoardMachine(
    string MachineId,
    string Number,
    string Name,
    string ProcessType,
    string? AxisType,
    IReadOnlyList<string> Capabilities,
    bool IsActive,
    IReadOnlyList<PlanningBoardOperation> Backlog);
