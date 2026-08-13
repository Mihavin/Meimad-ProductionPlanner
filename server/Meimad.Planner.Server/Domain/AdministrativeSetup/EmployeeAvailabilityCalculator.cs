using System.Globalization;
using Meimad.Planner.Server.Domain.WorkingCalendars;

namespace Meimad.Planner.Server.Domain.AdministrativeSetup;

internal static class EmployeeAvailabilityCalculator
{
    internal static EmployeeAvailability Calculate(
        EmployeeResource resource,
        WorkingCalendar calendar,
        IReadOnlyList<EmployeeCalendarException> employeeExceptions,
        IReadOnlyList<IsraeliHoliday> holidays,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        if (to <= from) throw new ArgumentException("Availability horizon end must be later than its start.");
        if (!resource.IsAvailableForFuturePlanning)
            return new(resource.ResourceId, resource.IsActive, NullIfBlank(resource.AssignedCalendarId), calendar.TimeZoneId, [], employeeExceptions);

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZoneId);
        var workdays = calendar.Workdays.Select(ParseDayOfWeek).ToHashSet();
        var calendarExceptions = calendar.Exceptions.ToDictionary(value => DateOnly.ParseExact(
            value.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        var employeeByDate = employeeExceptions.GroupBy(value => value.Date).ToDictionary(
            group => group.Key, group => group.ToArray());
        var holidaysByDate = holidays.ToDictionary(value => value.Date);
        var localStart = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, timeZone).Date).AddDays(-1);
        var localEnd = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, timeZone).Date).AddDays(1);
        var result = new List<EmployeeAvailabilityWindow>();

        for (var date = localStart; date <= localEnd; date = date.AddDays(1))
        {
            holidaysByDate.TryGetValue(date, out var holiday);
            IReadOnlyList<WorkingCalendarWindow> windows;
            IReadOnlyList<WorkingCalendarWindow> breaks;
            if (calendarExceptions.TryGetValue(date, out var calendarException))
            {
                windows = calendarException.Windows;
                breaks = calendarException.BreakWindows;
            }
            else if (calendar.UseIsraeliHolidays && holiday is not null
                     && holiday.Status == IsraeliHolidayStatus.NonWorking)
            {
                continue;
            }
            else if (holiday is not null && holiday.Status == IsraeliHolidayStatus.PartialWorking)
            {
                if (holiday.StartsAtLocal is null || holiday.EndsAtLocal is null) continue;
                windows = [new WorkingCalendarWindow(holiday.StartsAtLocal, holiday.EndsAtLocal)];
                breaks = [];
            }
            else if (workdays.Contains(date.DayOfWeek))
            {
                windows = calendar.Windows;
                breaks = calendar.BreakWindows;
            }
            else
            {
                continue;
            }

            var available = Subtract(
                windows.Select(value => (Start: Minutes(value.StartsAtLocal), End: Minutes(value.EndsAtLocal))).ToArray(),
                breaks.Select(value => (Start: Minutes(value.StartsAtLocal), End: Minutes(value.EndsAtLocal))).ToArray());
            if (employeeByDate.TryGetValue(date, out var absences))
            {
                if (absences.Any(value => value.IsFullDay)) continue;
                available = Subtract(available, absences.Select(value => (
                    Start: Minutes(value.StartsAtLocal!), End: Minutes(value.EndsAtLocal!))).ToArray());
            }

            foreach (var window in available)
            {
                var dateTime = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
                var startsLocal = dateTime.AddMinutes(window.Start);
                var endsLocal = dateTime.AddMinutes(window.End);
                if (timeZone.IsInvalidTime(startsLocal) || timeZone.IsInvalidTime(endsLocal)) continue;
                var startsAt = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(startsLocal, timeZone));
                var endsAt = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(endsLocal, timeZone));
                startsAt = startsAt < from ? from : startsAt;
                endsAt = endsAt > to ? to : endsAt;
                if (endsAt > startsAt) result.Add(new(startsAt, endsAt));
            }
        }

        return new(resource.ResourceId, true, resource.AssignedCalendarId, calendar.TimeZoneId,
            result.OrderBy(value => value.StartsAt).ToArray(), employeeExceptions);
    }

    private static (int Start, int End)[] Subtract(
        IReadOnlyList<(int Start, int End)> sources,
        IReadOnlyList<(int Start, int End)> exclusions)
    {
        var result = new List<(int Start, int End)>();
        foreach (var source in sources.OrderBy(value => value.Start))
        {
            var cursor = source.Start;
            foreach (var exclusion in exclusions
                         .Where(value => value.End > source.Start && value.Start < source.End)
                         .OrderBy(value => value.Start))
            {
                var exclusionStart = Math.Max(source.Start, exclusion.Start);
                var exclusionEnd = Math.Min(source.End, exclusion.End);
                if (exclusionStart > cursor) result.Add((cursor, exclusionStart));
                cursor = Math.Max(cursor, exclusionEnd);
            }
            if (cursor < source.End) result.Add((cursor, source.End));
        }
        return result.ToArray();
    }

    private static int Minutes(string value)
    {
        if (value == "24:00") return 1440;
        var parsed = TimeOnly.ParseExact(value, "HH:mm", CultureInfo.InvariantCulture);
        return parsed.Hour * 60 + parsed.Minute;
    }

    private static DayOfWeek ParseDayOfWeek(string value) =>
        Enum.TryParse<DayOfWeek>(value, true, out var result)
            ? result
            : throw new FormatException($"Unknown workday '{value}'.");

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
