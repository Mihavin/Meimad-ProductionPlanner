namespace Meimad.Planner.Server.Application.Concurrency;

/// <summary>
/// Another user changed the same item after this user read it. Single Edit Mode is retired
/// (owner decision 2026-09-27): users work in parallel and each change carries the version, state or
/// release it was based on. The Server refuses a change based on stale data with this conflict, which
/// the API answers as 409 <c>edit_conflict</c> naming what changed, who changed it and when, and what
/// the user can do.
/// </summary>
internal sealed class EditConflictException(
    string resource,
    string message,
    string advice,
    string? changedBy = null,
    DateTimeOffset? changedAt = null) : Exception(message)
{
    internal string Resource { get; } = resource;

    internal string Advice { get; } = advice;

    internal string? ChangedBy { get; } = changedBy;

    internal DateTimeOffset? ChangedAt { get; } = changedAt;
}
