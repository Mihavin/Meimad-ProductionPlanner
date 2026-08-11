namespace Meimad.Planner.Server.Domain.WorkingCalendars;

internal sealed record WorkingCalendar(
    string WorkingCalendarId,
    string Name,
    string TimeZoneId,
    IReadOnlyList<string> Workdays,
    string? ShiftStartsAtLocal,
    string? ShiftEndsAtLocal,
    string ScheduleKind,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
