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
    public async Task Initialize_shows_health_local_identity_and_editor_state()
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
        Assert.Contains("Miriam", viewModel.LocalIdentityText, StringComparison.Ordinal);
        Assert.True(viewModel.ReleaseEditCommand.CanExecute(null));
        Assert.False(viewModel.RequestEditCommand.CanExecute(null));
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

        internal EditModeStatus? EditMode { get; init; }

        internal Exception? Failure { get; init; }

        public Task<ServerHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
            Failure is null
                ? Task.FromResult(Health!)
                : Task.FromException<ServerHealth>(Failure);

        public Task<EditModeStatus> GetEditModeAsync(
            string clientId,
            CancellationToken cancellationToken = default) => Task.FromResult(EditMode!);

        public Task<EditModeStatus> RequestEditAsync(
            string clientId,
            string userId,
            CancellationToken cancellationToken = default) => Task.FromResult(EditMode!);

        public Task<EditModeStatus> ReleaseEditAsync(
            string clientId,
            long generation,
            CancellationToken cancellationToken = default) => Task.FromResult(EditMode!);

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
            Task.FromResult(new PlanningBoardSnapshot(
                DateTimeOffset.UtcNow,
                "unavailable",
                "Not implemented.",
                [],
                [],
                []));

        public Task<TimelineSnapshot> GetTimelineAsync(
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TimelineSnapshot(
                DateTimeOffset.UtcNow, from, to, [], [], [], []));

        public Task AssignOrMoveOperationAsync(
            string batchOperationId,
            string machineId,
            int backlogPosition,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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
