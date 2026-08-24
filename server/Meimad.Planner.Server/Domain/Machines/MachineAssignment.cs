namespace Meimad.Planner.Server.Domain.Machines;

internal sealed record MachineAssignment(
    string MachineAssignmentId,
    string BatchOperationId,
    string MachineId,
    int BacklogPosition,
    MachineAssignmentPlanningMode PlanningMode,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ProductionRunId = null);

internal enum MachineAssignmentPlanningMode
{
    Forward,
    Backward,
    Manual
}

internal static class MachineAssignmentPlanningModes
{
    internal const string ForwardToken = "forward";
    internal const string BackwardToken = "backward";
    internal const string ManualToken = "manual";

    internal static bool TryParse(string? value, out MachineAssignmentPlanningMode mode)
    {
        mode = value switch
        {
            ForwardToken => MachineAssignmentPlanningMode.Forward,
            BackwardToken => MachineAssignmentPlanningMode.Backward,
            ManualToken => MachineAssignmentPlanningMode.Manual,
            _ => default
        };
        return value is ForwardToken or BackwardToken or ManualToken;
    }

    internal static string ToToken(this MachineAssignmentPlanningMode mode) => mode switch
    {
        MachineAssignmentPlanningMode.Forward => ForwardToken,
        MachineAssignmentPlanningMode.Backward => BackwardToken,
        MachineAssignmentPlanningMode.Manual => ManualToken,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown Machine Assignment planning mode.")
    };
}

internal sealed record MachineBacklogItem(
    MachineAssignment Assignment,
    string BatchId,
    int OperationNumber,
    string OperationName,
    string? RequiredMachineType,
    DateTimeOffset? ActualStart = null,
    DateTimeOffset? ActualEnd = null,
    string? ActualMachineId = null);
