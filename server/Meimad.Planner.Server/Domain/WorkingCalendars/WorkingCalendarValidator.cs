using System.Globalization;

namespace Meimad.Planner.Server.Domain.WorkingCalendars;

internal static class WorkingCalendarValidator
{
    private const int NameMaximum = 200;
    private static readonly IReadOnlySet<string> ValidWorkdays = new HashSet<string>(
        ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"],
        StringComparer.Ordinal);

    internal static ValidatedWorkingCalendarValues ValidateAndNormalize(
        WorkingCalendarValues values)
    {
        var issues = new List<WorkingCalendarValidationIssue>();
        var name = values.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            issues.Add(new("name", "required", "name is required."));
        }
        else if (name.Length > NameMaximum)
        {
            issues.Add(new("name", "too_long", $"name must contain at most {NameMaximum} characters."));
        }

        var timeZoneId = values.TimeZoneId?.Trim();
        if (string.IsNullOrEmpty(timeZoneId))
        {
            issues.Add(new("timeZoneId", "required", "timeZoneId is required."));
        }
        else
        {
            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (Exception exception) when (exception is
                TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                issues.Add(new(
                    "timeZoneId",
                    "invalid_time_zone",
                    $"timeZoneId '{timeZoneId}' is not available on the Server."));
            }
        }

        var kind = values.ScheduleKind?.Trim().ToLowerInvariant() ?? WorkingCalendarScheduleKind.Weekly;
        if (kind is not (WorkingCalendarScheduleKind.Weekly or WorkingCalendarScheduleKind.Rotation))
        {
            issues.Add(new("scheduleKind", "invalid_schedule_kind", "scheduleKind must be weekly or rotation."));
            throw new WorkingCalendarValidationException(issues);
        }

        if (kind == WorkingCalendarScheduleKind.Rotation)
            return ValidateRotation(values, name, timeZoneId, issues);

        if (values.Rotation is not null)
            issues.Add(new("rotation", "rotation_requires_rotation_kind", "rotation is used only by a Calendar whose scheduleKind is rotation."));
        var workdays = NormalizeWorkdays(values.Workdays, issues);
        var windows = NormalizeWindows(values, issues);
        var breakWindows = NormalizeWindowList(values.BreakWindows, "breakWindows", false, issues);
        ValidateBreakContainment(breakWindows, windows, "breakWindows", issues);
        var exceptions = NormalizeExceptions(values.Exceptions, issues);
        var usages = NormalizeUsages(values.Usages, issues);

        if (issues.Count > 0)
        {
            throw new WorkingCalendarValidationException(issues);
        }

