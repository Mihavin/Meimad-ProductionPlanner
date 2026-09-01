using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed class PreparationQueueViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? api;
    private string clientId = string.Empty;
    private string userId = string.Empty;
    private bool isBusy;
    private PreparationQueueItem? selected;
    private string status;

    internal PreparationQueueViewModel(string stage, string title, string description)
    {
        Stage = stage;
        Title = title;
        Description = description;
        status = $"Connect to view {title.ToLowerInvariant()}.";
        RefreshCommand = new AsyncCommand(RefreshAsync, () => api is not null && !isBusy);
        OpenCaseCommand = new AsyncCommand(() => RequestActionAsync("OPEN_CASE"), CanUseSelected);
        OpenOperationCommand = new AsyncCommand(() => RequestActionAsync("OPEN_OPERATION"), CanUseSelected);
        UploadGCodeCommand = new AsyncCommand(() => RequestActionAsync("UPLOAD_GCODE"),
            () => CanUseSelected() && Stage == "PROGRAMMING_PENDING");
        OpenToolTableCommand = new AsyncCommand(OpenToolTableAsync,
            () => CanUseSelected() && Stage == "TOOL_PREPARATION_PENDING");
        ViewNcFileCommand = new AsyncCommand(ViewNcFileAsync,
            () => CanUseSelected() && Stage == "TOOL_PREPARATION_PENDING");
        CreateProductionPackageCommand = new AsyncCommand(CreateProductionPackageAsync,
            () => CanUseSelected() && Stage == "TOOL_PREPARATION_PENDING");
        OpenProductionPackageCommand = new AsyncCommand(OpenProductionPackageAsync,
            () => CanUseSelected() && Stage == "SETUP_PENDING");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal event EventHandler<PreparationQueueActionRequest>? ActionRequested;
    public string Stage { get; }
    public string Title { get; }
    public string Description { get; }
    public ObservableCollection<PreparationQueueItem> Items { get; } = [];
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand OpenCaseCommand { get; }
    public AsyncCommand OpenOperationCommand { get; }
    public AsyncCommand UploadGCodeCommand { get; }
    public AsyncCommand OpenToolTableCommand { get; }
    public AsyncCommand ViewNcFileCommand { get; }
    public AsyncCommand CreateProductionPackageCommand { get; }
    public AsyncCommand OpenProductionPackageCommand { get; }

    public PreparationQueueItem? Selected
    {
        get => selected;
        set
        {
            if (Set(ref selected, value)) RaiseActionStates();
        }
    }

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    internal void AttachSession(IPlannerApiClient? client, string? activeClientId = null, string? activeUserId = null)
    {
        api = client;
        clientId = activeClientId ?? string.Empty;
        userId = activeUserId ?? string.Empty;
        RefreshCommand.RaiseCanExecuteChanged();
        RaiseActionStates();
        if (api is not null) _ = RefreshAsync();
    }

    private bool CanUseSelected() => api is not null && !isBusy && Selected is not null;

    private Task RequestActionAsync(string kind)
    {
        if (Selected is not null)
            ActionRequested?.Invoke(this, new(kind, Selected, null));
        return Task.CompletedTask;
    }

    private async Task OpenToolTableAsync()
    {
        if (api is null || Selected?.CaseId is null || Selected.CaseOperationId is null
            || Selected.ToolTableReleaseId is null) return;
        await RunActionAsync(async () =>
        {
            var bytes = await api.ReadToolTableFileAsync(
                Selected.CaseId, Selected.CaseOperationId, Selected.ToolTableReleaseId);
            ActionRequested?.Invoke(this, new("OPEN_TOOL_TABLE", Selected, bytes));
            Status = "Current Tool Table opened from its immutable Server release.";
        });
    }

    private async Task ViewNcFileAsync()
    {
        if (api is null || Selected?.CaseId is null || Selected.CaseOperationId is null
            || Selected.GCodeReleaseId is null) return;
        await RunActionAsync(async () =>
        {
            var text = await api.ReadGCodeFileTextAsync(
                Selected.CaseId, Selected.CaseOperationId, Selected.GCodeReleaseId);
            ActionRequested?.Invoke(this, new("VIEW_NC_READ_ONLY", Selected, text));
            Status = "Current NC release opened read-only.";
        });
    }

    private async Task CreateProductionPackageAsync()
    {
        if (api is null || Selected is null) return;
        await RunActionAsync(async () =>
        {
            var package = await api.CreateProductionPackageAsync(
                Selected.BatchOperationId, clientId, userId);
            ActionRequested?.Invoke(this, new("PRODUCTION_PACKAGE_CREATED", Selected, package));
            Status = $"Production Package {package.ProductionPackageId} created and made current.";
        });
        await RefreshAsync();
    }

    private async Task OpenProductionPackageAsync()
    {
        if (api is null || Selected is null) return;
        await RunActionAsync(async () =>
        {
            var package = await api.GetCurrentProductionPackageAsync(Selected.BatchOperationId)
                ?? throw new InvalidOperationException("No current valid Production Package exists.");
            ActionRequested?.Invoke(this, new("OPEN_PRODUCTION_PACKAGE", Selected, package));
            Status = $"Opened current Production Package {package.ProductionPackageId}. No workflow state changed.";
        });
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        if (isBusy) return;
        isBusy = true;
        RaiseActionStates();
        try { await action(); }
        catch (Exception exception) { Status = exception.Message; }
        finally { isBusy = false; RaiseActionStates(); }
    }

    private void RaiseActionStates()
    {
        OpenCaseCommand.RaiseCanExecuteChanged();
        OpenOperationCommand.RaiseCanExecuteChanged();
        UploadGCodeCommand.RaiseCanExecuteChanged();
        OpenToolTableCommand.RaiseCanExecuteChanged();
        ViewNcFileCommand.RaiseCanExecuteChanged();
        CreateProductionPackageCommand.RaiseCanExecuteChanged();
        OpenProductionPackageCommand.RaiseCanExecuteChanged();
    }

    internal async Task RefreshAsync()
    {
        if (api is null || isBusy) return;
        isBusy = true;
        RefreshCommand.RaiseCanExecuteChanged();
        var selectedId = Selected?.BatchOperationId;
        try
        {
            var values = await api.ListPreparationQueueAsync(Stage);
            Items.Clear();
            foreach (var value in values) Items.Add(value);
            Selected = selectedId is null
                ? null
                : Items.FirstOrDefault(value => value.BatchOperationId == selectedId);
            Status = Items.Count == 0
                ? "No operations are waiting at this preparation gate."
                : $"{Items.Count} operation(s) waiting at this preparation gate.";
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            isBusy = false;
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new(property));
        return true;
    }
}

internal sealed record PreparationQueueActionRequest(
    string Action,
    PreparationQueueItem Item,
    object? Payload);
