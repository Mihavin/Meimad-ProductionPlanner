using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>A Machine filter choice of the Work Offsets view; a null ID means every Machine.</summary>
internal sealed record WorkOffsetMachineChoice(string? MachineId, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A status filter choice of the Work Offsets view: the Server statuses it lists.</summary>
internal sealed record WorkOffsetStatusChoice(string? Statuses, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Reports → Work Offsets: the measured work offsets (G54 ...) the Machines reported through DPRNT
/// (schema v116). Lists candidates waiting for review, unmatched machine evidence, current
/// measurements or everything; compares a candidate with the current measurement of its Machine +
/// Operation + NC release + offset; accepts or rejects it with the version and current record the user
/// saw (a stale decision is refused by the Server and explained); and shows the Machine's report
/// program in its NC dialect. Deciding needs the "Run operations" permission.
/// </summary>
internal sealed class WorkOffsetsViewModel : INotifyPropertyChanged
{
    internal const int Limit = 500;

    private IPlannerApiClient? api;
    private string clientId = string.Empty;
    private bool canReview;
    private bool isBusy;
    private WorkOffsetMachineChoice? machine;
    private WorkOffsetStatusChoice status;
    private WorkOffsetRecordInfo? selected;
    private WorkOffsetRecordInfo? currentForSelected;
    private string comment = string.Empty;
    private string message = "Connect to view the work offsets the machines measured.";
    private string reportOffsets = "G54";
    private string reportAxes = "XYZ";
    private string reportText = string.Empty;
    private int selectionVersion;

    internal WorkOffsetsViewModel()
    {
        Statuses =
        [
            new("REVIEW_REQUIRED", "Waiting for review"),
            new("UNMATCHED", "Unmatched machine evidence"),
            new("VALID", "Current measurements"),
            new("REJECTED", "Rejected"),
            new(null, "All measurements")
        ];
        status = Statuses[0];
        RefreshCommand = new AsyncCommand(RefreshAsync, () => api is not null && !isBusy);
        AcceptCommand = new AsyncCommand(() => ReviewAsync(accept: true), CanDecide);
        RejectCommand = new AsyncCommand(() => ReviewAsync(accept: false), CanDecide);
        ReportCommand = new AsyncCommand(LoadReportAsync,
            () => api is not null && !isBusy && Selected is { MachineId: not null, CaseOperationId: not null });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<WorkOffsetMachineChoice> Machines { get; } = [];
    public IReadOnlyList<WorkOffsetStatusChoice> Statuses { get; }
    public ObservableCollection<WorkOffsetRecordInfo> Items { get; } = [];

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand AcceptCommand { get; }
    public AsyncCommand RejectCommand { get; }
    public AsyncCommand ReportCommand { get; }

    public WorkOffsetMachineChoice? Machine
    {
        get => machine;
        set
        {
            if (SetField(ref machine, value)) _ = RefreshAsync();
        }
    }

    public WorkOffsetStatusChoice Status
    {
        get => status;
        set
        {
            if (SetField(ref status, value ?? Statuses[0])) _ = RefreshAsync();
        }
    }

    public WorkOffsetRecordInfo? Selected
    {
        get => selected;
        set
        {
            if (!SetField(ref selected, value)) return;
            Comment = string.Empty;
            ReportText = string.Empty;
            CurrentForSelected = null;
            RaiseCommandStates();
            OnPropertyChanged(nameof(DeltaText));
            if (value is { IsReviewRequired: true }) _ = LoadCurrentAsync(value);
        }
    }

    /// <summary>The current measurement the selected candidate would replace, if there is one.</summary>
    public WorkOffsetRecordInfo? CurrentForSelected
    {
        get => currentForSelected;
        private set
        {
            if (SetField(ref currentForSelected, value)) OnPropertyChanged(nameof(DeltaText));
        }
    }

    /// <summary>The candidate's change against the current measurement, axis by axis.</summary>
    public string DeltaText
    {
        get
        {
            if (Selected is not { IsReviewRequired: true } candidate) return string.Empty;
            if (CurrentForSelected is not { } current)
                return "There is no current measurement of this offset for this Machine, Operation and NC release.";
            static string? Delta(string axis, double? before, double? after) => before is { } b && after is { } a
                ? $"Δ{axis} {(a - b).ToString("+0.0000;-0.0000;0.0000", CultureInfo.InvariantCulture)}"
                : null;
            var parts = new[]
            {
                Delta("X", current.PositionMm.X, candidate.PositionMm.X),
                Delta("Y", current.PositionMm.Y, candidate.PositionMm.Y),
                Delta("Z", current.PositionMm.Z, candidate.PositionMm.Z),
                Delta("A", current.RotationDeg.A, candidate.RotationDeg.A),
                Delta("B", current.RotationDeg.B, candidate.RotationDeg.B),
                Delta("C", current.RotationDeg.C, candidate.RotationDeg.C)
            }.Where(text => text is not null);
            return $"Against the current measurement of {current.MeasuredText}: {string.Join("  ", parts)}";
        }
    }

    public string Comment
    {
        get => comment;
        set => SetField(ref comment, value ?? string.Empty);
    }

    public string Message
    {
        get => message;
        private set => SetField(ref message, value);
    }

    public bool CanReview => canReview;

    public string PermissionText => canReview
        ? "You may accept or reject measurements that wait for review."
        : "Viewing only: accepting or rejecting a measurement needs the permission \"Run operations\".";

    public string ReportOffsets
    {
        get => reportOffsets;
        set => SetField(ref reportOffsets, value ?? string.Empty);
    }

    public string ReportAxes
    {
        get => reportAxes;
        set => SetField(ref reportAxes, value ?? string.Empty);
    }

    public string ReportText
    {
        get => reportText;
        private set => SetField(ref reportText, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetField(ref isBusy, value)) RaiseCommandStates();
        }
    }

    internal void AttachSession(IPlannerApiClient? client, string activeClientId, EditModeStatus? edit)
    {
        var changed = !ReferenceEquals(api, client);
        api = client;
        clientId = activeClientId;
        canReview = edit?.State == ClientEditState.Editor;
        OnPropertyChanged(nameof(CanReview));
        OnPropertyChanged(nameof(PermissionText));
        RaiseCommandStates();
        if (api is not null && (changed || Machines.Count == 0)) _ = LoadMachinesAsync();
    }

    internal async Task LoadMachinesAsync()
    {
        if (api is null) return;
        try
        {
            var machines = await api.ListMachinesAsync();
            var chosen = machine?.MachineId;
            Machines.Clear();
            Machines.Add(new WorkOffsetMachineChoice(null, "All machines"));
            foreach (var value in machines.OrderBy(value => value.Number, StringComparer.CurrentCultureIgnoreCase))
                Machines.Add(new WorkOffsetMachineChoice(value.MachineId,
                    string.IsNullOrWhiteSpace(value.Number) || value.Number == value.Name ? value.Name : $"{value.Number} - {value.Name}"));
            machine = Machines.FirstOrDefault(value => value.MachineId == chosen) ?? Machines[0];
            OnPropertyChanged(nameof(Machine));
            await RefreshAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            Message = Friendly(exception);
        }
    }

    internal async Task RefreshAsync()
    {
        if (api is null || isBusy) return;
        IsBusy = true;
        var keep = Selected?.WorkOffsetId;
        try
        {
            var items = await api.ListWorkOffsetsAsync(Status.Statuses, Machine?.MachineId, null, Limit);
            Items.Clear();
            foreach (var item in items) Items.Add(item);
            Selected = Items.FirstOrDefault(item => item.WorkOffsetId == keep);
            Message = Items.Count == 0
                ? Status.Statuses == "REVIEW_REQUIRED"
                    ? "No measured work offset waits for review."
                    : "No measured work offsets match this filter."
                : $"{Items.Count} measured work offset(s).";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            Message = Friendly(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task ReviewAsync(bool accept)
    {
        if (!CanDecide()) return;
        var candidate = Selected!;
        var current = CurrentForSelected;
        IsBusy = true;
        string? done = null;
        try
        {
            var result = await api!.ReviewWorkOffsetAsync(
                candidate.WorkOffsetId,
                accept,
                new WorkOffsetReviewRequestInfo(
                    candidate.Version,
                    accept ? current?.WorkOffsetId ?? string.Empty : null,
                    string.IsNullOrWhiteSpace(Comment) ? null : Comment.Trim()),
                clientId);
            done = accept
                ? $"{result.WorkOffset} of {result.MachineText} is now the current measurement."
                : $"The {result.WorkOffset} measurement of {result.MachineText} was rejected.";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            Message = Friendly(exception);
        }
        finally
        {
            IsBusy = false;
        }
        if (done is not null)
        {
            await RefreshAsync();
            Message = done;
        }
    }

    internal async Task LoadReportAsync()
    {
        if (api is null || Selected is not { MachineId: { } machineId, CaseOperationId: { } operationId } record) return;
        IsBusy = true;
        try
        {
            var report = await api.GetWorkOffsetReportAsync(machineId, operationId, record.NcReleaseId,
                string.IsNullOrWhiteSpace(ReportOffsets) ? record.WorkOffset : ReportOffsets, ReportAxes);
            ReportText = report.Text;
            Message = $"Report program {report.FileName} ({report.NcDialect}): run it on the Machine after measuring; it prints the offsets to DPRNT and moves nothing.";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            Message = Friendly(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadCurrentAsync(WorkOffsetRecordInfo candidate)
    {
        if (api is null || candidate.MachineId is null || candidate.CaseOperationId is null) return;
        var version = ++selectionVersion;
        try
        {
            var view = await api.GetOperationWorkOffsetsAsync(candidate.MachineId, candidate.CaseOperationId, candidate.WorkOffset);
            if (version != selectionVersion || !ReferenceEquals(Selected, candidate)) return;
            CurrentForSelected = view.Current.FirstOrDefault(record => record.NcReleaseId == candidate.NcReleaseId);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            if (version == selectionVersion) Message = Friendly(exception);
        }
    }

    private bool CanDecide() =>
        api is not null && canReview && !isBusy && Selected is { IsReviewRequired: true } && !string.IsNullOrWhiteSpace(clientId);

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        AcceptCommand.RaiseCanExecuteChanged();
        RejectCommand.RaiseCanExecuteChanged();
        ReportCommand.RaiseCanExecuteChanged();
    }

    private static bool IsExpected(Exception exception) =>
        exception is PlannerApiException or PlannerProtocolException or HttpRequestException or TaskCanceledException;

    private static string Friendly(Exception exception) => exception switch
    {
        PlannerApiException { Conflict: { } conflict } api => string.Join(" ", new[]
        {
            api.Message,
            conflict.ChangedBy is null
                ? null
                : conflict.ChangedAt is { } at
                    ? $"Changed by {conflict.ChangedBy} at {at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}."
                    : $"Changed by {conflict.ChangedBy}.",
            conflict.Advice
        }.Where(text => !string.IsNullOrWhiteSpace(text))),
        TaskCanceledException => "The Server did not respond before the client timeout.",
        HttpRequestException => "The configured Server could not be reached.",
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
