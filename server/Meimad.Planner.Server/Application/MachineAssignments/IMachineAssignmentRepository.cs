using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Machines;

namespace Meimad.Planner.Server.Application.MachineAssignments;

internal interface IMachineAssignmentRepository
{
    Task<AssignmentMutationResult> AssignOrMoveAsync(
        string batchOperationId,
        string machineId,
        int backlogPosition,
        MachineAssignmentOverrideConfirmation? overrideConfirmation,
        DateTimeOffset now,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MachineAssignmentOverrideLog>> ListOverridesAsync(
        string batchOperationId,
        CancellationToken cancellationToken);

    Task<bool> UnassignAsync(
        string batchOperationId,
        DateTimeOffset now,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MachineBacklogItem>> GetBacklogAsync(
        string machineId,
        CancellationToken cancellationToken);

    Task<MachineAssignmentPlanningModeMutationResult> ChangePlanningModeAsync(
        string machineAssignmentId,
        int expectedVersion,
        MachineAssignmentPlanningMode planningMode,
        DateTimeOffset now,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task<BatchOperationExecutionResult> ChangeExecutionStatusAsync(
        string batchOperationId,
        BatchOperationExecutionAction action,
        OperationPauseReason? pauseReason,
        DateTimeOffset now,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);
}

internal sealed record AssignmentMutationResult(
    MachineAssignment Assignment,
    bool WasCreated);

internal sealed record MachineAssignmentPlanningModeMutationResult(
    MachineAssignment Assignment,
    bool Changed);

internal sealed record MachineAssignmentOverrideConfirmation(
    bool Confirmed,
    string Reason);

internal sealed record MachineAssignmentOverrideLog(
    string OverrideId,
    string BatchOperationId,
    string MachineId,
    string RequiredMachineType,
    string SelectedMachineType,
    string Reason,
    string ConfirmedByClientId,
    string ConfirmedByUserId,
    DateTimeOffset ConfirmedAt);

internal enum BatchOperationExecutionAction
{
    Start,
    Suspend,
    Finish,
    Reset
}

internal sealed record BatchOperationExecutionResult(
    string BatchOperationId,
    string MachineId,
    string Status,
    int Version,
    DateTimeOffset? ActualStart,
    DateTimeOffset? ActualEnd,
    string? ActualMachineId);
