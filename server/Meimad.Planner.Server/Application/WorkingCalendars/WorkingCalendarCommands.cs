using Meimad.Planner.Server.Domain.WorkingCalendars;

namespace Meimad.Planner.Server.Application.WorkingCalendars;

internal sealed record CreateWorkingCalendarCommand(
    string? Name,
    string? TimeZoneId,
    IReadOnlyList<string?>? Workdays,
    string? ShiftStartsAtLocal,
    string? ShiftEndsAtLocal,
    IReadOnlyList<WorkingCalendarWindow?>? Windows = null,
    IReadOnlyList<WorkingCalendarWindow?>? BreakWindows = null,
    IReadOnlyList<WorkingCalendarException?>? Exceptions = null,
    IReadOnlyList<string?>? Usages = null,
    bool UseIsraeliHolidays = false);

internal readonly record struct WorkingCalendarField<T>(bool IsSpecified, T Value)
{
    internal static WorkingCalendarField<T> Unspecified => new(false, default!);
    internal static WorkingCalendarField<T> Specified(T value) => new(true, value);
}

internal sealed record UpdateWorkingCalendarCommand(
    WorkingCalendarField<string?> Name,
    WorkingCalendarField<string?> TimeZoneId,
    WorkingCalendarField<IReadOnlyList<string?>?> Workdays,
    WorkingCalendarField<string?> ShiftStartsAtLocal,
    WorkingCalendarField<string?> ShiftEndsAtLocal,
    WorkingCalendarField<IReadOnlyList<WorkingCalendarWindow?>?> Windows,
    WorkingCalendarField<IReadOnlyList<WorkingCalendarWindow?>?> BreakWindows,
    WorkingCalendarField<IReadOnlyList<WorkingCalendarException?>?> Exceptions,
    WorkingCalendarField<IReadOnlyList<string?>?> Usages,
    WorkingCalendarField<bool?> UseIsraeliHolidays);
