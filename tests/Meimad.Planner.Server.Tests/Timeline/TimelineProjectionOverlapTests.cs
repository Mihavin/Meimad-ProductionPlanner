using Meimad.Planner.Server.Application.Timeline;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Timeline;

public sealed class TimelineProjectionOverlapTests
{
    [Fact]
    public void Duplicate_block_diagnostic_names_assignment_operation_and_machine()
    {
        Assert.Equal(
            "DUPLICATE_TIMELINE_BLOCK assignmentId={AssignmentId} operationId={OperationId} machineId={MachineId}",
            TimelineProjectionService.DuplicateTimelineBlockLogTemplate);
    }

    [Fact]
    public void Overlapping_forecast_blocks_every_later_backlog_row_without_leapfrogging()
    {
        var conflicts = new List<TimelineProjectionConflict>();
        var service = Service();
        var operations = new[]
        {
            Operation("op-1", "assignment-1", 0),
            Operation("op-2", "assignment-2", 1),
            Operation("op-3", "assignment-3", 2)
        };
        var machine = Machine(
            Forecast("op-1", "assignment-1", Utc(8), Utc(10)),
            Forecast("op-2", "assignment-2", Utc(9), Utc(11)),
            Forecast("op-3", "assignment-3", Utc(10), Utc(12)));

        var result = Assert.Single(service.ReconcileMachineOperationOverlaps(
            [machine], operations, conflicts));

        var retained = Assert.Single(result.Intervals, value => value.OperationId == "op-1");
        var blocked = Assert.Single(result.Intervals, value => value.OperationId == "op-2");
        var laterBlocked = Assert.Single(result.Intervals, value => value.OperationId == "op-3");
        Assert.Equal("operation", retained.Type);
        Assert.Equal("waiting", blocked.Type);
        Assert.Equal("blocked", blocked.TimingKind);
        Assert.Equal("waiting", laterBlocked.Type);
        Assert.Equal("blocked", laterBlocked.TimingKind);
        Assert.Single(conflicts, value => value.Code == "machine_operation_overlap");
        Assert.Single(conflicts, value => value.Code == "dependency_unresolved"
            && value.OperationIds.Contains("op-3"));
        Assert.Equal(0, operations[0].BacklogPosition);
        Assert.Equal(1, operations[1].BacklogPosition);
        Assert.Equal(2, operations[2].BacklogPosition);
    }

    [Fact]
    public void Overlapping_authoritative_blocks_are_retained_and_reported_as_blocking()
    {
        var conflicts = new List<TimelineProjectionConflict>();
        var service = Service();
        var operations = new[] { Operation("op-1", "assignment-1", 0), Operation("op-2", "assignment-2", 1) };
        var machine = Machine(
            Forecast("op-1", "assignment-1", Utc(8), Utc(10)) with { TimingKind = "actual" },
            Forecast("op-2", "assignment-2", Utc(9), Utc(11)) with { TimingKind = "hold" });

        var result = Assert.Single(service.ReconcileMachineOperationOverlaps(
            [machine], operations, conflicts));

        Assert.Equal(2, result.Intervals.Count(value => value.Type == "operation"));
        var conflict = Assert.Single(conflicts, value => value.Code == "machine_operation_overlap");
        Assert.Equal("blocking", conflict.Severity);
    }

