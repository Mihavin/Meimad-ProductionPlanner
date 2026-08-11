namespace Meimad.Planner.Server.Application.WorkingCalendars;

internal sealed record CreateWorkingCalendarCommand(
    string? Name,
    string? TimeZoneId,
    IReadOnlyList<string?>? Workdays,
    string? ShiftStartsAtLocal,
    string? ShiftEndsAtLocal);
