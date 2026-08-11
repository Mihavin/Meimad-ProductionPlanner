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
            (TimelineIntervalType.Idle, Utc(8), Utc(9)),
            (TimelineIntervalType.Idle, Utc(14), Utc(17)));
        var downtime = Assert.Single(machine.Intervals, interval =>
            interval.Type == TimelineIntervalType.Downtime);
        Assert.Equal(Utc(10), downtime.StartsAt);
        Assert.Equal(Utc(10, 30), downtime.EndsAt);
        Assert.Equal("Maintenance", downtime.Detail);
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

    private static TimelineCalculationInput Input(
        IReadOnlyList<TimelineMachineBacklog> backlogs,
        IReadOnlyList<TimelineMachineCalendar> calendars,
        TimelineSetupCalendar setupCalendar,
        IReadOnlyList<TimelineDowntime> downtimes,
        IReadOnlyList<TimelineDependency> dependencies) => new(
        HorizonStart,
        HorizonEnd,
        backlogs,
        calendars,
        setupCalendar,
        downtimes,
        dependencies);

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
