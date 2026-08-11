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
        var startsAt = ParseTime(values.ShiftStartsAtLocal, "shiftStartsAtLocal", allowEndOfDay: false, issues);
        var endsAt = ParseTime(values.ShiftEndsAtLocal, "shiftEndsAtLocal", allowEndOfDay: true, issues);
        if (startsAt.HasValue && endsAt.HasValue && endsAt.Value <= startsAt.Value)
        {
            issues.Add(new(
                "shiftEndsAtLocal",
                "shift_order_invalid",
                "shiftEndsAtLocal must be later than shiftStartsAtLocal; overnight shifts are not yet supported."));
        }

        if (issues.Count > 0)
        {
            throw new WorkingCalendarValidationException(issues);
        }

        return new ValidatedWorkingCalendarValues(
            name!,
            timeZoneId!,
            workdays,
            FormatMinutes(startsAt!.Value),
            endsAt!.Value == 24 * 60 ? "24:00" : FormatMinutes(endsAt.Value));
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
    string? ShiftEndsAtLocal);

internal sealed record ValidatedWorkingCalendarValues(
    string Name,
    string TimeZoneId,
    IReadOnlyList<string> Workdays,
    string ShiftStartsAtLocal,
    string ShiftEndsAtLocal);

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
