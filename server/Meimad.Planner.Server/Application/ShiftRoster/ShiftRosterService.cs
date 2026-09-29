using System.Globalization;
using Meimad.Planner.Server.Application.AdministrativeSetup;
using Meimad.Planner.Server.Application.Concurrency;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.WorkingCalendars;
using Meimad.Planner.Server.Domain.AdministrativeSetup;
using Meimad.Planner.Server.Domain.WorkingCalendars;

namespace Meimad.Planner.Server.Application.ShiftRoster;

/// <summary>
/// The Shift Roster (owner decisions 2026-09-29): the dated shift each employee on a rotation
/// Calendar starts, decided week by week. An entry overrides the crew's pattern day; removing it
/// lets the pattern apply again. The Server expands the result; the client only shows and edits it.
/// </summary>
internal sealed class ShiftRosterService(
    IShiftRosterRepository roster,
    IAdministrativeSetupRepository employees,
    IWorkingCalendarRepository calendars,
    TimeProvider timeProvider)
{
    internal const int MaximumDays = 62;
    internal const int MaximumChanges = 1000;
    private const int NoteMaximum = 200;

    internal async Task<ShiftRosterView> GetAsync(DateOnly from, DateOnly to, CancellationToken token = default)
    {
        if (to < from || to.DayNumber - from.DayNumber + 1 > MaximumDays)
            throw new ShiftRosterValidationException([new("to", "invalid_range", $"The roster range must run forward and cover at most {MaximumDays} days.")]);

        var rotationCalendars = (await calendars.ListAsync(token))
            .Where(calendar => calendar.IsRotation && calendar.Rotation is not null)
            .ToDictionary(calendar => calendar.WorkingCalendarId, StringComparer.Ordinal);
        var people = (await employees.ListResourcesAsync(token))
            .Where(employee => employee.IsActive && rotationCalendars.ContainsKey(employee.AssignedCalendarId))
            .ToArray();
        var entries = (await roster.ListAsync(from, to, null, token))
            .GroupBy(entry => entry.ResourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ShiftRosterEntry>)group.ToArray(), StringComparer.Ordinal);
        var holidays = (await employees.ListHolidaysAsync(token))
            .Where(holiday => holiday.Date >= from && holiday.Date <= to)
            .Select(holiday => new ShiftHoliday(holiday.Date, holiday.Name, holiday.Status, holiday.StartsAtLocal, holiday.EndsAtLocal))
            .ToArray();

        var rows = new List<ShiftRosterEmployee>();
        foreach (var employee in people)
        {
            var calendar = rotationCalendars[employee.AssignedCalendarId];
            var own = entries.TryGetValue(employee.ResourceId, out var found) ? found : [];
            var absences = (await employees.ListEmployeeExceptionsAsync(employee.ResourceId, from, to, token))
                .Select(value => new ShiftAbsence(value.Date, value.IsFullDay, value.StartsAtLocal, value.EndsAtLocal, value.ExceptionType))
                .ToArray();
            var days = EmployeeShiftCalculator.Expand(calendar, employee.ShiftCrewCode, own, absences, holidays, from, to);
            var byDate = own.ToDictionary(entry => entry.Date);
            rows.Add(new ShiftRosterEmployee(employee, days
                .Select(day => new ShiftRosterDay(day, byDate.TryGetValue(day.Date, out var entry) ? entry : null))
                .ToArray()));
        }

        return new ShiftRosterView(from, to, rotationCalendars.Values.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase).ToArray(), rows);
    }

    internal async Task<IReadOnlyList<ShiftRosterEntry>> SaveAsync(
        IReadOnlyList<ShiftRosterChangeValues?>? values, EditAuthority authority, CancellationToken token = default)
    {
        var issues = new List<ShiftRosterIssue>();
        if (values is null || values.Count == 0)
            throw new ShiftRosterValidationException([new("entries", "required", "At least one roster change is required.")]);
        if (values.Count > MaximumChanges)
            throw new ShiftRosterValidationException([new("entries", "too_many", $"One save may change at most {MaximumChanges} roster entries.")]);

        var calendarsById = (await calendars.ListAsync(token)).ToDictionary(value => value.WorkingCalendarId, StringComparer.Ordinal);
        var employeesById = (await employees.ListResourcesAsync(token)).ToDictionary(value => value.ResourceId, StringComparer.Ordinal);
        var seen = new HashSet<(string, DateOnly)>();
        var changes = new List<ShiftRosterChange>();
        for (var index = 0; index < values.Count; index++)
        {
            var field = $"entries[{index}]";
            var value = values[index];
            if (value?.ResourceId is not { } resourceId || !employeesById.TryGetValue(resourceId, out var employee))
            {
                issues.Add(new($"{field}.resourceId", "unknown_employee", "Each roster change must identify an existing employee."));
                continue;
            }
            if (value.Date is null || !DateOnly.TryParseExact(value.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                issues.Add(new($"{field}.date", "invalid_date", "date must use yyyy-MM-dd."));
                continue;
            }
            if (!seen.Add((resourceId, date)))
            {
                issues.Add(new($"{field}.date", "duplicate_entry", $"{employee.Name} on {value.Date} appears more than once."));
                continue;
            }
            if (!calendarsById.TryGetValue(employee.AssignedCalendarId, out var calendar) || calendar.Rotation is null)
            {
                issues.Add(new($"{field}.resourceId", "not_on_rotation", $"{employee.Name} has no rotation Calendar, so the Shift Roster does not apply."));
                continue;
            }
            var code = value.ShiftCode?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(code)) code = null;
            if (code is not null && code != RotationShiftCode.Off && calendar.Rotation.Shifts.All(shift => shift.Code != code))
            {
                issues.Add(new($"{field}.shiftCode", "unknown_shift", $"'{code}' is not a shift of Calendar '{calendar.Name}'."));
                continue;
            }
            var note = value.Note?.Trim();
            if (note?.Length > NoteMaximum)
                issues.Add(new($"{field}.note", "too_long", $"A roster note must contain at most {NoteMaximum} characters."));
            if (value.ExpectedVersion is < 1)
                issues.Add(new($"{field}.expectedVersion", "invalid_version", "expectedVersion must be positive, or null for a date without an entry."));
            changes.Add(new ShiftRosterChange(resourceId, date, code, string.IsNullOrEmpty(note) ? null : note, value.ExpectedVersion));
        }
        if (issues.Count > 0) throw new ShiftRosterValidationException(issues);

        try
        {
            return await roster.ApplyAsync(changes, timeProvider.GetUtcNow(), authority, token);
        }
        catch (ShiftRosterStaleException stale)
        {
            var name = employeesById[stale.Change.ResourceId].Name;
            throw new EditConflictException(
                "Shift Roster",
                $"{name}: {stale.Message}",
                "Your roster changes were not saved. Refresh the roster to see the current shifts, then make your changes again.",
                stale.Current?.UpdatedBy,
                stale.Current?.UpdatedAt);
        }
    }
}

internal sealed record ShiftRosterChangeValues(
    string? ResourceId, string? Date, string? ShiftCode, string? Note, int? ExpectedVersion);

internal sealed record ShiftRosterView(
    DateOnly From, DateOnly To, IReadOnlyList<WorkingCalendar> Calendars, IReadOnlyList<ShiftRosterEmployee> Employees);

internal sealed record ShiftRosterEmployee(EmployeeResource Employee, IReadOnlyList<ShiftRosterDay> Days);

internal sealed record ShiftRosterDay(EmployeeShiftDay Shift, ShiftRosterEntry? Entry);

internal sealed record ShiftRosterIssue(string Field, string Code, string Message);

internal sealed class ShiftRosterValidationException(IReadOnlyList<ShiftRosterIssue> issues)
    : Exception("Shift Roster validation failed.")
{
    internal IReadOnlyList<ShiftRosterIssue> Issues { get; } = issues;
}
