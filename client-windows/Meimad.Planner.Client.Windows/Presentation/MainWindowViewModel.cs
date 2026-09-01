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
    private EditModeStatus? editStatus;
    private string clientId = string.Empty;
    private string healthLevel = "offline";
    private string healthHeadline = "Not connected";
    private string healthDetail = "Enter the factory Server address and connect.";
    private string modeLevel = "offline";
    private string modeHeadline = "View Mode unavailable";
    private string modeDetail = "Connect to read the server-owned Edit Mode state.";
    private bool isBusy;
    private bool hasPendingTransfer;
    private string pendingTransferText = string.Empty;

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
        NcCreatorQueue = new PreparationQueueViewModel(
            "PROGRAMMING_PENDING", "NC Creator — Programming Pending",
            "Assigned operations that do not yet have one current Machine-compatible NC release selection.");
        ToolRoomQueue = new PreparationQueueViewModel(
            "TOOL_PREPARATION_PENDING", "Tool Room Manager — Tool Preparation Pending",
            "NC-ready operations whose current Tool Table, capacity, or exact tool-offset readiness gate is incomplete.");
        SetupQueue = new PreparationQueueViewModel(
            "SETUP_PENDING", "Setup — Setup Pending",
            "Operations whose NC and Tool Room gates are complete and remain in the setup workflow.");
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
        RequestEditCommand = new AsyncCommand(
            RequestEditAsync,
            () => !IsBusy && editStatus?.State == ClientEditState.Viewer && HealthLevel == "healthy");
        ReleaseEditCommand = new AsyncCommand(
            ReleaseEditAsync,
            () => !IsBusy && editStatus?.State == ClientEditState.Editor);
        ApproveTransferCommand = new AsyncCommand(
            () => DecideTransferAsync(release: true),
            CanDecideTransfer);
        RejectTransferCommand = new AsyncCommand(
            () => DecideTransferAsync(release: false),
            CanDecideTransfer);
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

    public AsyncCommand RequestEditCommand { get; }

    public AsyncCommand ReleaseEditCommand { get; }

    public AsyncCommand ApproveTransferCommand { get; }

    public AsyncCommand RejectTransferCommand { get; }

    public AsyncCommand UndoCommand { get; }

    public AsyncCommand RedoCommand { get; }

    public CaseWorkspaceViewModel CaseWorkspace { get; }

    public MachinePlanningBoardViewModel MachinePlanningBoard { get; }

    public TimelineViewModel Timeline { get; }

    public SetupViewModel Setup { get; }

    public UserTerminalsViewModel UserTerminals { get; }

    public QcQueueViewModel QcQueue { get; }

    public PreparationQueueViewModel NcCreatorQueue { get; }

    public PreparationQueueViewModel ToolRoomQueue { get; }

    public PreparationQueueViewModel SetupQueue { get; }

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

    public bool HasPendingTransfer
    {
        get => hasPendingTransfer;
        private set => SetField(ref hasPendingTransfer, value);
    }

    public string PendingTransferText
    {
        get => pendingTransferText;
        private set => SetField(ref pendingTransferText, value);
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

    internal async Task RequestEditAsync()
    {
        if (apiClient is null)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            editStatus = await apiClient.RequestEditAsync(
                ClientId,
                activeSettings!.LocalUserId);
            ApplyEditStatus(editStatus);
        });
    }

    internal async Task ReleaseEditAsync()
    {
        if (apiClient is null || editStatus?.State != ClientEditState.Editor)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            editStatus = await apiClient.ReleaseEditAsync(ClientId, editStatus.Generation);
            ApplyEditStatus(editStatus);
        });
    }

    public void Dispose()
    {
        apiClient?.Dispose();
    }

    private async Task DecideTransferAsync(bool release)
    {
        if (apiClient is null
            || editStatus?.State != ClientEditState.Editor
            || editStatus.PendingRequest is null)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            editStatus = await apiClient.DecideTransferAsync(
                ClientId,
                editStatus.Generation,
                editStatus.PendingRequest.RequestId,
                release);
            ApplyEditStatus(editStatus);
        });
    }

    private async Task RefreshCoreAsync()
    {
        var health = await apiClient!.GetHealthAsync();
        editStatus = await apiClient.GetEditModeAsync(ClientId);
        HealthLevel = string.Equals(health.Status, "healthy", StringComparison.OrdinalIgnoreCase)
            ? "healthy"
            : "attention";
        HealthHeadline = $"Connected — {health.Status}";
        HealthDetail = $"{health.Service} {health.Version} • Server UTC {health.ServerTimeUtc:yyyy-MM-dd HH:mm:ss}";
        Setup.ApplyConnectionStatus(HealthHeadline, HealthDetail);
        ApplyEditStatus(editStatus);
        await Setup.EnsureLoadedAsync();
        await CaseWorkspace.EnsureLoadedAsync();
        await MachinePlanningBoard.EnsureLoadedAsync();
        await Timeline.EnsureLoadedAsync();
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
        await settingsStore.SaveAsync(settings);
        ApplySettings(settings);
        ReplaceApiClient(settings.ServerBaseUri);
        HealthLevel = "attention";
        HealthHeadline = "Connection not verified";
        HealthDetail = "Settings saved. Connect or refresh to verify the configured Server.";
        ModeLevel = "offline";
        ModeHeadline = "View Mode unavailable";
        ModeDetail = "Planning changes remain disabled until the Server confirms Edit Mode.";
        editStatus = null;
        Setup.ApplyConnectionStatus(HealthHeadline, HealthDetail);
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

    private void ApplyEditStatus(EditModeStatus status)
    {
        HasPendingTransfer = status.PendingRequest is not null
            && status.State == ClientEditState.Editor;
        PendingTransferText = HasPendingTransfer
            ? $"Client {status.PendingRequest!.RequesterClientId} requests Edit Mode. Decision deadline: {status.PendingRequest.DecisionDeadline.ToLocalTime():HH:mm:ss}."
            : string.Empty;

        switch (status.State)
        {
            case ClientEditState.Editor:
                ModeLevel = "editor";
                ModeHeadline = "🔓 Edit Mode — you are the editor";
                ModeDetail = $"Generation {status.Generation}. All planning changes are authorized by the Server.";
                break;
            case ClientEditState.RequestingEdit:
                ModeLevel = "requesting";
                ModeHeadline = "⏳ Requesting Edit Mode";
                ModeDetail = status.PendingRequest is null
                    ? "Waiting for the current editor."
                    : $"Waiting for {status.Holder?.UserId ?? "the current editor"} until {status.PendingRequest.DecisionDeadline.ToLocalTime():HH:mm:ss}.";
                break;
            default:
                ModeLevel = "viewer";
                ModeHeadline = "🔒 View Mode — read only";
                ModeDetail = status.Holder is null
                    ? "No editor currently holds the token. Request Edit Mode to make changes."
                    : $"Edit Mode held by: {DisplayHolder(status.Holder)}. Request Edit Mode to ask for transfer.";
                break;
        }

        RaiseCommandStates();
        CaseWorkspace.AttachSession(apiClient, ClientId, status);
        MachinePlanningBoard.AttachSession(apiClient, ClientId, status);
        Timeline.AttachSession(apiClient);
        Setup.AttachSession(apiClient, ClientId, status);
        UserTerminals.AttachSession(apiClient, ClientId, status);
        QcQueue.AttachSession(
            apiClient, ClientId, activeSettings?.LocalUserId ?? string.Empty, status);
        AttachPreparationQueues(apiClient);
    }

    private void SetOffline(string headline, string detail)
    {
        HealthLevel = "offline";
        HealthHeadline = headline;
        HealthDetail = detail;
        ModeLevel = "offline";
        ModeHeadline = "⚠ View Mode unavailable";
        ModeDetail = "Planning changes are disabled until the Server confirms Edit Mode.";
        HasPendingTransfer = false;
        PendingTransferText = string.Empty;
        editStatus = null;
        Setup.ApplyConnectionStatus(headline, detail);
        CaseWorkspace.AttachSession(apiClient, ClientId, null);
        MachinePlanningBoard.AttachSession(apiClient, ClientId, null);
        Timeline.AttachSession(apiClient);
        Setup.AttachSession(apiClient, ClientId, null);
        UserTerminals.AttachSession(apiClient, ClientId, null);
        QcQueue.AttachSession(
            apiClient, ClientId, activeSettings?.LocalUserId ?? string.Empty, null);
        AttachPreparationQueues(apiClient);
        RaiseCommandStates();
    }

    private string DisplayHolder(EditModeHolder holder) =>
        string.Equals(holder.ClientId, ClientId, StringComparison.Ordinal)
            ? activeSettings?.LocalUserName ?? holder.UserId
            : holder.UserId;

    private void ReplaceApiClient(Uri serverBaseUri)
    {
        apiClient?.Dispose();
        apiClient = apiClientFactory.Create(serverBaseUri);
        CaseWorkspace.AttachSession(apiClient, ClientId, null);
        MachinePlanningBoard.AttachSession(apiClient, ClientId, null);
        Timeline.AttachSession(apiClient);
        Setup.AttachSession(apiClient, ClientId, null);
        UserTerminals.AttachSession(apiClient, ClientId, null);
        QcQueue.AttachSession(
            apiClient, ClientId, activeSettings?.LocalUserId ?? string.Empty, null);
        AttachPreparationQueues(apiClient);
        RaiseCommandStates();
    }

    private void AttachPreparationQueues(IPlannerApiClient? client)
    {
        var userId = activeSettings?.LocalUserId ?? string.Empty;
        NcCreatorQueue.AttachSession(client, ClientId, userId);
        ToolRoomQueue.AttachSession(client, ClientId, userId);
        SetupQueue.AttachSession(client, ClientId, userId);
    }

    private bool CanDecideTransfer() => !IsBusy
        && editStatus?.State == ClientEditState.Editor
        && editStatus.PendingRequest is not null;

    private void RaiseCommandStates()
    {
        RequestEditCommand.RaiseCanExecuteChanged();
        ReleaseEditCommand.RaiseCanExecuteChanged();
        ApproveTransferCommand.RaiseCanExecuteChanged();
        RejectTransferCommand.RaiseCanExecuteChanged();
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
