using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class ShopUncountedCyclesTests
{
    [Fact]
    public async Task Planner_counts_only_the_ticked_uncounted_cycle_ends_with_the_listed_version()
    {
        var api = new FakeApiClient();
        var viewModel = new ShopOverviewViewModel();
        viewModel.AttachSession(api);
        Assert.False(viewModel.LoadUncountedCommand.CanExecute(null));
        viewModel.SelectedOutput = new ShopOutput("run-1", "program-1", null!, "IN_PROGRESS", "NOT_READY");

        await viewModel.LoadUncountedAsync();

        Assert.Equal(["E-1", "E-2"], viewModel.UncountedCycles.Select(x => x.Cycle.SourceEventId));
        Assert.Equal("End without a start", viewModel.UncountedCycles[0].Reason);
        Assert.True(viewModel.CountUncountedCommand.CanExecute(null));
        await viewModel.CountUncountedAsync();
        Assert.Null(api.Counted);
        Assert.Equal("Tick at least one cycle end to count.", viewModel.UncountedStatus);

        viewModel.UncountedCycles[1].IsSelected = true;
        await viewModel.CountUncountedAsync();

        Assert.Equal(("run-1", "event-2", "\"production-run:run-1:v4\""), api.Counted);
        Assert.Empty(viewModel.UncountedCycles);
        Assert.Contains("Counted 1 cycles on Run run-1", viewModel.UncountedStatus);
        Assert.False(viewModel.CountUncountedCommand.CanExecute(null));
    }

    private sealed class FakeApiClient : IPlannerApiClient
    {
        internal (string Run, string Id, string Tag)? Counted { get; private set; }

        public Task<UncountedCncCycleResource> GetUncountedCncCyclesAsync(string productionRunId, CancellationToken cancellationToken = default)
        {
            var at = DateTimeOffset.Parse("2026-10-12T15:55:49Z");
            return Task.FromResult(new UncountedCncCycleResource(new(productionRunId, "IN_PROGRESS", 4,
            [
                new("event-1", "program-1", "HAAS_DPRINT:M15", "E-1", 23, at, null, "CYCLE_END_WITHOUT_START"),
                new("event-2", "program-1", "HAAS_DPRINT:M15", "E-2", 25, at.AddHours(5), null, "CYCLE_END_SEQUENCE_MISMATCH")
            ]), "\"production-run:run-1:v4\""));
        }

        public Task<UncountedCncCyclesCounted> CountUncountedCncCyclesAsync(string productionRunId, IReadOnlyList<string> workflowEventIds, string entityTag, CancellationToken cancellationToken = default)
        {
            Counted = (productionRunId, Assert.Single(workflowEventIds), entityTag);
            return Task.FromResult(new UncountedCncCyclesCounted(1));
        }

        public Task<ServerHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<EditModeStatus> GetEditModeAsync(string clientId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<EditModeStatus> RequestEditAsync(string clientId, string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<EditModeStatus> ReleaseEditAsync(string clientId, long generation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<EditModeStatus> DecideTransferAsync(string clientId, long generation, string requestId, bool release, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<PlannerCase>> ListCasesAsync(CaseQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CaseResource> GetCaseAsync(string caseId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CaseResource> UpdateCaseAsync(string caseId, CaseUpdate update, string entityTag, string clientId, long editGeneration, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<CaseOperation>> ListCaseOperationsAsync(string caseId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<PlannerOrder>> ListOrdersAsync(string caseId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ProductionBatch>> ListBatchesAsync(string caseId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<byte[]?> GetCasePreviewAsync(string caseId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<PlanningBoardSnapshot> GetPlanningBoardAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<TimelineSnapshot> GetTimelineAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task AssignOrMoveOperationAsync(string batchOperationId, string machineId, int backlogPosition, string clientId, long editGeneration, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task UnassignOperationAsync(string batchOperationId, string clientId, long editGeneration, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public void Dispose() { }
    }
}
