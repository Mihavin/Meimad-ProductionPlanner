using System.Text.Json.Serialization;
using Meimad.Planner.Server.Domain.Machines;
using Meimad.Planner.Server.Application.MachineAssignments;

namespace Meimad.Planner.Server.Api.MachineAssignments;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AssignMachineRequest(
    string? MachineId,
    int BacklogPosition,
    MachineAssignmentOverrideRequest? CompatibilityOverride);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MachineAssignmentOverrideRequest(bool Confirmed, string? Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SuspendOperationRequest(
    string? ReasonType, string? ProblemDescription, string? ToolingItemDescription,
    string? CustomerContactName, string? RequestDescription, string? Comment);

internal sealed record MachineAssignmentResponse(
    string MachineAssignmentId,
    string BatchOperationId,
    string MachineId,
    int BacklogPosition,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static MachineAssignmentResponse FromDomain(MachineAssignment assignment) => new(
        assignment.MachineAssignmentId,
        assignment.BatchOperationId,
        assignment.MachineId,
        assignment.BacklogPosition,
        assignment.Version,
        assignment.CreatedAt,
        assignment.UpdatedAt);
}

internal sealed record MachineBacklogItemResponse(
    MachineAssignmentResponse Assignment,
    string BatchId,
    int OperationNumber,
    string OperationName,
    string? RequiredMachineType)
{
    internal static MachineBacklogItemResponse FromDomain(MachineBacklogItem item) => new(
        MachineAssignmentResponse.FromDomain(item.Assignment),
        item.BatchId,
        item.OperationNumber,
        item.OperationName,
        item.RequiredMachineType);
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
    int Version)
{
    internal static BatchOperationExecutionResponse FromApplication(
        BatchOperationExecutionResult result) => new(
        result.BatchOperationId,
        result.MachineId,
        result.Status,
        result.Version);
}
