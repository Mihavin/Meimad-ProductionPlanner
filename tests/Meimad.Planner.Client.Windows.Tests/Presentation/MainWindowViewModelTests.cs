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
    public async Task Initialize_asks_for_sign_in_then_applies_the_accounts_permissions()
    {
        var api = new FakeApiClient { Health = Healthy(), Account = Planner() };
        using var viewModel = new MainWindowViewModel(
            new FakeSettingsStore(Settings),
            new FakeApiClientFactory(api));
        var signInRequests = 0;
        viewModel.SignInRequired += (_, _) => signInRequests++;

        await viewModel.InitializeAsync();

        Assert.Equal("healthy", viewModel.HealthLevel);
        Assert.Equal(1, signInRequests);
        Assert.False(viewModel.IsSignedIn);
        Assert.Equal("offline", viewModel.ModeLevel);
        Assert.False(viewModel.MachinePlanningBoard.CanDrag);
        Assert.Equal("Miriam", viewModel.LastUserName);

        Assert.Null(await viewModel.SignInAsync("dana", "secret-1"));
        await viewModel.CompleteSignInAsync();

        Assert.Equal("session-dana", api.SessionToken);
        Assert.True(viewModel.IsSignedIn);
        Assert.Equal("editor", viewModel.ModeLevel);
        Assert.Contains("Dana Planner", viewModel.ModeHeadline, StringComparison.Ordinal);
        Assert.Contains("Planning Board", viewModel.ModeDetail, StringComparison.Ordinal);
        Assert.True(viewModel.MachinePlanningBoard.CanDrag);
        Assert.False(viewModel.Setup.IsEditor);
        Assert.False(viewModel.CanManageUsers);
        Assert.True(viewModel.SignOutCommand.CanExecute(null));
        Assert.Equal("dana", viewModel.LastUserName);
    }

    [Fact]
    public async Task Initialize_exposes_attention_state_while_connecting()
    {
        var healthCompletion = new TaskCompletionSource<ServerHealth>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeApiClient
        {
            HealthTask = healthCompletion.Task,
            Account = Planner()
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
        Assert.Equal("Not signed in", viewModel.ModeHeadline);
        Assert.False(viewModel.SignOutCommand.CanExecute(null));
        Assert.False(viewModel.MachinePlanningBoard.CanDrag);
    }

    [Fact]
    public async Task An_empty_server_creates_the_first_administrator_who_may_manage_users()
    {
        var api = new FakeApiClient { Health = Healthy(), HasAccounts = false };
        using var viewModel = new MainWindowViewModel(
            new FakeSettingsStore(Settings),
            new FakeApiClientFactory(api));

        await viewModel.InitializeAsync();

        Assert.True(viewModel.NeedsFirstAdministrator);
        Assert.Contains("first administrator", viewModel.ModeDetail, StringComparison.Ordinal);
        Assert.Null(await viewModel.CreateFirstAdministratorAsync("owner", "The Owner", "secret-1"));
        await viewModel.CompleteSignInAsync();

        Assert.False(viewModel.NeedsFirstAdministrator);
        Assert.True(viewModel.CanManageUsers);
        Assert.True(viewModel.Setup.IsEditor);
        Assert.Contains("Administrator", viewModel.ModeDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_temporary_password_is_replaced_before_the_screens_load()
    {
        var api = new FakeApiClient { Health = Healthy(), Account = Planner() with { MustChangePassword = true } };
        using var viewModel = new MainWindowViewModel(
            new FakeSettingsStore(Settings),
            new FakeApiClientFactory(api));
        await viewModel.InitializeAsync();

        Assert.Null(await viewModel.SignInAsync("dana", "temporary-1"));
        Assert.True(viewModel.Account!.MustChangePassword);
        Assert.Equal("The current password is wrong.", await viewModel.ChangePasswordAsync("wrong", "dana-secret"));
        Assert.Null(await viewModel.ChangePasswordAsync("temporary-1", "dana-secret"));
        api.Account = Planner();
        await viewModel.CompleteSignInAsync();

        Assert.True(viewModel.IsSignedIn);
        Assert.True(viewModel.MachinePlanningBoard.CanDrag);
    }

    [Fact]
    public async Task Sign_out_and_an_ended_session_both_ask_to_sign_in_again()
    {
        var api = new FakeApiClient { Health = Healthy(), Account = Planner() };
        using var viewModel = new MainWindowViewModel(
            new FakeSettingsStore(Settings),
            new FakeApiClientFactory(api));
        var signInRequests = 0;
        viewModel.SignInRequired += (_, _) => signInRequests++;
        await viewModel.InitializeAsync();
        await viewModel.SignInAsync("dana", "secret-1");
        await viewModel.CompleteSignInAsync();

        await viewModel.SignOutAsync();

        Assert.Equal(1, api.SignOutCount);
        Assert.Null(api.SessionToken);
        Assert.False(viewModel.IsSignedIn);
        Assert.False(viewModel.MachinePlanningBoard.CanDrag);
        Assert.Equal(2, signInRequests);

        // The session ends on the Server (12 hours without use): the next refresh notices.
        await viewModel.SignInAsync("dana", "secret-1");
        await viewModel.CompleteSignInAsync();
        api.SessionEnded = true;
        await viewModel.RefreshAsync();

        Assert.False(viewModel.IsSignedIn);
        Assert.Equal("Session ended", viewModel.ModeHeadline);
        Assert.Equal(3, signInRequests);
    }

    [Fact]
    public async Task A_closed_sign_in_is_not_asked_again_until_the_user_presses_sign_in()
    {
        var api = new FakeApiClient { Health = Healthy(), Account = Planner() };
        using var viewModel = new MainWindowViewModel(
            new FakeSettingsStore(Settings),
            new FakeApiClientFactory(api));
        var signInRequests = 0;
        viewModel.SignInRequired += (_, _) => signInRequests++;
        await viewModel.InitializeAsync();

        await viewModel.CompleteSignInAsync();
        await viewModel.RefreshAsync();
        Assert.Equal(1, signInRequests);

        Assert.True(viewModel.SignInCommand.CanExecute(null));
        viewModel.SignInCommand.Execute(null);
        Assert.Equal(2, signInRequests);
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
            Account = Planner(),
            Board = new PlanningBoardSnapshot(
                DateTimeOffset.UtcNow, "available", "Calculated", [], [operation], [machine])
        };
        using var viewModel = new MainWindowViewModel(
            new FakeSettingsStore(Settings),
            new FakeApiClientFactory(api));
        await viewModel.InitializeAsync();
        await viewModel.SignInAsync("dana", "secret-1");
        await viewModel.CompleteSignInAsync();
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

    [Fact]
    public async Task Assignment_mode_change_recalculates_the_existing_shared_timeline()
    {
        var operation = new PlanningBoardOperation(
            "operation-1", "batch-1", "B-1", "case-1", "PN-1", 10, "Mill",
            "mill", 60, 30, "not_started", "machine-1", 0, 2, ["SO-1"], 120) with
        {
            MachineAssignmentId = "assignment-1",
            AssignmentVersion = 2,
            PlanningMode = "manual"
        };
        var machine = new PlanningBoardMachine(
            "machine-1", "M-1", "Mill 1", "mill", "3-axis", [], true, [operation]);
        var api = new FakeApiClient
        {
            Health = Healthy(),
            Account = Planner(),
            Board = new PlanningBoardSnapshot(
                DateTimeOffset.UtcNow, "available", "Calculated", [], [], [machine])
        };
        using var viewModel = new MainWindowViewModel(
            new FakeSettingsStore(Settings),
            new FakeApiClientFactory(api));
        await viewModel.InitializeAsync();
        await viewModel.SignInAsync("dana", "secret-1");
        await viewModel.CompleteSignInAsync();
        var sharedTimeline = viewModel.Timeline;
        var timelineRequestsBeforeChange = api.TimelineRequestCount;

        await viewModel.MachinePlanningBoard.ChangePlanningModeAsync(
            viewModel.MachinePlanningBoard.Machines.Single().Backlog.Single(),
            "backward");
        await Task.Yield();

        Assert.Same(sharedTimeline, viewModel.Timeline);
        Assert.Equal("assignment-1", api.PlanningModeAssignmentId);
        Assert.Equal("backward", api.PlanningMode);
        Assert.Equal("backward", viewModel.MachinePlanningBoard.Machines.Single().Backlog.Single().PlanningMode);
        Assert.True(api.TimelineRequestCount > timelineRequestsBeforeChange);
        Assert.False(viewModel.MachinePlanningBoard.CanUndo);
    }

    private static ServerHealth Healthy() => new(
        "healthy", "Meimad Planner Server", "0.1.0", DateTimeOffset.UtcNow);

    private static SignedInAccount Planner() => new(
        "user-dana", "dana", "Dana Planner", false,
        [PlannerPermissions.PlanMachines, PlannerPermissions.ManageWorkOrders], false);

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

        internal SignedInAccount? Account { get; set; }

        internal bool HasAccounts { get; init; } = true;

        internal bool SessionEnded { get; set; }

        internal string? SessionToken { get; private set; }

        internal int SignOutCount { get; private set; }

        internal Exception? Failure { get; init; }

        internal PlanningBoardSnapshot? Board { get; set; }

        internal string? AssignedOperationId { get; private set; }

        internal string? PlanningModeAssignmentId { get; private set; }

        internal string? PlanningMode { get; private set; }

        public Task<ServerHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
            HealthTask
            ?? (Failure is null
                ? Task.FromResult(Health!)
                : Task.FromException<ServerHealth>(Failure));

        public void SetSessionToken(string? token) => SessionToken = token;

        public Task<AuthState> GetAuthStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthState(HasAccounts));

        public Task<SignInSession> SignInAsync(
            string userName,
            string password,
            string clientId,
            CancellationToken cancellationToken = default)
        {
            SessionEnded = false;
            return Task.FromResult(new SignInSession($"session-{userName}", DateTimeOffset.UtcNow.AddHours(12), Account!));
        }

        public Task<SignInSession> CreateFirstAdministratorAsync(
            string userName,
            string displayName,
            string password,
            string clientId,
            CancellationToken cancellationToken = default)
        {
            Account = new SignedInAccount("user-owner", userName, displayName, true, [], false);
            return Task.FromResult(new SignInSession($"session-{userName}", DateTimeOffset.UtcNow.AddHours(12), Account));
        }

        public Task SignOutAsync(CancellationToken cancellationToken = default)
        {
            SignOutCount++;
            return Task.CompletedTask;
        }

        public Task<SignedInAccount> GetSignedInAccountAsync(CancellationToken cancellationToken = default) =>
            SessionEnded
                ? Task.FromException<SignedInAccount>(new PlannerApiException(
                    System.Net.HttpStatusCode.Unauthorized, "sign_in_required", "Sign in to the Meimad Planner."))
                : Task.FromResult(Account!);

        public Task ChangePasswordAsync(
            string currentPassword,
            string newPassword,
            CancellationToken cancellationToken = default) =>
            currentPassword == "wrong"
                ? Task.FromException(new PlannerApiException(
                    System.Net.HttpStatusCode.UnprocessableEntity, "current_password_wrong", "The current password is wrong."))
                : Task.CompletedTask;

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

        public Task<MachineAssignment> ChangeMachineAssignmentPlanningModeAsync(
            string machineAssignmentId,
            int assignmentVersion,
            string planningMode,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            PlanningModeAssignmentId = machineAssignmentId;
            PlanningMode = planningMode;
            if (Board is not null)
            {
                Board = Board with
                {
                    Machines = Board.Machines.Select(machine => machine with
                    {
                        Backlog = machine.Backlog.Select(operation =>
                            operation.MachineAssignmentId == machineAssignmentId
                                ? operation with
                                {
                                    PlanningMode = planningMode,
                                    AssignmentVersion = assignmentVersion + 1
                                }
                                : operation).ToArray()
                    }).ToArray()
                };
            }

            return Task.FromResult(new MachineAssignment(
                machineAssignmentId, "operation-1", "machine-1", 0,
                assignmentVersion + 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                planningMode));
        }

        public void Dispose()
        {
        }
    }
}
