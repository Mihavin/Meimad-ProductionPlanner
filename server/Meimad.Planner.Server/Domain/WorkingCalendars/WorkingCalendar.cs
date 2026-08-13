namespace Meimad.Planner.Server.Domain.WorkingCalendars;

internal sealed record WorkingCalendar(
    string WorkingCalendarId,
    string Name,
    string TimeZoneId,
    IReadOnlyList<string> Workdays,
    string? ShiftStartsAtLocal,
    string? ShiftEndsAtLocal,
    IReadOnlyList<WorkingCalendarWindow> Windows,
    IReadOnlyList<WorkingCalendarWindow> BreakWindows,
    IReadOnlyList<WorkingCalendarException> Exceptions,
    IReadOnlyList<string> Usages,
    string ScheduleKind,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool UseIsraeliHolidays = false);

internal sealed record WorkingCalendarWindow(string StartsAtLocal, string EndsAtLocal);

internal sealed record WorkingCalendarException(
    string Date,
    IReadOnlyList<WorkingCalendarWindow> Windows,
    IReadOnlyList<WorkingCalendarWindow> BreakWindows,
    string? Name);

internal static class WorkingCalendarUsage
{
    internal const string Machine = "machine";
    internal const string SetupWorker = "setup_worker";
    internal const string RegularWorker = "regular_worker";
    internal const string QaWorker = "qa_worker";

    internal static readonly IReadOnlyList<string> All =
        [Machine, SetupWorker, RegularWorker, QaWorker];
}