        return new ValidatedWorkingCalendarValues(
            name!,
            timeZoneId!,
            workdays,
            windows,
            breakWindows,
            exceptions,
            usages);
    }

    private static ValidatedWorkingCalendarValues ValidateRotation(
        WorkingCalendarValues values,
        string? name,
        string? timeZoneId,
        List<WorkingCalendarValidationIssue> issues)
    {
        // The rotation's shifts replace the weekly workdays, windows and breaks.
        if (values.Workdays is { Count: > 0 })
            issues.Add(new("workdays", "not_used_by_rotation", "A rotation Calendar has no workdays; its pattern and Shift Roster decide the working days."));
        if (values.Windows is { Count: > 0 } || values.ShiftStartsAtLocal is not null || values.ShiftEndsAtLocal is not null)
            issues.Add(new("windows", "not_used_by_rotation", "A rotation Calendar has no weekly windows; define its shifts instead."));
        if (values.BreakWindows is { Count: > 0 })
            issues.Add(new("breakWindows", "not_used_by_rotation", "A rotation Calendar has no weekly breaks; define each shift's breaks instead."));

        var exceptions = NormalizeExceptions(values.Exceptions, issues);
        for (var index = 0; index < exceptions.Count; index++)
        {
            if (exceptions[index].Windows.Count > 0 || exceptions[index].BreakWindows.Count > 0)
                issues.Add(new($"exceptions[{index}].windows", "rotation_exception_windows_unsupported",
                    "An exception of a rotation Calendar is a closure: no shift starts on that date. Use the Shift Roster to change a single employee's shift."));
        }

        var usages = values.Usages is null ? WorkingCalendarUsage.Workers : NormalizeUsages(values.Usages, issues);
        if (usages.Contains(WorkingCalendarUsage.Machine, StringComparer.Ordinal))
            issues.Add(new("usages", "rotation_machine_usage", "A rotation Calendar is an employee calendar and cannot have machine usage."));

        var rotation = NormalizeRotation(values.Rotation, issues);
        if (issues.Count > 0) throw new WorkingCalendarValidationException(issues);
        return new ValidatedWorkingCalendarValues(
            name!, timeZoneId!, [], [], [], exceptions, usages,
            WorkingCalendarScheduleKind.Rotation, rotation);
    }

    private const int MaximumShifts = 10;
    private const int MaximumPatternDays = 366;
    private const int MaximumCrews = 20;
    private const int CodeMaximum = 20;
    private const int ShiftNameMaximum = 100;

    private static ShiftRotation? NormalizeRotation(
        ShiftRotationValues? values,
        ICollection<WorkingCalendarValidationIssue> issues)
    {
        if (values is null)
        {
            issues.Add(new("rotation", "required", "A rotation Calendar requires its rotation: shifts, pattern and crews."));
            return null;
        }

        var shifts = new List<RotationShift>();
        var shiftCodes = new HashSet<string>(StringComparer.Ordinal);
        if (values.Shifts is null || values.Shifts.Count == 0)
            issues.Add(new("rotation.shifts", "required", "A rotation requires at least one shift."));
        else if (values.Shifts.Count > MaximumShifts)
            issues.Add(new("rotation.shifts", "too_many", $"A rotation may define at most {MaximumShifts} shifts."));
        else
        {
            for (var index = 0; index < values.Shifts.Count; index++)
            {
                var field = $"rotation.shifts[{index}]";
                var shift = values.Shifts[index];
                var code = NormalizeShiftCode(shift?.Code, $"{field}.code", issues);
                if (code is not null && !shiftCodes.Add(code))
                    issues.Add(new($"{field}.code", "duplicate_code", $"Shift code '{code}' is used more than once."));
                var shiftName = shift?.Name?.Trim();
                if (string.IsNullOrEmpty(shiftName)) shiftName = code;
                if (shiftName?.Length > ShiftNameMaximum)
                    issues.Add(new($"{field}.name", "too_long", $"A shift name must contain at most {ShiftNameMaximum} characters."));
                var start = ParseTime(shift?.StartsAtLocal, $"{field}.startsAtLocal", false, issues);
                var end = ParseTime(shift?.EndsAtLocal, $"{field}.endsAtLocal", true, issues);
                if (start.HasValue && end.HasValue && start == end)
                    issues.Add(new($"{field}.endsAtLocal", "window_order_invalid", "A shift end must differ from its start."));
                if (code is null || !start.HasValue || !end.HasValue || start == end) continue;
                var window = new WorkingCalendarWindow(FormatMinutes(start.Value), end.Value == 1440 ? "24:00" : FormatMinutes(end.Value));
                var breaks = NormalizeWindowList(shift?.BreakWindows, $"{field}.breakWindows", false, issues);
                ValidateBreakContainment(breaks, [window], $"{field}.breakWindows", issues);
                shifts.Add(new RotationShift(code, shiftName!, window.StartsAtLocal, window.EndsAtLocal, breaks));
            }
        }

        var pattern = new List<string>();
        if (values.Pattern is { Count: > MaximumPatternDays })
            issues.Add(new("rotation.pattern", "too_long", $"A rotation pattern may have at most {MaximumPatternDays} days."));
        else if (values.Pattern is not null)
        {
            for (var index = 0; index < values.Pattern.Count; index++)
            {
                var code = values.Pattern[index]?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(code)) code = RotationShiftCode.Off;
                if (code != RotationShiftCode.Off && !shiftCodes.Contains(code))
                    issues.Add(new($"rotation.pattern[{index}]", "unknown_shift", $"Pattern day {index + 1} uses unknown shift '{code}'."));
                pattern.Add(code);
            }
        }

        string? anchorDate = null;
        if (!string.IsNullOrWhiteSpace(values.AnchorDate))
        {
            if (DateOnly.TryParseExact(values.AnchorDate.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var anchor))
                anchorDate = anchor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            else
                issues.Add(new("rotation.anchorDate", "invalid_date", "anchorDate must use yyyy-MM-dd."));
        }
        else if (pattern.Count > 0)
        {
            issues.Add(new("rotation.anchorDate", "required", "A rotation pattern requires the anchorDate on which its first day falls."));
        }

        var crews = new List<RotationCrew>();
        var crewCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (values.Crews is { Count: > 0 })
        {
            if (pattern.Count == 0)
                issues.Add(new("rotation.crews", "crews_require_pattern", "Crews follow the rotation pattern; define the pattern first or leave the crews empty."));
            else if (values.Crews.Count > MaximumCrews)
                issues.Add(new("rotation.crews", "too_many", $"A rotation may define at most {MaximumCrews} crews."));
            else
            {
                for (var index = 0; index < values.Crews.Count; index++)
                {
                    var field = $"rotation.crews[{index}]";
                    var crew = values.Crews[index];
                    var code = crew?.Code?.Trim();
                    if (string.IsNullOrEmpty(code))
                    {
                        issues.Add(new($"{field}.code", "required", "Each crew requires a code."));
                        continue;
                    }
                    if (code.Length > CodeMaximum)
                        issues.Add(new($"{field}.code", "too_long", $"A crew code must contain at most {CodeMaximum} characters."));
                    if (!crewCodes.Add(code))
                        issues.Add(new($"{field}.code", "duplicate_code", $"Crew code '{code}' is used more than once."));
                    var crewName = crew?.Name?.Trim();
                    if (string.IsNullOrEmpty(crewName)) crewName = code;
                    if (crewName.Length > ShiftNameMaximum)
                        issues.Add(new($"{field}.name", "too_long", $"A crew name must contain at most {ShiftNameMaximum} characters."));
                    var offset = crew?.OffsetDays ?? 0;
                    if (offset < 0 || offset >= pattern.Count)
                        issues.Add(new($"{field}.offsetDays", "out_of_range", $"offsetDays must be from 0 to {pattern.Count - 1}, within the {pattern.Count}-day pattern."));
                    crews.Add(new RotationCrew(code, crewName, offset));
                }
            }
        }

        return new ShiftRotation(anchorDate, shifts, pattern, crews);
    }

    private static string? NormalizeShiftCode(string? value, string field, ICollection<WorkingCalendarValidationIssue> issues)
    {
        var code = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(code))
        {
            issues.Add(new(field, "required", "Each shift requires a code."));
            return null;
        }
        if (code == RotationShiftCode.Off)
        {
            issues.Add(new(field, "reserved_code", "'off' is reserved for a day on which no shift starts."));
            return null;
        }
        if (code.Length > CodeMaximum || !code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
        {
            issues.Add(new(field, "invalid_code", $"A shift code uses at most {CodeMaximum} letters, digits, '_' or '-'."));
            return null;
        }
        return code;
    }

    private static IReadOnlyList<WorkingCalendarWindow> NormalizeWindows(WorkingCalendarValues values, ICollection<WorkingCalendarValidationIssue> issues)
    {
        if (values.Windows is { Count: > 0 })
        {
            if (values.ShiftStartsAtLocal is not null || values.ShiftEndsAtLocal is not null)
                issues.Add(new("windows", "mixed_window_formats", "Use either windows or the legacy single-shift fields, not both."));
            return NormalizeWindowList(values.Windows, "windows", true, issues);
        }
        var startsAt = ParseTime(values.ShiftStartsAtLocal, "shiftStartsAtLocal", false, issues);
        var endsAt = ParseTime(values.ShiftEndsAtLocal, "shiftEndsAtLocal", true, issues);
        if (startsAt.HasValue && endsAt.HasValue && endsAt.Value == startsAt.Value)
            issues.Add(new("shiftEndsAtLocal", "shift_order_invalid", "shiftEndsAtLocal must differ from shiftStartsAtLocal."));
        return startsAt.HasValue && endsAt.HasValue && endsAt != startsAt
            ? [new WorkingCalendarWindow(FormatMinutes(startsAt.Value), endsAt.Value == 1440 ? "24:00" : FormatMinutes(endsAt.Value))]
            : [];
    }

    private static IReadOnlyList<WorkingCalendarWindow> NormalizeWindowList(
        IReadOnlyList<WorkingCalendarWindow?>? values,
        string field,
        bool requireNonEmpty,
        ICollection<WorkingCalendarValidationIssue> issues)
    {
        if (values is null || values.Count == 0)
        {
            if (requireNonEmpty) issues.Add(new(field, "required", $"{field} must contain at least one window."));
            return [];
        }

        var normalized = new List<(int Start, int End, WorkingCalendarWindow Window)>();
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            var start = ParseTime(value?.StartsAtLocal, $"{field}[{index}].startsAtLocal", false, issues);
            var end = ParseTime(value?.EndsAtLocal, $"{field}[{index}].endsAtLocal", true, issues);
            if (!start.HasValue || !end.HasValue) continue;
            if (end == start)
            {
                issues.Add(new($"{field}[{index}].endsAtLocal", "window_order_invalid", "A window end must differ from its start."));
                continue;
            }

            normalized.Add((start.Value, end.Value, new WorkingCalendarWindow(
                FormatMinutes(start.Value), end.Value == 1440 ? "24:00" : FormatMinutes(end.Value))));
        }

        if (normalized.Count > 1 && normalized.Any(value => value.End <= value.Start))
        {
            issues.Add(new(field, "overnight_window_combination_unsupported", "Use one overnight window per Calendar. Split/combined night windows are not supported."));
        }

        normalized.Sort((left, right) => left.Start.CompareTo(right.Start));
        for (var index = 1; index < normalized.Count; index++)
        {
            if (normalized[index].Start < normalized[index - 1].End)
                issues.Add(new(field, "overlapping_windows", $"{field} must not overlap."));
        }

        return normalized.Select(value => value.Window).ToArray();
    }

    private static IReadOnlyList<WorkingCalendarException> NormalizeExceptions(
        IReadOnlyList<WorkingCalendarException?>? values,
        ICollection<WorkingCalendarValidationIssue> issues)
    {
        if (values is null) return [];
        var normalized = new List<WorkingCalendarException>();
        var dates = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            var date = value?.Date?.Trim();
            if (date is null || !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                issues.Add(new($"exceptions[{index}].date", "invalid_date", "Exception dates must use yyyy-MM-dd."));
                continue;
            }

            date = parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!dates.Add(date))
                issues.Add(new($"exceptions[{index}].date", "duplicate_date", "Each exception date may appear only once."));
            var windows = NormalizeWindowList(value?.Windows, $"exceptions[{index}].windows", false, issues);
            var breaks = NormalizeWindowList(value?.BreakWindows, $"exceptions[{index}].breakWindows", false, issues);
            ValidateBreakContainment(breaks, windows, $"exceptions[{index}].breakWindows", issues);
            var name = value?.Name?.Trim();
            if (name?.Length > NameMaximum)
                issues.Add(new($"exceptions[{index}].name", "too_long", $"Exception name must contain at most {NameMaximum} characters."));
            normalized.Add(new WorkingCalendarException(date, windows, breaks, string.IsNullOrEmpty(name) ? null : name));
        }

        return normalized.OrderBy(value => value.Date, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> NormalizeUsages(
        IReadOnlyList<string?>? values,
        ICollection<WorkingCalendarValidationIssue> issues)
    {
        if (values is null) return WorkingCalendarUsage.All;
        if (values.Count == 0)
        {
            issues.Add(new("usages", "required", "At least one calendar usage is required."));
            return [];
        }

        var valid = WorkingCalendarUsage.All.ToHashSet(StringComparer.Ordinal);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index]?.Trim().ToLowerInvariant();
            if (value is null || !valid.Contains(value))
                issues.Add(new($"usages[{index}]", "invalid_usage", "Usage must be machine, setup_worker, regular_worker, or qa_worker."));
            else if (!seen.Add(value))
                issues.Add(new($"usages[{index}]", "duplicate_usage", "Each calendar usage may appear only once."));
            else
                result.Add(value);
        }

        return WorkingCalendarUsage.All.Where(result.Contains).ToArray();
    }

    private static void ValidateBreakContainment(
        IReadOnlyList<WorkingCalendarWindow> breaks,
        IReadOnlyList<WorkingCalendarWindow> workingWindows,
        string field,
        ICollection<WorkingCalendarValidationIssue> issues)
    {
        for (var index = 0; index < breaks.Count; index++)
        {
            var breakStart = LocalMinutes(breaks[index].StartsAtLocal);
            var breakEnd = LocalMinutes(breaks[index].EndsAtLocal);
            if (breakEnd <= breakStart) breakEnd += 1440;
            if (!workingWindows.Any(window =>
                ContainsBreak(window, breakStart, breakEnd)))
                issues.Add(new($"{field}[{index}]", "break_outside_working_window", "Each break must be fully contained in one working window."));
        }
    }

    private static bool ContainsBreak(WorkingCalendarWindow window, int breakStart, int breakEnd)
    {
        var workStart = LocalMinutes(window.StartsAtLocal);
        var workEnd = LocalMinutes(window.EndsAtLocal);
        if (workEnd <= workStart) workEnd += 1440;
        if (workStart <= breakStart && workEnd >= breakEnd) return true;
        // A 01:00 break belongs to the next-day portion of a 17:00–07:00 shift.
        return workEnd > 1440 && workStart <= breakStart + 1440 && workEnd >= breakEnd + 1440;
    }

    private static int LocalMinutes(string value)
    {
        if (value == "24:00") return 1440;
        var parsed = TimeOnly.ParseExact(value, "HH:mm", CultureInfo.InvariantCulture);
        return parsed.Hour * 60 + parsed.Minute;
    }

    private static IReadOnlyList<string> NormalizeWorkdays(
        IReadOnlyList<string?>? values,
        ICollection<WorkingCalendarValidationIssue> issues)
    {
        if (values is null || values.Count == 0)
        {
            issues.Add(new("workdays", "required", "At least one workday is required."));
            return [];
        }

        var normalized = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index]?.Trim().ToLowerInvariant();
            if (value is null || !ValidWorkdays.Contains(value))
            {
                issues.Add(new(
                    $"workdays[{index}]",
                    "invalid_workday",
                    "Workdays must use lowercase Sunday-through-Saturday tokens."));
            }
            else if (!seen.Add(value))
            {
                issues.Add(new(
                    $"workdays[{index}]",
                    "duplicate_workday",
                    "Each workday may appear only once."));
            }
            else
            {
                normalized.Add(value);
            }
        }

        return normalized;
    }

    private static int? ParseTime(
        string? value,
        string field,
        bool allowEndOfDay,
        ICollection<WorkingCalendarValidationIssue> issues)
    {
        var normalized = value?.Trim();
        if (allowEndOfDay && normalized == "24:00")
        {
            return 24 * 60;
        }

        if (normalized is not null
            && TimeOnly.TryParseExact(
                normalized,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return parsed.Hour * 60 + parsed.Minute;
        }

        issues.Add(new(field, "invalid_local_time", $"{field} must use HH:mm local time."));
        return null;
    }

    private static string FormatMinutes(int minutes) =>
        $"{minutes / 60:00}:{minutes % 60:00}";
}

