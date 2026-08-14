using Meimad.Planner.Server.Domain.Timeline;

namespace Meimad.Planner.Server.Tests.Timeline;

public sealed class TimelineBackwardCalculationTests
{
    private static readonly DateTimeOffset Start = Utc(8);
    private static readonly DateTimeOffset End = Utc(17);

    [Fact]
    public void Backward_places_single_operation_at_latest_available_time()
    {
        var operation = Operation("op-1", productionHours: 2, latest: End);
        var result = Calculate([Backlog("m-1", operation)], [Calendar("m-1")]);

        var scheduled = Assert.Single(result.Operations);
        Assert.Equal(Utc(15), scheduled.StartsAt);
        Assert.Equal(End, scheduled.FinishesAt);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Backward_preserves_three_row_backlog_and_dependency_chain()
    {
        var operations = new[]
        {
            Operation("op-1", 1, End),
            Operation("op-2", 1, End),
            Operation("op-3", 1, End)
        };
        var input = Input(
            [Backlog("m-1", operations)],
            [Calendar("m-1")],
            [
                new TimelineDependency("d-1", TimelineDependencyType.Sequential, "op-1", "op-2"),
                new TimelineDependency("d-2", TimelineDependencyType.Sequential, "op-2", "op-3")
            ]);

        var result = new TimelineCalculationEngine().Calculate(input);

        Assert.Equal(["op-1", "op-2", "op-3"], operations.Select(value => value.OperationId));
        Assert.Equal(Utc(14), Result(result, "op-1").StartsAt);
        Assert.Equal(Utc(15), Result(result, "op-1").FinishesAt);
        Assert.Equal(Utc(15), Result(result, "op-2").StartsAt);
        Assert.Equal(Utc(16), Result(result, "op-2").FinishesAt);
        Assert.Equal(Utc(16), Result(result, "op-3").StartsAt);
        Assert.Equal(End, Result(result, "op-3").FinishesAt);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Backward_respects_cross_machine_sequential_chain()
    {
        var result = Calculate(
            [
                Backlog("m-1", Operation("op-1", 1, End)),
                Backlog("m-2", Operation("op-2", 1, End)),
                Backlog("m-3", Operation("op-3", 1, End))
            ],
            [Calendar("m-1"), Calendar("m-2"), Calendar("m-3")],
            [
                new TimelineDependency("d-1", TimelineDependencyType.Sequential, "op-1", "op-2"),
                new TimelineDependency("d-2", TimelineDependencyType.Sequential, "op-2", "op-3")
            ]);

        Assert.True(Result(result, "op-1").FinishesAt <= Result(result, "op-2").StartsAt);
        Assert.True(Result(result, "op-2").FinishesAt <= Result(result, "op-3").StartsAt);
        Assert.Equal(End, Result(result, "op-3").FinishesAt);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Backward_splits_work_across_calendar_break()
    {
        var result = Calculate(
            [Backlog("m-1", Operation("op-1", 6, End))],
            [new TimelineMachineCalendar("m-1", [Window(8, 12), Window(13, 17)])]);

        var operation = Assert.Single(result.Operations);
        Assert.Equal(Utc(10), operation.StartsAt);
        Assert.Equal(End, operation.FinishesAt);
        Assert.Equal(2, operation.ProductionIntervals.Count);
        Assert.Equal((Utc(10), Utc(12)),
            (operation.ProductionIntervals[0].StartsAt, operation.ProductionIntervals[0].EndsAt));
        Assert.Equal((Utc(13), End),
            (operation.ProductionIntervals[1].StartsAt, operation.ProductionIntervals[1].EndsAt));
    }

    [Fact]
    public void Backward_splits_work_around_machine_downtime()
    {
        var input = Input(
            [Backlog("m-1", Operation("op-1", 2, End))],
            [Calendar("m-1")],
            []);
        input = input with
        {
            Downtimes = [new TimelineDowntime(
                "down-1", "m-1", Utc(15), Utc(16), "Planned maintenance")]
        };

        var operation = Assert.Single(new TimelineCalculationEngine().Calculate(input).Operations);
        Assert.Equal((Utc(14), Utc(15)),
            (operation.ProductionIntervals[0].StartsAt, operation.ProductionIntervals[0].EndsAt));
        Assert.Equal((Utc(16), End),
            (operation.ProductionIntervals[1].StartsAt, operation.ProductionIntervals[1].EndsAt));
        Assert.Contains(operation.WaitingIntervals,
            value => value.Detail!.Contains("maintenance", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Backward_equal_due_date_gives_shorter_setup_latest_worker_slot()
    {
        var result = new TimelineCalculationEngine().Calculate(Input(
            [
                Backlog("m-long", Operation("long", 0, End, setupHours: 2, order: "SO-20")),
                Backlog("m-short", Operation("short", 0, End, setupHours: 1, order: "SO-10"))
            ],
            [Calendar("m-long", ["m-long"]), Calendar("m-short", ["m-short"])],
            [],
            [new TimelineResourceCalendar(
                "setup-1", TimelineResourceRole.SetupWorker, [Window(8, 17)], ["*"]) ]));

        Assert.Equal(Utc(16), Result(result, "short").StartsAt);
        Assert.Equal(End, Result(result, "short").FinishesAt);
        Assert.Equal(Utc(14), Result(result, "long").StartsAt);
        Assert.Equal(Utc(16), Result(result, "long").FinishesAt);
    }

    [Fact]
    public void Backward_resource_contention_gives_earlier_delivery_date_first()
    {
        var earlier = Operation("earlier", 0, End, setupHours: 1, order: "SO-20") with
        {
            PriorityWorkFinishDate = new DateOnly(2026, 8, 11)
        };
        var later = Operation("later", 0, End, setupHours: 1, order: "SO-10") with
        {
            PriorityWorkFinishDate = new DateOnly(2026, 8, 12)
        };
        var result = new TimelineCalculationEngine().Calculate(Input(
            [Backlog("m-earlier", earlier), Backlog("m-later", later)],
            [Calendar("m-earlier", ["m-earlier"]), Calendar("m-later", ["m-later"])],
            [],
            [new TimelineResourceCalendar(
                "setup-1", TimelineResourceRole.SetupWorker, [Window(16, 17)], ["*"]) ]));

        Assert.Equal(Utc(16), Result(result, "earlier").StartsAt);
        Assert.DoesNotContain(result.Operations, value => value.OperationId == "later");
        Assert.Contains(result.Conflicts, value =>
            value.Code == "backward_schedule_cannot_fit"
            && value.OperationIds.Contains("later", StringComparer.Ordinal));
    }

    [Fact]
    public void Backward_respects_qa_and_regular_worker_windows()
    {
        var operation = new TimelineOperationInput(
            "op-1",
            TimeSpan.Zero,
            TimeSpan.FromHours(1),
            QaDuration: TimeSpan.FromHours(1),
            LoadUnloadDuration: TimeSpan.FromHours(1),
            LoadUnloadRequiresWorker: true,
            PriorityWorkFinishDate: new DateOnly(2026, 8, 11),
            PriorityOrderNumber: "SO-1",
            EarliestStart: Start,
            LatestFinish: End);
        var result = new TimelineCalculationEngine().Calculate(Input(
            [Backlog("m-1", operation)],
            [Calendar("m-1")],
            [],
            [
                new TimelineResourceCalendar(
                    "qa-1", TimelineResourceRole.QaWorker, [Window(13, 14)]),
                new TimelineResourceCalendar(
                    "regular-1", TimelineResourceRole.RegularWorker, [Window(15, 16)])
            ]));

        var scheduled = Result(result, "op-1");
        Assert.Equal((Utc(13), Utc(14)),
            (scheduled.QaIntervals![0].StartsAt, scheduled.QaIntervals[0].EndsAt));
        Assert.Equal((Utc(15), Utc(16)),
            (scheduled.LoadUnloadIntervals![0].StartsAt, scheduled.LoadUnloadIntervals[0].EndsAt));
        Assert.Equal((Utc(16), End),
            (scheduled.ProductionIntervals[0].StartsAt, scheduled.ProductionIntervals[0].EndsAt));
    }

    [Fact]
    public void Backward_places_periodic_load_unload_before_each_production_run()
    {
        var operation = new TimelineOperationInput(
            "op-periodic",
            TimeSpan.Zero,
            TimeSpan.FromMinutes(10),
            LoadUnloadDuration: TimeSpan.FromMinutes(5),
            PriorityWorkFinishDate: new DateOnly(2026, 8, 11),
            PriorityOrderNumber: "SO-1",
            EarliestStart: Start,
            LatestFinish: End,
            PlanningMode: TimelinePlanningMode.Backward,
            PlannedQuantity: 5,
            AutomaticLoading: true,
            LoadUnloadEveryNParts: 2);

        var result = Calculate(
            [Backlog("m-1", operation)],
            [Calendar("m-1")]);

        Assert.Empty(result.Conflicts);
        var scheduled = Assert.Single(result.Operations);
        Assert.Equal(Utc(15, 55), scheduled.StartsAt);
        Assert.Equal(End, scheduled.FinishesAt);
        Assert.Equal(
            [
                (Utc(15, 55), Utc(16)),
                (Utc(16, 20), Utc(16, 25)),
                (Utc(16, 45), Utc(16, 50))
            ],
            scheduled.LoadUnloadIntervals!.Select(interval =>
                (interval.StartsAt, interval.EndsAt)));
        Assert.Equal(
            [
                (Utc(16), Utc(16, 20)),
                (Utc(16, 25), Utc(16, 45)),
                (Utc(16, 50), Utc(17))
            ],
            scheduled.ProductionIntervals.Select(interval =>
                (interval.StartsAt, interval.EndsAt)));
        Assert.Equal(
            ["Part reload 1/3", "Part reload 2/3", "Part reload 3/3"],
            scheduled.LoadUnloadIntervals!.Select(interval =>
                interval.Detail!.Split(';')[0]));
    }

    [Fact]
    public void Backward_day_shift_only_production_uses_day_shift_calendar()
    {
        var operation = Operation("op-1", 2, End) with { DayShiftOnly = true };
        var input = Input([Backlog("m-1", operation)], [Calendar("m-1")], []);
        input = input with
        {
            DayShiftCalendars = [new TimelineMachineCalendar("m-1", [Window(8, 12)])]
        };

        var scheduled = Assert.Single(new TimelineCalculationEngine().Calculate(input).Operations);
        Assert.Equal(Utc(10), scheduled.StartsAt);
        Assert.Equal(Utc(12), scheduled.FinishesAt);
    }

    [Fact]
    public void Backward_equal_due_and_duration_uses_natural_order_number()
    {
        var result = new TimelineCalculationEngine().Calculate(Input(
            [
                Backlog("m-10", Operation("order-10", 0, End, setupHours: 1, order: "SO-10")),
                Backlog("m-2", Operation("order-2", 0, End, setupHours: 1, order: "SO-2"))
            ],
            [Calendar("m-10", ["m-10"]), Calendar("m-2", ["m-2"])],
            [],
            [new TimelineResourceCalendar(
                "setup-1", TimelineResourceRole.SetupWorker, [Window(8, 17)], ["*"]) ]));

        Assert.Equal(Utc(16), Result(result, "order-2").StartsAt);
        Assert.Equal(Utc(15), Result(result, "order-10").StartsAt);
    }

    [Fact]
    public void Backward_locked_group_shares_start_and_finish_and_reserves_shorter_machine()
    {
        var result = Calculate(
            [
                Backlog("m-1", Operation("short", 1, End)),
                Backlog("m-2", Operation("long", 2, End))
            ],
            [Calendar("m-1"), Calendar("m-2")],
            [new TimelineDependency(
                "locked", TimelineDependencyType.LockedSimultaneous,
                "short", "long", "group-1")]);

        var shortOperation = Result(result, "short");
        var longOperation = Result(result, "long");
        Assert.Equal(longOperation.StartsAt, shortOperation.StartsAt);
        Assert.Equal(longOperation.FinishesAt, shortOperation.FinishesAt);
        Assert.Equal(shortOperation.StartsAt, shortOperation.ProductionIntervals[0].StartsAt);
        Assert.Equal(longOperation.StartsAt, longOperation.ProductionIntervals[0].StartsAt);
        var reservation = Assert.Single(shortOperation.ReservedIntervals);
        Assert.Equal(shortOperation.ProductionIntervals[^1].EndsAt, reservation.StartsAt);
        Assert.Equal(shortOperation.FinishesAt, reservation.EndsAt);
    }

    [Fact]
    public void Backward_locked_group_moves_to_previous_common_start_when_latest_one_cannot_fit()
    {
        var result = Calculate(
            [
                Backlog("m-short", Operation("short", 1, End)),
                Backlog("m-long", Operation("long", 2, End))
            ],
            [
                new TimelineMachineCalendar(
                    "m-short", [Window(14, 15), Window(16, 17)]),
                Calendar("m-long")
            ],
            [new TimelineDependency(
                "locked", TimelineDependencyType.LockedSimultaneous,
                "short", "long", "group-1")]);

        var shortOperation = Result(result, "short");
        var longOperation = Result(result, "long");
        Assert.Equal(Utc(14), shortOperation.StartsAt);
        Assert.Equal(shortOperation.StartsAt, longOperation.StartsAt);
        Assert.Equal(Utc(16), shortOperation.FinishesAt);
        Assert.Equal(shortOperation.FinishesAt, longOperation.FinishesAt);
        var reservation = Assert.Single(shortOperation.ReservedIntervals);
        Assert.Equal(Utc(15), reservation.StartsAt);
        Assert.Equal(Utc(16), reservation.EndsAt);
    }

    [Fact]
    public void Backward_returns_structured_conflict_when_operation_cannot_fit()
    {
        var result = Calculate(
            [Backlog("m-1", Operation("op-1", 10, End))],
            [Calendar("m-1")]);

        Assert.Empty(result.Operations);
        Assert.Contains(result.Conflicts, value => value.Code == "backward_schedule_cannot_fit");
    }

    [Fact]
    public void Backward_does_not_show_later_backlog_row_when_earlier_row_cannot_fit()
    {
        var result = Calculate(
            [Backlog(
                "m-1",
                Operation("earlier", 10, End),
                Operation("later", 1, End))],
            [Calendar("m-1")]);

        Assert.Empty(result.Operations);
        Assert.Contains(result.Conflicts, value =>
            value.Code == "backward_schedule_cannot_fit"
            && value.OperationIds.Contains("earlier", StringComparer.Ordinal));
        Assert.Contains(result.Conflicts, value =>
            value.Code == "dependency_unresolved"
            && value.OperationIds.Contains("later", StringComparer.Ordinal));
    }

    [Fact]
    public void Backward_recalculates_unrelated_resource_after_failed_backlog_chain_is_removed()
    {
        var result = new TimelineCalculationEngine().Calculate(Input(
            [
                Backlog(
                    "m-chain",
                    Operation("a-earlier", 10, End),
                    Operation("b-later", 0, End, setupHours: 1)),
                Backlog("m-independent", Operation("c-independent", 0, End, setupHours: 1))
            ],
            [
                Calendar("m-chain", ["m-chain"]),
                Calendar("m-independent", ["m-independent"])
            ],
            [],
            [new TimelineResourceCalendar(
                "setup-1", TimelineResourceRole.SetupWorker, [Window(8, 17)], ["*"]) ]));

        var independent = Result(result, "c-independent");
        Assert.Equal(Utc(16), independent.StartsAt);
        Assert.Equal(End, independent.FinishesAt);
        Assert.DoesNotContain(result.Operations, value => value.OperationId == "a-earlier");
        Assert.DoesNotContain(result.Operations, value => value.OperationId == "b-later");
    }

    private static TimelineCalculationResult Calculate(
        IReadOnlyList<TimelineMachineBacklog> backlogs,
        IReadOnlyList<TimelineMachineCalendar> calendars,
        IReadOnlyList<TimelineDependency>? dependencies = null) =>
        new TimelineCalculationEngine().Calculate(Input(backlogs, calendars, dependencies ?? []));

    private static TimelineCalculationInput Input(
        IReadOnlyList<TimelineMachineBacklog> backlogs,
        IReadOnlyList<TimelineMachineCalendar> calendars,
        IReadOnlyList<TimelineDependency> dependencies,
        IReadOnlyList<TimelineResourceCalendar>? resources = null) => new(
        Start,
        End,
        backlogs,
        calendars,
        new TimelineSetupCalendar([Window(8, 17)]),
        [],
        dependencies,
        resources,
        calendars);

    private static TimelineMachineBacklog Backlog(
        string machineId,
        params TimelineOperationInput[] operations) => new(machineId, operations);

    private static TimelineMachineCalendar Calendar(
        string machineId,
        IReadOnlyList<string>? skills = null) =>
        new(machineId, [Window(8, 17)], skills);

    private static TimelineOperationInput Operation(
        string id,
        double productionHours,
        DateTimeOffset latest,
        double setupHours = 0,
        string order = "SO-1") => new(
        id,
        TimeSpan.FromHours(setupHours),
        TimeSpan.FromHours(productionHours),
        PriorityWorkFinishDate: new DateOnly(2026, 8, 11),
        PriorityOrderNumber: order,
        EarliestStart: Start,
        LatestFinish: latest,
        PlanningMode: TimelinePlanningMode.Backward);

    private static TimelineOperationResult Result(TimelineCalculationResult result, string operationId) =>
        Assert.Single(result.Operations, value => value.OperationId == operationId);

    private static TimelineWindow Window(int startHour, int endHour) =>
        new(Utc(startHour), Utc(endHour));

    private static DateTimeOffset Utc(int hour, int minute = 0) =>
        new(2026, 8, 11, hour, minute, 0, TimeSpan.Zero);
}
