using Meimad.Planner.Server.Application.EditMode;

namespace Meimad.Planner.Server.Application.Timeline;

/// <summary>
/// A pin fixes the Workstation and/or Employee of one auxiliary work item (Batch Operation plus
/// requirement) and optionally its start. It is a recalculation constraint for the deterministic
/// allocator, stored in the schema-v65 resource schedule tables as state PINNED; the projection
/// itself is never persisted.
/// </summary>
internal sealed record TimelineAuxiliaryPin(
    string BatchOperationId,
    string RequirementId,
    string? WorkstationId,
    string? EmployeeId,
    DateTimeOffset PlannedStartsAt,
    DateTimeOffset PlannedEndsAt,
    bool PinStart,
    string? Reason);

internal interface ITimelineAuxiliaryPinRepository
{
    Task<TimelineSourceAuxiliaryPin> SetAsync(TimelineAuxiliaryPin pin, EditAuthority authority, string? userId, CancellationToken cancellationToken);

    Task<bool> ClearAsync(string batchOperationId, string requirementId, EditAuthority authority, CancellationToken cancellationToken);
}

internal sealed class TimelineAuxiliaryPinException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}
