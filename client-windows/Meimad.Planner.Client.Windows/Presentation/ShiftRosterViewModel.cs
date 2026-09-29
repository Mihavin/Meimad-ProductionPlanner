using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// The weekly Shift Roster (owner decisions 2026-09-29): which 12h shift each employee on a rotation
/// Calendar starts on each day of a Sunday-to-Saturday week. The Server supplies each day's pattern
/// shift, roster entry and resulting shift; this screen only lets the planner change entries and saves
/// them with the version each was read at, so a parallel change is refused rather than overwritten.
/// </summary>
internal sealed class ShiftRosterViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? api;
    private string clientId = string.Empty;
    private bool canEdit;
    private bool isBusy;
    private DateOnly weekStart = WeekOf(DateOnly.FromDateTime(DateTime.Today));
    private string status = "Connect to the Server to read the Shift Roster.";

    internal ShiftRosterViewModel()
    {
        RefreshCommand = new AsyncCommand(RefreshAsync, () => api is not null && !isBusy);
        PreviousWeekCommand = new AsyncCommand(() => MoveAsync(-7), CanNavigate);
        NextWeekCommand = new AsyncCommand(() => MoveAsync(7), CanNavigate);
        ThisWeekCommand = new AsyncCommand(() => MoveAsync(WeekOf(DateOnly.FromDateTime(DateTime.Today)).DayNumber - weekStart.DayNumber), CanNavigate);
        SaveCommand = new AsyncCommand(SaveAsync, () => api is not null && canEdit && !isBusy && HasChanges);
        DiscardCommand = new AsyncCommand(RefreshAsync, () => api is not null && !isBusy && HasChanges);
        CopyPreviousWeekCommand = new AsyncCommand(CopyPreviousWeekAsync, () => api is not null && canEdit && !isBusy && Rows.Count > 0);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ShiftRosterRow> Rows { get; } = [];

    public ObservableCollection<string> DayHeaders { get; } = [];

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand PreviousWeekCommand { get; }
    public AsyncCommand NextWeekCommand { get; }
    public AsyncCommand ThisWeekCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DiscardCommand { get; }
    public AsyncCommand CopyPreviousWeekCommand { get; }

    public bool CanEdit => canEdit;

    public bool IsBusy => isBusy;

    public bool HasChanges => Rows.Any(row => row.Cells.Any(cell => cell.IsChanged));

    public string WeekTitle =>
        $"Week of {weekStart.ToString("ddd dd/MM/yyyy", CultureInfo.CurrentCulture)} – {weekStart.AddDays(6).ToString("ddd dd/MM/yyyy", CultureInfo.CurrentCulture)}";

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    internal DateOnly WeekStart => weekStart;

    internal void AttachSession(IPlannerApiClient? client, string sessionClientId, bool editor)
    {
        var connected = api is null && client is not null;
        api = client;
        clientId = sessionClientId;
        canEdit = editor;
        OnPropertyChanged(nameof(CanEdit));
        RaiseCommands();
        if (connected) _ = RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        if (api is not { } client || isBusy) return;
        SetBusy(true);
        try
        {
            var snapshot = await client.GetShiftRosterAsync(weekStart, weekStart.AddDays(6));
            Load(snapshot);
            Status = Rows.Count == 0
                ? "No active employee is on a shift rotation Calendar. Create one in Setup → Calendars and assign it to employees."
                : canEdit
                    ? $"{Rows.Count} employee(s) on shift rotations. Change a day, then Save."
                    : $"{Rows.Count} employee(s) on shift rotations. Your account may view the roster but not change it.";
        }
        catch (Exception exception) when (exception is PlannerApiException or HttpRequestException or TaskCanceledException)
        {
            Status = exception.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    internal void Load(ShiftRosterSnapshot snapshot)
    {
        foreach (var row in Rows) row.Changed -= OnCellChanged;
        Rows.Clear();
        DayHeaders.Clear();
        for (var day = 0; day < 7; day++)
            DayHeaders.Add(weekStart.AddDays(day).ToString("ddd dd/MM", CultureInfo.CurrentCulture));
        var calendars = snapshot.Calendars.ToDictionary(calendar => calendar.WorkingCalendarId, StringComparer.Ordinal);
        foreach (var employee in snapshot.Employees)
        {
            if (!calendars.TryGetValue(employee.WorkingCalendarId, out var calendar)) continue;
            var row = new ShiftRosterRow(employee, calendar);
            row.Changed += OnCellChanged;
            Rows.Add(row);
        }
        OnPropertyChanged(nameof(WeekTitle));
        OnPropertyChanged(nameof(HasChanges));
        RaiseCommands();
    }

    internal async Task SaveAsync()
    {
        if (api is not { } client || !HasChanges) return;
        var changes = Rows.SelectMany(row => row.Cells.Where(cell => cell.IsChanged).Select(cell => cell.ToChange(row.ResourceId))).ToArray();
        SetBusy(true);
        var saved = false;
        try
        {
            await client.SaveShiftRosterAsync(changes, clientId, 1);
            saved = true;
        }
        catch (Exception exception) when (exception is PlannerApiException or HttpRequestException or TaskCanceledException)
        {
            Status = exception is PlannerApiException { Conflict: not null }
                ? $"{exception.Message} Nothing was saved; the roster was reloaded."
                : exception.Message;
            if (exception is not PlannerApiException { Conflict: not null }) return;
        }
        finally
        {
            SetBusy(false);
        }

        await RefreshAsync();
        if (saved) Status = $"{changes.Length} roster day(s) saved by the Server.";
    }

    /// <summary>
    /// Makes this week's roster repeat last week's shifts. Only the cells are changed; nothing is
    /// saved until the planner presses Save.
    /// </summary>
    internal async Task CopyPreviousWeekAsync()
    {
        if (api is not { } client) return;
        SetBusy(true);
        try
        {
            var previous = await client.GetShiftRosterAsync(weekStart.AddDays(-7), weekStart.AddDays(-1));
            var byEmployee = previous.Employees.ToDictionary(employee => employee.ResourceId, StringComparer.Ordinal);
            var changed = 0;
            foreach (var row in Rows)
            {
                if (!byEmployee.TryGetValue(row.ResourceId, out var last)) continue;
                for (var day = 0; day < Math.Min(7, last.Days.Count); day++)
                {
                    if (row.Cells[day].CopyFrom(last.Days[day].EffectiveShiftCode)) changed++;
                }
            }
            Status = changed == 0
                ? "This week already matches last week."
                : $"{changed} day(s) now repeat last week. Check them, then Save.";
        }
        catch (Exception exception) when (exception is PlannerApiException or HttpRequestException or TaskCanceledException)
        {
            Status = exception.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task MoveAsync(int days)
    {
        weekStart = weekStart.AddDays(days);
        OnPropertyChanged(nameof(WeekTitle));
        await RefreshAsync();
    }

    private bool CanNavigate() => api is not null && !isBusy && !HasChanges;

    private void OnCellChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(HasChanges));
        RaiseCommands();
        if (HasChanges && !canEdit) Status = "Your account may not change the Shift Roster.";
    }

    /// <summary>The Sunday that starts the factory week of <paramref name="date"/>.</summary>
    internal static DateOnly WeekOf(DateOnly date) => date.AddDays(-(int)date.DayOfWeek);

    private void SetBusy(bool value)
    {
        isBusy = value;
        OnPropertyChanged(nameof(IsBusy));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        PreviousWeekCommand.RaiseCanExecuteChanged();
        NextWeekCommand.RaiseCanExecuteChanged();
        ThisWeekCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        DiscardCommand.RaiseCanExecuteChanged();
        CopyPreviousWeekCommand.RaiseCanExecuteChanged();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One employee's week in the Shift Roster.</summary>
internal sealed class ShiftRosterRow
{
    internal ShiftRosterRow(ShiftRosterEmployee employee, ShiftRosterCalendar calendar)
    {
        ResourceId = employee.ResourceId;
        Name = $"{employee.EmployeeNumber} - {employee.Name}";
        var crew = calendar.Crews.FirstOrDefault(value => value.Code == employee.ShiftCrewCode);
        Detail = $"{RoleLabel(employee.Role)} · {calendar.Name}{(crew is null ? string.Empty : $" · {crew.DisplayName}")}";
        Cells = employee.Days.Select(day => new ShiftRosterCell(day, calendar)).ToArray();
        foreach (var cell in Cells) cell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShiftRosterCell.IsChanged)) Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    internal event EventHandler? Changed;

    internal string ResourceId { get; }

    public string Name { get; }

    public string Detail { get; }

    public IReadOnlyList<ShiftRosterCell> Cells { get; }

    private static string RoleLabel(string role) => role switch
    {
        "setup_worker" => "Setup worker",
        "qa_worker" => "QA worker",
        _ => "Regular worker"
    };
}

/// <summary>A choice of a roster cell: a shift, off, or no entry so the pattern applies.</summary>
internal sealed record ShiftRosterChoice(string? Code, string Label);

/// <summary>
/// One employee-day. The chosen value is a roster entry (a shift or off) or no entry; the text and
/// colour show the shift that results, and the text always names it, so colour is never the only
/// signal. Absences and closures come from the Server and are shown as text.
/// </summary>
internal sealed class ShiftRosterCell : INotifyPropertyChanged
{
    private readonly ShiftRosterDay day;
    private readonly IReadOnlyDictionary<string, ShiftRosterShift> shifts;
    private ShiftRosterChoice selected;

    internal ShiftRosterCell(ShiftRosterDay day, ShiftRosterCalendar calendar)
    {
        this.day = day;
        shifts = calendar.Shifts.ToDictionary(shift => shift.Code, StringComparer.Ordinal);
        var pattern = day.PatternShiftCode is null or "off"
            ? day.PatternShiftCode is null && calendar.PatternLength == 0 ? "no shift" : "off"
            : ShiftName(day.PatternShiftCode);
        Choices =
        [
            new ShiftRosterChoice(null, $"Pattern ({pattern})"),
            .. calendar.Shifts.Select(shift => new ShiftRosterChoice(shift.Code, $"{shift.Name} {shift.StartsAtLocal}–{shift.EndsAtLocal}")),
            new ShiftRosterChoice("off", "Off")
        ];
        selected = Choices.FirstOrDefault(choice => choice.Code == day.RosterShiftCode) ?? Choices[0];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<ShiftRosterChoice> Choices { get; }

    public ShiftRosterChoice SelectedChoice
    {
        get => selected;
        set
        {
            if (value is null || value == selected) return;
            selected = value;
            foreach (var name in new[] { nameof(SelectedChoice), nameof(IsChanged), nameof(ShiftText), nameof(Kind), nameof(DetailText) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public bool IsChanged => selected.Code != day.RosterShiftCode;

    /// <summary>The resulting shift: the choice, or the pattern day when no entry is chosen.</summary>
    private string ResultCode => IsChanged
        ? selected.Code ?? day.PatternShiftCode ?? "off"
        : day.EffectiveShiftCode;

    /// <summary>day, night, other, off or closed, for the cell colour; the text states the same.</summary>
    public string Kind => day.ClosureName is not null && !IsChanged ? "closed"
        : day.AbsenceType is not null && !IsChanged ? "absent"
        : ResultCode == "off" ? "off"
        : shifts.TryGetValue(ResultCode, out var shift)
            // A shift that starts between 05:00 and 17:00 is shown as a day shift, any other as a night shift.
            ? string.CompareOrdinal(shift.StartsAtLocal, "05:00") >= 0 && string.CompareOrdinal(shift.StartsAtLocal, "17:00") < 0 ? "day" : "night"
            : "other";

    public string ShiftText => ResultCode == "off" ? "Off" : ShiftName(ResultCode) + (IsChanged ? " *" : string.Empty);

    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (ResultCode != "off" && shifts.TryGetValue(ResultCode, out var shift))
                parts.Add($"{shift.StartsAtLocal}–{shift.EndsAtLocal}");
            if (day.ClosureName is not null) parts.Add($"Closed: {day.ClosureName}");
            if (day.AbsenceType is not null) parts.Add(AbsenceLabel(day.AbsenceType));
            if (day.UnknownShift) parts.Add("Unknown shift");
            if (!IsChanged && day.RosterShiftCode is not null) parts.Add("Roster");
            if (IsChanged) parts.Add("Not saved");
            return string.Join(" · ", parts);
        }
    }

    public string ToolTip => string.Join(Environment.NewLine, new[]
    {
        day.Date,
        $"Pattern: {(day.PatternShiftCode is null ? "none" : ShiftName(day.PatternShiftCode))}",
        day.RosterShiftCode is null ? "No roster entry" : $"Roster: {ShiftName(day.RosterShiftCode)}{(day.UpdatedBy is null ? string.Empty : $" by {day.UpdatedBy}")}",
        day.Note is null ? null : $"Note: {day.Note}",
        day.ClosureName is null ? null : $"Closed: {day.ClosureName}",
        day.AbsenceType is null ? null : $"Absence: {AbsenceLabel(day.AbsenceType)}"
    }.Where(line => line is not null));

    internal ShiftRosterChange ToChange(string resourceId) =>
        new(resourceId, day.Date, selected.Code, selected.Code is null ? null : day.Note, day.EntryVersion);

    /// <summary>Repeats <paramref name="code"/>; an entry is only made where the pattern differs.</summary>
    internal bool CopyFrom(string code)
    {
        var patternCode = day.PatternShiftCode ?? "off";
        var choice = code == patternCode
            ? Choices[0]
            : Choices.FirstOrDefault(value => value.Code == code) ?? Choices[0];
        if (choice == selected) return false;
        SelectedChoice = choice;
        return true;
    }

    private string ShiftName(string code) =>
        code == "off" ? "Off" : shifts.TryGetValue(code, out var shift) ? shift.Name : code;

    private static string AbsenceLabel(string type) => type switch
    {
        "vacation" => "Vacation",
        "sick_day" => "Sick day",
        "personal_day" => "Personal day",
        "custom_note" => "Absence note",
        _ => "Unavailable"
    };
}
