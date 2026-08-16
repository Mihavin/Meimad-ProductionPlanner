using Meimad.Planner.Server.Domain.Timeline;

namespace Meimad.Planner.Server.Tests.Timeline;

public sealed class TimelineMixedPlanningModeTests
{
    private static readonly DateTimeOffset Start = Utc(8);
    private static readonly DateTimeOffset End = Utc(18);

    [Fact]
    public void Working_day_external_delay_skips_closed_master_calendar_dates()
    {
        var horizonEnd = new DateTimeOffset(2026, 8, 14, 18, 0, 0, TimeSpan.Zero);
        var workWindows = new[]
        {
            new TimelineWindow(new DateTimeOffset(2026, 8, 12, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero)),
            new TimelineWindow(new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 13, 18, 0, 0, TimeSpan.Zero))
        };
        var result = new TimelineCalculationEngine().Calculate(new TimelineCalculationInput(
            Start, horizonEnd,
            [
                new TimelineMachineBacklog("machine-1", [new TimelineOperationInput(
                    "op-1", TimeSpan.Zero, TimeSpan.FromHours(1), EarliestStart: Start,
                    ExternalWorkingDayDelay: new TimelineWorkingDayDelay(2, "UTC", workWindows))]),
                new TimelineMachineBacklog("machine-2", [new TimelineOperationInput(
                    "op-2", TimeSpan.Zero, TimeSpan.FromHours(1), EarliestStart: Start)])
            ],
            [
                new TimelineMachineCalendar("machine-1", [new TimelineWindow(Start, horizonEnd)]),
                new TimelineMachineCalendar("machine-2", [new TimelineWindow(Start, horizonEnd)])
            ],
            new TimelineSetupCalendar([new TimelineWindow(Start, horizonEnd)]), [],
            [new TimelineDependency("dep", TimelineDependencyType.Sequential, "op-1", "op-2")], []));

