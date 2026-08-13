using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed class MachinePlanningBoardViewModel : INotifyPropertyChanged
{
    private readonly Func<AssignmentOverridePrompt, string?>? requestOverrideReason;
    private IPlannerApiClient? apiClient;
    private string clientId = string.Empty;
    private long editGeneration;
    private bool isEditor;
    private bool hasLoaded;
    private bool isBusy;
    private string statusMessage = "Connect to the Server to load the Machine Planning Board.";
    private string conflictCalculationStatus = "Conflict calculation unavailable.";
    private bool isAddingMachine;
    private string machineNumber = string.Empty;
    private string machineName = string.Empty;
    private string machineProcessType = "mill";
    private string machineAxisType = string.Empty;
    private string machineCapabilitiesText = string.Empty;
    private WorkingCalendar? selectedWorkingCalendar;
    private string machinePicturePath = string.Empty;
    private bool machineIsActive = true;
    private bool machineDisplayEnabled = true;
    private string? editingMachineId;
    private string? editingMachineEntityTag;
    private bool isAddingCalendar;
    private string calendarName = string.Empty;
    private string calendarTimeZoneId = "Asia/Jerusalem";
    private CalendarWorkweekOption selectedCalendarWorkweek;
    private CalendarShiftOption selectedCalendarShift;
    private readonly Stack<ManualPlacementChange> undoHistory = [];
    private readonly Stack<ManualPlacementChange> redoHistory = [];

    internal MachinePlanningBoardViewModel(
        Func<AssignmentOverridePrompt, string?>? requestOverrideReason = null)
    {
        this.requestOverrideReason = requestOverrideReason;
        selectedCalendarWorkweek = CalendarWorkweeks[0];
        selectedCalendarShift = CalendarShifts[0];
        RefreshCommand = new AsyncCommand(RefreshAsync, () => apiClient is not null && !IsBusy);
        BeginAddMachineCommand = new AsyncCommand(BeginAddMachineAsync, () => CanAddMachine);
        CancelAddMachineCommand = new AsyncCommand(CancelAddMachineAsync, () => IsAddingMachine && !IsBusy);
        SaveMachineCommand = new AsyncCommand(
            SaveMachineAsync,
            () => IsAddingMachine && CanAddMachine && SelectedWorkingCalendar is not null);
        BeginAddCalendarCommand = new AsyncCommand(BeginAddCalendarAsync, () => CanAddCalendar);
        CancelAddCalendarCommand = new AsyncCommand(CancelAddCalendarAsync, () => IsAddingCalendar && !IsBusy);
        SaveCalendarCommand = new AsyncCommand(SaveCalendarAsync, () => IsAddingCalendar && CanAddCalendar);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? PlanChanged;

    internal event EventHandler<BackwardTimelineRequest>? BackwardTimelineRequested;

    internal event EventHandler? HistoryChanged;

    public ObservableCollection<PlanningOperationViewModel> Pool { get; } = [];

    public ObservableCollection<PlanningMachineColumnViewModel> Machines { get; } = [];

    public ObservableCollection<PlanningConflictViewModel> ServerConflicts { get; } = [];

    public ObservableCollection<PlanningFeedbackViewModel> Feedback { get; } = [];

    public ObservableCollection<WorkingCalendar> WorkingCalendars { get; } = [];

    public IReadOnlyList<string> MachineProcessTypes { get; } =
        ["mill", "lathe", "inspection", "saw", "external"];

    public IReadOnlyList<string> MachineAxisTypes { get; } =
        ["", "2-axis", "3-axis", "4-axis", "5-axis", "live-tooling", "cmm", "manual"];

    public IReadOnlyList<string> CalendarTimeZones { get; } = ["Asia/Jerusalem", "UTC"];

    public IReadOnlyList<CalendarWorkweekOption> CalendarWorkweeks { get; } =
    [
        new("Sunday-Thursday", ["sunday", "monday", "tuesday", "wednesday", "thursday"]),
        new("Monday-Friday", ["monday", "tuesday", "wednesday", "thursday", "friday"]),
        new("Every day", ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"])
    ];

    public IReadOnlyList<CalendarShiftOption> CalendarShifts { get; } =
    [
        new("Day shift 06:00-18:00", "06:00", "18:00"),
        new("Extended 06:00-22:00", "06:00", "22:00"),
        new("Full day 00:00-24:00", "00:00", "24:00")
    ];

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand BeginAddMachineCommand { get; }

    public AsyncCommand CancelAddMachineCommand { get; }

    public AsyncCommand SaveMachineCommand { get; }

    public AsyncCommand BeginAddCalendarCommand { get; }

    public AsyncCommand CancelAddCalendarCommand { get; }

    public AsyncCommand SaveCalendarCommand { get; }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetField(ref isBusy, value))
            {
                OnPropertyChanged(nameof(CanDrag));
                OnPropertyChanged(nameof(CanUndo));
                OnPropertyChanged(nameof(CanRedo));
                OnPropertyChanged(nameof(CanAddMachine));
                OnPropertyChanged(nameof(CanAddCalendar));
                RefreshCommand.RaiseCanExecuteChanged();
                RaiseMachineCommandStates();
            }
        }
    }

    public bool CanDrag => isEditor && !IsBusy;

    internal bool CanUndo => isEditor && !IsBusy && undoHistory.Count > 0;

    internal bool CanRedo => isEditor && !IsBusy && redoHistory.Count > 0;

    public bool CanAddMachine => isEditor && apiClient is not null && !IsBusy;

    public bool CanAddCalendar => isEditor && apiClient is not null && !IsBusy;

    public bool IsAddingMachine
    {
        get => isAddingMachine;
        private set
        {
            if (SetField(ref isAddingMachine, value))
            {
                OnPropertyChanged(nameof(MachineFormHeading));
                OnPropertyChanged(nameof(MachineSaveButtonText));
                RaiseMachineCommandStates();
            }
        }
    }

    public bool IsAddingCalendar
    {
        get => isAddingCalendar;
        private set
        {
            if (SetField(ref isAddingCalendar, value))
            {
                RaiseMachineCommandStates();
            }
        }
    }

    public string MachineNumber { get => machineNumber; set => SetField(ref machineNumber, value); }
    public string MachineName { get => machineName; set => SetField(ref machineName, value); }
    public string MachineProcessType { get => machineProcessType; set => SetField(ref machineProcessType, value); }
    public string MachineAxisType { get => machineAxisType; set => SetField(ref machineAxisType, value); }
    public string MachineCapabilitiesText { get => machineCapabilitiesText; set => SetField(ref machineCapabilitiesText, value); }
    public WorkingCalendar? SelectedWorkingCalendar
    {
        get => selectedWorkingCalendar;
        set
        {
            if (SetField(ref selectedWorkingCalendar, value))
            {
                SaveMachineCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public string MachinePicturePath { get => machinePicturePath; set => SetField(ref machinePicturePath, value); }
    public bool MachineIsActive { get => machineIsActive; set => SetField(ref machineIsActive, value); }
    public bool MachineDisplayEnabled { get => machineDisplayEnabled; set => SetField(ref machineDisplayEnabled, value); }
    public string MachineFormHeading => editingMachineId is null ? "NEW MACHINE" : "EDIT MACHINE";
    public string MachineSaveButtonText => editingMachineId is null ? "Create Machine" : "Save Machine";
    public string CalendarName { get => calendarName; set => SetField(ref calendarName, value); }
    public string CalendarTimeZoneId { get => calendarTimeZoneId; set => SetField(ref calendarTimeZoneId, value); }
    public CalendarWorkweekOption SelectedCalendarWorkweek { get => selectedCalendarWorkweek; set => SetField(ref selectedCalendarWorkweek, value); }
    public CalendarShiftOption SelectedCalendarShift { get => selectedCalendarShift; set => SetField(ref selectedCalendarShift, value); }

    public string ModeInstruction => isEditor
        ? "Edit Mode: drag an operation to the exact Machine and backlog position you choose."
        : "View Mode: assignments and backlog order are read-only.";

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetField(ref statusMessage, value);
    }

    public string ConflictCalculationStatus
    {
        get => conflictCalculationStatus;
        private set => SetField(ref conflictCalculationStatus, value);
    }

    internal void AttachSession(
        IPlannerApiClient? newApiClient,
        string newClientId,
        EditModeStatus? editStatus)
    {
        if (!ReferenceEquals(apiClient, newApiClient))
        {
            apiClient = newApiClient;
            hasLoaded = false;
            Pool.Clear();
            Machines.Clear();
            ServerConflicts.Clear();
            Feedback.Clear();
            WorkingCalendars.Clear();
        }

        clientId = newClientId;
        isEditor = editStatus?.State == ClientEditState.Editor;
        editGeneration = editStatus?.Generation ?? 0;
        OnPropertyChanged(nameof(CanDrag));
        OnPropertyChanged(nameof(CanAddMachine));
        OnPropertyChanged(nameof(CanAddCalendar));
        OnPropertyChanged(nameof(ModeInstruction));
        RaiseMachineCommandStates();
    }

    internal async Task EnsureLoadedAsync()
    {
        if (!hasLoaded && apiClient is not null)
        {
            await RefreshAsync();
        }
    }

    internal async Task RefreshAsync()
    {
        if (apiClient is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var snapshotTask = apiClient.GetPlanningBoardAsync();
            var calendarsTask = apiClient.ListWorkingCalendarsAsync();
            await Task.WhenAll(snapshotTask, calendarsTask);
            var snapshot = await snapshotTask;
            ApplyCalendars(await calendarsTask);
            Apply(snapshot);
            await Task.WhenAll(LoadMachinePicturesAsync(), LoadOperationPreviewsAsync());
            hasLoaded = true;
            StatusMessage = $"Board loaded from the Server at {snapshot.ReadAt.ToLocalTime():HH:mm:ss}.";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal Task BeginAddMachineAsync()
    {
        if (!CanAddMachine)
        {
            return Task.CompletedTask;
        }

        ClearMachineForm();
        IsAddingCalendar = false;
        IsAddingMachine = true;
        StatusMessage = WorkingCalendars.Count == 0
            ? "Create a Working Calendar before creating a Machine."
            : "Enter Machine data and select its Working Calendar.";
        return Task.CompletedTask;
    }

    internal async Task BeginEditMachineAsync(PlanningMachineColumnViewModel machine)
    {
        if (!CanAddMachine || apiClient is null) return;
        IsBusy = true;
        try
        {
            var resource = await apiClient.GetMachineAsync(machine.MachineId);
            var value = resource.Value;
            editingMachineId = value.MachineId;
            editingMachineEntityTag = resource.EntityTag;
            MachineNumber = value.Number;
            MachineName = value.Name;
            MachineProcessType = value.ProcessType;
            MachineAxisType = value.AxisType ?? string.Empty;
            MachineCapabilitiesText = string.Join(", ", value.Capabilities);
            SelectedWorkingCalendar = WorkingCalendars.FirstOrDefault(c => c.WorkingCalendarId == value.WorkingCalendarId);
            MachinePicturePath = value.PicturePath ?? string.Empty;
            MachineIsActive = value.IsActive;
            MachineDisplayEnabled = value.DisplayEnabled;
            IsAddingCalendar = false;
            IsAddingMachine = true;
            OnPropertyChanged(nameof(MachineFormHeading));
            OnPropertyChanged(nameof(MachineSaveButtonText));
            StatusMessage = $"Editing Machine {value.Number}.";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
        }
        finally { IsBusy = false; }
    }

    internal async Task DeleteMachineAsync(PlanningMachineColumnViewModel machine)
    {
        if (!CanAddMachine || apiClient is null) return;
        IsBusy = true;
        var deleted = false;
        try
        {
            await apiClient.DeleteMachineAsync(machine.MachineId, clientId, editGeneration);
            StatusMessage = $"Machine {machine.Number} deleted.";
            deleted = true;
        }
        catch (Exception exception) when (IsExpected(exception)) { StatusMessage = FriendlyMessage(exception); }
        finally { IsBusy = false; }
        if (deleted)
        {
            await RefreshAsync();
            PlanChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal Task CancelAddMachineAsync()
    {
        IsAddingMachine = false;
        ClearMachineForm();
        StatusMessage = "New Machine entry cancelled.";
        return Task.CompletedTask;
    }

    internal void SetMachinePictureSelection(string path) => MachinePicturePath = path;

    internal Task BeginAddCalendarAsync()
    {
        if (!CanAddCalendar)
        {
            return Task.CompletedTask;
        }

        IsAddingMachine = false;
        ClearCalendarForm();
        IsAddingCalendar = true;
        StatusMessage = "Create a recurring weekly Working Calendar for Machine availability.";
        return Task.CompletedTask;
    }

    internal Task CancelAddCalendarAsync()
    {
        IsAddingCalendar = false;
        ClearCalendarForm();
        StatusMessage = "Working Calendar entry cancelled.";
        return Task.CompletedTask;
    }

    internal async Task SaveCalendarAsync()
    {
        if (!IsAddingCalendar || !CanAddCalendar || apiClient is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var calendar = await apiClient.CreateWorkingCalendarAsync(
                new WorkingCalendarCreate(
                    CalendarName,
                    CalendarTimeZoneId,
                    SelectedCalendarWorkweek.Workdays,
                    SelectedCalendarShift.StartsAt,
                    SelectedCalendarShift.EndsAt),
                clientId,
                editGeneration);
            WorkingCalendars.Add(calendar);
            SelectedWorkingCalendar = calendar;
            IsAddingCalendar = false;
            ClearCalendarForm();
            StatusMessage = $"Working Calendar {calendar.Name} created by the Server.";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task SaveMachineAsync()
    {
        if (!IsAddingMachine || !CanAddMachine || apiClient is null)
        {
            return;
        }

        var capabilities = MachineCapabilitiesText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Cast<string>()
            .ToArray();
        IsBusy = true;
        var created = false;
        try
        {
            var values = new MachineCreate(
                    MachineNumber,
                    MachineName,
                    MachineProcessType,
                    NullIfBlank(MachineAxisType),
                    capabilities,
                    SelectedWorkingCalendar!.WorkingCalendarId,
                    MachineIsActive,
                    MachineDisplayEnabled,
                    NullIfBlank(MachinePicturePath));
            var editing = editingMachineId is not null;
            var machine = editing
                ? (await apiClient.UpdateMachineAsync(
                    editingMachineId!, values, editingMachineEntityTag!, clientId, editGeneration)).Value
                : await apiClient.CreateMachineAsync(values, clientId, editGeneration);
            IsAddingMachine = false;
            ClearMachineForm();
            StatusMessage = editing
                ? $"Machine {machine.Number} updated by the Server."
                : $"Machine {machine.Number} created by the Server.";
            created = true;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
        }
        finally
        {
            IsBusy = false;
        }

        if (created)
        {
            await RefreshAsync();
            PlanChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task AssignOrMoveAsync(
        PlanningOperationViewModel operation,
        PlanningMachineColumnViewModel targetMachine,
        int targetPosition)
    {
        if (!TryBeginManualChange(operation))
        {
            return;
        }

        var before = PlacementFrom(operation);

        if (targetPosition < 0)
        {
            targetPosition = 0;
        }

        if (string.Equals(operation.MachineId, targetMachine.MachineId, StringComparison.Ordinal))
        {
            if (operation.BacklogPosition is int sourcePosition
                && sourcePosition < targetPosition)
            {
                targetPosition--;
            }

            targetPosition = Math.Min(targetPosition, Math.Max(0, targetMachine.Backlog.Count - 1));
        }
        else
        {
            targetPosition = Math.Min(targetPosition, targetMachine.Backlog.Count);
        }

        IsBusy = true;
        try
        {
            try
            {
                await apiClient!.AssignOrMoveOperationAsync(
                    operation.BatchOperationId,
                    targetMachine.MachineId,
                    targetPosition,
                    clientId,
                    editGeneration);
                AddFeedback(
                    "information",
                    "Manual assignment accepted",
                    $"{operation.DisplayTitle} was placed on {targetMachine.DisplayName} at position {targetPosition + 1}.");
            }
            catch (PlannerApiException exception)
                when (exception.Code == "machine_type_override_required")
            {
                var reason = requestOverrideReason?.Invoke(new AssignmentOverridePrompt(
                    operation.DisplayTitle,
                    exception.RequiredMachineType ?? operation.RequiredMachineText,
                    targetMachine.DisplayName,
                    exception.SelectedMachineType ?? targetMachine.ProcessType));
                if (string.IsNullOrWhiteSpace(reason))
                {
                    AddFeedback(
                        "warning",
                        "Assignment override cancelled",
                        $"{operation.DisplayTitle} remains unchanged. A confirmation reason is required to assign it to {targetMachine.DisplayName}.");
                    StatusMessage = "The incompatible assignment was not confirmed.";
                    return;
                }

                await apiClient!.AssignOrMoveOperationAsync(
                    operation.BatchOperationId,
                    targetMachine.MachineId,
                    targetPosition,
                    clientId,
                    editGeneration,
                    new MachineAssignmentCompatibilityOverride(true, reason.Trim()));
                AddFeedback(
                    "warning",
                    "Cross-type assignment confirmed",
                    $"{operation.DisplayTitle} was placed on {targetMachine.DisplayName}. The Server recorded the confirmation and reason.");
            }
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            AddAssignmentError(operation, targetMachine, exception);
            StatusMessage = FriendlyMessage(exception);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
        RecordPlacementChange(operation.BatchOperationId, before, PlacementFor(operation.BatchOperationId));
        PlanChanged?.Invoke(this, EventArgs.Empty);
    }

    internal async Task UnassignAsync(PlanningOperationViewModel operation)
    {
        if (!TryBeginManualChange(operation) || operation.MachineId is null)
        {
            return;
        }

        var before = PlacementFrom(operation);

        IsBusy = true;
        try
        {
            await apiClient!.UnassignOperationAsync(
                operation.BatchOperationId,
                clientId,
                editGeneration);
            AddFeedback(
                "information",
                "Manual unassignment accepted",
                $"{operation.DisplayTitle} was returned to the operation pool.");
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            AddFeedback("blocking", "Unassignment rejected", FriendlyMessage(exception));
            StatusMessage = FriendlyMessage(exception);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
        RecordPlacementChange(operation.BatchOperationId, before, PlacementFor(operation.BatchOperationId));
        PlanChanged?.Invoke(this, EventArgs.Empty);
    }

    internal async Task ChangeExecutionStatusAsync(
        PlanningOperationViewModel operation,
        string action,
        OperationPauseRequest? pause = null)
    {
        if (apiClient is null || !isEditor || IsBusy)
        {
            AddFeedback(
                "attention",
                "Edit Mode required",
                $"{operation.DisplayTitle} was not changed. Acquire Edit Mode and try again.");
            return;
        }

        if (operation.MachineId is null)
        {
            AddFeedback(
                "blocking",
                "Machine assignment required",
                $"{operation.DisplayTitle} must be assigned before its execution state can change.");
            return;
        }

        var succeeded = false;
        IsBusy = true;
        try
        {
            var result = action == "suspend"
                ? await apiClient.PauseOperationAsync(
                    operation.BatchOperationId,
                    pause ?? throw new InvalidOperationException("A pause reason is required."),
                    clientId,
                    editGeneration)
                : await apiClient.ChangeOperationExecutionAsync(
                    operation.BatchOperationId, action, clientId, editGeneration);
            AddFeedback(
                "information",
                $"Operation {action} accepted",
                $"{operation.DisplayTitle} is now {result.Status.Replace('_', ' ')}.");
            StatusMessage = $"The Server changed {operation.DisplayTitle} to {result.Status.Replace('_', ' ')}.";
            succeeded = true;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            AddFeedback(
                "blocking",
                $"Operation {action} rejected",
                FriendlyMessage(exception));
            StatusMessage = FriendlyMessage(exception);
        }
        finally
        {
            IsBusy = false;
        }

        if (succeeded)
        {
            await RefreshAsync();
            PlanChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void RequestBackwardTimeline(PlanningOperationViewModel operation)
    {
        if (!operation.CanViewBackward)
        {
            return;
        }

        StatusMessage = $"Opening a visual backward projection for {operation.DisplayTitle}. No plan data will be changed.";
        BackwardTimelineRequested?.Invoke(
            this,
            new BackwardTimelineRequest(operation.BatchId, operation.BatchOperationId));
    }

    internal async Task UndoAsync() =>
        await ReplayPlacementAsync(undoHistory, redoHistory, undo: true);

    internal async Task RedoAsync() =>
        await ReplayPlacementAsync(redoHistory, undoHistory, undo: false);

    private async Task ReplayPlacementAsync(
        Stack<ManualPlacementChange> source,
        Stack<ManualPlacementChange> destination,
        bool undo)
    {
        if (apiClient is null || !isEditor || IsBusy || source.Count == 0)
        {
            return;
        }

        var change = source.Peek();
        var target = undo ? change.Before : change.After;
        IsBusy = true;
        try
        {
            if (target.MachineId is null)
            {
                await apiClient.UnassignOperationAsync(change.OperationId, clientId, editGeneration);
            }
            else
            {
                await apiClient.AssignOrMoveOperationAsync(
                    change.OperationId,
                    target.MachineId,
                    target.BacklogPosition ?? 0,
                    clientId,
                    editGeneration);
            }
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
            AddFeedback("blocking", undo ? "Undo rejected" : "Redo rejected", StatusMessage);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        source.Pop();
        destination.Push(change);
        RaiseHistoryChanged();
        await RefreshAsync();
        StatusMessage = undo ? "Manual placement undone." : "Manual placement redone.";
        PlanChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool TryBeginManualChange(PlanningOperationViewModel operation)
    {
        if (apiClient is null || !isEditor || IsBusy)
        {
            AddFeedback(
                "attention",
                "Edit Mode required",
                $"{operation.DisplayTitle} was not moved. Acquire Edit Mode and try again.");
            return false;
        }

        return true;
    }

    private void RecordPlacementChange(
        string operationId,
        ManualOperationPlacement before,
        ManualOperationPlacement? after)
    {
        if (after is null || before == after)
        {
            return;
        }

        undoHistory.Push(new ManualPlacementChange(operationId, before, after));
        redoHistory.Clear();
        RaiseHistoryChanged();
    }

    private ManualOperationPlacement PlacementFrom(PlanningOperationViewModel operation) =>
        new(operation.MachineId, operation.BacklogPosition);

    private ManualOperationPlacement? PlacementFor(string operationId) =>
        FindOperation(operationId) is { } operation ? PlacementFrom(operation) : null;

    private PlanningOperationViewModel? FindOperation(string operationId) => Pool
        .Concat(Machines.SelectMany(machine => machine.Backlog))
        .FirstOrDefault(operation => operation.BatchOperationId == operationId);

    private void RaiseHistoryChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(PlanningBoardSnapshot snapshot)
    {
        Pool.Clear();
        foreach (var operation in snapshot.Pool)
        {
            Pool.Add(new PlanningOperationViewModel(operation));
        }

        Machines.Clear();
        foreach (var machine in snapshot.Machines)
        {
            Machines.Add(new PlanningMachineColumnViewModel(machine));
        }

        ServerConflicts.Clear();
        foreach (var conflict in snapshot.Conflicts)
        {
            ServerConflicts.Add(new PlanningConflictViewModel(conflict));
        }

        ConflictCalculationStatus = snapshot.ConflictCalculationStatus == "current"
            ? $"Current: {snapshot.ConflictCalculationMessage}"
            : $"Unavailable: {snapshot.ConflictCalculationMessage}";
    }

    private void ApplyCalendars(IReadOnlyList<WorkingCalendar> calendars)
    {
        var selectedId = SelectedWorkingCalendar?.WorkingCalendarId;
        WorkingCalendars.Clear();
        foreach (var calendar in calendars)
        {
            WorkingCalendars.Add(calendar);
        }

        SelectedWorkingCalendar = WorkingCalendars.FirstOrDefault(
            calendar => calendar.WorkingCalendarId == selectedId)
            ?? WorkingCalendars.FirstOrDefault();
    }

    private async Task LoadMachinePicturesAsync()
    {
        if (apiClient is null)
        {
            return;
        }

        foreach (var machine in Machines)
        {
            machine.Picture = ToBitmap(await apiClient.GetMachinePictureAsync(machine.MachineId));
        }
    }

    private async Task LoadOperationPreviewsAsync()
    {
        if (apiClient is null) return;
        var operations = Pool.Concat(Machines.SelectMany(machine => machine.Backlog)).ToArray();
        var previewTasks = operations.Select(operation => operation.CaseId)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(caseId => caseId, caseId => apiClient.GetCasePreviewAsync(caseId), StringComparer.Ordinal);
        await Task.WhenAll(previewTasks.Values);
        var previews = previewTasks.ToDictionary(
            pair => pair.Key, pair => ToBitmap(pair.Value.Result), StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            operation.Preview = previews[operation.CaseId];
        }
    }

    private void ClearMachineForm()
    {
        editingMachineId = null;
        editingMachineEntityTag = null;
        MachineNumber = string.Empty;
        MachineName = string.Empty;
        MachineProcessType = MachineProcessTypes[0];
        MachineAxisType = string.Empty;
        MachineCapabilitiesText = string.Empty;
        SelectedWorkingCalendar ??= WorkingCalendars.FirstOrDefault();
        MachinePicturePath = string.Empty;
        MachineIsActive = true;
        MachineDisplayEnabled = true;
        OnPropertyChanged(nameof(MachineFormHeading));
        OnPropertyChanged(nameof(MachineSaveButtonText));
    }

    private void ClearCalendarForm()
    {
        CalendarName = string.Empty;
        CalendarTimeZoneId = CalendarTimeZones[0];
        SelectedCalendarWorkweek = CalendarWorkweeks[0];
        SelectedCalendarShift = CalendarShifts[0];
    }

    private static BitmapImage? ToBitmap(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is
            IOException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void RaiseMachineCommandStates()
    {
        BeginAddMachineCommand.RaiseCanExecuteChanged();
        CancelAddMachineCommand.RaiseCanExecuteChanged();
        SaveMachineCommand.RaiseCanExecuteChanged();
        BeginAddCalendarCommand.RaiseCanExecuteChanged();
        CancelAddCalendarCommand.RaiseCanExecuteChanged();
        SaveCalendarCommand.RaiseCanExecuteChanged();
    }

    private void AddAssignmentError(
        PlanningOperationViewModel operation,
        PlanningMachineColumnViewModel machine,
        Exception exception)
    {
        if (exception is PlannerApiException
            { Code: "incompatible_machine" or "machine_type_override_required" })
        {
            AddFeedback(
                "blocking",
                "Incompatible Machine",
                $"{operation.DisplayTitle} requires {operation.RequiredMachineText}; {machine.DisplayName} accepts {machine.CompatibilityText}. The Server rejected the move and kept the board unchanged.");
            return;
        }

        AddFeedback(
            "blocking",
            "Assignment rejected",
            $"{FriendlyMessage(exception)} The board was not rearranged by the client.");
    }

    private void AddFeedback(string severity, string title, string message)
    {
        Feedback.Insert(0, new PlanningFeedbackViewModel(severity, title, message));
        while (Feedback.Count > 8)
        {
            Feedback.RemoveAt(Feedback.Count - 1);
        }
    }

    private static bool IsExpected(Exception exception) => exception is
        PlannerApiException or PlannerProtocolException or HttpRequestException or TaskCanceledException;

    private static string FriendlyMessage(Exception exception) => exception switch
    {
        TaskCanceledException => "The Server did not respond before the client timeout.",
        HttpRequestException => "The configured Server could not be reached.",
        PlannerApiException api => $"{api.Message} ({api.Code})",
        _ => exception.Message
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed record CalendarWorkweekOption(string Label, IReadOnlyList<string> Workdays);

internal sealed record CalendarShiftOption(string Label, string StartsAt, string EndsAt);

internal sealed record ManualOperationPlacement(string? MachineId, int? BacklogPosition);

internal sealed record ManualPlacementChange(
    string OperationId,
    ManualOperationPlacement Before,
    ManualOperationPlacement After);

internal sealed class PlanningOperationViewModel : INotifyPropertyChanged
{
    private BitmapImage? preview;

    internal PlanningOperationViewModel(PlanningBoardOperation operation)
    {
        BatchOperationId = operation.BatchOperationId;
        BatchId = operation.BatchId;
        CaseId = operation.CaseId;
        CaseName = operation.CaseName;
        BatchNumber = operation.BatchNumber;
        PartNumber = operation.PartNumber;
        OperationNumber = operation.OperationNumber;
        OperationName = operation.OperationName;
        RequiredMachineType = operation.RequiredMachineType;
        PlannedQuantity = operation.PlannedQuantity;
        OrderReferences = operation.OrderReferences ?? [];
        EstimatedTimeSeconds = operation.EstimatedTimeSeconds
            ?? CalculateEstimatedTime(
                operation.SetupTimeSeconds,
                operation.CycleTimePerPartSeconds,
                operation.PlannedQuantity);
        Status = operation.Status;
        MachineId = operation.MachineId;
        BacklogPosition = operation.BacklogPosition;
        ActivePauseReason = operation.ActivePauseReason;
        PausedBy = operation.PausedBy;
        PauseStartedAt = operation.PauseStartedAt;
    }

    public string BatchOperationId { get; }
    public string BatchId { get; }
    public string CaseId { get; }
    public string? CaseName { get; }
    public string BatchNumber { get; }
    public string PartNumber { get; }
    public int OperationNumber { get; }
    public string OperationName { get; }
    public string? RequiredMachineType { get; }
    public int PlannedQuantity { get; }
    public IReadOnlyList<string> OrderReferences { get; }
    public long? EstimatedTimeSeconds { get; }
    public string Status { get; }
    public string? MachineId { get; }
    public int? BacklogPosition { get; }
    public BitmapImage? Preview
    {
        get => preview;
        internal set
        {
            if (ReferenceEquals(preview, value)) return;
            preview = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string PartCaseText => $"{PartNumber} / {CaseName ?? CaseId}";
    public string OperationText => $"OP{OperationNumber} {OperationName}";
    public string BatchOrderText => OrderReferences.Count == 0
        ? $"Batch {BatchNumber}"
        : $"{BatchNumber} / {string.Join(", ", OrderReferences)}";
    public string? ActivePauseReason { get; }
    public string? PausedBy { get; }
    public DateTimeOffset? PauseStartedAt { get; }
    public string DisplayTitle => $"{PartNumber} / {BatchNumber} / OP{OperationNumber}";
    public string RequiredMachineText => RequiredMachineType ?? "Any active Machine";
    public string PlannedQuantityText => $"Qty {PlannedQuantity}";
    public string OrderReferencesText => OrderReferences.Count == 0
        ? "Stock / no Order"
        : string.Join(", ", OrderReferences);
    public string EstimatedTimeText => EstimatedTimeSeconds.HasValue
        ? $"Time {Formatting.DurationText.Format(EstimatedTimeSeconds.Value)}"
        : "Time unavailable";
    public string StatusText => Status switch
    {
        "not_started" => "Not started",
        "in_progress" => "In progress",
        "suspended" => "Paused",
        "completed" => "Complete",
        _ => Status.Replace('_', ' ')
    };
    public string StatusDetail => ActivePauseReason is null
        ? StatusText
        : $"Paused by {PausedBy} at {PauseStartedAt:g}. {ActivePauseReason}";
    public string StatusGlyph => Status switch
    {
        "not_started" => "○",
        "in_progress" => "▶",
        "suspended" => "Ⅱ",
        "completed" => "✓",
        _ => "•"
    };
    public bool CanStart => MachineId is not null
        && BacklogPosition == 0
        && Status is "not_started" or "suspended";
    public bool CanSuspend => MachineId is not null && Status == "in_progress";
    public bool CanFinish => MachineId is not null && Status == "in_progress";
    public bool CanReset => MachineId is not null && Status == "suspended";
    public bool CanMove => Status != "in_progress";
    public bool CanViewBackward => MachineId is not null && Status != "completed";

    private static long? CalculateEstimatedTime(
        int? setupTimeSeconds,
        int? cycleTimePerPartSeconds,
        int plannedQuantity)
    {
        if (!setupTimeSeconds.HasValue
            || !cycleTimePerPartSeconds.HasValue
            || plannedQuantity < 0)
        {
            return null;
        }

        try
        {
            return checked(
                (long)setupTimeSeconds.Value
                + (long)plannedQuantity * cycleTimePerPartSeconds.Value);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

}

internal sealed record AssignmentOverridePrompt(
    string OperationDisplayName,
    string RequiredMachineType,
    string MachineDisplayName,
    string SelectedMachineType);

internal sealed class BackwardTimelineRequest : EventArgs
{
    internal BackwardTimelineRequest(string batchId, string batchOperationId)
    {
        BatchId = batchId;
        BatchOperationId = batchOperationId;
    }

    internal string BatchId { get; }

    internal string BatchOperationId { get; }
}

internal sealed class PlanningMachineColumnViewModel : INotifyPropertyChanged
{
    private BitmapImage? picture;

    internal PlanningMachineColumnViewModel(PlanningBoardMachine machine)
    {
        MachineId = machine.MachineId;
        Number = machine.Number;
        Name = machine.Name;
        ProcessType = machine.ProcessType;
        AxisType = machine.AxisType;
        Capabilities = machine.Capabilities;
        IsActive = machine.IsActive;
        Backlog = new ObservableCollection<PlanningOperationViewModel>(
            machine.Backlog.Select(operation => new PlanningOperationViewModel(operation)));
    }

    public string MachineId { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Number { get; }
    public string Name { get; }
    public string ProcessType { get; }
    public string? AxisType { get; }
    public IReadOnlyList<string> Capabilities { get; }
    public bool IsActive { get; }
    public ObservableCollection<PlanningOperationViewModel> Backlog { get; }
    public BitmapImage? Picture
    {
        get => picture;
        set
        {
            if (!ReferenceEquals(picture, value))
            {
                picture = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Picture)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PictureStatus)));
            }
        }
    }
    public string PictureStatus => Picture is null ? "No picture" : "Machine picture";
    public string DisplayName => $"{Number} — {Name}";
    public string MachineStatusText => IsActive ? "Active Machine" : "Inactive Machine — drops will be rejected";
    public string CompatibilityText => string.Join(
        ", ",
        new[] { ProcessType, AxisType }.Where(value => !string.IsNullOrWhiteSpace(value))
            .Concat(Capabilities)
            .Distinct(StringComparer.OrdinalIgnoreCase));
}

internal sealed record PlanningConflictViewModel(
    string Severity,
    string Title,
    string Message)
{
    internal PlanningConflictViewModel(PlanningConflict conflict)
        : this(conflict.Severity, conflict.Title, conflict.Message)
    {
    }

    public string SignalText => $"{Severity.ToUpperInvariant()} CONFLICT";
}

internal sealed record PlanningFeedbackViewModel(
    string Severity,
    string Title,
    string Message)
{
    public string SignalText => Severity switch
    {
        "blocking" => "BLOCKING FEEDBACK",
        "attention" => "ATTENTION",
        _ => "INFORMATION"
    };
}
