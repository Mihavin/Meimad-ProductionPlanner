using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class QcQueueViewModelTests
{
    [Fact]
    public async Task View_mode_loads_queue_details_but_blocks_decisions()
    {
        var api = new FakeApiClient([Item()]);
        var viewModel = new QcQueueViewModel();
        viewModel.AttachSession(api, "viewer-client", "viewer-user", Status(ClientEditState.Viewer));

        await viewModel.RefreshAsync();
        viewModel.Selected = Assert.Single(viewModel.Items);

        Assert.Equal("M-1 — Mill One", viewModel.Selected.MachineText);
        Assert.Equal("Setup Worker", viewModel.Selected.SetupistText);
        Assert.False(viewModel.PassCommand.CanExecute(null));
        Assert.False(viewModel.FailCommand.CanExecute(null));
        Assert.Contains("monitoring only", viewModel.EditModeText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("PASS", "READY_FOR_PRODUCTION")]
    [InlineData("FAIL", "IN_SETUP_RUN")]
    public async Task Editor_decision_sends_identity_generation_and_optional_comment(
        string decision,
        string resultingStatus)
    {
        var api = new FakeApiClient([Item()]);
        var viewModel = new QcQueueViewModel();
        viewModel.AttachSession(api, "qc-client", "qc-user", Status(ClientEditState.Editor));
        await viewModel.RefreshAsync();
        viewModel.Selected = Assert.Single(viewModel.Items);
        viewModel.Reason = "  First article checked  ";

        await viewModel.DecideAsync(decision);

        Assert.NotNull(api.Decision);
        Assert.Equal(decision, api.Decision!.Request.Decision);
        Assert.Equal("First article checked", api.Decision.Request.Reason);
        Assert.Equal("qc-client", api.Decision.ClientId);
        Assert.Equal("qc-user", api.Decision.UserId);
        Assert.Equal(7, api.Decision.Generation);
        Assert.Empty(viewModel.Items);
        Assert.Contains(resultingStatus, viewModel.Status, StringComparison.Ordinal);
    }

    private static QcQueueItem Item() => new(
        "run-1", "machine-1", "M-1", "Mill One", "PN-100",
        "OP10 Rough", DateTimeOffset.Parse("2026-08-26T10:00:00Z"),
        "setup-1", "Setup Worker");

    private static EditModeStatus Status(ClientEditState state) => new(
        state, 7, null, null, DateTimeOffset.Parse("2026-08-26T10:05:00Z"), 30);

    private sealed class FakeApiClient(IReadOnlyList<QcQueueItem> initialItems)
        : IPlannerApiClient
    {
        private IReadOnlyList<QcQueueItem> items = initialItems;

        internal DecisionCall? Decision { get; private set; }

        public Task<IReadOnlyList<QcQueueItem>> ListQcQueueAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(items);

        public Task<QcDecisionResult> DecideQcAsync(
            string productionRunId,
            QcDecisionRequest request,
            string clientId,
            string userId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            Decision = new(request, clientId, userId, editGeneration);
            items = [];
            var at = DateTimeOffset.Parse("2026-08-26T10:06:00Z");
            return Task.FromResult(new QcDecisionResult(
                "event-1", productionRunId, request.Decision,
                request.Decision == "PASS" ? "READY_FOR_PRODUCTION" : "IN_SETUP_RUN",
                userId, request.Reason, at, request.Decision == "PASS" ? at : null));
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

    private sealed record DecisionCall(
        QcDecisionRequest Request,
        string ClientId,
        string UserId,
        long Generation);
}
