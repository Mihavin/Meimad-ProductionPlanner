using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Machines;

namespace Meimad.Planner.Server.Application.MachineAssignments;

internal interface IMachineAssignmentRepository
{
    Task<AssignmentMutationResult> AssignOrMoveAsync(
        string batchOperationId,
        string machineId,
        int backlogPosition,
        DateTimeOffset now,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task<bool> UnassignAsync(
        string batchOperationId,
        DateTimeOffset now,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MachineBacklogItem>> GetBacklogAsync(
        string machineId,
        CancellationToken cancellationToken);

    Task<BatchOperationExecutionResult> ChangeExecutionStatusAsync(
        string batchOperationId,
        BatchOperationExecutionAction action,
        DateTimeOffset now,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);
}

internal sealed record AssignmentMutationResult(
    MachineAssignment Assignment,
    bool WasCreated);

internal enum BatchOperationExecutionAction
{
    Start,
    Suspend,
    Finish
}

internal sealed record BatchOperationExecutionResult(
    string BatchOperationId,
    string MachineId,
    string Status,
    int Version);
