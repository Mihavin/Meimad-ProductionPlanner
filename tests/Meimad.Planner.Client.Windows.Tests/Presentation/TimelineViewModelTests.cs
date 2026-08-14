using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;
using Meimad.Planner.Client.Windows.Views;
using System.Windows.Media;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class TimelineViewModelTests
{
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
        Assert.Equal(30, TimelineView.CompactRowHeight);
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
        private readonly bool pauseFirstRequest;

        internal FakeApiClient(
            TimelineSnapshot snapshot,
            TimelineSnapshot? nextSnapshot = null,
            bool pauseFirstRequest = false)
        {
            this.snapshot = snapshot;
            this.nextSnapshot = nextSnapshot;
            this.pauseFirstRequest = pauseFirstRequest;
        }

        internal TaskCompletionSource FirstRequestStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseFirstRequest { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal DateTimeOffset RequestedFrom { get; private set; }

        internal DateTimeOffset RequestedTo { get; private set; }

        internal int RequestCount { get; private set; }

        public async Task<TimelineSnapshot> GetTimelineAsync(
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken = default)
        {
            RequestedFrom = from;
            RequestedTo = to;
            RequestCount++;
            if (RequestCount == 1 && pauseFirstRequest)
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
        public Task AssignOrMoveOperationAsync(string batchOperationId, string machineId, int backlogPosition, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UnassignOperationAsync(string batchOperationId, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
