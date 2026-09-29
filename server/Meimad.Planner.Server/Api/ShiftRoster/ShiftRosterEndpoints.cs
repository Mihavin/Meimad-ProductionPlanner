using System.Globalization;
using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.ShiftRoster;
using Meimad.Planner.Server.Domain.WorkingCalendars;

namespace Meimad.Planner.Server.Api.ShiftRoster;

internal static class ShiftRosterEndpoints
{
    internal static void MapShiftRosterEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/shift-roster", GetAsync);
        endpoints.MapPut("/api/v1/shift-roster", SaveAsync);
    }

    private static async Task<IResult> GetAsync(
        string? from, string? to, HttpContext context, ShiftRosterService service, CancellationToken token)
    {
        if (!TryDate(from, out var first) || !TryDate(to, out var last))
            return PlanningHttpSupport.Error(StatusCodes.Status400BadRequest, "invalid_range",
                "from and to are required local dates in yyyy-MM-dd format.", context);
        try
        {
            return Results.Ok(ShiftRosterResponse.FromDomain(await service.GetAsync(first, last, token)));
        }
        catch (ShiftRosterValidationException exception)
        {
            return Invalid(exception, context);
        }
    }

    private static async Task<IResult> SaveAsync(
        SaveShiftRosterRequest request, HttpContext context, ShiftRosterService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryAuthorizeEdit(context, Permissions.PlanMachines, out var authority, out var error))
            return error!;
        try
        {
            var written = await service.SaveAsync(request.Entries, authority!, token);
            return Results.Ok(new { entries = written.Select(ShiftRosterEntryResponse.FromDomain).ToArray() });
        }
        catch (ShiftRosterValidationException exception)
        {
            return Invalid(exception, context);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, context);
        }
    }

    private static IResult Invalid(ShiftRosterValidationException exception, HttpContext context) =>
        PlanningHttpSupport.Error(
            StatusCodes.Status422UnprocessableEntity,
            "validation_failed",
            exception.Issues.Count == 1 ? exception.Issues[0].Message : exception.Message,
            context,
            exception.Issues.Select(issue => (object)new { field = issue.Field, code = issue.Code, message = issue.Message }));

    private static bool TryDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}

internal sealed record SaveShiftRosterRequest(IReadOnlyList<ShiftRosterChangeValues?>? Entries);

internal sealed record ShiftRosterEntryResponse(
    string EntryId, string ResourceId, string Date, string ShiftCode, string? Note, int Version,
    DateTimeOffset UpdatedAt, string? UpdatedBy)
{
    internal static ShiftRosterEntryResponse FromDomain(ShiftRosterEntry entry) => new(
        entry.EntryId, entry.ResourceId, entry.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        entry.ShiftCode, entry.Note, entry.Version, entry.UpdatedAt, entry.UpdatedBy);
}

internal sealed record ShiftRosterResponse(
    string From, string To, IReadOnlyList<ShiftRosterCalendarResponse> Calendars, IReadOnlyList<ShiftRosterEmployeeResponse> Employees)
{
    internal static ShiftRosterResponse FromDomain(ShiftRosterView view) => new(
        Date(view.From), Date(view.To),
        view.Calendars.Select(calendar => new ShiftRosterCalendarResponse(
            calendar.WorkingCalendarId, calendar.Name, calendar.TimeZoneId,
            calendar.Rotation!.Shifts.Select(shift => new ShiftRosterShiftResponse(shift.Code, shift.Name, shift.StartsAtLocal, shift.EndsAtLocal)).ToArray(),
            calendar.Rotation.Crews,
            calendar.Rotation.Pattern.Count)).ToArray(),
        view.Employees.Select(row => new ShiftRosterEmployeeResponse(
            row.Employee.ResourceId, row.Employee.EmployeeNumber, row.Employee.Name, row.Employee.ResourceType,
            row.Employee.AssignedCalendarId, row.Employee.ShiftCrewCode,
            row.Days.Select(day => new ShiftRosterDayResponse(
                Date(day.Shift.Date),
                day.Shift.PatternShiftCode,
                day.Shift.RosterShiftCode,
                day.Shift.EffectiveShiftCode,
                day.Shift.ClosureName,
                day.Shift.AbsenceType,
                day.Shift.UnknownShift,
                day.Entry?.Version,
                day.Entry?.Note,
                day.Entry?.UpdatedBy,
                day.Entry?.UpdatedAt,
                day.Shift.Intervals.Count == 0 ? null : day.Shift.Intervals[0].StartsAt,
                day.Shift.Intervals.Count == 0 ? null : day.Shift.Intervals[^1].EndsAt)).ToArray())).ToArray());

    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

internal sealed record ShiftRosterCalendarResponse(
    string WorkingCalendarId, string Name, string TimeZoneId,
    IReadOnlyList<ShiftRosterShiftResponse> Shifts, IReadOnlyList<RotationCrew> Crews, int PatternLength);

internal sealed record ShiftRosterShiftResponse(string Code, string Name, string StartsAtLocal, string EndsAtLocal);

internal sealed record ShiftRosterEmployeeResponse(
    string ResourceId, string EmployeeNumber, string Name, string Role, string WorkingCalendarId, string? ShiftCrewCode,
    IReadOnlyList<ShiftRosterDayResponse> Days);

internal sealed record ShiftRosterDayResponse(
    string Date,
    string? PatternShiftCode,
    string? RosterShiftCode,
    string EffectiveShiftCode,
    string? ClosureName,
    string? AbsenceType,
    bool UnknownShift,
    int? EntryVersion,
    string? Note,
    string? UpdatedBy,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt);
