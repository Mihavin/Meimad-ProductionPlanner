namespace Meimad.Planner.Server.Domain.Timeline;

internal sealed record TimelineCalculationInput(
    DateTimeOffset HorizonStart,
    DateTimeOffset HorizonEnd,
    IReadOnlyList<TimelineMachineBacklog> MachineBacklogs,
    IReadOnlyList<TimelineMachineCalendar> MachineCalendars,
    TimelineSetupCalendar SetupCalendar,
    IReadOnlyList<TimelineDowntime> Downtimes,
    IReadOnlyList<TimelineDependency> Dependencies,
    IReadOnlyList<TimelineResourceCalendar>? ResourceCalendars = null,
    IReadOnlyList<TimelineMachineCalendar>? DayShiftCalendars = null);

internal enum TimelinePlanningMode
{
    Manual,
    Forward,
    Backward
}

internal sealed record TimelineMachineBacklog(
    string MachineId,
    IReadOnlyList<TimelineOperationInput> Operations);

internal sealed record TimelineOperationInput(
    string OperationId,
    TimeSpan SetupDuration,
    TimeSpan ProductionDuration,
    TimeSpan QaDuration = default,
    TimeSpan LoadUnloadDuration = default,
    bool LoadUnloadRequiresWorker = false,
    bool DayShiftOnly = false,
    DateOnly? PriorityWorkFinishDate = null,
    string? PriorityOrderNumber = null,
    DateTimeOffset? EarliestStart = null,
    DateTimeOffset? LatestFinish = null,
    TimelinePlanningMode PlanningMode = TimelinePlanningMode.Manual,
    int PlannedQuantity = 1,
    bool AutomaticLoading = false,
    int? LoadUnloadEveryNParts = null,
    TimeSpan ExternalDelayAfter = default,
    TimelineWorkingDayDelay? ExternalWorkingDayDelay = null);

internal sealed record TimelineWorkingDayDelay(
    int Days,
    string TimeZoneId,
    IReadOnlyList<TimelineWindow> Availability);

internal static class TimelinePriorityComparer
{
    internal static int CompareOrderNumbers(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return 1;
        if (right is null) return -1;
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            if (char.IsDigit(left[leftIndex]) && char.IsDigit(right[rightIndex]))
            {
                var leftEnd = leftIndex;
                while (leftEnd < left.Length && char.IsDigit(left[leftEnd])) leftEnd++;
                var rightEnd = rightIndex;
                while (rightEnd < right.Length && char.IsDigit(right[rightEnd])) rightEnd++;
                var leftDigits = left.AsSpan(leftIndex, leftEnd - leftIndex).TrimStart('0');
                var rightDigits = right.AsSpan(rightIndex, rightEnd - rightIndex).TrimStart('0');
                if (leftDigits.Length != rightDigits.Length) return leftDigits.Length.CompareTo(rightDigits.Length);
                var digitsComparison = leftDigits.CompareTo(rightDigits, StringComparison.Ordinal);
                if (digitsComparison != 0) return digitsComparison;
                leftIndex = leftEnd;
                rightIndex = rightEnd;
                continue;
            }

            var comparison = char.ToUpperInvariant(left[leftIndex]).CompareTo(char.ToUpperInvariant(right[rightIndex]));
            if (comparison != 0) return comparison;
            leftIndex++;
            rightIndex++;
        }
        return left.Length.CompareTo(right.Length);
    }
}

internal sealed record TimelineMachineCalendar(
    string MachineId,
    IReadOnlyList<TimelineWindow> Availability,
    IReadOnlyList<string>? SkillTokens = null);

internal sealed record TimelineSetupCalendar(IReadOnlyList<TimelineWindow> Availability);

internal sealed record TimelineResourceCalendar(
    string ResourceId,
    TimelineResourceRole Role,
    IReadOnlyList<TimelineWindow> Availability,
    IReadOnlyList<string>? Skills = null);

internal enum TimelineResourceRole
{
    SetupWorker,
    QaWorker,
    RegularWorker
}

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
    IReadOnlyList<TimelineInterval> ReservedIntervals,
    IReadOnlyList<TimelineInterval> WaitingIntervals,
    IReadOnlyList<TimelineInterval>? QaIntervals = null,
    IReadOnlyList<TimelineInterval>? LoadUnloadIntervals = null);

internal sealed record TimelineMachineResult(
    string MachineId,
    IReadOnlyList<TimelineInterval> Intervals);

internal enum TimelineIntervalType
{
    Setup,
    Qa,
    LoadUnload,
    Production,
    Waiting,
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
