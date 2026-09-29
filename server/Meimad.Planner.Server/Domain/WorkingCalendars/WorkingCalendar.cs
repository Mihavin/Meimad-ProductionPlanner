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
    bool UseIsraeliHolidays = false,
    ShiftRotation? Rotation = null)
{
    internal bool IsRotation => ScheduleKind == WorkingCalendarScheduleKind.Rotation;
}

internal sealed record WorkingCalendarWindow(string StartsAtLocal, string EndsAtLocal);

internal sealed record WorkingCalendarException(
    string Date,
    IReadOnlyList<WorkingCalendarWindow> Windows,
    IReadOnlyList<WorkingCalendarWindow> BreakWindows,
    string? Name);

internal static class WorkingCalendarScheduleKind
{
    internal const string Weekly = "weekly";

    /// <summary>
    /// Employee shift rotation (owner decisions 2026-09-29): named shifts such as a 07:00–19:00 day and
    /// a 19:00–07:00 night, an optional repeating pattern that crews follow at an offset, and a dated
    /// Shift Roster that overrides the pattern per employee. It is an employee-only calendar.
    /// </summary>
    internal const string Rotation = "rotation";
}

/// <summary>
/// The shift rotation of a <see cref="WorkingCalendarScheduleKind.Rotation"/> calendar. Day
/// <c>i</c> of the pattern is the shift that starts on that local date; a crew with offset
/// <c>o</c> starts the pattern <c>o</c> days after <see cref="AnchorDate"/>. An empty pattern
/// means the Shift Roster alone decides who works which shift.
/// </summary>
internal sealed record ShiftRotation(
    string? AnchorDate,
    IReadOnlyList<RotationShift> Shifts,
    IReadOnlyList<string> Pattern,
    IReadOnlyList<RotationCrew> Crews);

/// <summary>A named shift; an end earlier than its start ends on the next day.</summary>
internal sealed record RotationShift(
    string Code,
    string Name,
    string StartsAtLocal,
    string EndsAtLocal,
    IReadOnlyList<WorkingCalendarWindow> BreakWindows);

internal sealed record RotationCrew(string Code, string Name, int OffsetDays);

internal static class RotationShiftCode
{
    /// <summary>A pattern day or roster entry on which no shift starts.</summary>
    internal const string Off = "off";
}

internal static class WorkingCalendarUsage
{
    internal const string Machine = "machine";
    internal const string SetupWorker = "setup_worker";
    internal const string RegularWorker = "regular_worker";
    internal const string QaWorker = "qa_worker";

    internal static readonly IReadOnlyList<string> All =
        [Machine, SetupWorker, RegularWorker, QaWorker];

    internal static readonly IReadOnlyList<string> Workers =
        [SetupWorker, RegularWorker, QaWorker];
}