        var predecessor = Assert.Single(result.Operations, value => value.OperationId == "op-1");
        var successor = Assert.Single(result.Operations, value => value.OperationId == "op-2");
        Assert.Equal(Utc(9), predecessor.FinishesAt);
        Assert.Equal(new DateTimeOffset(2026, 8, 13, 9, 0, 0, TimeSpan.Zero), successor.StartsAt);
    }

    [Fact]
    public void Mixed_forward_and_backward_assignments_share_one_machine_without_overlap_or_reorder()
    {
        var backlog = new TimelineMachineBacklog("machine-1",
        [
            Operation("forward", 1, TimelinePlanningMode.Forward),
            Operation("backward", 1, TimelinePlanningMode.Backward, Utc(16))
        ]);
        var originalOrder = backlog.Operations.Select(value => value.OperationId).ToArray();

        var result = Calculate([backlog], [Calendar("machine-1")]);

        var forward = Assert.Single(result.Operations, value => value.OperationId == "forward");
        var backward = Assert.Single(result.Operations, value => value.OperationId == "backward");
        Assert.Equal(Utc(8), forward.StartsAt);
        Assert.Equal(Utc(9), forward.FinishesAt);
        Assert.Equal(Utc(15), backward.StartsAt);
        Assert.Equal(Utc(16), backward.FinishesAt);
        Assert.True(backward.StartsAt >= forward.FinishesAt);
        Assert.Equal(originalOrder, backlog.Operations.Select(value => value.OperationId));
        Assert.DoesNotContain(result.Conflicts, value => value.Severity == TimelineConflictSeverity.Blocking);
    }

    [Fact]
    public void Mixed_three_operation_chain_auto_shifts_each_backward_child_after_predecessor()
    {
        var dependencies = new[]
        {
            new TimelineDependency("dep-1", TimelineDependencyType.Sequential, "op-10", "op-20"),
            new TimelineDependency("dep-2", TimelineDependencyType.Sequential, "op-20", "op-30")
        };
        var result = Calculate(
        [
            new TimelineMachineBacklog("machine-1",
                [Operation("op-10", 2, TimelinePlanningMode.Manual)]),
            new TimelineMachineBacklog("machine-2",
                [Operation("op-20", 1, TimelinePlanningMode.Backward, Utc(16))]),
            new TimelineMachineBacklog("machine-3",
                [Operation("op-30", 1, TimelinePlanningMode.Backward, Utc(17))])
        ],
        [Calendar("machine-1"), Calendar("machine-2"), Calendar("machine-3")],
        dependencies);

        var op10 = Assert.Single(result.Operations, value => value.OperationId == "op-10");
        var op20 = Assert.Single(result.Operations, value => value.OperationId == "op-20");
        var op30 = Assert.Single(result.Operations, value => value.OperationId == "op-30");
        Assert.True(op20.StartsAt >= op10.FinishesAt);
        Assert.True(op30.StartsAt >= op20.FinishesAt);
        Assert.Equal(Utc(15), op20.StartsAt);
        Assert.Equal(Utc(16), op20.FinishesAt);
        Assert.Equal(Utc(16), op30.StartsAt);
        Assert.Equal(Utc(17), op30.FinishesAt);
        Assert.DoesNotContain(result.Conflicts, value => value.Code == "dependency_unresolved");
    }

    [Fact]
    public void External_delay_moves_the_successor_without_reserving_the_predecessor_machine()
    {
        var first = Operation("op-10", 1, TimelinePlanningMode.Manual) with
        {
            ExternalDelayAfter = TimeSpan.FromHours(2)
        };
        var result = Calculate(
        [
            new TimelineMachineBacklog("machine-1", [first]),
            new TimelineMachineBacklog("machine-2", [Operation("op-20", 1, TimelinePlanningMode.Manual)])
        ],
        [Calendar("machine-1"), Calendar("machine-2")],
        [new TimelineDependency("dep", TimelineDependencyType.Sequential, "op-10", "op-20")]);

        var predecessor = Assert.Single(result.Operations, value => value.OperationId == "op-10");
        var successor = Assert.Single(result.Operations, value => value.OperationId == "op-20");
        Assert.Equal(Utc(9), predecessor.FinishesAt);
        Assert.Equal(Utc(11), successor.StartsAt);
        Assert.Contains(result.Machines.Single(value => value.MachineId == "machine-1").Intervals,
            value => value.Type == TimelineIntervalType.Idle && value.StartsAt <= Utc(9) && value.EndsAt >= Utc(11));
    }

    [Fact]
    public void Future_backward_gap_is_available_capacity_not_a_conflict()
    {
        var result = Calculate(
        [
            new TimelineMachineBacklog("machine-1",
            [
                Operation("manual", 1, TimelinePlanningMode.Manual),
                Operation("future", 1, TimelinePlanningMode.Backward, Utc(17))
            ])
        ],
        [Calendar("machine-1")]);

        var future = Assert.Single(result.Operations, value => value.OperationId == "future");
        Assert.Equal(Utc(16), future.StartsAt);
        Assert.Contains(Assert.Single(result.Machines).Intervals, value =>
            value.Type == TimelineIntervalType.Idle
            && value.StartsAt <= Utc(9)
            && value.EndsAt >= Utc(16));
        Assert.DoesNotContain(result.Conflicts, value => value.Severity == TimelineConflictSeverity.Blocking);
    }

    [Fact]
    public void Locked_simultaneous_group_with_mixed_modes_returns_structured_conflict()
    {
        var result = Calculate(
        [
            new TimelineMachineBacklog("machine-1",
                [Operation("manual", 1, TimelinePlanningMode.Manual)]),
            new TimelineMachineBacklog("machine-2",
                [Operation("backward", 1, TimelinePlanningMode.Backward, Utc(17))])
        ],
        [Calendar("machine-1"), Calendar("machine-2")],
        [new TimelineDependency(
            "locked-1", TimelineDependencyType.LockedSimultaneous,
            "manual", "backward", "group-1")]);

        var conflict = Assert.Single(result.Conflicts, value =>
            value.Code == "locked_group_planning_mode_conflict");
        Assert.Equal(TimelineConflictSeverity.Blocking, conflict.Severity);
        Assert.Empty(result.Operations);
    }

    [Fact]
    public void Mixed_three_contenders_never_overlap_repeated_regular_worker_reload_reservations()
    {
        static TimelineOperationInput Contender(
            string id,
            TimelinePlanningMode mode,
            string order,
            DateTimeOffset? latest = null) => new(
            id,
            TimeSpan.Zero,
            TimeSpan.FromMinutes(10),
            LoadUnloadDuration: TimeSpan.FromMinutes(5),
            LoadUnloadRequiresWorker: true,
            PriorityWorkFinishDate: new DateOnly(2026, 8, 11),
            PriorityOrderNumber: order,
            EarliestStart: Start,
            LatestFinish: latest,
            PlanningMode: mode,
            PlannedQuantity: 2);
        var result = Calculate(
            [
                new TimelineMachineBacklog("machine-backward",
                    [Contender("backward", TimelinePlanningMode.Backward, "SO-1", Utc(17))]),
                new TimelineMachineBacklog("machine-forward-1",
                    [Contender("forward-1", TimelinePlanningMode.Forward, "SO-2")]),
                new TimelineMachineBacklog("machine-forward-2",
                    [Contender("forward-2", TimelinePlanningMode.Forward, "SO-3")])
            ],
            [
                Calendar("machine-backward"),
                Calendar("machine-forward-1"),
                Calendar("machine-forward-2")
            ],
            resources: [new TimelineResourceCalendar(
                "regular-1", TimelineResourceRole.RegularWorker,
                [new TimelineWindow(Start, End)])]);

        Assert.Empty(result.Conflicts);
        Assert.Equal(3, result.Operations.Count);
        var reloads = result.Operations
            .SelectMany(operation => operation.LoadUnloadIntervals ?? [])
            .OrderBy(interval => interval.StartsAt)
            .ToArray();
        Assert.Equal(6, reloads.Length);
        for (var index = 1; index < reloads.Length; index++)
        {
            Assert.True(reloads[index - 1].EndsAt <= reloads[index].StartsAt,
                $"Regular worker overlap: {reloads[index - 1]} and {reloads[index]}.");
        }
    }

    [Fact]
    public void Mixed_large_backward_locked_group_returns_conflict_without_overflow()
    {
        var hugeDuration = TimeSpan.FromTicks(long.MaxValue / 2 + 1);
        TimelineOperationInput Huge(string id) => new(
            id, TimeSpan.Zero, hugeDuration,
            PriorityWorkFinishDate: new DateOnly(2026, 8, 11),
            PriorityOrderNumber: "SO-2",
            EarliestStart: Start,
            LatestFinish: Utc(17),
            PlanningMode: TimelinePlanningMode.Backward);

        var result = Calculate(
            [
                new TimelineMachineBacklog("machine-huge-1", [Huge("huge-1")]),
                new TimelineMachineBacklog("machine-huge-2", [Huge("huge-2")]),
                new TimelineMachineBacklog("machine-small", [
                    Operation("small-backward", 1, TimelinePlanningMode.Backward, Utc(17))]),
                new TimelineMachineBacklog("machine-forward", [
                    Operation("forward", 1, TimelinePlanningMode.Forward)])
            ],
            [
                Calendar("machine-huge-1"),
                Calendar("machine-huge-2"),
                Calendar("machine-small"),
                Calendar("machine-forward")
            ],
            [new TimelineDependency(
                "locked-huge", TimelineDependencyType.LockedSimultaneous,
                "huge-1", "huge-2", "huge-group")]);

        Assert.Contains(result.Operations, operation => operation.OperationId == "small-backward");
        Assert.Contains(result.Operations, operation => operation.OperationId == "forward");
        var conflict = Assert.Single(result.Conflicts, value =>
            value.Code == "backward_schedule_cannot_fit"
            && value.OperationIds.Contains("huge-1", StringComparer.Ordinal));
        Assert.Equal(["huge-1", "huge-2"], conflict.OperationIds);
    }

    private static TimelineCalculationResult Calculate(
        IReadOnlyList<TimelineMachineBacklog> backlogs,
        IReadOnlyList<TimelineMachineCalendar> calendars,
        IReadOnlyList<TimelineDependency>? dependencies = null,
        IReadOnlyList<TimelineResourceCalendar>? resources = null) =>
        new TimelineCalculationEngine().Calculate(new TimelineCalculationInput(
            Start,
            End,
            backlogs,
            calendars,
            new TimelineSetupCalendar([new TimelineWindow(Start, End)]),
            [],
            dependencies ?? [],
            resources));

    private static TimelineOperationInput Operation(
        string id,
        double productionHours,
        TimelinePlanningMode mode,
        DateTimeOffset? latest = null) => new(
            id,
            TimeSpan.Zero,
            TimeSpan.FromHours(productionHours),
            PriorityWorkFinishDate: new DateOnly(2026, 8, 11),
            PriorityOrderNumber: $"SO-{id}",
            EarliestStart: Start,
            LatestFinish: latest,
            PlanningMode: mode);

    private static TimelineMachineCalendar Calendar(string machineId) =>
        new(machineId, [new TimelineWindow(Start, End)]);

    private static DateTimeOffset Utc(int hour) =>
        new(2026, 8, 11, hour, 0, 0, TimeSpan.Zero);
}
