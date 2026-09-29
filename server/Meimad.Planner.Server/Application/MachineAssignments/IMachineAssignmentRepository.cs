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
        string? expectedBacklogStamp,
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

    Task<MachineAssignmentPlanningModeMutationResult> ChangeManualPriorityAsync(
        string machineAssignmentId,
        int expectedVersion,
        int? manualPriority,
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

    Task<ManualWorkflowStatusResult> ReportWorkflowStatusAsync(
        string batchOperationId, string status, DateTimeOffset now, EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task<ManualOperationReportResult> RecordManualReportAsync(
        string batchOperationId, ManualOperationReportType reportType, int? partTimeSeconds,
        DateTimeOffset now, EditAuthority editAuthority, CancellationToken cancellationToken);

    /// <summary>Finished operations of Work Orders that are not cancelled, newest finish first.</summary>
    Task<IReadOnlyList<FinishedOperation>> ListFinishedOperationsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Redo: a finished operation goes back to not started with its Done quantity reset.</summary>
    Task<RedoOperationResult> RedoFinishedOperationAsync(
        string batchOperationId, int expectedVersion, DateTimeOffset now,
        EditAuthority editAuthority, CancellationToken cancellationToken);
}

/// <summary>A finished (completed) operation for the Planning Board's Finished tab.</summary>
internal sealed record FinishedOperation(
    string BatchOperationId,
    int Version,
    string BatchId,
    string BatchNumber,
    string CaseId,
    string PartNumber,
    string? CaseName,
    int OperationNumber,
    string OperationName,
    int PlannedQuantity,
    long ProducedQuantity,
    DateTimeOffset? ActualStart,
    DateTimeOffset? ActualEnd,
    string? MachineId,
    string? MachineName);

internal sealed record RedoOperationResult(
    string BatchOperationId,
    string BatchId,
    int OperationNumber,
    string BatchNumber,
    long PreviousProducedQuantity);

/// <summary>Why a finished operation cannot be redone.</summary>
internal sealed class OperationRedoException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
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

internal enum ManualOperationReportType { SetupStart, SetupEnd, PartTimeUpdate, ProductionEnd }

/// <summary>A reported workflow status; <see cref="EventId"/> is null when the run already had that status.</summary>
internal sealed record ManualWorkflowStatusResult(
    string BatchOperationId, string MachineId, string Status, string PreviousStatus, string? EventId, DateTimeOffset RecordedAt);

internal sealed record ManualOperationReportResult(
    string BatchOperationId, string MachineId, string ReportType, DateTimeOffset RecordedAt, int? PartTimeSeconds);

internal sealed record BatchOperationExecutionResult(
    string BatchOperationId,
    string MachineId,
    string Status,
    int Version,
    DateTimeOffset? ActualStart,
    DateTimeOffset? ActualEnd,
    string? ActualMachineId);