    [Fact]
    public void Completed_actual_history_is_not_current_machine_work_and_does_not_block_forecast()
    {
        var conflicts = new List<TimelineProjectionConflict>();
        var service = Service();
        var operations = new[]
        {
            Operation("history", null, null) with { Status = "completed" },
            Operation("forecast", "assignment-2", 0)
        };
        var history = Forecast("history", null, Utc(8), Utc(10)) with
        {
            Type = "actual_history",
            TimingKind = "actual"
        };
        var machine = Machine(history, Forecast("forecast", "assignment-2", Utc(9), Utc(11)));

        var result = Assert.Single(service.ReconcileMachineOperationOverlaps(
            [machine], operations, conflicts));

        Assert.Equal("operation", Assert.Single(result.Intervals,
            value => value.OperationId == "forecast").Type);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void Blocked_parent_demotes_child_and_grandchild_across_machines()
    {
        var conflicts = new List<TimelineProjectionConflict>();
        var service = Service();
        var keeper = Operation("keeper", "assignment-keeper", 0);
        var parent = Operation("parent", "assignment-parent", 1);
        var child = Operation("child", "assignment-child", 0) with
        {
            SourceCaseOperationId = "case-child",
            DependencyType = "sequential",
            PredecessorSourceCaseOperationId = parent.SourceCaseOperationId,
            MachineId = "machine-2"
        };
        var grandchild = Operation("grandchild", "assignment-grandchild", 0) with
        {
            SourceCaseOperationId = "case-grandchild",
            DependencyType = "sequential",
            PredecessorSourceCaseOperationId = child.SourceCaseOperationId,
            MachineId = "machine-3"
        };
        var sibling = Operation("sibling", "assignment-sibling", 1) with
        {
            SourceCaseOperationId = "case-sibling",
            MachineId = "machine-2"
        };
        var siblingChild = Operation("sibling-child", "assignment-sibling-child", 0) with
        {
            SourceCaseOperationId = "case-sibling-child",
            DependencyType = "sequential",
            PredecessorSourceCaseOperationId = sibling.SourceCaseOperationId,
            MachineId = "machine-4"
        };
        var result = service.ReconcileMachineOperationOverlaps(
        [
            Machine("machine-1",
                Forecast("machine-1", "keeper", "assignment-keeper", Utc(8), Utc(10)),
                Forecast("machine-1", "parent", "assignment-parent", Utc(9), Utc(11))),
            Machine("machine-2",
                Forecast("machine-2", "child", "assignment-child", Utc(11), Utc(12)),
                Forecast("machine-2", "sibling", "assignment-sibling", Utc(12), Utc(13))),
            Machine("machine-3",
                Forecast("machine-3", "grandchild", "assignment-grandchild", Utc(12), Utc(13))),
            Machine("machine-4",
                Forecast("machine-4", "sibling-child", "assignment-sibling-child", Utc(13), Utc(14)))
        ],
        [keeper, parent, child, sibling, grandchild, siblingChild], conflicts);

        var intervals = result.SelectMany(machine => machine.Intervals).ToArray();
        Assert.Equal("operation", Assert.Single(intervals,
            value => value.OperationId == "keeper").Type);
        Assert.All(new[] { "parent", "child", "sibling", "grandchild", "sibling-child" }, operationId =>
        {
            var blocked = Assert.Single(intervals, value => value.OperationId == operationId);
            Assert.Equal("waiting", blocked.Type);
            Assert.Equal("blocked", blocked.TimingKind);
        });
        Assert.Contains(conflicts, value => value.Code == "dependency_unresolved"
            && value.OperationIds.Contains("parent")
            && value.OperationIds.Contains("child"));
        Assert.Contains(conflicts, value => value.Code == "dependency_unresolved"
            && value.OperationIds.Contains("child")
            && value.OperationIds.Contains("grandchild"));
        Assert.Contains(conflicts, value => value.Code == "dependency_unresolved"
            && value.OperationIds.Contains("child")
            && value.OperationIds.Contains("sibling"));
        Assert.Contains(conflicts, value => value.Code == "dependency_unresolved"
            && value.OperationIds.Contains("sibling")
            && value.OperationIds.Contains("sibling-child"));
    }

    [Fact]
    public void Blocked_locked_group_member_blocks_forecasts_but_retains_authoritative_member()
    {
        var conflicts = new List<TimelineProjectionConflict>();
        var service = Service();
        var keeper = Operation("keeper", "assignment-keeper", 0);
        var blockedMember = Operation("locked-a", "assignment-a", 1) with
        {
            DependencyType = "locked_simultaneous",
            SimultaneousGroupKey = "group-1"
        };
        var forecastMember = Operation("locked-b", "assignment-b", 0) with
        {
            DependencyType = "locked_simultaneous",
            SimultaneousGroupKey = "group-1",
            MachineId = "machine-2"
        };
        var actualMember = Operation("locked-c", "assignment-c", 0) with
        {
            DependencyType = "locked_simultaneous",
            SimultaneousGroupKey = "group-1",
            MachineId = null,
            MachineAssignmentId = null,
            BacklogPosition = null,
            Status = "completed"
        };

        var result = service.ReconcileMachineOperationOverlaps(
        [
            Machine("machine-1",
                Forecast("machine-1", "keeper", "assignment-keeper", Utc(8), Utc(10)),
                Forecast("machine-1", "locked-a", "assignment-a", Utc(9), Utc(11))),
            Machine("machine-2",
                Forecast("machine-2", "locked-b", "assignment-b", Utc(9), Utc(11))),
            Machine("machine-3",
                Forecast("machine-3", "locked-c", "assignment-c", Utc(9), Utc(11)) with
                {
                    Type = "actual_history",
                    TimingKind = "actual"
                })
        ],
        [keeper, blockedMember, forecastMember, actualMember], conflicts);

        var intervals = result.SelectMany(machine => machine.Intervals).ToArray();
        Assert.Equal("waiting", Assert.Single(intervals,
            value => value.OperationId == "locked-a").Type);
        Assert.Equal("waiting", Assert.Single(intervals,
            value => value.OperationId == "locked-b").Type);
        var actual = Assert.Single(intervals, value => value.OperationId == "locked-c");
        Assert.Equal("actual_history", actual.Type);
        Assert.Equal("actual", actual.TimingKind);
        Assert.Contains(conflicts, value => value.Code == "dependency_unresolved"
            && value.OperationIds.Contains("locked-a")
            && value.OperationIds.Contains("locked-b"));
        Assert.Contains(conflicts, value => value.Code == "dependency_unresolved"
            && value.OperationIds.Contains("locked-a")
            && value.OperationIds.Contains("locked-c"));
    }

    [Fact]
    public void Authoritative_child_of_blocked_parent_stays_visible_but_blocks_grandchild()
    {
        var conflicts = new List<TimelineProjectionConflict>();
        var service = Service();
        var parent = Operation("parent", "assignment-parent", 0);
        var child = Operation("child", "assignment-child", 0) with
        {
            SourceCaseOperationId = "case-child",
            DependencyType = "sequential",
            PredecessorSourceCaseOperationId = parent.SourceCaseOperationId,
            MachineId = "machine-2"
        };
        var grandchild = Operation("grandchild", "assignment-grandchild", 0) with
        {
            SourceCaseOperationId = "case-grandchild",
            DependencyType = "sequential",
            PredecessorSourceCaseOperationId = child.SourceCaseOperationId,
            MachineId = "machine-3"
        };
        var blockedParent = Forecast("machine-1", "parent", "assignment-parent", Utc(8), Utc(9)) with
        {
            Type = "waiting",
            TimingKind = "blocked"
        };

        var result = service.ReconcileMachineOperationOverlaps(
        [
            Machine("machine-1", blockedParent),
            Machine("machine-2",
                Forecast("machine-2", "child", "assignment-child", Utc(9), Utc(10)) with
                {
                    TimingKind = "actual"
                }),
            Machine("machine-3",
                Forecast("machine-3", "grandchild", "assignment-grandchild", Utc(10), Utc(11)))
        ],
        [parent, child, grandchild], conflicts);

        var intervals = result.SelectMany(machine => machine.Intervals).ToArray();
        Assert.Equal("operation", Assert.Single(intervals,
            value => value.OperationId == "child").Type);
        var blockedGrandchild = Assert.Single(intervals,
            value => value.OperationId == "grandchild");
        Assert.Equal("waiting", blockedGrandchild.Type);
        Assert.Equal("blocked", blockedGrandchild.TimingKind);
        Assert.Contains(conflicts, value => value.Code == "dependency_unresolved"
            && value.OperationIds.Contains("parent")
            && value.OperationIds.Contains("child"));
        Assert.Contains(conflicts, value => value.Code == "dependency_unresolved"
            && value.OperationIds.Contains("child")
            && value.OperationIds.Contains("grandchild"));
    }

    [Fact]
    public void Completed_history_child_is_retained_and_propagates_unresolved_dependency()
    {
        var conflicts = new List<TimelineProjectionConflict>();
        var service = Service();
        var parent = Operation("parent", "assignment-parent", 0);
        var history = Operation("history", null, null) with
        {
            SourceCaseOperationId = "case-history",
            DependencyType = "sequential",
            PredecessorSourceCaseOperationId = parent.SourceCaseOperationId,
            Status = "completed"
        };
        var grandchild = Operation("grandchild", "assignment-grandchild", 0) with
        {
            SourceCaseOperationId = "case-grandchild",
            DependencyType = "sequential",
            PredecessorSourceCaseOperationId = history.SourceCaseOperationId,
            MachineId = "machine-3"
        };
        var blockedParent = Forecast("machine-1", "parent", "assignment-parent", Utc(8), Utc(9)) with
        {
            Type = "waiting",
            TimingKind = "blocked"
        };
        var actualHistory = Forecast("machine-2", "history", null, Utc(8), Utc(9)) with
        {
            Type = "actual_history",
            TimingKind = "actual"
        };

        var result = service.ReconcileMachineOperationOverlaps(
        [
            Machine("machine-1", blockedParent),
            Machine("machine-2", actualHistory),
            Machine("machine-3",
                Forecast("machine-3", "grandchild", "assignment-grandchild", Utc(9), Utc(10)))
        ],
        [parent, history, grandchild], conflicts);

        var intervals = result.SelectMany(machine => machine.Intervals).ToArray();
        var retainedHistory = Assert.Single(intervals, value => value.OperationId == "history");
        Assert.Equal("actual_history", retainedHistory.Type);
        Assert.Equal("actual", retainedHistory.TimingKind);
        var blockedGrandchild = Assert.Single(intervals,
            value => value.OperationId == "grandchild");
        Assert.Equal("waiting", blockedGrandchild.Type);
        Assert.Equal("blocked", blockedGrandchild.TimingKind);
        Assert.Contains(conflicts, value => value.Code == "dependency_unresolved"
            && value.OperationIds.Contains("parent")
            && value.OperationIds.Contains("history"));
        Assert.Contains(conflicts, value => value.Code == "dependency_unresolved"
            && value.OperationIds.Contains("history")
            && value.OperationIds.Contains("grandchild"));
    }

    [Fact]
    public void Zero_duration_blocked_marker_at_horizon_end_retains_identity()
    {
        var conflicts = new List<TimelineProjectionConflict>();
        var service = Service();
        var operation = Operation("blocked", "assignment-blocked", 1);
        var marker = Forecast("blocked", "assignment-blocked", Utc(18), Utc(18)) with
        {
            Type = "waiting",
            TimingKind = "blocked"
        };

        var result = Assert.Single(service.ReconcileMachineOperationOverlaps(
            [Machine(marker)], [operation], conflicts));

        var retained = Assert.Single(result.Intervals);
        Assert.Equal("blocked", retained.OperationId);
        Assert.Equal("assignment-blocked", retained.MachineAssignmentId);
        Assert.Equal("waiting", retained.Type);
        Assert.Equal("blocked", retained.TimingKind);
        Assert.Equal(Utc(18), retained.StartsAt);
        Assert.Equal(Utc(18), retained.EndsAt);
    }

    [Fact]
    public void Authoritative_later_backlog_row_is_retained_with_blocking_conflict()
    {
        var conflicts = new List<TimelineProjectionConflict>();
        var service = Service();
        var blockedOperation = Operation("blocked", "assignment-blocked", 0);
        var actualOperation = Operation("actual", "assignment-actual", 1);
        var blocked = Forecast("blocked", "assignment-blocked", Utc(8), Utc(9)) with
        {
            Type = "waiting",
            TimingKind = "blocked"
        };
        var actual = Forecast("actual", "assignment-actual", Utc(9), Utc(10)) with
        {
            TimingKind = "actual"
        };

        var result = Assert.Single(service.ReconcileMachineOperationOverlaps(
            [Machine(blocked, actual)], [blockedOperation, actualOperation], conflicts));

        var retained = Assert.Single(result.Intervals, value => value.OperationId == "actual");
        Assert.Equal("operation", retained.Type);
        Assert.Equal("actual", retained.TimingKind);
        Assert.Contains(conflicts, value => value.Code == "dependency_unresolved"
            && value.OperationIds.Contains("blocked")
            && value.OperationIds.Contains("actual")
            && value.Severity == "blocking");
    }

    private static TimelineProjectionService Service() => new(
        null!, null!, null!, null!, NullLogger<TimelineProjectionService>.Instance);

    private static TimelineProjectionMachine Machine(params TimelineProjectionInterval[] intervals) =>
        new("machine-1", "M-1", "Mill", intervals);

    private static TimelineProjectionMachine Machine(
        string machineId, params TimelineProjectionInterval[] intervals) =>
        new(machineId, machineId, "Mill", intervals);

    private static TimelineProjectionInterval Forecast(
        string operationId, string? assignmentId,
        DateTimeOffset startsAt, DateTimeOffset endsAt) =>
        Forecast("machine-1", operationId, assignmentId, startsAt, endsAt);

    private static TimelineProjectionInterval Forecast(
        string machineId, string operationId, string? assignmentId,
        DateTimeOffset startsAt, DateTimeOffset endsAt) => new(
            "operation", machineId, operationId, "batch-1", "B-1", "PN-1",
            10, operationId, startsAt, endsAt, "Calculated work", "forecast", "not_started",
            startsAt, endsAt, MachineAssignmentId: assignmentId, PlanningMode: "manual");

    private static TimelineSourceOperation Operation(
        string operationId, string? assignmentId, int? backlogPosition) => new(
            OperationId: operationId,
            BatchId: "batch-1",
            BatchNumber: "B-1",
            CaseId: "case-1",
            PartNumber: "PN-1",
            OperationNumber: 10,
            OperationName: operationId,
            Status: "not_started",
            PlannedQuantity: 1,
            SetupSeconds: 0,
            CycleSeconds: 3600,
            SourceCaseOperationId: $"case-{operationId}",
            DependencyType: "independent",
            PredecessorSourceCaseOperationId: null,
            SimultaneousGroupKey: null,
            MachineAssignmentId: assignmentId,
            MachineId: assignmentId is null ? null : "machine-1",
            BacklogPosition: backlogPosition,
            PlanningMode: "manual",
            MachineMovedAt: null,
            QaSeconds: 0,
            LoadUnloadSeconds: 0,
            LoadUnloadRequiresWorker: false,
            AutomaticLoading: false,
            LoadUnloadEveryNParts: null,
            DayShiftOnly: false,
            PriorityWorkFinishDate: null,
            PriorityOrderNumber: null,
            ActivePauseReason: null,
            PausedBy: null,
            PauseStartedAt: null,
            MovePauseStartedAt: null,
            MovePauseEndedAt: null,
            ActualStart: null,
            ActualEnd: null,
            ActualMachineId: null);

    private static DateTimeOffset Utc(int hour) =>
        new(2026, 8, 11, hour, 0, 0, TimeSpan.Zero);
}
