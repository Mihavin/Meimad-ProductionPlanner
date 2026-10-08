using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// Setup → Kitaron Push: which Planner value the Server writes into each pushable column of Kitaron's
/// Work Order operations, whether it pushes automatically and how often, a preview of the changes, a
/// manual push, and the log of recent pushes. Every signed-in user may look; the Setup permission
/// changes it. The Server does the reading, matching and writing.
/// </summary>
internal sealed class KitaronPushViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? apiClient;
    private bool isEditor;
    private string? previewStamp;
    private bool isBusy;
    private bool enabled;
    private string intervalMinutes = "15";
    private int version;
    private string statusMessage = "Connect to see what the Planner pushes to Kitaron.";
    private string resultTitle = string.Empty;
    private KitaronPushRunInfo? selectedRun;
    private IReadOnlyList<KitaronPushValueInfo> plannerValues = [];

    internal KitaronPushViewModel()
    {
        RefreshCommand = new AsyncCommand(RefreshAsync, () => apiClient is not null && !IsBusy);
        SaveCommand = new AsyncCommand(SaveAsync, () => CanEdit && Columns.Count > 0);
        PreviewCommand = new AsyncCommand(PreviewAsync, () => CanEdit);
        PushNowCommand = new AsyncCommand(PushNowAsync, () => CanEdit);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>One row per Kitaron column the Planner may write.</summary>
    public ObservableCollection<KitaronPushColumnRow> Columns { get; } = [];

    public ObservableCollection<KitaronPushRunRow> Runs { get; } = [];

    /// <summary>The changes of the last preview or push, or of the selected logged push.</summary>
    public ObservableCollection<KitaronPushChangeInfo> Changes { get; } = [];

    public ObservableCollection<string> Notes { get; } = [];

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand PreviewCommand { get; }
    public AsyncCommand PushNowCommand { get; }

    public bool IsEditor => isEditor;

    public bool CanEdit => apiClient is not null && isEditor && !IsBusy;

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!SetField(ref isBusy, value)) return;
            OnPropertyChanged(nameof(CanEdit));
            RaiseCommands();
        }
    }

    /// <summary>Push automatically every <see cref="IntervalMinutes"/> minutes.</summary>
    public bool Enabled
    {
        get => enabled;
        set => SetField(ref enabled, value);
    }

    public string IntervalMinutes
    {
        get => intervalMinutes;
        set => SetField(ref intervalMinutes, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetField(ref statusMessage, value);
    }

    public string ResultTitle
    {
        get => resultTitle;
        private set => SetField(ref resultTitle, value);
    }

    public KitaronPushRunRow? SelectedRun
    {
        get => selectedRun is null ? null : Runs.FirstOrDefault(row => row.Run == selectedRun);
        set
        {
            if (ReferenceEquals(selectedRun, value?.Run)) return;
            selectedRun = value?.Run;
            OnPropertyChanged();
            if (value is not null) _ = LoadRunChangesAsync(value.Run);
        }
    }

    internal void AttachSession(IPlannerApiClient? client, bool editor)
    {
        if (!ReferenceEquals(apiClient, client) || isEditor != editor) previewStamp = null;
        apiClient = client;
        isEditor = editor;
        OnPropertyChanged(nameof(IsEditor));
        OnPropertyChanged(nameof(CanEdit));
        RaiseCommands();
    }

    internal async Task RefreshAsync()
    {
        if (apiClient is null) return;
        IsBusy = true;
        try
        {
            Apply(await apiClient.GetKitaronPushAsync());
            StatusMessage = $"Kitaron push settings read at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception exception) when (IsExpected(exception)) { StatusMessage = Friendly(exception); }
        catch (NotSupportedException)
        {
            // Minimal API fakes used by isolated Setup tests do not expose Kitaron push.
        }
        finally { IsBusy = false; }
    }

    internal async Task SaveAsync()
    {
        if (apiClient is null) return;
        if (!int.TryParse(IntervalMinutes.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var interval)
            || interval is < 5 or > 1440)
        {
            StatusMessage = "The interval is 5 to 1,440 minutes.";
            return;
        }

        IsBusy = true;
        try
        {
            var mappings = Columns
                .Where(row => row.SelectedValue is not null)
                .Select(row => new KitaronPushMappingModel(row.Column.Column, row.SelectedValue!.Code, row.Push))
                .ToArray();
            Apply(await apiClient.UpdateKitaronPushAsync(Enabled, interval, mappings, version));
            StatusMessage = Enabled
                ? $"Saved. The Server pushes to Kitaron every {interval} minutes."
                : "Saved. Automatic pushing is off; use Push now to push once.";
        }
        catch (Exception exception) when (IsExpected(exception)) { StatusMessage = Friendly(exception); }
        finally { IsBusy = false; }
    }

    internal async Task PreviewAsync()
    {
        if (apiClient is null) return;
        IsBusy = true;
        StatusMessage = "Reading Kitaron and calculating the Timeline…";
        try
        {
            var preview = await apiClient.PreviewKitaronPushAsync();
            previewStamp = preview.PreviewStamp;
            ShowResult(preview, $"Preview: {preview.Changes.Count} values would change");
            StatusMessage = preview.Changes.Count == 0
                ? "Kitaron already has the Planner's values; a push would write nothing."
                : $"A push would write {preview.Changes.Count} values in {preview.OperationsMatched} matched operations. Nothing was written.";
        }
        catch (Exception exception) when (IsExpected(exception)) { StatusMessage = Friendly(exception); }
        finally { IsBusy = false; }
    }

    internal async Task PushNowAsync()
    {
        if (apiClient is null) return;
        IsBusy = true;
        StatusMessage = "Pushing to Kitaron…";
        try
        {
            var result = await apiClient.RunKitaronPushAsync(previewStamp: previewStamp);
            previewStamp = null;
            ShowResult(result, $"Pushed: {result.Changes.Count} values written");
            StatusMessage = result.Changes.Count == 0
                ? "Kitaron already had the Planner's values; nothing was written."
                : $"{result.Changes.Count} values written to Kitaron.";
            Apply(await apiClient.GetKitaronPushAsync(), keepEdits: true);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = Friendly(exception);
            try { Apply(await apiClient.GetKitaronPushAsync(), keepEdits: true); }
            catch (Exception refresh) when (IsExpected(refresh)) { }
        }
        finally { IsBusy = false; }
    }

    private async Task LoadRunChangesAsync(KitaronPushRunInfo run)
    {
        if (apiClient is null) return;
        try
        {
            var changes = await apiClient.ListKitaronPushChangesAsync(run.RunId);
            Replace(Changes, changes);
            Notes.Clear();
            if (!string.IsNullOrWhiteSpace(run.Message)) Notes.Add(run.Message);
            ResultTitle = $"Push of {run.StartedAt.ToLocalTime():g}: {changes.Count} values written";
        }
        catch (Exception exception) when (IsExpected(exception)) { StatusMessage = Friendly(exception); }
    }

    private void Apply(KitaronPushSettingsResource settings, bool keepEdits = false)
    {
        version = settings.Version;
        plannerValues = settings.PlannerValues;
        if (!keepEdits)
        {
            Enabled = settings.Enabled;
            IntervalMinutes = settings.IntervalMinutes.ToString(CultureInfo.CurrentCulture);
            Columns.Clear();
            foreach (var column in settings.KitaronColumns)
            {
                var mapping = settings.Mappings.FirstOrDefault(value =>
                    string.Equals(value.KitaronColumn, column.Column, StringComparison.OrdinalIgnoreCase));
                var choices = plannerValues.Where(value => value.Kind == column.Kind).ToArray();
                var selected = choices.FirstOrDefault(value => value.Code == mapping?.PlannerValue) ?? choices.FirstOrDefault();
                Columns.Add(new KitaronPushColumnRow(column, choices, selected, mapping?.Enabled == true));
            }
        }

        var selectedRunId = selectedRun?.RunId;
        Runs.Clear();
        foreach (var run in settings.Runs) Runs.Add(new KitaronPushRunRow(run));
        selectedRun = Runs.FirstOrDefault(row => row.Run.RunId == selectedRunId)?.Run;
        OnPropertyChanged(nameof(SelectedRun));
        RaiseCommands();
    }

    private void ShowResult(KitaronPushResultInfo result, string title)
    {
        Replace(Changes, result.Changes);
        Replace(Notes, result.Notes);
        ResultTitle = title;
    }

    private void RaiseCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        PreviewCommand.RaiseCanExecuteChanged();
        PushNowCommand.RaiseCanExecuteChanged();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    private static bool IsExpected(Exception exception) =>
        exception is PlannerApiException or PlannerProtocolException or HttpRequestException or TaskCanceledException;

    private static string Friendly(Exception exception) => exception switch
    {
        TaskCanceledException => "The Server did not respond before the client timeout.",
        HttpRequestException => "The configured Server could not be reached.",
        PlannerApiException { Conflict: { } conflict } api => $"{api.Message} {conflict.Advice}",
        _ => exception.Message
    };

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>A pushable Kitaron column: the Planner value written into it and whether it is pushed.</summary>
internal sealed class KitaronPushColumnRow(
    KitaronPushColumnInfo column,
    IReadOnlyList<KitaronPushValueInfo> choices,
    KitaronPushValueInfo? selected,
    bool push) : INotifyPropertyChanged
{
    private KitaronPushValueInfo? selectedValue = selected;
    private bool pushValue = push;

    public event PropertyChangedEventHandler? PropertyChanged;

    public KitaronPushColumnInfo Column { get; } = column;
    public string ColumnName => Column.Column;
    public string ColumnTitle => Column.Name;
    public string ColumnNote => Column.Description;
    public IReadOnlyList<KitaronPushValueInfo> Choices { get; } = choices;

    public KitaronPushValueInfo? SelectedValue
    {
        get => selectedValue;
        set
        {
            if (selectedValue == value) return;
            selectedValue = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedValue)));
        }
    }

    public bool Push
    {
        get => pushValue;
        set
        {
            if (pushValue == value) return;
            pushValue = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Push)));
        }
    }
}

/// <summary>One logged push.</summary>
internal sealed class KitaronPushRunRow(KitaronPushRunInfo run)
{
    public KitaronPushRunInfo Run { get; } = run;
    public string StartedText => Run.StartedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string TriggerText => Run.Trigger == "automatic" ? "Automatic" : $"By {Run.RequestedBy ?? "?"}";
    public string StatusText => Run.Status switch
    {
        "succeeded" => "Done",
        "failed" => "Failed",
        _ => "Running"
    };
    public int ValuesWritten => Run.ValuesWritten;
    public int OperationsMatched => Run.OperationsMatched;
    public string Message => Run.Message ?? string.Empty;
}
