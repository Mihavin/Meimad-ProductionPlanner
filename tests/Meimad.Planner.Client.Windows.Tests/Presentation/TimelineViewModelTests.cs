using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;
using Meimad.Planner.Client.Windows.Views;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class TimelineViewModelTests
{
    [Fact]
    public async Task View_model_preserves_server_intervals_and_filters_dependencies_by_batch()
    {
        var start = DateTimeOffset.Parse("2026-08-11T00:00:00Z");
        var end = DateTimeOffset.Parse("2026-08-13T00:00:00Z");
        var setupStart = DateTimeOffset.Parse("2026-08-11T08:15:00Z");
        var setupEnd = DateTimeOffset.Parse("2026-08-11T08:45:00Z");
        var snapshot = new TimelineSnapshot(
            DateTimeOffset.Parse("2026-08-11T07:00:00Z"),
            start,
            end,
            [new TimelineBatch("batch-1", "B-1", "PN-1"), new TimelineBatch("batch-2", "B-2", "PN-2")],
            [new TimelineMachine(
                "machine-1", "M-1", "Mill",
                [
                    new TimelineInterval(
                        "setup", "machine-1", "op-1", "batch-1", "B-1", "PN-1", 10,
                        "Rough mill", setupStart, setupEnd, "server detail"),
                    new TimelineInterval(
                        "waiting", "machine-1", "op-2", "batch-1", "B-1", "PN-1", 20,
                        "Finish mill", setupEnd, setupEnd.AddHours(1),
                        "Waiting for OP10 on Machine M-1 to finish."),
                    new TimelineInterval(
                        "production", "machine-1", "op-3", "batch-1", "B-1", "PN-1", 30,
                        "Forecast finish", setupEnd.AddHours(1), setupEnd.AddHours(2), null,
                        "forecast", "not_started", setupEnd.AddHours(1), setupEnd.AddHours(2))
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

        Assert.Equal(setupStart, viewModel.Machines[0].Intervals[0].StartsAt);
        Assert.Equal(setupEnd, viewModel.Machines[0].Intervals[0].EndsAt);
        Assert.Equal("Rough mill", viewModel.Machines[0].Intervals[0].OperationName);
        Assert.Equal("server detail", viewModel.Machines[0].Intervals[0].Detail);
        Assert.Equal("waiting", viewModel.Machines[0].Intervals[1].Type);
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

        public async Task<TimelineSnapshot> GetTimelineAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
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
