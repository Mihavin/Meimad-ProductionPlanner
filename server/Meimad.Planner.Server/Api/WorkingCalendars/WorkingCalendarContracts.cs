using Meimad.Planner.Server.Application.WorkingCalendars;
using Meimad.Planner.Server.Domain.WorkingCalendars;

namespace Meimad.Planner.Server.Api.WorkingCalendars;

internal sealed record CreateWorkingCalendarRequest(
    string? Name,
    string? TimeZoneId,
    IReadOnlyList<string?>? Workdays,
    string? ShiftStartsAtLocal,
    string? ShiftEndsAtLocal)
{
    internal CreateWorkingCalendarCommand ToCommand() => new(
        Name, TimeZoneId, Workdays, ShiftStartsAtLocal, ShiftEndsAtLocal);
}

internal sealed record WorkingCalendarResponse(
    string WorkingCalendarId,
    string Name,
    string TimeZoneId,
    IReadOnlyList<string> Workdays,
    string? ShiftStartsAtLocal,
    string? ShiftEndsAtLocal,
    string ScheduleKind,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static WorkingCalendarResponse FromDomain(WorkingCalendar calendar) => new(
        calendar.WorkingCalendarId,
        calendar.Name,
        calendar.TimeZoneId,
        calendar.Workdays,
        calendar.ShiftStartsAtLocal,
        calendar.ShiftEndsAtLocal,
        calendar.ScheduleKind,
        calendar.Version,
        calendar.CreatedAt,
        calendar.UpdatedAt);
}

internal sealed record WorkingCalendarListResponse(
    IReadOnlyList<WorkingCalendarResponse> Items,
    string? NextCursor);
