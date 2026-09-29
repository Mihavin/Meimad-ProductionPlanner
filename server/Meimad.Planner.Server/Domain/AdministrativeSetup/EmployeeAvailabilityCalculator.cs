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
        DateTimeOffset to,
        IReadOnlyList<ShiftRosterEntry>? roster = null)
    {
        if (to <= from) throw new ArgumentException("Availability horizon end must be later than its start.");
        if (!resource.IsAvailableForFuturePlanning)
            return new(resource.ResourceId, resource.IsActive, NullIfBlank(resource.AssignedCalendarId), calendar.TimeZoneId, [], employeeExceptions);

        var intervals = EmployeeShiftCalculator.Intervals(
            calendar,
            resource.ShiftCrewCode,
            roster ?? [],
            employeeExceptions.Select(value => new ShiftAbsence(
                value.Date, value.IsFullDay, value.StartsAtLocal, value.EndsAtLocal, value.ExceptionType)).ToArray(),
            holidays.Select(value => new ShiftHoliday(
                value.Date, value.Name, value.Status, value.StartsAtLocal, value.EndsAtLocal)).ToArray(),
            from,
            to);
        return new(resource.ResourceId, true, resource.AssignedCalendarId, calendar.TimeZoneId,
            intervals.Select(value => new EmployeeAvailabilityWindow(value.StartsAt, value.EndsAt)).ToArray(),
            employeeExceptions);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
