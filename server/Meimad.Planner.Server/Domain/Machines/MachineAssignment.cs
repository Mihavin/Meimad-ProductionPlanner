namespace Meimad.Planner.Server.Domain.Machines;

internal sealed record MachineAssignment(
    string MachineAssignmentId,
    string BatchOperationId,
    string MachineId,
    int BacklogPosition,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record MachineBacklogItem(
    MachineAssignment Assignment,
    string BatchId,
    int OperationNumber,
    string OperationName,
    string? RequiredMachineType);
