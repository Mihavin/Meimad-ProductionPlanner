using Meimad.Planner.Client.Windows.Api;
using System.Text.Json;
using Meimad.Planner.Client.Windows.Presentation;
using Meimad.Planner.Client.Windows.Views;
using System.Windows.Media;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class TimelineViewModelTests
{
    [Fact]
    public void Planned_not_ready_interval_keeps_its_forecast_and_explains_readiness()
    {
        var interval = new TimelineInterval(
            "operation", "machine-1", "operation-1", "batch-1", "B-1", "PN-1",
            10, "Mill", DateTimeOffset.Parse("2026-08-20T08:00:00Z"),
            DateTimeOffset.Parse("2026-08-20T09:00:00Z"), "Calculated production",
            TimingKind: "forecast", OperationStatus: "not_started",
            MachineAssignmentId: "assignment-1",
            OverallReadinessState: "NOT_READY",
            IsReadyForProduction: false,
            ReadinessSummary: "Not ready: Tool Offsets missing");

        Assert.True(interval.IsForecast);
        Assert.True(interval.IsPlannedNotReady);
        Assert.Contains("Readiness: Not ready", TimelineView.IntervalToolTip(
            interval, TimelineView.IntervalLabel(interval)), StringComparison.Ordinal);
    }

    [Fact]
    public void Timeline_defaults_to_a_thirty_day_window_for_multi_day_dependency_chains()
    {
        var viewModel = new TimelineViewModel();

        Assert.Equal(30, (viewModel.ToDate!.Value.Date - viewModel.FromDate!.Value.Date).TotalDays);
    }

    [Fact]
    public async Task One_backward_assignment_is_one_normalized_operation_block_plus_capacity_annotation()
    {
        var start = DateTimeOffset.Parse("2026-08-11T00:00:00Z");
        var end = start.AddDays(10);
        var due = new DateOnly(2026, 8, 19);
        var snapshot = new TimelineSnapshot(
            start, start, end,
            [new TimelineBatch("batch-1", "B-1", "PN-1", due)],
            [new TimelineMachine("machine-1", "M-1", "Mill", [
                new TimelineInterval(
                    "operation", "machine-1", "op-1", "batch-1", "B-1", "PN-1", 10,
                    "Mill", start.AddHours(2), start.AddHours(3), null,
                    PlanningMode: "backward", MachineAssignmentId: "assignment-1"),
                new TimelineInterval(
                    "waiting", "machine-1", null, null, null, null, null, null,
                    start, start.AddHours(2), "Available capacity before delivery-date placement.")
            ])],
            [], []);
        var api = new FakeApiClient(snapshot);
        var viewModel = new TimelineViewModel
        {
            FromDate = start.UtcDateTime,
            ToDate = end.UtcDateTime
        };
        viewModel.AttachSession(api);

        await viewModel.RefreshAsync();

        Assert.Equal(1, api.RequestCount);
        var intervals = viewModel.Machines.Single().Intervals;
        Assert.Equal(2, intervals.Count);
        var interval = Assert.Single(intervals, TimelineView.IsOperationWorkInterval);
        Assert.Equal("backward", interval.PlanningMode);
        Assert.Equal("assignment-1", interval.MachineAssignmentId);
        Assert.Equal(due, interval.WorkFinishDate);
        Assert.True(TimelineView.IsOperationWorkInterval(interval));
        Assert.Null(Assert.Single(intervals, value => value.Type == "waiting").MachineAssignmentId);
        Assert.Empty(TimelineViewModel.DuplicateMachineAssignmentIds(viewModel.Machines));
        Assert.Contains("per operation assignment", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Duplicate_diagnostic_groups_only_by_machine_assignment_identity()
    {
        var start = DateTimeOffset.Parse("2026-08-18T08:00:00Z");
        var first = new TimelineInterval(
            "operation", "machine-1", "op-1", "batch-1", "B-1", "PN-1", 10,
            "Mill", start, start.AddHours(1), null,
            MachineAssignmentId: "assignment-1");
        var duplicateAtAnotherTime = first with
        {
            StartsAt = start.AddHours(4),
            EndsAt = start.AddHours(5)
        };
        var capacityAnnotation = new TimelineInterval(
            "waiting", "machine-1", null, null, null, null, null, null,
            start.AddHours(1), start.AddHours(4), "Available capacity before delivery-date placement.");
        var machine = new TimelineMachine(
            "machine-1", "M-1", "Mill", [first, duplicateAtAnotherTime, capacityAnnotation]);

        Assert.Equal(
            ["assignment-1"],
            TimelineViewModel.DuplicateMachineAssignmentIds([machine]));
        Assert.Null(capacityAnnotation.MachineAssignmentId);
    }

    [Fact]
    public void Normalized_operation_block_has_the_operation_color_and_is_the_only_dependency_endpoint_type()
    {
        var start = DateTimeOffset.Parse("2026-08-18T08:00:00Z");
        var operation = new TimelineInterval(
            "operation", "machine-1", "op-1", "batch-1", "B-1", "PN-1", 10,
            "Mill", start, start.AddHours(1), null,
            MachineAssignmentId: "assignment-1");
        var legacyPhase = operation with { Type = "production", MachineAssignmentId = null };
        var operationBrush = Assert.IsType<SolidColorBrush>(TimelineView.IntervalBrush("operation"));

        Assert.Equal(Color.FromRgb(30, 136, 229), operationBrush.Color);
        Assert.True(TimelineView.IsOperationWorkInterval(operation));
        Assert.False(TimelineView.IsOperationWorkInterval(legacyPhase));
        Assert.Contains("OP10", TimelineView.IntervalLabel(operation), StringComparison.Ordinal);
    }

    [Fact]
    public void Assignment_and_capacity_intervals_use_separate_non_overlapping_vertical_lanes()
    {
        var start = DateTimeOffset.Parse("2026-08-18T08:00:00Z");
        var operation = new TimelineInterval(
            "operation", "machine-1", "op-1", "batch-1", "B-1", "PN-1", 10,
            "Mill", start, start.AddHours(3), null,
            MachineAssignmentId: "assignment-1");
        var pausedAssignment = operation with
        {
            Type = "waiting",
            Detail = "Operation paused",
            MachineAssignmentId = "assignment-1"
        };
        var capacityWait = pausedAssignment with { MachineAssignmentId = null };
        var downtime = capacityWait with { Type = "downtime", OperationId = null };

        Assert.True(TimelineView.UsesPrimaryLane(operation));
        Assert.False(TimelineView.UsesPrimaryLane(pausedAssignment));
        Assert.True(pausedAssignment.IsBlocked);
        Assert.False(TimelineView.UsesPrimaryLane(capacityWait));
        Assert.False(TimelineView.UsesPrimaryLane(downtime));
        Assert.True(
            TimelineView.AssignmentLaneTop + TimelineView.AssignmentLaneHeight
            < TimelineView.CapacityLaneTop);
        Assert.True(
            TimelineView.CapacityLaneTop + TimelineView.CapacityLaneHeight
            <= TimelineView.CompactRowHeight);
    }

    [Fact]
    public void Partially_overlapping_primary_intervals_are_partitioned_deterministically()
    {
        var start = DateTimeOffset.Parse("2026-08-18T08:00:00Z");
        var intervals = new[]
        {
            new TimelineInterval("operation", "machine-1", "op-a", null, null, null, 10, "A", start, start.AddHours(3), null),
            new TimelineInterval("operation", "machine-1", "op-b", null, null, null, 20, "B", start.AddHours(1), start.AddHours(4), null),
            new TimelineInterval("operation", "machine-1", "op-c", null, null, null, 30, "C", start.AddHours(3), start.AddHours(5), null)
        };

        var lanes = TimelineView.PartitionIntervals(intervals);

        Assert.Equal((true, 0), lanes[0]);
        Assert.Equal((true, 1), lanes[1]);
        Assert.Equal((true, 0), lanes[2]);
    }

    [Fact]
    public void Assignment_owned_blocked_interval_is_capacity_only_and_remains_identified()
    {
        var start = DateTimeOffset.Parse("2026-08-18T08:00:00Z");
        var blocked = new TimelineInterval(
            "waiting", "machine-1", "op-1", "batch-1", "B-1", "PN-1", 10,
            "Mill", start, start.AddHours(1), "Waiting for setup worker.",
            TimingKind: "blocked", MachineAssignmentId: "assignment-1", PlanningMode: "backward");

        Assert.True(blocked.IsBlocked);
        Assert.False(TimelineView.UsesPrimaryLane(blocked));
        Assert.False(TimelineView.IsOperationWorkInterval(blocked));
        Assert.Contains("BLOCKED", TimelineView.IntervalLabel(blocked), StringComparison.Ordinal);
        Assert.Contains("OP10", TimelineView.IntervalLabel(blocked), StringComparison.Ordinal);
        Assert.Contains("Operation: PN-1/B-1 OP10 Mill",
            TimelineView.IntervalToolTip(blocked, TimelineView.IntervalLabel(blocked)),
            StringComparison.Ordinal);
        Assert.Contains("Waiting for setup worker", TimelineView.IntervalToolTip(blocked, TimelineView.IntervalLabel(blocked)), StringComparison.Ordinal);
        Assert.Contains("Planning mode: Backward", TimelineView.IntervalToolTip(blocked, TimelineView.IntervalLabel(blocked)), StringComparison.Ordinal);
    }

    [Fact]
    public void Suspended_operation_without_timing_kind_is_rendered_as_hold()
    {
        var start = DateTimeOffset.Parse("2026-08-18T08:00:00Z");
        var suspended = new TimelineInterval(
            "operation", "machine-1", "op-1", "batch-1", "B-1", "PN-1", 10,
            "Mill", start, start.AddHours(1), null, OperationStatus: "suspended");

        Assert.True(suspended.IsHold);
        Assert.False(suspended.IsBlocked);
        Assert.Contains("HOLD", TimelineView.IntervalLabel(suspended), StringComparison.Ordinal);
        Assert.Equal(Color.FromRgb(126, 87, 194),
            Assert.IsType<SolidColorBrush>(TimelineView.IntervalBrush(suspended)).Color);
    }

    [Fact]
    public void Ordinary_waiting_is_not_rendered_but_blocked_waiting_remains_visible()
    {
        var start = DateTimeOffset.Parse("2026-08-18T08:00:00Z");
        var waiting = new TimelineInterval(
            "waiting", "machine-1", "op-1", "batch-1", "B-1", "PN-1", 10,
            "Mill", start, start.AddHours(1), "Waiting for calendar.");

        Assert.False(waiting.IsBlocked);
        Assert.False(TimelineView.IsDefaultTimelineIntervalVisible(waiting));

        var blocked = waiting with { MachineAssignmentId = "assignment-1" };
        Assert.True(TimelineView.IsDefaultTimelineIntervalVisible(blocked));

        var hold = waiting with { TimingKind = "hold" };
        Assert.True(hold.IsHold);
        Assert.True(TimelineView.IsDefaultTimelineIntervalVisible(hold));
    }

    [Fact]
    public void Renderable_phases_preserve_work_and_reservation_colors()
    {
        var start = DateTimeOffset.Parse("2026-08-18T08:00:00Z");
        var operation = new TimelineInterval(
            "operation", "machine-1", "op-1", "batch-1", "B-1", "PN-1", 10,
            "Mill", start, start.AddHours(4), null,
            Phases:
            [
                new TimelinePhase("setup", start, start.AddHours(1)),
                new TimelinePhase("reserved", start.AddHours(1), start.AddHours(2)),
                new TimelinePhase("production", start.AddHours(3), start.AddHours(4))
            ]);

        Assert.True(TimelineView.HasRenderablePhases(operation));
        Assert.Equal(Color.FromRgb(30, 136, 229),
            Assert.IsType<SolidColorBrush>(TimelineView.PhaseBrush("production")).Color);
        Assert.Equal(Color.FromRgb(123, 31, 162),
            Assert.IsType<SolidColorBrush>(TimelineView.PhaseBrush("loadunload")).Color);
        Assert.Equal(Color.FromRgb(251, 192, 45),
            Assert.IsType<SolidColorBrush>(TimelineView.PhaseBrush("setup")).Color);
        Assert.Equal(Color.FromRgb(67, 160, 71),
            Assert.IsType<SolidColorBrush>(TimelineView.PhaseBrush("qc")).Color);
        Assert.NotEqual(
            Assert.IsType<SolidColorBrush>(TimelineView.PhaseBrush("setup")).Color,
            Assert.IsType<SolidColorBrush>(TimelineView.PhaseBrush("production")).Color);
        Assert.Equal(Color.FromRgb(245, 124, 0),
            Assert.IsType<SolidColorBrush>(TimelineView.PhaseBrush("reserved")).Color);
        Assert.Equal(Brushes.Transparent, TimelineView.PhaseBrush("waiting"));
        Assert.True(TimelineView.IsRenderablePhaseType("loadunload"));
        Assert.True(TimelineView.IsRenderablePhaseType("qc"));
        Assert.True(TimelineView.IsRenderablePhaseType("part_reload"));
        Assert.False(TimelineView.IsRenderablePhaseType("waiting"));
    }

    [Fact]
    public void Calendar_closures_are_separate_machine_background_context_not_timeline_intervals()
    {
        var start = DateTimeOffset.Parse("2026-08-15T00:00:00Z");
        var closure = new TimelineNonWorkingWindow(
            start, start.AddDays(1), "Machine calendar: non-working time.");
        var machine = new TimelineMachine(
            "machine-1", "M-1", "Mill", [], [closure]);

        Assert.Single(machine.NonWorkingWindows!);
        Assert.Empty(machine.Intervals);
        Assert.DoesNotContain("OP", TimelineView.CalendarBackgroundToolTip(closure),
            StringComparison.Ordinal);
        Assert.Equal(Color.FromArgb(224, 224, 228, 232),
            Assert.IsType<SolidColorBrush>(TimelineView.CalendarBackgroundBrush).Color);
    }

    [Fact]
    public void Timeline_machine_deserializes_non_working_windows_as_additive_context()
    {
        var machine = JsonSerializer.Deserialize<TimelineMachine>("""
            {
              "machineId":"machine-1",
              "number":"M-1",
              "name":"Mill",
              "intervals":[],
              "nonWorkingWindows":[
                {"startsAt":"2026-08-15T00:00:00Z","endsAt":"2026-08-16T00:00:00Z","detail":"Machine calendar: non-working time."}
              ]
            }
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(machine);
        Assert.Empty(machine!.Intervals);
        var window = Assert.Single(machine.NonWorkingWindows!);
        Assert.Equal(DateTimeOffset.Parse("2026-08-15T00:00:00Z"), window.StartsAt);
        Assert.Equal("Machine calendar: non-working time.", window.Detail);
    }

    [Fact]
    public void Identified_history_is_a_primary_operation_endpoint_while_anonymous_annotations_are_capacity_only()
    {
        var start = DateTimeOffset.Parse("2026-08-18T08:00:00Z");
        var actualHistory = new TimelineInterval(
            "actual_history", "machine-1", "op-1", "batch-1", "B-1", "PN-1", 10,
            "Mill", start, start.AddHours(1), "Recorded on the prior Machine.",
            TimingKind: "actual", OperationStatus: "completed", PlanningMode: "manual");
        var annotation = actualHistory with
        {
            Type = "assignment_annotation",
            TimingKind = null,
            Detail = "Non-canonical assignment annotation."
        };
        var hold = actualHistory with
        {
            Type = "operation",
            TimingKind = "hold",
            OperationStatus = "suspended",
            PlanningMode = "backward",
            MachineAssignmentId = "assignment-1",
            Detail = "Operation paused by planner."
        };

        var historyLabel = TimelineView.IntervalLabel(actualHistory);
        var annotationLabel = TimelineView.IntervalLabel(annotation);
        var holdLabel = TimelineView.IntervalLabel(hold);
        var historyBrush = Assert.IsType<SolidColorBrush>(TimelineView.IntervalBrush(actualHistory));
        var annotationBrush = Assert.IsType<SolidColorBrush>(TimelineView.IntervalBrush(annotation));
        var holdBrush = Assert.IsType<SolidColorBrush>(TimelineView.IntervalBrush(hold));

        Assert.Contains("ACTUAL HISTORY", historyLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("Manual", historyLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OP10", historyLabel, StringComparison.Ordinal);
        Assert.True(TimelineView.IsOperationWorkInterval(actualHistory));
        Assert.True(TimelineView.UsesPrimaryLane(actualHistory));
        Assert.DoesNotContain("OP10", annotationLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("Mill", annotationLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("Manual", annotationLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ANNOTATION", annotationLabel, StringComparison.Ordinal);
        Assert.False(TimelineView.IsOperationWorkInterval(annotation));
        Assert.False(TimelineView.UsesPrimaryLane(annotation));
        Assert.Equal(Color.FromRgb(0, 137, 123), historyBrush.Color);
        Assert.Equal(Color.FromRgb(158, 158, 158), annotationBrush.Color);

        Assert.True(hold.IsHold);
        Assert.Equal("Hold — paused", hold.TimingLabel);
        Assert.Contains("HOLD", holdLabel, StringComparison.Ordinal);
        Assert.Contains("Backward", holdLabel, StringComparison.Ordinal);
        Assert.True(TimelineView.UsesPrimaryLane(hold));
        Assert.Equal(Color.FromRgb(126, 87, 194), holdBrush.Color);
    }

    [Fact]
    public void Anonymous_capacity_interval_keeps_operation_identity_only_in_tooltip()
    {
        var start = DateTimeOffset.Parse("2026-08-18T08:00:00Z");
        var waiting = new TimelineInterval(
            "waiting", "machine-1", "op-1", "batch-1", "B-1", "PN-1", 10,
            "Mill", start, start.AddHours(1), "Waiting for Machine calendar.");

        var label = TimelineView.IntervalLabel(waiting);
        var tooltip = TimelineView.IntervalToolTip(waiting, label);

        Assert.DoesNotContain("OP10", label, StringComparison.Ordinal);
        Assert.DoesNotContain("Mill", label, StringComparison.Ordinal);
        Assert.Contains("Waiting for Machine calendar", label, StringComparison.Ordinal);
        Assert.Contains("Operation: PN-1/B-1 OP10 Mill", tooltip, StringComparison.Ordinal);
        Assert.Contains("Waiting for Machine calendar", tooltip, StringComparison.Ordinal);
        Assert.False(TimelineView.UsesPrimaryLane(waiting));
        Assert.Equal(label, TimelineView.TimelineBlockLabel(waiting));
        Assert.DoesNotContain("Calculated:", TimelineView.TimelineBlockLabel(waiting),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Anonymous_actual_history_has_no_calculated_layer_prefix()
    {
        var start = DateTimeOffset.Parse("2026-08-18T08:00:00Z");
        var history = new TimelineInterval(
            "actual_history", "machine-1", null, null, null, null, null, null,
            start, start.AddHours(1), "Recorded prior-Machine occupancy.");

        Assert.False(TimelineView.UsesPrimaryLane(history));
        Assert.StartsWith("ACTUAL HISTORY", TimelineView.TimelineBlockLabel(history),
            StringComparison.Ordinal);
        Assert.DoesNotContain("Calculated", TimelineView.TimelineBlockLabel(history),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task View_model_preserves_server_intervals_and_filters_dependencies_by_batch()
    {
        var start = DateTimeOffset.Parse("2026-08-11T00:00:00Z");
        var end = DateTimeOffset.Parse("2026-08-13T00:00:00Z");
        var operationStart = DateTimeOffset.Parse("2026-08-11T08:15:00Z");
        var operationEnd = DateTimeOffset.Parse("2026-08-11T08:45:00Z");
        var snapshot = new TimelineSnapshot(
            DateTimeOffset.Parse("2026-08-11T07:00:00Z"),
            start,
            end,
            [new TimelineBatch("batch-1", "B-1", "PN-1"), new TimelineBatch("batch-2", "B-2", "PN-2")],
            [new TimelineMachine(
                "machine-1", "M-1", "Mill",
                [
                    new TimelineInterval(
                        "operation", "machine-1", "op-1", "batch-1", "B-1", "PN-1", 10,
                        "Rough mill", operationStart, operationEnd, "server detail",
                        MachineAssignmentId: "assignment-1"),
                    new TimelineInterval(
                        "waiting", "machine-1", null, null, null, null, null,
                        null, operationEnd, operationEnd.AddHours(1),
                        "Waiting for OP10 on Machine M-1 to finish."),
                    new TimelineInterval(
                        "operation", "machine-1", "op-3", "batch-1", "B-1", "PN-1", 30,
                        "Forecast finish", operationEnd.AddHours(1), operationEnd.AddHours(2), null,
                        "forecast", "not_started", operationEnd.AddHours(1), operationEnd.AddHours(2),
                        MachineAssignmentId: "assignment-3")
                ])],
            [
                Dependency("dep-1", "batch-1", 10, 20),
                Dependency("dep-2", "batch-2", 30, 40)
            ],
            [new TimelineConflict("conflict-1", "late_finish", "warning", "Late", ["op-1"], ["machine-1"])]);
        var api = new FakeApiClient(snapshot);
        var viewModel = new TimelineViewModel
        {
            FromDate = start.UtcDateTime,
            ToDate = end.UtcDateTime
        };
        viewModel.AttachSession(api);

        await viewModel.RefreshAsync();

        Assert.Equal(operationStart, viewModel.Machines[0].Intervals[0].StartsAt);
        Assert.Equal(operationEnd, viewModel.Machines[0].Intervals[0].EndsAt);
        Assert.Equal("Rough mill", viewModel.Machines[0].Intervals[0].OperationName);
        Assert.Equal("server detail", viewModel.Machines[0].Intervals[0].Detail);
        Assert.Equal("waiting", viewModel.Machines[0].Intervals[1].Type);
        Assert.Null(viewModel.Machines[0].Intervals[1].MachineAssignmentId);
        Assert.Equal("Waiting for OP10 on Machine M-1 to finish.", viewModel.Machines[0].Intervals[1].Detail);
        Assert.True(viewModel.Machines[0].Intervals[2].IsForecast);
        Assert.Equal("Forecast — not started", viewModel.Machines[0].Intervals[2].TimingLabel);
        Assert.Equal("dep-1", Assert.Single(viewModel.SelectedDependencies).DependencyId);
        viewModel.SelectedBatch = viewModel.Batches[1];
        Assert.Equal("dep-2", Assert.Single(viewModel.SelectedDependencies).DependencyId);
        Assert.Equal("warning", Assert.Single(viewModel.Conflicts).Severity);
        Assert.Equal(start, api.RequestedFrom);
        Assert.Equal(end, api.RequestedTo);
        Assert.Equal(38, TimelineView.CompactRowHeight);
        Assert.Equal("M-1 — Mill", TimelineView.MachineDisplayLabel(viewModel.Machines[0]));

        viewModel.Invalidate();
        await viewModel.EnsureLoadedAsync();
        Assert.Equal(2, api.RequestCount);
    }

    [Fact]
    public void Missed_forecast_start_conflict_has_a_user_facing_explanation()
    {
        var conflict = new TimelineConflict(
            "warning-1", "missed_forecast_start", "warning",
            "Operation was moved to the next available slot.", ["op-1"], ["machine-1"]);

        Assert.True(conflict.IsMissedForecastStart);
        Assert.Contains("Planned start was missed", conflict.DisplayMessage, StringComparison.Ordinal);
        Assert.Contains("next available slot", conflict.DisplayMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Timeline_view_keeps_dependency_waiting_visible_without_treating_it_as_the_child_operation_start()
    {
        var start = DateTimeOffset.Parse("2026-08-11T08:00:00Z");
        var wait = new TimelineInterval(
            "waiting", "machine-2", null, null, null, null, null,
            null, start, start.AddHours(2), "Waiting for OP10 on Machine M-1 to finish.");
        var operation = new TimelineInterval(
            "operation", "machine-2", "op-2", "batch-1", "B-1", "PN-1", 20,
            "Finish", start.AddHours(2), start.AddHours(3), null,
            MachineAssignmentId: "assignment-2");

        Assert.False(TimelineView.IsOperationWorkInterval(wait));
        Assert.True(TimelineView.IsOperationWorkInterval(operation));
        Assert.Null(wait.MachineAssignmentId);
        Assert.DoesNotContain("OP20", TimelineView.IntervalLabel(wait), StringComparison.Ordinal);
        Assert.Contains("Waiting for OP10", TimelineView.IntervalLabel(wait), StringComparison.Ordinal);
        Assert.Contains("OP20", TimelineView.IntervalLabel(operation), StringComparison.Ordinal);
    }

    [Fact]
    public void Backward_interval_tooltip_shows_assignment_mode_due_date_and_calculated_times()
    {
        var start = DateTimeOffset.Parse("2026-08-18T08:00:00Z");
        var interval = new TimelineInterval(
            "operation", "machine-1", "op-1", "batch-1", "B-1", "PN-1", 10,
            "Mill", start, start.AddHours(1), null, PlanningMode: "backward",
            WorkFinishDate: new DateOnly(2026, 8, 21), MachineAssignmentId: "assignment-1");

        var tooltip = TimelineView.IntervalToolTip(interval, TimelineView.IntervalLabel(interval));

        Assert.Contains("Planning mode: Backward", tooltip, StringComparison.Ordinal);
        Assert.Contains("Work Finish Date: 2026-08-21", tooltip, StringComparison.Ordinal);
        Assert.Contains(
            $"Calculated start: {start.ToLocalTime():yyyy-MM-dd HH:mm}",
            tooltip,
            StringComparison.Ordinal);
        Assert.Contains(
            $"Calculated finish: {start.AddHours(1).ToLocalTime():yyyy-MM-dd HH:mm}",
            tooltip,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Capacity_annotation_tooltip_does_not_claim_an_assignment_planning_mode()
    {
        var start = DateTimeOffset.Parse("2026-08-18T08:00:00Z");
        var interval = new TimelineInterval(
            "waiting", "machine-1", "op-1", "batch-1", "B-1", "PN-1", 10,
            "Mill", start, start.AddHours(1), "Waiting for Machine calendar.");

        var tooltip = TimelineView.IntervalToolTip(interval, TimelineView.IntervalLabel(interval));

        Assert.DoesNotContain("Planning mode", tooltip, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Work Finish Date", tooltip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Waiting for Machine calendar", tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalidation_during_refresh_reloads_the_new_server_projection()
    {
        var start = DateTimeOffset.Parse("2026-08-11T00:00:00Z");
        var end = start.AddDays(2);
        var first = Snapshot("Old projection", start, end);
        var latest = Snapshot("Assigned operation", start, end);
        var api = new FakeApiClient(first, latest, pauseFirstRequest: true);
        var viewModel = new TimelineViewModel
        {
            FromDate = start.UtcDateTime,
            ToDate = end.UtcDateTime
        };
        viewModel.AttachSession(api);

        var refresh = viewModel.RefreshAsync();
        await api.FirstRequestStarted.Task;
        viewModel.Invalidate();
        api.ReleaseFirstRequest.SetResult();
        await refresh;

        Assert.Equal(2, api.RequestCount);
        Assert.Equal("Assigned operation", viewModel.Machines[0].Name);
        Assert.Contains("loaded", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Automatic_forecast_refresh_replaces_not_started_operation_positions_from_the_server()
    {
        var horizonStart = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var horizonEnd = horizonStart.AddDays(2);
        var initial = ForecastSnapshot(horizonStart, horizonEnd, horizonStart.AddHours(2));
        var refreshed = ForecastSnapshot(horizonStart, horizonEnd, horizonStart.AddHours(5));
        var api = new FakeApiClient(initial, refreshed);
        var viewModel = await LoadedTimelineAsync(api, horizonStart, horizonEnd);

        await viewModel.RequestAutomaticForecastRefreshAsync(horizonStart.AddMinutes(30));

        Assert.Equal(2, api.RequestCount);
        Assert.Equal(0, api.MutationRequestCount);
        var interval = Assert.Single(viewModel.Machines.Single().Intervals);
        Assert.True(interval.IsForecast);
        Assert.Equal(horizonStart.AddHours(5), interval.StartsAt);
        Assert.Equal(horizonStart.AddHours(6), interval.EndsAt);
    }

    [Fact]
    public async Task Automatic_forecast_refresh_is_throttled_once_for_two_shared_timeline_viewport_ticks()
    {
        var horizonStart = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var horizonEnd = horizonStart.AddDays(2);
        var api = new FakeApiClient(ForecastSnapshot(horizonStart, horizonEnd, horizonStart.AddHours(2)));
        var viewModel = await LoadedTimelineAsync(api, horizonStart, horizonEnd);
        var now = horizonStart.AddMinutes(30);
        api.PauseRequestNumber = 2;

        var firstViewportTick = viewModel.RequestAutomaticForecastRefreshAsync(now);
        await api.FirstRequestStarted.Task;
        var secondViewportTick = viewModel.RequestAutomaticForecastRefreshAsync(now);
        api.ReleaseFirstRequest.SetResult();
        await Task.WhenAll(firstViewportTick, secondViewportTick);

        Assert.Equal(2, api.RequestCount); // Initial projection plus one shared GET.
        Assert.Equal(0, api.MutationRequestCount);
    }

    [Fact]
    public async Task Automatic_forecast_refresh_is_a_no_op_without_a_loaded_in_horizon_projection()
    {
        var horizonStart = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var horizonEnd = horizonStart.AddDays(2);
        var now = horizonStart.AddHours(1);

        var noSession = new TimelineViewModel();
        await noSession.RequestAutomaticForecastRefreshAsync(now);

        var unloadedApi = new FakeApiClient(ForecastSnapshot(horizonStart, horizonEnd, horizonStart.AddHours(2)));
        var notLoaded = new TimelineViewModel
        {
            FromDate = horizonStart.UtcDateTime,
            ToDate = horizonEnd.UtcDateTime
        };
        notLoaded.AttachSession(unloadedApi);
        await notLoaded.RequestAutomaticForecastRefreshAsync(now);
        Assert.Equal(0, unloadedApi.RequestCount);

        var loadedApi = new FakeApiClient(ForecastSnapshot(horizonStart, horizonEnd, horizonStart.AddHours(2)));
        var loaded = await LoadedTimelineAsync(loadedApi, horizonStart, horizonEnd);
        await loaded.RequestAutomaticForecastRefreshAsync(horizonEnd);

        Assert.Equal(1, loadedApi.RequestCount); // Initial GET only.
        Assert.Equal(0, loadedApi.MutationRequestCount);
    }

    [Fact]
    public async Task Automatic_forecast_refresh_handles_an_expected_server_failure_without_mutating_the_projection()
    {
        var horizonStart = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var horizonEnd = horizonStart.AddDays(2);
        var api = new FakeApiClient(ForecastSnapshot(horizonStart, horizonEnd, horizonStart.AddHours(2)))
        {
            ExceptionOnRequestNumber = 2
        };
        var viewModel = await LoadedTimelineAsync(api, horizonStart, horizonEnd);
        var existingStart = Assert.Single(viewModel.Machines.Single().Intervals).StartsAt;

        await viewModel.RequestAutomaticForecastRefreshAsync(horizonStart.AddMinutes(30));

        Assert.Equal(2, api.RequestCount);
        Assert.Equal(0, api.MutationRequestCount);
        Assert.Equal(existingStart, Assert.Single(viewModel.Machines.Single().Intervals).StartsAt);
        Assert.Contains("could not be reached", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Automatic_forecast_refresh_does_not_poll_a_projection_without_floating_not_started_assignment_work()
    {
        var horizonStart = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var horizonEnd = horizonStart.AddDays(2);
        var snapshot = new TimelineSnapshot(
            horizonStart, horizonStart, horizonEnd, [],
            [new TimelineMachine("machine-1", "M-1", "Mill",
            [
                new TimelineInterval(
                    "actual_history", "machine-1", "op-complete", "batch-1", "B-1", "PN-1", 10,
                    "Completed", horizonStart, horizonStart.AddHours(1), null,
                    TimingKind: "actual", OperationStatus: "completed", MachineAssignmentId: "assignment-complete"),
                new TimelineInterval(
                    "operation", "machine-1", "op-hold", "batch-1", "B-1", "PN-1", 20,
                    "Paused", horizonStart.AddHours(2), horizonStart.AddHours(3), null,
                    TimingKind: "hold", OperationStatus: "suspended", MachineAssignmentId: "assignment-hold"),
                new TimelineInterval(
                    "waiting", "machine-1", null, null, null, null, null,
                    null, horizonStart.AddHours(3), horizonStart.AddHours(4), "Available capacity.")
            ])],
            [], []);
        var api = new FakeApiClient(snapshot);
        var viewModel = await LoadedTimelineAsync(api, horizonStart, horizonEnd);

        await viewModel.RequestAutomaticForecastRefreshAsync(horizonStart.AddMinutes(30));

        Assert.Equal(1, api.RequestCount); // Initial projection only.
        Assert.Equal(0, api.MutationRequestCount);
    }

    [Fact]
    public async Task Automatic_forecast_refresh_uses_server_snapshot_time_for_marker_and_horizon_decision()
    {
        var horizonStart = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var horizonEnd = horizonStart.AddDays(2);
        var serverReadAt = horizonStart.AddHours(1);
        var workstationAppliedAt = horizonEnd.AddDays(10);
        var workstationNow = workstationAppliedAt.AddSeconds(30);
        var snapshot = ForecastSnapshot(horizonStart, horizonEnd, horizonStart.AddHours(2)) with
        {
            ReadAt = serverReadAt
        };
        var api = new FakeApiClient(snapshot);
        var viewModel = await LoadedTimelineAsync(
            api, horizonStart, horizonEnd, workstationAppliedAt);

        Assert.Equal(
            serverReadAt.AddSeconds(30),
            TimelineView.CurrentTimelineNow(viewModel, workstationNow));

        await viewModel.RequestAutomaticForecastRefreshAsync(workstationNow);

        // The raw workstation time is outside the horizon; the aligned server
        // time is inside it, so exactly one automatic GET is made.
        Assert.Equal(2, api.RequestCount);
        Assert.Equal(0, api.MutationRequestCount);
    }

    [Fact]
    public async Task Automatic_forecast_refresh_includes_an_identified_blocked_not_started_assignment()
    {
        var horizonStart = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var horizonEnd = horizonStart.AddDays(2);
        var blockedSnapshot = new TimelineSnapshot(
            horizonStart, horizonStart, horizonEnd, [],
            [new TimelineMachine("machine-1", "M-1", "Mill",
            [
                new TimelineInterval(
                    "waiting", "machine-1", "op-1", "batch-1", "B-1", "PN-1", 10,
                    "Mill", horizonStart.AddHours(1), horizonStart.AddHours(2), "Waiting for setup worker.",
                    TimingKind: "blocked", OperationStatus: "not_started", MachineAssignmentId: "assignment-1")
            ])],
            [], []);
        var api = new FakeApiClient(blockedSnapshot);
        var viewModel = await LoadedTimelineAsync(api, horizonStart, horizonEnd);

        await viewModel.RequestAutomaticForecastRefreshAsync(horizonStart.AddMinutes(30));

        Assert.Equal(2, api.RequestCount);
        Assert.Equal(0, api.MutationRequestCount);
    }

    [Fact]
    public async Task Short_horizon_with_an_unschedulable_predecessor_explains_how_to_view_the_chain()
    {
        var start = DateTimeOffset.Parse("2026-08-13T00:00:00Z");
        var snapshot = new TimelineSnapshot(
            start, start, start.AddDays(3), [], [], [],
            [new TimelineConflict("conflict-1", "insufficient_availability", "blocking",
                "The operation cannot fit within the horizon.", ["op-1"], ["machine-1"])]);
        var viewModel = new TimelineViewModel
        {
            FromDate = start.UtcDateTime,
            ToDate = start.AddDays(3).UtcDateTime
        };
        viewModel.AttachSession(new FakeApiClient(snapshot));

        await viewModel.RefreshAsync();

        Assert.Contains("Extend the To date", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    private static TimelineSnapshot Snapshot(string machineName, DateTimeOffset start, DateTimeOffset end) => new(
        DateTimeOffset.UtcNow, start, end, [],
        [new TimelineMachine("machine-1", "M-1", machineName, [])], [], []);

    private static TimelineSnapshot ForecastSnapshot(
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        DateTimeOffset forecastStart) => new(
        horizonStart,
        horizonStart,
        horizonEnd,
        [],
        [new TimelineMachine("machine-1", "M-1", "Mill",
        [
            new TimelineInterval(
                "operation", "machine-1", "op-1", "batch-1", "B-1", "PN-1", 10,
                "Mill", forecastStart, forecastStart.AddHours(1), null,
                TimingKind: "forecast", OperationStatus: "not_started",
                ForecastStart: forecastStart, ForecastEnd: forecastStart.AddHours(1),
                MachineAssignmentId: "assignment-1")
        ])],
        [], []);

    private static async Task<TimelineViewModel> LoadedTimelineAsync(
        FakeApiClient api,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        DateTimeOffset? clientObservedAt = null)
    {
        var observedAt = clientObservedAt ?? horizonStart;
        var viewModel = new TimelineViewModel(() => observedAt)
        {
            FromDate = horizonStart.UtcDateTime,
            ToDate = horizonEnd.UtcDateTime
        };
        viewModel.AttachSession(api);
        await viewModel.RefreshAsync();
        return viewModel;
    }

    private static TimelineDependency Dependency(
        string id,
        string batchId,
        int from,
        int to) => new(
        id, batchId, batchId == "batch-1" ? "B-1" : "B-2",
        batchId == "batch-1" ? "PN-1" : "PN-2", "SEQUENTIAL",
        $"op-{from}", from, "From", $"op-{to}", to, "To", null);

    private sealed class FakeApiClient : IPlannerApiClient
    {
        private readonly TimelineSnapshot snapshot;
        private readonly TimelineSnapshot? nextSnapshot;

        internal FakeApiClient(
            TimelineSnapshot snapshot,
            TimelineSnapshot? nextSnapshot = null,
            bool pauseFirstRequest = false)
        {
            this.snapshot = snapshot;
            this.nextSnapshot = nextSnapshot;
            PauseRequestNumber = pauseFirstRequest ? 1 : null;
        }

        internal TaskCompletionSource FirstRequestStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseFirstRequest { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal DateTimeOffset RequestedFrom { get; private set; }

        internal DateTimeOffset RequestedTo { get; private set; }

        internal int RequestCount { get; private set; }

        internal int MutationRequestCount { get; private set; }

        internal int? PauseRequestNumber { get; set; }

        internal int? ExceptionOnRequestNumber { get; set; }

        public async Task<TimelineSnapshot> GetTimelineAsync(
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken = default)
        {
            RequestedFrom = from;
            RequestedTo = to;
            RequestCount++;
            if (RequestCount == ExceptionOnRequestNumber)
            {
                throw new HttpRequestException("Test server unavailable.");
            }

            if (RequestCount == PauseRequestNumber)
            {
                FirstRequestStarted.SetResult();
                await ReleaseFirstRequest.Task.WaitAsync(cancellationToken);
            }
            return RequestCount > 1 && nextSnapshot is not null ? nextSnapshot : snapshot;
        }

        public Task<ServerHealth> GetHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EditModeStatus> GetEditModeAsync(string clientId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EditModeStatus> RequestEditAsync(string clientId, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EditModeStatus> ReleaseEditAsync(string clientId, long generation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EditModeStatus> DecideTransferAsync(string clientId, long generation, string requestId, bool release, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlannerCase>> ListCasesAsync(CaseQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CaseResource> GetCaseAsync(string caseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CaseResource> UpdateCaseAsync(string caseId, CaseUpdate update, string entityTag, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CaseOperation>> ListCaseOperationsAsync(string caseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlannerOrder>> ListOrdersAsync(string caseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProductionBatch>> ListBatchesAsync(string caseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]?> GetCasePreviewAsync(string caseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PlanningBoardSnapshot> GetPlanningBoardAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AssignOrMoveOperationAsync(string batchOperationId, string machineId, int backlogPosition, string clientId, long editGeneration, CancellationToken cancellationToken = default)
        {
            MutationRequestCount++;
            throw new NotSupportedException();
        }

        public Task UnassignOperationAsync(string batchOperationId, string clientId, long editGeneration, CancellationToken cancellationToken = default)
        {
            MutationRequestCount++;
            throw new NotSupportedException();
        }
        public void Dispose() { }
    }
}
