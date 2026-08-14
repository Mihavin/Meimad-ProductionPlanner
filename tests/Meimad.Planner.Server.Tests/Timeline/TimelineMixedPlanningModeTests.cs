using Meimad.Planner.Server.Domain.Timeline;

namespace Meimad.Planner.Server.Tests.Timeline;

public sealed class TimelineMixedPlanningModeTests
{
    private static readonly DateTimeOffset Start = Utc(8);
    private static readonly DateTimeOffset End = Utc(18);

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

    private static TimelineCalculationResult Calculate(
        IReadOnlyList<TimelineMachineBacklog> backlogs,
        IReadOnlyList<TimelineMachineCalendar> calendars,
        IReadOnlyList<TimelineDependency>? dependencies = null) =>
        new TimelineCalculationEngine().Calculate(new TimelineCalculationInput(
            Start,
            End,
            backlogs,
            calendars,
            new TimelineSetupCalendar([new TimelineWindow(Start, End)]),
            [],
            dependencies ?? []));

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