internal sealed record WorkingCalendarValues(
    string? Name,
    string? TimeZoneId,
    IReadOnlyList<string?>? Workdays,
    string? ShiftStartsAtLocal,
    string? ShiftEndsAtLocal,
    IReadOnlyList<WorkingCalendarWindow?>? Windows = null,
    IReadOnlyList<WorkingCalendarWindow?>? BreakWindows = null,
    IReadOnlyList<WorkingCalendarException?>? Exceptions = null,
    IReadOnlyList<string?>? Usages = null,
    string? ScheduleKind = null,
    ShiftRotationValues? Rotation = null);

internal sealed record ShiftRotationValues(
    string? AnchorDate,
    IReadOnlyList<RotationShiftValues?>? Shifts,
    IReadOnlyList<string?>? Pattern,
    IReadOnlyList<RotationCrewValues?>? Crews)
{
    internal static ShiftRotationValues From(ShiftRotation rotation) => new(
        rotation.AnchorDate,
        rotation.Shifts.Select(shift => (RotationShiftValues?)new RotationShiftValues(
            shift.Code, shift.Name, shift.StartsAtLocal, shift.EndsAtLocal,
            shift.BreakWindows.Cast<WorkingCalendarWindow?>().ToArray())).ToArray(),
        rotation.Pattern.Cast<string?>().ToArray(),
        rotation.Crews.Select(crew => (RotationCrewValues?)new RotationCrewValues(crew.Code, crew.Name, crew.OffsetDays)).ToArray());
}

internal sealed record RotationShiftValues(
    string? Code,
    string? Name,
    string? StartsAtLocal,
    string? EndsAtLocal,
    IReadOnlyList<WorkingCalendarWindow?>? BreakWindows);

internal sealed record RotationCrewValues(string? Code, string? Name, int? OffsetDays);

internal sealed record ValidatedWorkingCalendarValues(
    string Name,
    string TimeZoneId,
    IReadOnlyList<string> Workdays,
    IReadOnlyList<WorkingCalendarWindow> Windows,
    IReadOnlyList<WorkingCalendarWindow> BreakWindows,
    IReadOnlyList<WorkingCalendarException> Exceptions,
    IReadOnlyList<string> Usages,
    string ScheduleKind = WorkingCalendarScheduleKind.Weekly,
    ShiftRotation? Rotation = null);

internal sealed record WorkingCalendarValidationIssue(string Field, string Code, string Message);

internal sealed class WorkingCalendarValidationException : Exception
{
    internal WorkingCalendarValidationException(IReadOnlyList<WorkingCalendarValidationIssue> issues)
        : base("Working Calendar validation failed.")
    {
        Issues = issues;
    }

    internal IReadOnlyList<WorkingCalendarValidationIssue> Issues { get; }
}
