using System.Net.Http;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Configuration;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class MainWindowViewModelTests
{
    private static readonly ClientSettings Settings = ClientSettings.Create(
        "http://planner-server:5080/",
        "Miriam",
        "windows-client-01");

    [Fact]
    public async Task Initialize_shows_health_and_editor_state()
    {
        var api = new FakeApiClient
        {
            Health = new ServerHealth(
                "healthy",
                "Meimad Planner Server",
                "0.1.0",
                DateTimeOffset.Parse("2026-08-11T10:00:00Z")),
            EditMode = new EditModeStatus(
                ClientEditState.Editor,
                12,
                new EditModeHolder(
                    Settings.ClientId,
                    Settings.LocalUserName,
                    12,
                    DateTimeOffset.Parse("2026-08-11T09:00:00Z")),
                null,
                DateTimeOffset.Parse("2026-08-11T10:00:00Z"),
                30)
        };
        using var viewModel = new MainWindowViewModel(
            new FakeSettingsStore(Settings),
            new FakeApiClientFactory(api));

        await viewModel.InitializeAsync();

        Assert.Equal("healthy", viewModel.HealthLevel);
        Assert.Contains("Connected", viewModel.HealthHeadline, StringComparison.Ordinal);
        Assert.Equal("editor", viewModel.ModeLevel);
        Assert.Contains("you are the editor", viewModel.ModeHeadline, StringComparison.Ordinal);
        Assert.True(viewModel.ReleaseEditCommand.CanExecute(null));
        Assert.False(viewModel.RequestEditCommand.CanExecute(null));
    }

    [Fact]
    public async Task Initialize_exposes_attention_state_while_connecting()
    {
        var healthCompletion = new TaskCompletionSource<ServerHealth>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeApiClient
        {
            HealthTask = healthCompletion.Task,
            EditMode = Editor()
        };
        using var viewModel = new MainWindowViewModel(
            new FakeSettingsStore(Settings),
            new FakeApiClientFactory(api));

        var initialization = viewModel.InitializeAsync();
        await Task.Yield();

        Assert.Equal("attention", viewModel.HealthLevel);
        Assert.Contains("Server connection", viewModel.HealthHeadline, StringComparison.Ordinal);

        healthCompletion.SetResult(Healthy());
        await initialization;
        Assert.Equal("healthy", viewModel.HealthLevel);
    }

    [Fact]
    public async Task Unreachable_server_disables_editing_and_shows_explicit_status()
    {
        var api = new FakeApiClient
        {
            Failure = new HttpRequestException("socket details")
        };
        using var viewModel = new MainWindowViewModel(
            new FakeSettingsStore(Settings),
            new FakeApiClientFactory(api));

        await viewModel.InitializeAsync();

        Assert.Equal("offline", viewModel.HealthLevel);
        Assert.Equal("Server unavailable", viewModel.HealthHeadline);
        Assert.Equal("offline", viewModel.ModeLevel);
        Assert.Contains("disabled", viewModel.ModeDetail, StringComparison.Ordinal);
        Assert.False(viewModel.RequestEditCommand.CanExecute(null));
        Assert.False(viewModel.ReleaseEditCommand.CanExecute(null));
    }

    [Fact]
    public async Task Viewer_tooltip_identifies_the_current_editor_by_user_name()
    {
        var api = new FakeApiClient
        {
            Health = Healthy(),
            EditMode = new EditModeStatus(
                ClientEditState.Viewer,
                12,
                new EditModeHolder("windows-remote", "Rivka", 12, DateTimeOffset.UtcNow),
                null,
                DateTimeOffset.UtcNow,
                30)
        };
        using var viewModel = new MainWindowViewModel(
            new FakeSettingsStore(Settings),
            new FakeApiClientFactory(api));

        await viewModel.InitializeAsync();

        Assert.Contains("Edit Mode held by: Rivka", viewModel.ModeDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("windows-remote", viewModel.ModeDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compact_toggle_flow_requests_and_releases_existing_edit_token()
    {
        var viewer = new EditModeStatus(
            ClientEditState.Viewer,
            12,
            new EditModeHolder("windows-remote", "Rivka", 12, DateTimeOffset.UtcNow),
            null,
            DateTimeOffset.UtcNow,
            30);
        var editor = Editor();
        var api = new FakeApiClient
        {
            Health = Healthy(),
            EditMode = viewer,
            RequestEditResult = editor,
            ReleaseEditResult = viewer
        };
        using var viewModel = new MainWindowViewModel(
            new FakeSettingsStore(Settings),
            new FakeApiClientFactory(api));
        await viewModel.InitializeAsync();

        Assert.False(viewModel.Setup.IsEditor);
        Assert.True(viewModel.RequestEditCommand.CanExecute(null));
        await viewModel.RequestEditAsync();

        Assert.Equal(1, api.RequestEditCount);
        Assert.Equal("editor", viewModel.ModeLevel);
        Assert.True(viewModel.Setup.IsEditor);
        Assert.True(viewModel.ReleaseEditCommand.CanExecute(null));

        await viewModel.ReleaseEditAsync();

        Assert.Equal(1, api.ReleaseEditCount);
        Assert.Equal("viewer", viewModel.ModeLevel);
        Assert.False(viewModel.Setup.IsEditor);
    }

    [Fact]
    public async Task Accepted_backlog_change_invalidates_the_timeline_shared_with_auxiliary_window()
    {
        var operation = new PlanningBoardOperation(
            "operation-1", "batch-1", "B-1", "case-1", "PN-1", 10, "Mill",
            "mill", 60, 30, "not_started", null, null, 2, ["SO-1"], 120);
        var machine = new PlanningBoardMachine(
            "machine-1", "M-1", "Mill 1", "mill", "3-axis", [], true, []);
        var api = new FakeApiClient
        {
            Health = Healthy(),
            EditMode = Editor(),
            Board = new PlanningBoardSnapshot(
                DateTimeOffset.UtcNow, "available", "Calculated", [], [operation], [machine])
        };
        using var viewModel = new MainWindowViewModel(
            new FakeSettingsStore(Settings),
            new FakeApiClientFactory(api));
        await viewModel.InitializeAsync();
        var sharedTimeline = viewModel.Timeline;
        var timelineRequestsBeforeChange = api.TimelineRequestCount;

        await viewModel.MachinePlanningBoard.AssignOrMoveAsync(
            viewModel.MachinePlanningBoard.Pool.Single(),
            viewModel.MachinePlanningBoard.Machines.Single(),
            0);

        Assert.Same(sharedTimeline, viewModel.Timeline);
        await Task.Yield();
        Assert.Equal(timelineRequestsBeforeChange + 1, api.TimelineRequestCount);
        Assert.Contains("Server calculation loaded", sharedTimeline.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("operation-1", api.AssignedOperationId);
    }

    private static ServerHealth Healthy() => new(
        "healthy", "Meimad Planner Server", "0.1.0", DateTimeOffset.UtcNow);

    private static EditModeStatus Editor() => new(
        ClientEditState.Editor,
        12,
        new EditModeHolder(Settings.ClientId, Settings.LocalUserName, 12, DateTimeOffset.UtcNow),
        null,
        DateTimeOffset.UtcNow,
        30);

    private sealed class FakeSettingsStore : IClientSettingsStore
    {
        private readonly ClientSettings settings;

        internal FakeSettingsStore(ClientSettings settings)
        {
            this.settings = settings;
        }

        public Task<ClientSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(settings);

        public Task SaveAsync(
            ClientSettings settings,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeApiClientFactory : IPlannerApiClientFactory
    {
        private readonly IPlannerApiClient apiClient;

        internal FakeApiClientFactory(IPlannerApiClient apiClient)
        {
            this.apiClient = apiClient;
        }

        public IPlannerApiClient Create(Uri serverBaseUri) => apiClient;
    }

    private sealed class FakeApiClient : IPlannerApiClient
    {
        internal ServerHealth? Health { get; init; }

        internal Task<ServerHealth>? HealthTask { get; init; }

        internal EditModeStatus? EditMode { get; init; }

        internal EditModeStatus? RequestEditResult { get; init; }

        internal EditModeStatus? ReleaseEditResult { get; init; }

        internal int RequestEditCount { get; private set; }

        internal int ReleaseEditCount { get; private set; }

        internal Exception? Failure { get; init; }

        internal PlanningBoardSnapshot? Board { get; init; }

        internal string? AssignedOperationId { get; private set; }

        public Task<ServerHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
            HealthTask
            ?? (Failure is null
                ? Task.FromResult(Health!)
                : Task.FromException<ServerHealth>(Failure));

        public Task<EditModeStatus> GetEditModeAsync(
            string clientId,
            CancellationToken cancellationToken = default) => Task.FromResult(EditMode!);

        public Task<EditModeStatus> RequestEditAsync(
            string clientId,
            string userId,
            CancellationToken cancellationToken = default)
        {
            RequestEditCount++;
            return Task.FromResult(RequestEditResult ?? EditMode!);
        }

        public Task<EditModeStatus> ReleaseEditAsync(
            string clientId,
            long generation,
            CancellationToken cancellationToken = default)
        {
            ReleaseEditCount++;
            return Task.FromResult(ReleaseEditResult ?? EditMode!);
        }

        public Task<EditModeStatus> DecideTransferAsync(
            string clientId,
            long generation,
            string requestId,
            bool release,
            CancellationToken cancellationToken = default) => Task.FromResult(EditMode!);

        public Task<IReadOnlyList<PlannerCase>> ListCasesAsync(
            CaseQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerCase>>([]);

        public Task<CaseResource> GetCaseAsync(
            string caseId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CaseResource> UpdateCaseAsync(
            string caseId,
            CaseUpdate update,
            string entityTag,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CaseOperation>> ListCaseOperationsAsync(
            string caseId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CaseOperation>>([]);

        public Task<IReadOnlyList<PlannerOrder>> ListOrdersAsync(
            string caseId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerOrder>>([]);

        public Task<IReadOnlyList<ProductionBatch>> ListBatchesAsync(
            string caseId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProductionBatch>>([]);

        public Task<byte[]?> GetCasePreviewAsync(
            string caseId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);

        public Task<PlanningBoardSnapshot> GetPlanningBoardAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Board ?? new PlanningBoardSnapshot(
                DateTimeOffset.UtcNow,
                "unavailable",
                "Not implemented.",
                [],
                [],
                []));

        public Task<TimelineSnapshot> GetTimelineAsync(
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken = default)
        {
            TimelineRequestCount++;
            return Task.FromResult(new TimelineSnapshot(
                DateTimeOffset.UtcNow, from, to, [], [], [], []));
        }

        public int TimelineRequestCount { get; private set; }

        public Task AssignOrMoveOperationAsync(
            string batchOperationId,
            string machineId,
            int backlogPosition,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            AssignedOperationId = batchOperationId;
            return Task.CompletedTask;
        }

        public Task UnassignOperationAsync(
            string batchOperationId,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
