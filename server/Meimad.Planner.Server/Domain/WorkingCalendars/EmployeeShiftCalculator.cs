using System.Globalization;

namespace Meimad.Planner.Server.Domain.WorkingCalendars;

/// <summary>One dated Shift Roster decision: the shift an employee starts on a local date, or <c>off</c>.</summary>
internal sealed record ShiftRosterEntry(
    string EntryId,
    string ResourceId,
    DateOnly Date,
    string ShiftCode,
    string? Note,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);

/// <summary>An employee absence on a local date: the whole shift that starts that day, or an HH:mm part of it.</summary>
internal sealed record ShiftAbsence(
    DateOnly Date, bool IsFullDay, string? StartsAtLocal, string? EndsAtLocal, string? Type = null);

internal sealed record ShiftHoliday(
    DateOnly Date, string Name, string Status, string? StartsAtLocal, string? EndsAtLocal);

internal sealed record ShiftInterval(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>
/// What an employee works from one local date: the shift that starts that day and the working
/// intervals it yields after breaks and absences. <see cref="PatternShiftCode"/> and
/// <see cref="RosterShiftCode"/> exist only for rotation calendars.
/// </summary>
internal sealed record EmployeeShiftDay(
    DateOnly Date,
    string? PatternShiftCode,
    string? RosterShiftCode,
    string EffectiveShiftCode,
    string? ClosureName,
    string? AbsenceType,
    bool UnknownShift,
    IReadOnlyList<ShiftInterval> Intervals);

/// <summary>
/// Expands an employee's Working Calendar into dated shifts (owner decisions 2026-09-29). Every
/// shift belongs to the local date it starts on, so a 19:00–07:00 night shift and its breaks belong
/// to the evening's date, and a full-day absence on that date removes the whole shift, including
/// its hours after midnight. A rotation day takes the Shift Roster entry when there is one, and
/// otherwise the crew's pattern day. Closures, non-working holidays and partial-working holidays
/// change only pattern and weekly days; an explicit roster entry is applied as entered.
/// </summary>
internal static class EmployeeShiftCalculator
{
    internal const string WeeklyShiftCode = "weekly";
    private const int Day = 24 * 60;

    internal static IReadOnlyList<EmployeeShiftDay> Expand(
        WorkingCalendar calendar,
        string? crewCode,
        IReadOnlyList<ShiftRosterEntry> roster,
        IReadOnlyList<ShiftAbsence> absences,
        IReadOnlyList<ShiftHoliday> holidays,
        DateOnly firstDate,
        DateOnly lastDate)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZoneId);
        var closures = calendar.Exceptions.ToDictionary(
            value => DateOnly.ParseExact(value.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        var holidaysByDate = calendar.UseIsraeliHolidays
            ? holidays.GroupBy(value => value.Date).ToDictionary(group => group.Key, group => group.First())
            : [];
        var rosterByDate = roster.GroupBy(value => value.Date).ToDictionary(group => group.Key, group => group.First());
        var absencesByDate = absences.GroupBy(value => value.Date).ToDictionary(group => group.Key, group => group.ToArray());
        var workdays = calendar.Workdays.Select(ParseDayOfWeek).ToHashSet();
        var days = new List<EmployeeShiftDay>();

        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            closures.TryGetValue(date, out var exception);
            holidaysByDate.TryGetValue(date, out var holiday);
            string? patternCode = null;
            string? rosterCode = null;
            string effective;
            string? closureName = null;
            var unknownShift = false;
            (int Start, int End)[] windows = [];
            (int Start, int End)[] breaks = [];

            if (calendar.IsRotation && calendar.Rotation is { } rotation)
            {
                patternCode = PatternCode(rotation, crewCode, date);
                if (rosterByDate.TryGetValue(date, out var entry)) rosterCode = entry.ShiftCode;
                effective = rosterCode ?? patternCode ?? RotationShiftCode.Off;
                if (rosterCode is null && effective != RotationShiftCode.Off)
                {
                    if (exception is not null)
                        closureName = exception.Name ?? "Closed";
                    else if (holiday?.Status == "non_working")
                        closureName = holiday.Name;
                }
                if (closureName is not null) effective = RotationShiftCode.Off;
                if (effective != RotationShiftCode.Off)
                {
                    var shift = rotation.Shifts.FirstOrDefault(value => value.Code == effective);
                    if (shift is null)
                    {
                        unknownShift = true;
                    }
                    else
                    {
                        windows = [Window(shift.StartsAtLocal, shift.EndsAtLocal)];
                        breaks = AlignToOvernight(windows, shift.BreakWindows.Select(value => Window(value.StartsAtLocal, value.EndsAtLocal)));
                        if (rosterCode is null && holiday?.Status == "partial_working"
                            && holiday.StartsAtLocal is not null && holiday.EndsAtLocal is not null)
                            windows = Intersect(windows, Window(holiday.StartsAtLocal, holiday.EndsAtLocal));
                    }
                }
            }
            else
            {
                effective = WeeklyShiftCode;
                if (exception is not null)
                {
                    windows = exception.Windows.Select(value => Window(value.StartsAtLocal, value.EndsAtLocal)).ToArray();
                    breaks = AlignToOvernight(windows, exception.BreakWindows.Select(value => Window(value.StartsAtLocal, value.EndsAtLocal)));
                    if (windows.Length == 0) closureName = exception.Name ?? "Closed";
                }
                else if (holiday?.Status == "non_working")
                {
                    closureName = holiday.Name;
                }
                else if (holiday?.Status == "partial_working")
                {
                    if (holiday.StartsAtLocal is not null && holiday.EndsAtLocal is not null)
                        windows = [Window(holiday.StartsAtLocal, holiday.EndsAtLocal)];
                }
                else if (workdays.Contains(date.DayOfWeek))
                {
                    windows = calendar.Windows.Select(value => Window(value.StartsAtLocal, value.EndsAtLocal)).ToArray();
                    breaks = AlignToOvernight(windows, calendar.BreakWindows.Select(value => Window(value.StartsAtLocal, value.EndsAtLocal)));
                }
                if (windows.Length == 0) effective = RotationShiftCode.Off;
            }

            string? absenceType = null;
            var working = Subtract(windows, breaks);
            if (absencesByDate.TryGetValue(date, out var dayAbsences) && working.Length > 0)
            {
                var full = dayAbsences.FirstOrDefault(value => value.IsFullDay);
                if (full is not null)
                {
                    absenceType = full.Type ?? "unavailable";
                    working = [];
                }
                else
                {
                    var parts = AlignToOvernight(windows, dayAbsences
                        .Where(value => value.StartsAtLocal is not null && value.EndsAtLocal is not null)
                        .Select(value => Window(value.StartsAtLocal!, value.EndsAtLocal!)));
                    var remaining = Subtract(working, parts);
                    if (remaining.Sum(value => value.End - value.Start) < working.Sum(value => value.End - value.Start))
                        absenceType = dayAbsences[0].Type ?? "unavailable";
                    working = remaining;
                }
            }

            var intervals = new List<ShiftInterval>();
            foreach (var (start, end) in working)
            {
                var midnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
                var localStart = midnight.AddMinutes(start);
                var localEnd = midnight.AddMinutes(end);
                if (zone.IsInvalidTime(localStart) || zone.IsInvalidTime(localEnd)) continue;
                intervals.Add(new ShiftInterval(
                    new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, zone)),
                    new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localEnd, zone))));
            }

            days.Add(new EmployeeShiftDay(date, patternCode, rosterCode, effective, closureName, absenceType, unknownShift, intervals));
        }

        return days;
    }

    /// <summary>The working intervals of the shifts that touch <c>[from, to)</c>, clipped to it.</summary>
    internal static IReadOnlyList<ShiftInterval> Intervals(
        WorkingCalendar calendar,
        string? crewCode,
        IReadOnlyList<ShiftRosterEntry> roster,
        IReadOnlyList<ShiftAbsence> absences,
        IReadOnlyList<ShiftHoliday> holidays,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZoneId);
        // A shift that starts the evening before the horizon still reaches into it.
        var firstDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).Date).AddDays(-1);
        var lastDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, zone).Date).AddDays(1);
        return Expand(calendar, crewCode, roster, absences, holidays, firstDate, lastDate)
            .SelectMany(day => day.Intervals)
            .Select(interval => new ShiftInterval(
                interval.StartsAt < from ? from : interval.StartsAt,
                interval.EndsAt > to ? to : interval.EndsAt))
            .Where(interval => interval.EndsAt > interval.StartsAt)
            .OrderBy(interval => interval.StartsAt)
            .ToArray();
    }

    /// <summary>The crew's pattern day for <paramref name="date"/>, or null when the rotation has no pattern for it.</summary>
    internal static string? PatternCode(ShiftRotation rotation, string? crewCode, DateOnly date)
    {
        if (rotation.Pattern.Count == 0 || rotation.AnchorDate is null) return null;
        var offset = 0;
        if (rotation.Crews.Count > 0)
        {
            var crew = rotation.Crews.FirstOrDefault(value =>
                string.Equals(value.Code, crewCode, StringComparison.OrdinalIgnoreCase));
            if (crew is null) return null;
            offset = crew.OffsetDays;
        }
        var anchor = DateOnly.ParseExact(rotation.AnchorDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var length = rotation.Pattern.Count;
        var index = ((date.DayNumber - anchor.DayNumber - offset) % length + length) % length;
        return rotation.Pattern[index];
    }

    private static (int Start, int End) Window(string startsAtLocal, string endsAtLocal)
    {
        var start = Minutes(startsAtLocal);
        var end = Minutes(endsAtLocal);
        return (start, end <= start ? end + Day : end);
    }

    /// <summary>A break or absence earlier than an overnight shift's start is in its after-midnight part.</summary>
    private static (int Start, int End)[] AlignToOvernight(
        IReadOnlyList<(int Start, int End)> windows,
        IEnumerable<(int Start, int End)> parts)
    {
        var overnight = windows.Where(window => window.End > Day).Select(window => (int?)window.Start).FirstOrDefault();
        return parts.Select(part => overnight is { } start && part.Start < start
            ? (part.Start + Day, part.End + Day)
            : part).ToArray();
    }

    private static (int Start, int End)[] Intersect(IReadOnlyList<(int Start, int End)> windows, (int Start, int End) limit) =>
        windows.Select(window => (Start: Math.Max(window.Start, limit.Start), End: Math.Min(window.End, limit.End)))
            .Where(window => window.End > window.Start)
            .ToArray();

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
                if (exclusionStart > cursor) result.Add((cursor, exclusionStart));
                cursor = Math.Max(cursor, Math.Min(source.End, exclusion.End));
            }
            if (cursor < source.End) result.Add((cursor, source.End));
        }
        return result.ToArray();
    }

    private static int Minutes(string value)
    {
        if (value == "24:00") return Day;
        var parsed = TimeOnly.ParseExact(value, "HH:mm", CultureInfo.InvariantCulture);
        return parsed.Hour * 60 + parsed.Minute;
    }

    private static DayOfWeek ParseDayOfWeek(string value) =>
        Enum.TryParse<DayOfWeek>(value, true, out var result)
            ? result
            : throw new FormatException($"Unknown workday '{value}'.");
}
