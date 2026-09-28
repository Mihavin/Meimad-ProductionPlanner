using System.Text.Json.Serialization;
using Meimad.Planner.Server.Domain.Machines;
using Meimad.Planner.Server.Application.MachineAssignments;

namespace Meimad.Planner.Server.Api.MachineAssignments;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AssignMachineRequest(
    string? MachineId,
    int BacklogPosition,
    MachineAssignmentOverrideRequest? CompatibilityOverride,
    // The target Machine's backlogStamp from the Planning Board the planner saw; omit to skip the check.
    string? ExpectedBacklogStamp = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MachineAssignmentOverrideRequest(bool Confirmed, string? Reason);

/// <summary>Redo of a finished operation: the operation version the planner saw.</summary>
internal sealed record RedoOperationRequest(int? ExpectedVersion);

/// <summary>A row of the Planning Board's Finished tab.</summary>
internal sealed record FinishedOperationResponse(
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
    string? MachineName)
{
    internal static FinishedOperationResponse FromApplication(FinishedOperation value) => new(
        value.BatchOperationId, value.Version, value.BatchId, value.BatchNumber, value.CaseId,
        value.PartNumber, value.CaseName, value.OperationNumber, value.OperationName,
        value.PlannedQuantity, value.ProducedQuantity, value.ActualStart, value.ActualEnd,
        value.MachineId, value.MachineName);
}

// ManualPriority: lower wins ahead of any Work Finish Date when two Machines contend for the same
// scarce worker. Omit it to leave the stored value alone; send ClearManualPriority to remove it.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PatchMachineAssignmentRequest(
    string? PlanningMode,
    int? ManualPriority = null,
    bool ClearManualPriority = false);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SuspendOperationRequest(
    string? ReasonType, string? ProblemDescription, string? ToolingItemDescription,
    string? CustomerContactName, string? RequestDescription, string? Comment);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ManualOperationReportRequest(string? ReportType, int? PartTimeSeconds);

internal sealed record MachineAssignmentResponse(
    string MachineAssignmentId,
    string BatchOperationId,
    string MachineId,
    int BacklogPosition,
    string PlanningMode,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ProductionRunId,
    int? ManualPriority)
{
    internal static MachineAssignmentResponse FromDomain(MachineAssignment assignment) => new(
        assignment.MachineAssignmentId,
        assignment.BatchOperationId,
        assignment.MachineId,
        assignment.BacklogPosition,
        assignment.PlanningMode.ToToken(),
        assignment.Version,
        assignment.CreatedAt,
        assignment.UpdatedAt,
        assignment.ProductionRunId,
        assignment.ManualPriority);
}

internal sealed record MachineBacklogItemResponse(
    MachineAssignmentResponse Assignment,
    string BatchId,
    int OperationNumber,
    string OperationName,
    string? RequiredMachineType,
    DateTimeOffset? ActualStart,
    DateTimeOffset? ActualEnd,
    string? ActualMachineId)
{
    internal static MachineBacklogItemResponse FromDomain(MachineBacklogItem item) => new(
        MachineAssignmentResponse.FromDomain(item.Assignment),
        item.BatchId,
        item.OperationNumber,
        item.OperationName,
        item.RequiredMachineType,
        item.ActualStart,
        item.ActualEnd,
        item.ActualMachineId);
}

internal sealed record MachineBacklogResponse(
    string MachineId,
    IReadOnlyList<MachineBacklogItemResponse> Items);

internal sealed record MachineAssignmentOverrideResponse(
    string OverrideId,
    string BatchOperationId,
    string MachineId,
    string RequiredMachineType,
    string SelectedMachineType,
    string Reason,
    string ConfirmedByClientId,
    string ConfirmedByUserId,
    DateTimeOffset ConfirmedAt)
{
    internal static MachineAssignmentOverrideResponse FromApplication(
        MachineAssignmentOverrideLog value) => new(
        value.OverrideId,
        value.BatchOperationId,
        value.MachineId,
        value.RequiredMachineType,
        value.SelectedMachineType,
        value.Reason,
        value.ConfirmedByClientId,
        value.ConfirmedByUserId,
        value.ConfirmedAt);
}

internal sealed record BatchOperationExecutionResponse(
    string BatchOperationId,
    string MachineId,
    string Status,
    int Version,
    DateTimeOffset? ActualStart,
    DateTimeOffset? ActualEnd,
    string? ActualMachineId)
{
    internal static BatchOperationExecutionResponse FromApplication(
        BatchOperationExecutionResult result) => new(
        result.BatchOperationId,
        result.MachineId,
        result.Status,
        result.Version,
        result.ActualStart,
        result.ActualEnd,
        result.ActualMachineId);
}
