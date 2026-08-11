using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

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
                [new TimelineInterval(
                    "setup", "machine-1", "op-1", "batch-1", "B-1", "PN-1", 10,
                    setupStart, setupEnd, "server detail")])],
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
        Assert.Equal("server detail", viewModel.Machines[0].Intervals[0].Detail);
        Assert.Equal("dep-1", Assert.Single(viewModel.SelectedDependencies).DependencyId);
        viewModel.SelectedBatch = viewModel.Batches[1];
        Assert.Equal("dep-2", Assert.Single(viewModel.SelectedDependencies).DependencyId);
        Assert.Equal("warning", Assert.Single(viewModel.Conflicts).Severity);
        Assert.Equal(start, api.RequestedFrom);
        Assert.Equal(end, api.RequestedTo);
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

        internal FakeApiClient(TimelineSnapshot snapshot) => this.snapshot = snapshot;

        internal DateTimeOffset RequestedFrom { get; private set; }

        internal DateTimeOffset RequestedTo { get; private set; }

        public Task<TimelineSnapshot> GetTimelineAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        {
            RequestedFrom = from;
            RequestedTo = to;
            return Task.FromResult(snapshot);
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
