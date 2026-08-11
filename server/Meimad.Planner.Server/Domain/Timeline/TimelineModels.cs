namespace Meimad.Planner.Server.Domain.Timeline;

internal sealed record TimelineCalculationInput(
    DateTimeOffset HorizonStart,
    DateTimeOffset HorizonEnd,
    IReadOnlyList<TimelineMachineBacklog> MachineBacklogs,
    IReadOnlyList<TimelineMachineCalendar> MachineCalendars,
    TimelineSetupCalendar SetupCalendar,
    IReadOnlyList<TimelineDowntime> Downtimes,
    IReadOnlyList<TimelineDependency> Dependencies);

internal sealed record TimelineMachineBacklog(
    string MachineId,
    IReadOnlyList<TimelineOperationInput> Operations);

internal sealed record TimelineOperationInput(
    string OperationId,
    TimeSpan SetupDuration,
    TimeSpan ProductionDuration);

internal sealed record TimelineMachineCalendar(
    string MachineId,
    IReadOnlyList<TimelineWindow> Availability);

internal sealed record TimelineSetupCalendar(IReadOnlyList<TimelineWindow> Availability);

internal sealed record TimelineWindow(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

internal sealed record TimelineDowntime(
    string DowntimeId,
    string MachineId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Reason);

internal enum TimelineDependencyType
{
    Sequential,
    ParallelCapable,
    Independent,
    LockedSimultaneous
}

internal sealed record TimelineDependency(
    string DependencyId,
    TimelineDependencyType Type,
    string FromOperationId,
    string ToOperationId,
    string? SimultaneousGroupKey = null);

internal sealed record TimelineCalculationResult(
    DateTimeOffset HorizonStart,
    DateTimeOffset HorizonEnd,
    IReadOnlyList<TimelineOperationResult> Operations,
    IReadOnlyList<TimelineMachineResult> Machines,
    IReadOnlyList<TimelineConflict> Conflicts);

internal sealed record TimelineOperationResult(
    string OperationId,
    string MachineId,
    int BacklogPosition,
    DateTimeOffset StartsAt,
    DateTimeOffset FinishesAt,
    IReadOnlyList<TimelineInterval> SetupIntervals,
    IReadOnlyList<TimelineInterval> ProductionIntervals,
    IReadOnlyList<TimelineInterval> ReservedIntervals);

internal sealed record TimelineMachineResult(
    string MachineId,
    IReadOnlyList<TimelineInterval> Intervals);

internal enum TimelineIntervalType
{
    Setup,
    Production,
    Idle,
    Reserved,
    Downtime
}

internal sealed record TimelineInterval(
    TimelineIntervalType Type,
    string MachineId,
    string? OperationId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Detail = null);

internal enum TimelineConflictSeverity
{
    Blocking,
    Warning,
    Attention
}

internal sealed record TimelineConflict(
    string ConflictId,
    string Code,
    TimelineConflictSeverity Severity,
    string Message,
    IReadOnlyList<string> OperationIds,
    IReadOnlyList<string> MachineIds);
