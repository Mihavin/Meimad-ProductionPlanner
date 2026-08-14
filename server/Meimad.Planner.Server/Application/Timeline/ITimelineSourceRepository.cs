namespace Meimad.Planner.Server.Application.Timeline;

internal interface ITimelineSourceRepository
{
    Task<TimelineSourceSnapshot> ReadAsync(
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        CancellationToken cancellationToken);
}

internal sealed record TimelineSourceSnapshot(
    DateTimeOffset ReadAt,
    IReadOnlyList<TimelineSourceMachine> Machines,
    IReadOnlyList<TimelineSourceOperation> Operations,
    IReadOnlyList<TimelineSourceDowntime> Downtimes,
    string? SetupCalendarJson,
    string? SetupCalendarTimeZoneId,
    IReadOnlyList<TimelineSourceHoliday> Holidays,
    IReadOnlyList<TimelineSourceResource> Resources);

internal sealed record TimelineSourceHoliday(
    DateOnly Date, string Name, string Status, string? StartsAtLocal, string? EndsAtLocal);

internal sealed record TimelineSourceMachine(
    string MachineId,
    string Number,
    string Name,
    string TimeZoneId,
    string CalendarJson,
    IReadOnlyList<string> SkillTokens);

internal sealed record TimelineSourceOperation(
    string OperationId,
    string BatchId,
    string BatchNumber,
    string CaseId,
    string PartNumber,
    int OperationNumber,
    string OperationName,
    string Status,
    int PlannedQuantity,
    int? SetupSeconds,
    int? CycleSeconds,
    string SourceCaseOperationId,
    string DependencyType,
    string? PredecessorSourceCaseOperationId,
    string? SimultaneousGroupKey,
    string? MachineAssignmentId,
    string? MachineId,
    int? BacklogPosition,
    string? PlanningMode,
    int QaSeconds,
    int LoadUnloadSeconds,
    bool LoadUnloadRequiresWorker,
    bool AutomaticLoading,
    int? LoadUnloadEveryNParts,
    bool DayShiftOnly,
    DateOnly? PriorityWorkFinishDate,
    string? PriorityOrderNumber,
    string? ActivePauseReason,
    string? PausedBy,
    DateTimeOffset? PauseStartedAt,
    DateTimeOffset? ActualStart,
    DateTimeOffset? ActualEnd,
    string? ActualMachineId);

internal sealed record TimelineSourceResource(
    string ResourceId,
    string Role,
    string TimeZoneId,
    string CalendarJson,
    IReadOnlyList<TimelineSourceResourceException> Exceptions,
    IReadOnlyList<string> Skills);

internal sealed record TimelineSourceResourceException(
    DateOnly Date,
    bool IsFullDay,
    string? StartsAtLocal,
    string? EndsAtLocal);

internal sealed record TimelineSourceDowntime(
    string DowntimeId,
    string MachineId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Reason);
