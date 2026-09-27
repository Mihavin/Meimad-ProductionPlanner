using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Configuration;

namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IClientSettingsStore settingsStore;
    private readonly IPlannerApiClientFactory apiClientFactory;
    private IPlannerApiClient? apiClient;
    private ClientSettings? activeSettings;
    private SignedInAccount? account;
    private string clientId = string.Empty;
    private string healthLevel = "offline";
    private string healthHeadline = "Not connected";
    private string healthDetail = "Enter the factory Server address and connect.";
    private string modeLevel = "offline";
    private string modeHeadline = "Not signed in";
    private string modeDetail = "Connect to the Server and sign in.";
    private bool isBusy;
    private bool needsFirstAdministrator;
    private bool signInRequested;
    private bool signInDeclined;

    internal MainWindowViewModel(
        IClientSettingsStore settingsStore,
        IPlannerApiClientFactory apiClientFactory,
        Func<AssignmentOverridePrompt, string?>? requestAssignmentOverrideReason = null)
    {
        this.settingsStore = settingsStore;
        this.apiClientFactory = apiClientFactory;
        Setup = new SetupViewModel(
            ConnectAsync,
            SaveConnectionAsync,
            RefreshAsync,
            () => !IsBusy,
            () => !IsBusy && apiClient is not null);
        UserTerminals = new UserTerminalsViewModel();
        QcQueue = new QcQueueViewModel();
        MaterialOrders = new MaterialOrdersViewModel();
        ToolRequirements = new ToolRequirementsViewModel();
        NcCreatorQueue = new PreparationQueueViewModel(
            "PROGRAMMING_PENDING", "NC Creator — Programming Pending",
            "Assigned operations that do not yet have one current Machine-compatible NC release selection.");
        ToolRoomQueue = new PreparationQueueViewModel(
            "TOOL_PREPARATION_PENDING", "Tool Room Manager — Tool Preparation Pending",
            "NC-ready operations whose current Tool Table, capacity, or exact tool-offset readiness gate is incomplete.");
        SetupQueue = new PreparationQueueViewModel(
            "SETUP_PENDING", "Setup — Setup Pending",
            "Operations whose NC and Tool Room gates are complete and remain in the setup workflow.");
        ToolCatalog = new ToolCatalog.ToolCatalogViewModel();
        UserAdministration = new UserAdministrationViewModel();
        CaseWorkspace = new CaseWorkspaceViewModel(new WorkingFolderLauncher());
        MachinePlanningBoard = new MachinePlanningBoardViewModel(requestAssignmentOverrideReason);
        Timeline = new TimelineViewModel();
        MachinePlanningBoard.HistoryChanged += (_, _) => RaiseCommandStates();
        CaseWorkspace.PlanChanged += (_, _) => RefreshTimelineAfterPlanChange();
        MachinePlanningBoard.PlanChanged += (_, _) =>
        {
            CaseWorkspace.InvalidateSelectedDetails();
            RefreshTimelineAfterPlanChange();
        };
        Setup.ConfigurationChanged += (_, _) =>
        {
            CaseWorkspace.InvalidateSelectedDetails();
            RefreshTimelineAfterPlanChange();
            _ = MachinePlanningBoard.RefreshAsync();
        };
        Setup.LegacyImport.ImportCommitted += (_, _) =>
        {
            CaseWorkspace.InvalidateSelectedDetails();
            _ = CaseWorkspace.LoadCasesAsync();
            _ = MachinePlanningBoard.RefreshAsync();
            RefreshTimelineAfterPlanChange();
        };
        SignOutCommand = new AsyncCommand(SignOutAsync, () => !IsBusy && account is not null);
        SignInCommand = new AsyncCommand(() =>
        {
            signInDeclined = false;
            RequestSignIn();
            return Task.CompletedTask;
        }, () => !IsBusy && account is null && apiClient is not null);
        UndoCommand = new AsyncCommand(
            MachinePlanningBoard.UndoAsync,
            () => !IsBusy && MachinePlanningBoard.CanUndo);
        RedoCommand = new AsyncCommand(
            MachinePlanningBoard.RedoAsync,
            () => !IsBusy && MachinePlanningBoard.CanRedo);
    }

    private void RefreshTimelineAfterPlanChange()
    {
        Timeline.Invalidate();
        _ = Timeline.EnsureLoadedAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AsyncCommand SignOutCommand { get; }

    public AsyncCommand SignInCommand { get; }

    /// <summary>
    /// Raised when the Server needs a signed-in account: no session yet, the session ended, or the
    /// administrator set a temporary password that must be changed. The window shows the sign-in.
    /// </summary>
    public event EventHandler? SignInRequired;

    public UserAdministrationViewModel UserAdministration { get; }

    /// <summary>The signed-in account, or null before sign-in.</summary>
    internal SignedInAccount? Account => account;

    public bool IsSignedIn => account is not null;

    public bool CanManageUsers => account?.Has(PlannerPermissions.ManageUsers) == true;

    /// <summary>The Server has no account yet: the sign-in window creates the first administrator.</summary>
    public bool NeedsFirstAdministrator
    {
        get => needsFirstAdministrator;
        private set => SetField(ref needsFirstAdministrator, value);
    }

    /// <summary>The user name to offer in the sign-in window: the last one that signed in here.</summary>
    internal string LastUserName => activeSettings?.LocalUserName ?? string.Empty;

    public AsyncCommand UndoCommand { get; }

    public AsyncCommand RedoCommand { get; }

    public CaseWorkspaceViewModel CaseWorkspace { get; }

    public MachinePlanningBoardViewModel MachinePlanningBoard { get; }

    public TimelineViewModel Timeline { get; }

    public SetupViewModel Setup { get; }

    public UserTerminalsViewModel UserTerminals { get; }

    public QcQueueViewModel QcQueue { get; }

    public MaterialOrdersViewModel MaterialOrders { get; }

    public ToolRequirementsViewModel ToolRequirements { get; }

    public PreparationQueueViewModel NcCreatorQueue { get; }

    public PreparationQueueViewModel ToolRoomQueue { get; }

    public PreparationQueueViewModel SetupQueue { get; }

    public ToolCatalog.ToolCatalogViewModel ToolCatalog { get; }

    public string ClientId
    {
        get => clientId;
        private set => SetField(ref clientId, value);
    }

    public string HealthLevel
    {
        get => healthLevel;
        private set => SetField(ref healthLevel, value);
    }

    public string HealthHeadline
    {
        get => healthHeadline;
        private set => SetField(ref healthHeadline, value);
    }

    public string HealthDetail
    {
        get => healthDetail;
        private set => SetField(ref healthDetail, value);
    }

    public string ModeLevel
    {
        get => modeLevel;
        private set => SetField(ref modeLevel, value);
    }

    public string ModeHeadline
    {
        get => modeHeadline;
        private set => SetField(ref modeHeadline, value);
    }

    public string ModeDetail
    {
        get => modeDetail;
        private set => SetField(ref modeDetail, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetField(ref isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }


    internal async Task InitializeAsync()
    {
        try
        {
            var settings = await settingsStore.LoadAsync();
            ApplySettings(settings);
            ReplaceApiClient(settings.ServerBaseUri);
            await RefreshAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            var defaults = ClientSettings.Default();
            ApplySettings(defaults);
            SetOffline("Local settings unavailable", FriendlyMessage(exception));
        }
    }

    internal async Task ConnectAsync()
    {
        if (IsBusy)
        {
            return;
        }

        SetConnecting("Connecting to Server");
        await RunBusyAsync(async () =>
        {
            await SaveConnectionCoreAsync();
            await RefreshCoreAsync();
        });
    }

    internal Task SaveConnectionAsync() => RunBusyAsync(SaveConnectionCoreAsync);

    internal async Task RefreshAsync()
    {
        if (apiClient is null || IsBusy)
        {
            return;
        }

        SetConnecting("Refreshing Server connection");
        await RunBusyAsync(RefreshCoreAsync);
    }

    /// <summary>Signs in; returns an error to show in the sign-in window, or null when signed in.</summary>
    internal Task<string?> SignInAsync(string userName, string password) =>
        StartSessionAsync(api => api.SignInAsync(userName.Trim(), password, ClientId));

    /// <summary>Creates the first administrator of an empty Server and signs in as that account.</summary>
    internal Task<string?> CreateFirstAdministratorAsync(string userName, string displayName, string password) =>
        StartSessionAsync(api => api.CreateFirstAdministratorAsync(userName.Trim(), displayName.Trim(), password, ClientId));

    /// <summary>Changes the signed-in user's password; returns an error to show, or null.</summary>
    internal async Task<string?> ChangePasswordAsync(string currentPassword, string newPassword)
    {
        if (apiClient is null) return "Connect to the Server first.";
        try
        {
            await apiClient.ChangePasswordAsync(currentPassword, newPassword);
            if (account is not null) account = account with { MustChangePassword = false };
            return null;
        }
        catch (PlannerApiException exception)
        {
            return exception.Message;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return FriendlyMessage(exception);
        }
    }

    /// <summary>Called by the sign-in window when it closes, signed in or not.</summary>
    internal async Task CompleteSignInAsync()
    {
        signInRequested = false;
        // Closed without signing in: wait for the Sign in button instead of asking every refresh.
        signInDeclined = account is null;
        RaiseCommandStates();
        if (account is { MustChangePassword: false })
        {
            await RefreshAsync();
        }
    }

    internal async Task SignOutAsync()
    {
        if (apiClient is null) return;
        try
        {
            await apiClient.SignOutAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            // The session is dropped locally in any case.
        }

        EndSession("Signed out", "Sign in to continue.");
    }

    private async Task<string?> StartSessionAsync(Func<IPlannerApiClient, Task<SignInSession>> start)
    {
        if (apiClient is null) return "Connect to the Server first.";
        try
        {
            var session = await start(apiClient);
            apiClient.SetSessionToken(session.Token);
            account = session.User;
            NeedsFirstAdministrator = false;
            await RememberUserNameAsync(session.User.UserName);
            return null;
        }
        catch (PlannerApiException exception)
        {
            return exception.Message;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return FriendlyMessage(exception);
        }
    }

    private async Task RememberUserNameAsync(string userName)
    {
        if (activeSettings is null || string.Equals(activeSettings.LocalUserName, userName, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var settings = activeSettings with { LocalUserName = userName };
            await settingsStore.SaveAsync(settings);
            ApplySettings(settings);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            // Only the sign-in prefill is lost.
        }
    }

    /// <summary>Drops the session and every screen's rights, and asks for a new sign-in.</summary>
    private void EndSession(string headline, string detail)
    {
        account = null;
        signInDeclined = false;
        apiClient?.SetSessionToken(null);
        ModeLevel = "offline";
        ModeHeadline = headline;
        ModeDetail = detail;
        AttachSessions(null);
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(CanManageUsers));
        RaiseCommandStates();
        RequestSignIn();
    }

    private void RequestSignIn()
    {
        if (signInRequested || signInDeclined) return;
        signInRequested = true;
        SignInRequired?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        apiClient?.Dispose();
    }

    private async Task RefreshCoreAsync()
    {
        var health = await apiClient!.GetHealthAsync();
        HealthLevel = string.Equals(health.Status, "healthy", StringComparison.OrdinalIgnoreCase)
            ? "healthy"
            : "attention";
        HealthHeadline = $"Connected — {health.Status}";
        HealthDetail = $"{health.Service} {health.Version} • Server UTC {health.ServerTimeUtc:yyyy-MM-dd HH:mm:ss}";
        Setup.ApplyConnectionStatus(HealthHeadline, HealthDetail);
        await CheckClientUpdateAsync();
        if (account is null)
        {
            if (signInRequested) return;
            NeedsFirstAdministrator = !(await apiClient.GetAuthStateAsync()).HasAccounts;
            ModeLevel = "offline";
            ModeHeadline = "Not signed in";
            ModeDetail = NeedsFirstAdministrator
                ? "The Server has no accounts yet: create the first administrator."
                : "Sign in to continue.";
            RequestSignIn();
            return;
        }

        try
        {
            // Re-read the account: an administrator may have changed its user types meanwhile.
            account = await apiClient.GetSignedInAccountAsync();
        }
        catch (PlannerApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            EndSession("Session ended", "Your session ended after 12 hours without use or was ended by an administrator. Sign in again.");
            return;
        }
        catch (PlannerApiException exception) when (exception.Code == "password_change_required")
        {
            account = account with { MustChangePassword = true };
        }

        if (account.MustChangePassword)
        {
            RequestSignIn();
            return;
        }

        ApplyAccount(account);
        await Setup.EnsureLoadedAsync();
        await CaseWorkspace.EnsureLoadedAsync();
        await MachinePlanningBoard.EnsureLoadedAsync();
        await Timeline.EnsureLoadedAsync();
    }

    private bool clientUpdateChecked;

    /// <summary>Raised once per session when the Server distributes a newer client than the running one.</summary>
    public event EventHandler<ClientUpdateAvailableEventArgs>? ClientUpdateAvailable;

    /// <summary>The last client/Server version-pair decision, for diagnostics and tests.</summary>
    public ClientUpdateDecision? LastClientUpdateDecision { get; private set; }

    private async Task CheckClientUpdateAsync()
    {
        if (clientUpdateChecked || apiClient is null)
        {
            return;
        }

        clientUpdateChecked = true;
        var api = apiClient;
        ClientInstallerManifest manifest;
        try
        {
            manifest = await api.GetClientInstallerManifestAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A Server older than 0.1.117 has no installer endpoint, and a transient failure must
            // never break the session; the check simply runs again at the next client start.
            return;
        }

        var decision = ClientUpdatePolicy.Evaluate(ClientUpdatePolicy.RunningVersion, manifest);
        LastClientUpdateDecision = decision;
        switch (decision.Action)
        {
            case ClientUpdateAction.Install:
                ClientUpdateAvailable?.Invoke(this, new ClientUpdateAvailableEventArgs(
                    decision,
                    manifest,
                    (folder, progress, token) => api.DownloadClientInstallerAsync(
                        folder, manifest.Sha256 ?? string.Empty, progress, token)));
                break;
            case ClientUpdateAction.ClientNewerThanServer:
            case ClientUpdateAction.InstallerMissing:
                HealthLevel = "attention";
                HealthDetail = decision.Message;
                Setup.ApplyConnectionStatus(HealthHeadline, HealthDetail);
                break;
        }
    }

    private async Task RunBusyAsync(Func<Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await operation();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetOffline("Server unavailable", FriendlyMessage(exception));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveConnectionCoreAsync()
    {
        var settings = ClientSettings.Create(Setup.ServerAddress, Setup.LocalUserName, ClientId);
        var serverChanged = activeSettings is null || settings.ServerBaseUri != activeSettings.ServerBaseUri;
        await settingsStore.SaveAsync(settings);
        ApplySettings(settings);
        HealthLevel = "attention";
        HealthHeadline = "Connection not verified";
        HealthDetail = "Settings saved. Connect or refresh to verify the configured Server.";
        Setup.ApplyConnectionStatus(HealthHeadline, HealthDetail);
        if (serverChanged)
        {
            // Another Server knows other accounts: sign in there.
            account = null;
            ReplaceApiClient(settings.ServerBaseUri);
            ModeLevel = "offline";
            ModeHeadline = "Not signed in";
            ModeDetail = "Connect to the Server and sign in.";
            OnPropertyChanged(nameof(IsSignedIn));
            OnPropertyChanged(nameof(CanManageUsers));
        }
        RaiseCommandStates();
    }

    private void ApplySettings(ClientSettings settings)
    {
        activeSettings = settings;
        Setup.ApplyConnectionSettings(settings.ServerBaseUri.AbsoluteUri, settings.LocalUserName);
        ClientId = settings.ClientId;
    }

    private void SetConnecting(string headline)
    {
        HealthLevel = "attention";
        HealthHeadline = headline;
        HealthDetail = "Waiting for the configured Server to respond.";
        Setup.ApplyConnectionStatus(HealthHeadline, HealthDetail);
    }

    private static readonly (string Permission, string Name)[] PermissionNames =
    [
        (PlannerPermissions.EditCases, "Cases"),
        (PlannerPermissions.ManageWorkOrders, "Work Orders"),
        (PlannerPermissions.ReleaseNc, "NC release"),
        (PlannerPermissions.PlanMachines, "Planning Board"),
        (PlannerPermissions.RunOperations, "Production"),
        (PlannerPermissions.VerifyMaterials, "Materials"),
        (PlannerPermissions.DecideQc, "QC"),
        (PlannerPermissions.PrepareTools, "Tool Room"),
        (PlannerPermissions.EditToolLibrary, "Tool library"),
        (PlannerPermissions.ManageSetup, "Setup"),
        (PlannerPermissions.ManageUsers, "Users")
    ];

    /// <summary>Shows who is signed in and gives each screen the rights of the account's user types.</summary>
    private void ApplyAccount(SignedInAccount signedIn)
    {
        var allowed = PermissionNames.Where(item => signedIn.Has(item.Permission)).Select(item => item.Name).ToArray();
        ModeLevel = allowed.Length > 0 ? "editor" : "viewer";
        ModeHeadline = $"👤 {signedIn.DisplayName}";
        ModeDetail = signedIn.IsAdministrator
            ? $"{signedIn.UserName} • Administrator: may change everything."
            : allowed.Length == 0
                ? $"{signedIn.UserName} • May view; no changes."
                : $"{signedIn.UserName} • May change: {string.Join(", ", allowed)}.";
        AttachSessions(signedIn);
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(CanManageUsers));
        RaiseCommandStates();
    }

    /// <summary>
    /// The screens predate accounts and take an Edit Mode status; each now gets "editor" when the
    /// account holds a permission of that screen. The Server checks every change again.
    /// </summary>
    private void AttachSessions(SignedInAccount? signedIn)
    {
        EditModeStatus? For(params string[] permissions) => signedIn is null
            ? null
            : new EditModeStatus(
                permissions.Any(signedIn.Has) ? ClientEditState.Editor : ClientEditState.Viewer,
                1, null, null, DateTimeOffset.UtcNow, 0);

        var userId = signedIn?.UserName ?? string.Empty;
        CaseWorkspace.AttachSession(apiClient, ClientId, For(
            PlannerPermissions.EditCases, PlannerPermissions.ManageWorkOrders,
            PlannerPermissions.ReleaseNc, PlannerPermissions.VerifyMaterials));
        MachinePlanningBoard.AttachSession(apiClient, ClientId, For(
            PlannerPermissions.PlanMachines, PlannerPermissions.RunOperations));
        Timeline.AttachSession(apiClient, ClientId, For(PlannerPermissions.PlanMachines));
        Setup.AttachSession(apiClient, ClientId, For(PlannerPermissions.ManageSetup));
        UserTerminals.AttachSession(apiClient, ClientId, For(PlannerPermissions.ManageSetup));
        QcQueue.AttachSession(apiClient, ClientId, userId, For(PlannerPermissions.DecideQc));
        MaterialOrders.AttachSession(apiClient);
        ToolRequirements.AttachSession(apiClient);
        UserAdministration.AttachSession(
            signedIn?.Has(PlannerPermissions.ManageUsers) == true ? apiClient : null, userId);
        AttachPreparationQueues(apiClient, userId);
    }

    private void SetOffline(string headline, string detail)
    {
        HealthLevel = "offline";
        HealthHeadline = headline;
        HealthDetail = detail;
        Setup.ApplyConnectionStatus(headline, detail);
        if (account is null)
        {
            ModeLevel = "offline";
            ModeHeadline = "Not signed in";
            ModeDetail = "Connect to the Server and sign in.";
        }
        // A signed-in session survives a short Server outage; the next refresh re-reads the account.
        AttachSessions(account);
        RaiseCommandStates();
    }

    private void ReplaceApiClient(Uri serverBaseUri)
    {
        apiClient?.Dispose();
        apiClient = apiClientFactory.Create(serverBaseUri);
        account = null;
        AttachSessions(null);
        RaiseCommandStates();
    }

    private void AttachPreparationQueues(IPlannerApiClient? client, string userId)
    {
        NcCreatorQueue.AttachSession(client, ClientId, userId);
        ToolRoomQueue.AttachSession(client, ClientId, userId);
        SetupQueue.AttachSession(client, ClientId, userId);
        ToolCatalog.AttachSession(client, ClientId, userId);
    }

    private void RaiseCommandStates()
    {
        SignOutCommand.RaiseCanExecuteChanged();
        SignInCommand.RaiseCanExecuteChanged();
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
        Setup.UpdateConnectionCommandStates();
    }

    private static bool IsExpected(Exception exception) => exception is
        ClientSettingsException or
        PlannerApiException or
        PlannerProtocolException or
        HttpRequestException or
        TaskCanceledException or
        IOException or
        UnauthorizedAccessException;

    private static string FriendlyMessage(Exception exception) => exception switch
    {
        TaskCanceledException => "The Server did not respond before the client timeout.",
        HttpRequestException => "The configured Server could not be reached.",
        PlannerApiException api => $"{api.Message} ({api.Code})",
        _ => exception.Message
    };

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
