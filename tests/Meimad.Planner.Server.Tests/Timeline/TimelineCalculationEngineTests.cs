using System.Text.Json;
using Meimad.Planner.Server.Domain.Timeline;

namespace Meimad.Planner.Server.Tests.Timeline;

public sealed class TimelineCalculationEngineTests
{
    private static readonly DateTimeOffset HorizonStart = Utc(8);
    private static readonly DateTimeOffset HorizonEnd = Utc(17);

    [Fact]
    public void Calculates_setup_production_idle_and_downtime_without_changing_backlog_order()
    {
        var operations = new[]
        {
            Operation("op-a", setupHours: 1, productionHours: 2),
            Operation("op-b", setupHours: 0.5, productionHours: 1)
        };
        var input = Input(
            [Backlog("machine-1", operations)],
            [Calendar("machine-1", Window(8, 17))],
            SetupCalendar(Window(9, 16)),
            [new TimelineDowntime("down-1", "machine-1", Utc(10), Utc(10, 30), "Maintenance")],
            []);

        var result = new TimelineCalculationEngine().Calculate(input);

        Assert.Empty(result.Conflicts);
        Assert.Equal(["op-a", "op-b"], operations.Select(operation => operation.OperationId));
        Assert.Equal(["op-a", "op-b"], result.Operations.Select(operation => operation.OperationId));
        AssertIntervals(
            result.Operations[0].SetupIntervals,
            (TimelineIntervalType.Setup, Utc(9), Utc(10)));
        AssertIntervals(
            result.Operations[0].ProductionIntervals,
            (TimelineIntervalType.Production, Utc(10, 30), Utc(12, 30)));
        AssertIntervals(
            result.Operations[1].SetupIntervals,
            (TimelineIntervalType.Setup, Utc(12, 30), Utc(13)));
        AssertIntervals(
            result.Operations[1].ProductionIntervals,
            (TimelineIntervalType.Production, Utc(13), Utc(14)));

        var machine = Assert.Single(result.Machines);
        AssertIntervals(
            machine.Intervals.Where(interval => interval.Type == TimelineIntervalType.Idle).ToArray(),
            (TimelineIntervalType.Idle, Utc(14), Utc(17)));
        Assert.Contains(result.Operations[0].WaitingIntervals, interval =>
            interval.StartsAt == Utc(8)
            && interval.EndsAt == Utc(9)
            && interval.Detail!.Contains("setup calendar", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Operations[0].WaitingIntervals, interval =>
            interval.StartsAt == Utc(10)
            && interval.EndsAt == Utc(10, 30)
            && interval.Detail!.Contains("maintenance", StringComparison.OrdinalIgnoreCase));
        var downtime = Assert.Single(machine.Intervals, interval =>
            interval.Type == TimelineIntervalType.Downtime);
        Assert.Equal(Utc(10), downtime.StartsAt);
        Assert.Equal(Utc(10, 30), downtime.EndsAt);
        Assert.Equal("op-a", downtime.OperationId);
        Assert.Equal("Operation delayed by Maintenance", downtime.Detail);
    }

    [Fact]
    public void Splits_setup_and_production_across_explicit_calendar_windows()
    {
        var input = Input(
            [Backlog("machine-1", [Operation("op-a", 2, 1)])],
            [Calendar("machine-1", Window(8, 12), Window(13, 17))],
            SetupCalendar(Window(8, 9), Window(13, 14)),
            [],
            []);

        var result = new TimelineCalculationEngine().Calculate(input);

        Assert.Empty(result.Conflicts);
        var operation = Assert.Single(result.Operations);
        AssertIntervals(
            operation.SetupIntervals,
            (TimelineIntervalType.Setup, Utc(8), Utc(9)),
            (TimelineIntervalType.Setup, Utc(13), Utc(14)));
        AssertIntervals(
            operation.ProductionIntervals,
            (TimelineIntervalType.Production, Utc(14), Utc(15)));
        Assert.Equal(Utc(8), operation.StartsAt);
        Assert.Equal(Utc(15), operation.FinishesAt);
    }

    [Fact]
    public void Splits_production_around_downtime_without_creating_an_overlap()
    {
        var input = Input(
            [Backlog("machine-1", [Operation("op-a", 0, 2)])],
            [Calendar("machine-1", Window(8, 12))],
            SetupCalendar(Window(8, 12)),
            [new TimelineDowntime("down-1", "machine-1", Utc(9), Utc(10), "Service")],
            []);

        var result = new TimelineCalculationEngine().Calculate(input);

        Assert.Empty(result.Conflicts);
        AssertIntervals(
            Assert.Single(result.Operations).ProductionIntervals,
            (TimelineIntervalType.Production, Utc(8), Utc(9)),
            (TimelineIntervalType.Production, Utc(10), Utc(11)));
        Assert.Contains(Assert.Single(result.Operations).WaitingIntervals, interval =>
            interval.StartsAt == Utc(9)
            && interval.EndsAt == Utc(10)
            && interval.Detail!.Contains("Service", StringComparison.Ordinal));
    }

    [Fact]
    public void Starts_immediately_when_machine_and_resources_are_available()
    {
        var result = new TimelineCalculationEngine().Calculate(Input(
            [Backlog("machine-1", [Operation("op-now", 1, 1)])],
            [new TimelineMachineCalendar("machine-1", [Window(8, 17)], ["milling"])],
            SetupCalendar(Window(8, 17)), [], [],
            [new TimelineResourceCalendar("setup-1", TimelineResourceRole.SetupWorker,
                [Window(8, 17)], ["milling"])]));

        Assert.Empty(result.Conflicts);
        var operation = Assert.Single(result.Operations);
        Assert.Equal(Utc(8), operation.StartsAt);
        Assert.Equal(Utc(10), operation.FinishesAt);
        Assert.Empty(operation.WaitingIntervals);
    }

    [Theory]
    [InlineData("Planned maintenance: spindle service", "maintenance")]
    [InlineData("Breakdown: hydraulic alarm", "breakdown")]
    public void Initial_downtime_moves_operation_to_nearest_available_time_with_reason(
        string reason,
        string expectedReason)
    {
        var result = new TimelineCalculationEngine().Calculate(Input(
            [Backlog("machine-1", [Operation("op-delayed", 0, 1)])],
            [Calendar("machine-1", Window(8, 17))],
            SetupCalendar(Window(8, 17)),
            [new TimelineDowntime("down-1", "machine-1", Utc(8), Utc(10), reason)],
            []));

        Assert.Empty(result.Conflicts);
        var operation = Assert.Single(result.Operations);
        Assert.Equal(Utc(10), operation.StartsAt);
        var wait = Assert.Single(operation.WaitingIntervals);
        Assert.Equal(Utc(8), wait.StartsAt);
        Assert.Equal(Utc(10), wait.EndsAt);
        Assert.Contains(expectedReason, wait.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_working_machine_time_is_visible_before_nearest_shift_start()
    {
        var result = new TimelineCalculationEngine().Calculate(Input(
            [Backlog("machine-1", [Operation("op-next-shift", 0, 1)])],
            [Calendar("machine-1", Window(12, 17))],
            SetupCalendar(Window(8, 17)), [], []));

        Assert.Empty(result.Conflicts);
        var operation = Assert.Single(result.Operations);
        Assert.Equal(Utc(12), operation.StartsAt);
        Assert.Contains(operation.WaitingIntervals, interval =>
            interval.StartsAt == Utc(8)
            && interval.EndsAt == Utc(12)
            && interval.Detail!.Contains("machine working calendar", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 8)]
    [InlineData(2, 8)]
    public void Preserves_dependency_semantics(
        int dependencyTypeValue,
        int expectedSecondStartHour)
    {
        var dependencyType = (TimelineDependencyType)dependencyTypeValue;
        var dependency = new TimelineDependency(
            "dependency-1",
            dependencyType,
            "op-a",
            "op-b");
        var input = Input(
            [
                Backlog("machine-1", [Operation("op-a", 0, 2)]),
                Backlog("machine-2", [Operation("op-b", 0, 1)])
            ],
            [
                Calendar("machine-1", Window(8, 17)),
                Calendar("machine-2", Window(8, 17))
            ],
            SetupCalendar(Window(8, 17)),
            [],
            [dependency]);

        var result = new TimelineCalculationEngine().Calculate(input);

        Assert.Empty(result.Conflicts);
        Assert.Equal(
            Utc(expectedSecondStartHour),
            result.Operations.Single(operation => operation.OperationId == "op-b").StartsAt);
    }

    [Fact]
    public void Locked_simultaneous_operations_share_start_finish_and_reserve_shorter_machine()
    {
        var input = Input(
            [
                Backlog("machine-1", [Operation("op-long", 1, 2)]),
                Backlog("machine-2", [Operation("op-short", 0.5, 1)])
            ],
            [
                Calendar("machine-1", Window(8, 17)),
                Calendar("machine-2", Window(8, 17))
            ],
            SetupCalendar(Window(8, 17)),
            [],
            [new TimelineDependency(
                "locked-1",
                TimelineDependencyType.LockedSimultaneous,
                "op-long",
                "op-short",
                "group-1")]);

        var result = new TimelineCalculationEngine().Calculate(input);

        Assert.Empty(result.Conflicts);
        var longOperation = result.Operations.Single(operation => operation.OperationId == "op-long");
        var shortOperation = result.Operations.Single(operation => operation.OperationId == "op-short");
        Assert.Equal(Utc(8), longOperation.StartsAt);
        Assert.Equal(longOperation.StartsAt, shortOperation.StartsAt);
        Assert.Equal(Utc(11), longOperation.FinishesAt);
        Assert.Equal(longOperation.FinishesAt, shortOperation.FinishesAt);
        Assert.Empty(longOperation.ReservedIntervals);
        AssertIntervals(
            shortOperation.ReservedIntervals,
            (TimelineIntervalType.Reserved, Utc(9, 30), Utc(11)));
    }

    [Fact]
    public void Locked_simultaneous_group_retries_at_common_worker_availability()
    {
        var result = new TimelineCalculationEngine().Calculate(Input(
            [
                Backlog("machine-a", [Operation("op-a", 1, 1)]),
                Backlog("machine-b", [Operation("op-b", 1, 2)])
            ],
            [
                new TimelineMachineCalendar("machine-a", [Window(8, 17)], ["milling"]),
                new TimelineMachineCalendar("machine-b", [Window(8, 17)], ["milling"])
            ],
            SetupCalendar(Window(8, 17)), [],
            [new TimelineDependency("locked", TimelineDependencyType.LockedSimultaneous,
                "op-a", "op-b", "group")],
            [
                new TimelineResourceCalendar("setup-a", TimelineResourceRole.SetupWorker,
                    [Window(10, 17)], ["milling"]),
                new TimelineResourceCalendar("setup-b", TimelineResourceRole.SetupWorker,
                    [Window(10, 17)], ["milling"])
            ]));

        Assert.Empty(result.Conflicts);
        Assert.All(result.Operations, operation => Assert.Equal(Utc(10), operation.StartsAt));
        Assert.All(result.Operations, operation => Assert.Equal(Utc(13), operation.FinishesAt));
    }

    [Fact]
    public void Sequential_operations_on_same_machine_follow_the_manual_backlog_without_dependency_waiting()
    {
        var operations = new[] { Operation("op-10", 0, 2), Operation("op-20", 0, 1) };
        var result = new TimelineCalculationEngine().Calculate(Input(
            [Backlog("machine-1", operations)],
            [Calendar("machine-1", Window(8, 17))],
            SetupCalendar(Window(8, 17)),
            [],
            [new TimelineDependency("10-20", TimelineDependencyType.Sequential, "op-10", "op-20")]));

        Assert.Empty(result.Conflicts);
        Assert.Equal(["op-10", "op-20"], operations.Select(operation => operation.OperationId));
        Assert.Equal(Utc(10), result.Operations.Single(operation => operation.OperationId == "op-20").StartsAt);
        Assert.Empty(result.Operations.Single(operation => operation.OperationId == "op-20").WaitingIntervals);
    }

    [Fact]
    public void Sequential_child_on_free_different_machine_waits_for_parent_and_exposes_waiting_interval()
    {
        var result = new TimelineCalculationEngine().Calculate(Input(
            [
                Backlog("machine-1", [Operation("op-10", 0, 6)]),
                Backlog("machine-5", [Operation("op-20", 0, 1)])
            ],
            [Calendar("machine-1", Window(8, 17)), Calendar("machine-5", Window(8, 17))],
            SetupCalendar(Window(8, 17)),
            [],
            [new TimelineDependency("10-20", TimelineDependencyType.Sequential, "op-10", "op-20")]));

        Assert.Empty(result.Conflicts);
        var child = result.Operations.Single(operation => operation.OperationId == "op-20");
        Assert.Equal(Utc(14), child.StartsAt);
        AssertIntervals(
            child.WaitingIntervals,
            (TimelineIntervalType.Waiting, Utc(8), Utc(14)));
        Assert.Equal("op-10", Assert.Single(child.WaitingIntervals).Detail);
    }

    [Fact]
    public void Multiple_children_wait_for_the_same_sequential_parent_without_reordering_backlogs()
    {
        var result = new TimelineCalculationEngine().Calculate(Input(
            [
                Backlog("machine-1", [Operation("parent", 0, 4)]),
                Backlog("machine-2", [Operation("child-a", 0, 1)]),
                Backlog("machine-3", [Operation("child-b", 0, 1)])
            ],
            [
                Calendar("machine-1", Window(8, 17)),
                Calendar("machine-2", Window(8, 17)),
                Calendar("machine-3", Window(8, 17))
            ],
            SetupCalendar(Window(8, 17)),
            [],
            [
                new TimelineDependency("parent-a", TimelineDependencyType.Sequential, "parent", "child-a"),
                new TimelineDependency("parent-b", TimelineDependencyType.Sequential, "parent", "child-b")
            ]));

        Assert.Empty(result.Conflicts);
        Assert.All(result.Operations.Where(operation => operation.OperationId.StartsWith("child", StringComparison.Ordinal)),
            child => Assert.Equal(Utc(12), child.StartsAt));
    }

    [Fact]
    public void Child_with_multiple_sequential_parents_waits_for_the_latest_finish()
    {
        var result = new TimelineCalculationEngine().Calculate(Input(
            [
                Backlog("machine-1", [Operation("parent-a", 0, 2)]),
                Backlog("machine-2", [Operation("parent-b", 0, 4)]),
                Backlog("machine-3", [Operation("child", 0, 1)])
            ],
            [
                Calendar("machine-1", Window(8, 17)),
                Calendar("machine-2", Window(8, 17)),
                Calendar("machine-3", Window(8, 17))
            ],
            SetupCalendar(Window(8, 17)),
            [],
            [
                new TimelineDependency("a-child", TimelineDependencyType.Sequential, "parent-a", "child"),
                new TimelineDependency("b-child", TimelineDependencyType.Sequential, "parent-b", "child")
            ]));

        Assert.Empty(result.Conflicts);
        var child = result.Operations.Single(operation => operation.OperationId == "child");
        Assert.Equal(Utc(12), child.StartsAt);
        Assert.Equal("parent-b", Assert.Single(child.WaitingIntervals).Detail);
    }

    [Fact]
    public void Missing_parent_dependency_returns_conflict_and_does_not_schedule_child()
    {
        var result = new TimelineCalculationEngine().Calculate(Input(
            [Backlog("machine-1", [Operation("child", 0, 1)])],
            [Calendar("machine-1", Window(8, 17))],
            SetupCalendar(Window(8, 17)),
            [],
            [new TimelineDependency("missing-parent", TimelineDependencyType.Sequential, "parent", "child")]));

        Assert.Empty(result.Operations);
        Assert.Contains(result.Conflicts, conflict => conflict.Code == "invalid_dependency_reference");
    }

    [Fact]
    public void Recalculation_uses_reordered_manual_backlog_without_mutating_either_input()
    {
        var firstOrder = new[] { Operation("op-a", 0, 1), Operation("op-b", 0, 1) };
        var secondOrder = new[] { Operation("op-b", 0, 1), Operation("op-a", 0, 1) };
        var engine = new TimelineCalculationEngine();
        var first = engine.Calculate(Input(
            [Backlog("machine-1", firstOrder)],
            [Calendar("machine-1", Window(8, 17))], SetupCalendar(Window(8, 17)), [], []));
        var second = engine.Calculate(Input(
            [Backlog("machine-1", secondOrder)],
            [Calendar("machine-1", Window(8, 17))], SetupCalendar(Window(8, 17)), [], []));

        Assert.Equal(["op-a", "op-b"], firstOrder.Select(operation => operation.OperationId));
        Assert.Equal(["op-b", "op-a"], secondOrder.Select(operation => operation.OperationId));
        Assert.Equal(Utc(8), first.Operations.Single(operation => operation.OperationId == "op-a").StartsAt);
        Assert.Equal(Utc(8), second.Operations.Single(operation => operation.OperationId == "op-b").StartsAt);
    }

    [Fact]
    public void Dependency_cycle_is_reported_without_reordering_or_partial_schedule()
    {
        var input = Input(
            [
                Backlog("machine-1", [Operation("op-a", 0, 1)]),
                Backlog("machine-2", [Operation("op-b", 0, 1)])
            ],
            [
                Calendar("machine-1", Window(8, 17)),
                Calendar("machine-2", Window(8, 17))
            ],
            SetupCalendar(Window(8, 17)),
            [],
            [
                new TimelineDependency("seq-a-b", TimelineDependencyType.Sequential, "op-a", "op-b"),
                new TimelineDependency("seq-b-a", TimelineDependencyType.Sequential, "op-b", "op-a")
            ]);

        var result = new TimelineCalculationEngine().Calculate(input);

        Assert.Empty(result.Operations);
        var conflict = Assert.Single(result.Conflicts, value => value.Code == "dependency_cycle");
        Assert.Equal(TimelineConflictSeverity.Blocking, conflict.Severity);
        Assert.Equal(["op-a", "op-b"], conflict.OperationIds);
    }

    [Fact]
    public void Insufficient_availability_reports_conflict_instead_of_moving_an_operation_elsewhere()
    {
        var input = Input(
            [Backlog("machine-1", [Operation("op-a", 0, 2)])],
            [Calendar("machine-1", Window(8, 9))],
            SetupCalendar(Window(8, 17)),
            [],
            []);

        var result = new TimelineCalculationEngine().Calculate(input);

        Assert.Empty(result.Operations);
        var conflict = Assert.Single(result.Conflicts, value =>
            value.Code == "insufficient_availability");
        Assert.Equal(["machine-1"], conflict.MachineIds);
    }

    [Fact]
    public void Failed_predecessor_propagates_to_all_descendants_without_false_cycle()
    {
        var input = Input(
            [Backlog("machine-1", [
                Operation("op-a", 0, 2),
                Operation("op-b", 0, 1),
                Operation("op-c", 0, 1)])],
            [Calendar("machine-1", Window(8, 9))],
            SetupCalendar(Window(8, 17)),
            [],
            [
                new TimelineDependency("a-to-b", TimelineDependencyType.Sequential, "op-a", "op-b"),
                new TimelineDependency("b-to-c", TimelineDependencyType.Sequential, "op-b", "op-c")
            ]);

        var result = new TimelineCalculationEngine().Calculate(input);

        Assert.Empty(result.Operations);
        Assert.Single(result.Conflicts, value => value.Code == "insufficient_availability");
        Assert.Equal(2, result.Conflicts.Count(value => value.Code == "dependency_unresolved"));
        Assert.DoesNotContain(result.Conflicts, value => value.Code == "dependency_cycle");
    }

    [Fact]
    public void Calculation_is_deterministic_and_does_not_mutate_input_collections()
    {
        var operations = new List<TimelineOperationInput>
        {
            Operation("z-operation", 0, 1),
            Operation("a-operation", 0, 1)
        };
        var dependencies = new List<TimelineDependency>
        {
            new("parallel", TimelineDependencyType.ParallelCapable, "z-operation", "a-operation")
        };
        var input = Input(
            [Backlog("machine-1", operations)],
            [Calendar("machine-1", Window(8, 17))],
            SetupCalendar(Window(8, 17)),
            [],
            dependencies);
        var engine = new TimelineCalculationEngine();

        var first = engine.Calculate(input);
        var second = engine.Calculate(input);

        Assert.Equal(
            JsonSerializer.Serialize(first),
            JsonSerializer.Serialize(second));
        Assert.Equal(["z-operation", "a-operation"], operations.Select(value => value.OperationId));
        Assert.Equal("parallel", dependencies.Single().DependencyId);
        Assert.Equal(["z-operation", "a-operation"], first.Operations.Select(value => value.OperationId));
        Assert.Equal(Utc(8), first.Operations[0].StartsAt);
        Assert.Equal(Utc(9), first.Operations[1].StartsAt);
    }

    [Fact]
    public void Locked_operations_on_same_machine_are_conflicts_not_overlapping_work()
    {
        var input = Input(
            [Backlog("machine-1", [Operation("op-a", 0, 1), Operation("op-b", 0, 2)])],
            [Calendar("machine-1", Window(8, 17))],
            SetupCalendar(Window(8, 17)),
            [],
            [new TimelineDependency(
                "locked",
                TimelineDependencyType.LockedSimultaneous,
                "op-a",
                "op-b",
                "group")]);

        var result = new TimelineCalculationEngine().Calculate(input);

        Assert.Empty(result.Operations);
        Assert.Contains(result.Conflicts, value => value.Code == "simultaneous_same_machine");
    }

    [Fact]
    public void Setup_qa_worker_load_and_production_are_calculated_in_sequence()
    {
        var operation = new TimelineOperationInput(
            "op-phases", TimeSpan.FromHours(1), TimeSpan.FromHours(1),
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(30), true);
        var result = new TimelineCalculationEngine().Calculate(Input(
            [Backlog("machine-1", [operation])],
            [Calendar("machine-1", Window(8, 17))],
            SetupCalendar(Window(8, 17)), [], [],
            [
                Resource("setup", TimelineResourceRole.SetupWorker, Window(8, 17)),
                Resource("qa", TimelineResourceRole.QaWorker, Window(10, 17)),
                Resource("regular", TimelineResourceRole.RegularWorker, Window(8, 17))
            ]));

        Assert.Empty(result.Conflicts);
        var scheduled = Assert.Single(result.Operations);
        AssertIntervals(scheduled.SetupIntervals, (TimelineIntervalType.Setup, Utc(8), Utc(9)));
        AssertIntervals(scheduled.QaIntervals!, (TimelineIntervalType.Qa, Utc(10), Utc(11)));
        AssertIntervals(scheduled.LoadUnloadIntervals!,
            (TimelineIntervalType.LoadUnload, Utc(11), Utc(11, 30)));
        AssertIntervals(scheduled.ProductionIntervals,
            (TimelineIntervalType.Production, Utc(11, 30), Utc(12, 30)));
    }

    [Fact]
    public void Day_shift_only_operation_is_intersected_with_the_day_shift_calendar()
    {
        var operation = new TimelineOperationInput(
            "op-day", TimeSpan.Zero, TimeSpan.FromHours(1), DayShiftOnly: true);
        var result = new TimelineCalculationEngine().Calculate(Input(
            [Backlog("machine-1", [operation])],
            [Calendar("machine-1", Window(8, 17))],
            SetupCalendar(Window(8, 17)), [], [], null,
            [Calendar("machine-1", Window(12, 17))]));

        Assert.Empty(result.Conflicts);
        AssertIntervals(Assert.Single(result.Operations).ProductionIntervals,
            (TimelineIntervalType.Production, Utc(12), Utc(13)));
    }

    [Fact]
    public void Day_shift_only_constrains_production_but_allows_setup_before_day_shift()
    {
        var operation = new TimelineOperationInput(
            "op-day-setup", TimeSpan.FromHours(1), TimeSpan.FromHours(1), DayShiftOnly: true);
        var result = new TimelineCalculationEngine().Calculate(Input(
            [Backlog("machine-1", [operation])],
            [new TimelineMachineCalendar("machine-1", [Window(8, 17)], ["milling"])],
            SetupCalendar(Window(8, 17)), [], [],
            [new TimelineResourceCalendar("setup", TimelineResourceRole.SetupWorker,
                [Window(8, 17)], ["milling"])],
            [Calendar("machine-1", Window(12, 17))]));

        Assert.Empty(result.Conflicts);
        var scheduled = Assert.Single(result.Operations);
        AssertIntervals(scheduled.SetupIntervals,
            (TimelineIntervalType.Setup, Utc(8), Utc(9)));
        AssertIntervals(scheduled.ProductionIntervals,
            (TimelineIntervalType.Production, Utc(12), Utc(13)));
        Assert.Contains(scheduled.WaitingIntervals, interval =>
            interval.StartsAt == Utc(9)
            && interval.EndsAt == Utc(12)
            && interval.Detail!.Contains("day-shift", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Worker_required_load_unload_waits_for_regular_worker_availability()
    {
        var operation = new TimelineOperationInput(
            "op-load", TimeSpan.Zero, TimeSpan.FromHours(1),
            LoadUnloadDuration: TimeSpan.FromMinutes(30), LoadUnloadRequiresWorker: true);
        var result = new TimelineCalculationEngine().Calculate(Input(
            [Backlog("machine-1", [operation])],
            [Calendar("machine-1", Window(8, 17))],
            SetupCalendar(Window(8, 17)), [], [],
            [Resource("regular", TimelineResourceRole.RegularWorker, Window(12, 17))]));

        Assert.Empty(result.Conflicts);
        var scheduled = Assert.Single(result.Operations);
        AssertIntervals(scheduled.LoadUnloadIntervals!,
            (TimelineIntervalType.LoadUnload, Utc(12), Utc(12, 30)));
        AssertIntervals(scheduled.ProductionIntervals,
            (TimelineIntervalType.Production, Utc(12, 30), Utc(13, 30)));
    }

    [Fact]
    public void Four_simultaneous_setups_with_three_workers_make_one_machine_wait()
    {
        var backlogs = Enumerable.Range(1, 4)
            .Select(index => Backlog($"machine-{index}",
                [Operation($"op-{index}", setupHours: 1, productionHours: 0)]))
            .ToArray();
        var calendars = Enumerable.Range(1, 4)
            .Select(index => new TimelineMachineCalendar(
                $"machine-{index}", [Window(8, 17)], ["milling"]))
            .ToArray();
        var workers = Enumerable.Range(1, 3)
            .Select(index => new TimelineResourceCalendar(
                $"setup-{index}", TimelineResourceRole.SetupWorker,
                [Window(8, 17)], ["milling"]))
            .ToArray();

        var result = new TimelineCalculationEngine().Calculate(Input(
            backlogs, calendars, SetupCalendar(Window(8, 17)), [], [], workers));

        Assert.Empty(result.Conflicts);
        Assert.Equal(3, result.Operations.Count(value => value.StartsAt == Utc(8)));
        var delayed = Assert.Single(result.Operations, value => value.StartsAt == Utc(9));
        var wait = Assert.Single(delayed.WaitingIntervals);
        Assert.Equal(Utc(8), wait.StartsAt);
        Assert.Equal(Utc(9), wait.EndsAt);
        Assert.Contains("skilled setup worker", wait.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["op-1", "op-2", "op-3", "op-4"],
            backlogs.SelectMany(value => value.Operations).Select(value => value.OperationId));
    }

    [Fact]
    public void Setup_worker_without_machine_skill_is_not_selected()
    {
        var result = new TimelineCalculationEngine().Calculate(Input(
            [Backlog("machine-1", [Operation("op-skilled", 1, 0)])],
            [new TimelineMachineCalendar("machine-1", [Window(8, 17)], ["five-axis milling"])],
            SetupCalendar(Window(8, 17)), [], [],
            [
                new TimelineResourceCalendar("unskilled", TimelineResourceRole.SetupWorker,
                    [Window(8, 17)], ["turning"]),
                new TimelineResourceCalendar("skilled", TimelineResourceRole.SetupWorker,
                    [Window(10, 17)], ["five-axis milling"])
            ]));

        Assert.Empty(result.Conflicts);
        var operation = Assert.Single(result.Operations);
        Assert.Equal(Utc(10), operation.StartsAt);
        Assert.All(operation.SetupIntervals,
            interval => Assert.Contains("skilled", interval.Detail, StringComparison.Ordinal));
        Assert.Contains(operation.WaitingIntervals,
            interval => interval.StartsAt == Utc(8)
                && interval.EndsAt == Utc(10)
                && interval.Detail!.Contains("setup worker", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_required_qa_role_is_explained_in_the_conflict()
    {
        var operation = new TimelineOperationInput(
            "op-qa", TimeSpan.Zero, TimeSpan.FromHours(1),
            QaDuration: TimeSpan.FromHours(1));
        var result = new TimelineCalculationEngine().Calculate(Input(
            [Backlog("machine-1", [operation])],
            [Calendar("machine-1", Window(8, 17))],
            SetupCalendar(Window(8, 17)), [], [],
            [Resource("setup", TimelineResourceRole.SetupWorker, Window(8, 17))]));

        var conflict = Assert.Single(result.Conflicts,
            value => value.Code == "insufficient_availability");
        Assert.Contains("no active QA worker", conflict.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Regular_worker_lunch_break_splits_manual_part_change_and_shows_waiting()
    {
        var operation = new TimelineOperationInput(
            "op-lunch", TimeSpan.Zero, TimeSpan.FromHours(1),
            LoadUnloadDuration: TimeSpan.FromHours(2), LoadUnloadRequiresWorker: true);
        var result = new TimelineCalculationEngine().Calculate(Input(
            [Backlog("machine-1", [operation])],
            [Calendar("machine-1", Window(11, 17))],
            SetupCalendar(Window(11, 17)), [], [],
            [new TimelineResourceCalendar("regular", TimelineResourceRole.RegularWorker,
                [Window(8, 12), Window(13, 17)])]));

        Assert.Empty(result.Conflicts);
        var scheduled = Assert.Single(result.Operations);
        AssertIntervals(scheduled.LoadUnloadIntervals!,
            (TimelineIntervalType.LoadUnload, Utc(11), Utc(12)),
            (TimelineIntervalType.LoadUnload, Utc(13), Utc(14)));
        Assert.Contains(scheduled.WaitingIntervals,
            interval => interval.StartsAt == Utc(12)
                && interval.EndsAt == Utc(13)
                && interval.Detail!.Contains("regular worker", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Setup_contention_uses_earlier_work_finish_date_without_reordering_backlogs()
    {
        var late = new TimelineOperationInput(
            "op-a-late", TimeSpan.FromHours(1), TimeSpan.Zero,
            PriorityWorkFinishDate: new DateOnly(2026, 8, 20), PriorityOrderNumber: "SO-1");
        var urgent = new TimelineOperationInput(
            "op-z-urgent", TimeSpan.FromHours(1), TimeSpan.Zero,
            PriorityWorkFinishDate: new DateOnly(2026, 8, 12), PriorityOrderNumber: "SO-99");
        var backlogs = new[]
        {
            Backlog("machine-late", [late]),
            Backlog("machine-urgent", [urgent])
        };

        var result = new TimelineCalculationEngine().Calculate(Input(
            backlogs,
            [
                new TimelineMachineCalendar("machine-late", [Window(8, 17)], ["milling"]),
                new TimelineMachineCalendar("machine-urgent", [Window(8, 17)], ["milling"])
            ],
            SetupCalendar(Window(8, 17)), [], [],
            [new TimelineResourceCalendar("setup-1", TimelineResourceRole.SetupWorker,
                [Window(8, 17)], ["milling"])]));

        Assert.Empty(result.Conflicts);
        Assert.Equal(Utc(8), Assert.Single(result.Operations, value => value.OperationId == urgent.OperationId).StartsAt);
        var delayed = Assert.Single(result.Operations, value => value.OperationId == late.OperationId);
        Assert.Equal(Utc(9), delayed.StartsAt);
        Assert.Contains(delayed.WaitingIntervals, interval =>
            interval.Detail!.Contains("Work Finish Date 2026-08-12 is earlier", StringComparison.Ordinal));
        Assert.Equal(["op-a-late", "op-z-urgent"],
            backlogs.SelectMany(value => value.Operations).Select(value => value.OperationId));
    }

    [Fact]
    public void Equal_due_date_setup_contention_uses_naturally_smaller_order_number()
    {
        var largerOrder = new TimelineOperationInput(
            "op-a-large-order", TimeSpan.FromHours(1), TimeSpan.Zero,
            PriorityWorkFinishDate: new DateOnly(2026, 8, 12), PriorityOrderNumber: "SO-10");
        var smallerOrder = new TimelineOperationInput(
            "op-z-small-order", TimeSpan.FromHours(1), TimeSpan.Zero,
            PriorityWorkFinishDate: new DateOnly(2026, 8, 12), PriorityOrderNumber: "SO-2");
        var backlogs = new[]
        {
            Backlog("machine-large", [largerOrder]),
            Backlog("machine-small", [smallerOrder])
        };

        var result = new TimelineCalculationEngine().Calculate(Input(
            backlogs,
            [
                new TimelineMachineCalendar("machine-large", [Window(8, 17)], ["milling"]),
                new TimelineMachineCalendar("machine-small", [Window(8, 17)], ["milling"])
            ],
            SetupCalendar(Window(8, 17)), [], [],
            [new TimelineResourceCalendar("setup-1", TimelineResourceRole.SetupWorker,
                [Window(8, 17)], ["milling"])]));

        Assert.Empty(result.Conflicts);
        Assert.Equal(Utc(8), Assert.Single(result.Operations, value => value.OperationId == smallerOrder.OperationId).StartsAt);
        var delayed = Assert.Single(result.Operations, value => value.OperationId == largerOrder.OperationId);
        Assert.Equal(Utc(9), delayed.StartsAt);
        Assert.Contains(delayed.WaitingIntervals, interval =>
            interval.Detail!.Contains("smaller Order number SO-2", StringComparison.Ordinal));
        Assert.Equal(["op-a-large-order", "op-z-small-order"],
            backlogs.SelectMany(value => value.Operations).Select(value => value.OperationId));
    }

    private static TimelineCalculationInput Input(
        IReadOnlyList<TimelineMachineBacklog> backlogs,
        IReadOnlyList<TimelineMachineCalendar> calendars,
        TimelineSetupCalendar setupCalendar,
        IReadOnlyList<TimelineDowntime> downtimes,
        IReadOnlyList<TimelineDependency> dependencies,
        IReadOnlyList<TimelineResourceCalendar>? resources = null,
        IReadOnlyList<TimelineMachineCalendar>? dayShiftCalendars = null) => new(
        HorizonStart,
        HorizonEnd,
        backlogs,
        calendars,
        setupCalendar,
        downtimes,
        dependencies,
        resources,
        dayShiftCalendars);

    private static TimelineMachineBacklog Backlog(
        string machineId,
        IReadOnlyList<TimelineOperationInput> operations) => new(machineId, operations);

    private static TimelineOperationInput Operation(
        string operationId,
        double setupHours,
        double productionHours) => new(
        operationId,
        TimeSpan.FromHours(setupHours),
        TimeSpan.FromHours(productionHours));

    private static TimelineMachineCalendar Calendar(
        string machineId,
        params TimelineWindow[] windows) => new(machineId, windows);

    private static TimelineSetupCalendar SetupCalendar(params TimelineWindow[] windows) => new(windows);

    private static TimelineResourceCalendar Resource(
        string resourceId, TimelineResourceRole role, params TimelineWindow[] windows) =>
        new(resourceId, role, windows, ["*"]);

    private static TimelineWindow Window(int startHour, int endHour) =>
        new(Utc(startHour), Utc(endHour));

    private static DateTimeOffset Utc(int hour, int minute = 0) =>
        new(2026, 8, 11, hour, minute, 0, TimeSpan.Zero);

    private static void AssertIntervals(
        IReadOnlyList<TimelineInterval> actual,
        params (TimelineIntervalType Type, DateTimeOffset Start, DateTimeOffset End)[] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Type, actual[index].Type);
            Assert.Equal(expected[index].Start, actual[index].StartsAt);
            Assert.Equal(expected[index].End, actual[index].EndsAt);
        }
    }
}
