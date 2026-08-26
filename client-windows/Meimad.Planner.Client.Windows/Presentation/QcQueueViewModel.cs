using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed class QcQueueViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? api;
    private string clientId = string.Empty;
    private string userId = string.Empty;
    private long generation;
    private bool isEditor;
    private bool isBusy;
    private QcQueueItem? selected;
    private string reason = string.Empty;
    private string status = "Connect to view operations waiting for QC.";

    internal QcQueueViewModel()
    {
        RefreshCommand = new AsyncCommand(RefreshAsync, () => api is not null && !isBusy);
        PassCommand = new AsyncCommand(() => DecideAsync("PASS"), CanDecide);
        FailCommand = new AsyncCommand(() => DecideAsync("FAIL"), CanDecide);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<QcQueueItem> Items { get; } = [];
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand PassCommand { get; }
    public AsyncCommand FailCommand { get; }

    public QcQueueItem? Selected
    {
        get => selected;
        set
        {
            if (!Set(ref selected, value)) return;
            Reason = string.Empty;
            RaiseCommandStates();
        }
    }

    public string Reason
    {
        get => reason;
        set => Set(ref reason, value);
    }

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    public bool IsEditor => isEditor;
    public string EditModeText => isEditor
        ? "Edit Mode — QC decisions enabled"
        : "View Mode — queue monitoring only";

    internal void AttachSession(
        IPlannerApiClient? client,
        string activeClientId,
        string activeUserId,
        EditModeStatus? edit)
    {
        api = client;
        clientId = activeClientId;
        userId = activeUserId;
        generation = edit?.Generation ?? 0;
        isEditor = edit?.State == ClientEditState.Editor;
        Raise(nameof(IsEditor));
        Raise(nameof(EditModeText));
        RaiseCommandStates();
        if (api is not null) _ = RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        if (api is null || isBusy) return;
        isBusy = true;
        RaiseCommandStates();
        var selectedId = Selected?.ProductionRunId;
        try
        {
            var values = await api.ListQcQueueAsync();
            Items.Clear();
            foreach (var value in values) Items.Add(value);
            Selected = selectedId is null
                ? null
                : Items.FirstOrDefault(value => value.ProductionRunId == selectedId);
            Status = Items.Count == 0
                ? "No operations are waiting for QC."
                : $"{Items.Count} Production Run(s) waiting for inspection.";
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            isBusy = false;
            RaiseCommandStates();
        }
    }

    private bool CanDecide() =>
        api is not null && isEditor && !isBusy && Selected is not null
        && !string.IsNullOrWhiteSpace(clientId)
        && !string.IsNullOrWhiteSpace(userId);

    internal async Task DecideAsync(string decision)
    {
        if (!CanDecide()) return;
        var runId = Selected!.ProductionRunId;
        string? successMessage = null;
        isBusy = true;
        RaiseCommandStates();
        try
        {
            var result = await api!.DecideQcAsync(
                runId,
                new(decision, string.IsNullOrWhiteSpace(Reason) ? null : Reason.Trim()),
                clientId,
                userId,
                generation);
            successMessage = result.Decision == "PASS"
                ? $"{runId} passed QC and is READY_FOR_PRODUCTION."
                : $"{runId} failed QC and returned to IN_SETUP_RUN.";
            Reason = string.Empty;
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            isBusy = false;
            RaiseCommandStates();
        }
        if (successMessage is not null)
        {
            await RefreshAsync();
            Status = successMessage;
        }
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        PassCommand.RaiseCanExecuteChanged();
        FailCommand.RaiseCanExecuteChanged();
    }

    private bool Set<T>(
        ref T field,
        T value,
        [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(property);
        return true;
    }

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new(property));
}
