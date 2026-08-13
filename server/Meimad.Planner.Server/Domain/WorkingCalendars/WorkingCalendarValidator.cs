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
    IReadOnlyList<string?>? Usages = null);

internal sealed record ValidatedWorkingCalendarValues(
    string Name,
    string TimeZoneId,
    IReadOnlyList<string> Workdays,
    IReadOnlyList<WorkingCalendarWindow> Windows,
    IReadOnlyList<WorkingCalendarWindow> BreakWindows,
    IReadOnlyList<WorkingCalendarException> Exceptions,
    IReadOnlyList<string> Usages);

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
