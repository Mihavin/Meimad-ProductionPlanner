using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.WorkingCalendars;
using Meimad.Planner.Server.Domain.WorkingCalendars;
using Microsoft.Extensions.Primitives;

namespace Meimad.Planner.Server.Api.WorkingCalendars;

internal static class WorkingCalendarEndpoints
{
    internal static void MapWorkingCalendarEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var calendars = endpoints.MapGroup("/api/v1/working-calendars");
        calendars.MapGet(string.Empty, ListAsync);
        calendars.MapPost(string.Empty, CreateAsync);
        calendars.MapGet("/{workingCalendarId}", GetAsync);
        calendars.MapPatch("/{workingCalendarId}", UpdateAsync);
        calendars.MapDelete("/{workingCalendarId}", DeleteAsync);
        endpoints.MapGet("/api/v1/setup-calendar", GetSetupAsync);
        endpoints.MapPut("/api/v1/setup-calendar", SetSetupAsync);
        endpoints.MapDelete("/api/v1/setup-calendar", ClearSetupAsync);
    }

    private static async Task<IResult> ListAsync(
        WorkingCalendarService service,
        CancellationToken cancellationToken)
    {
        var calendars = await service.ListAsync(cancellationToken);
        return Results.Ok(new WorkingCalendarListResponse(
            calendars.Select(WorkingCalendarResponse.FromDomain).ToArray(), null));
    }

    private static async Task<IResult> CreateAsync(
        CreateWorkingCalendarRequest request,
        HttpContext context,
        WorkingCalendarService service,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var accessError))
        {
            return accessError!;
        }

        try
        {
            var calendar = await service.CreateAsync(request.ToCommand(), authority!, cancellationToken);
            SetEntityTag(context.Response, calendar);
            return Results.Created(
                $"/api/v1/working-calendars/{calendar.WorkingCalendarId}",
                WorkingCalendarResponse.FromDomain(calendar));
        }
        catch (WorkingCalendarValidationException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status422UnprocessableEntity,
                "validation_failed",
                exception.Message,
                context,
                exception.Issues.Select(issue => (object)new
                {
                    field = issue.Field,
                    code = issue.Code,
                    message = issue.Message
                }));
        }
        catch (WorkingCalendarNameConflictException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict,
                "working_calendar_name_conflict",
                exception.Message,
                context);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict,
                exception.Code,
                exception.Message,
                context);
        }
    }

    private static async Task<IResult> GetAsync(
        string workingCalendarId,
        HttpContext context,
        WorkingCalendarService service,
        CancellationToken cancellationToken)
    {
        var calendar = await service.GetByIdAsync(workingCalendarId, cancellationToken);
        if (calendar is null) return NotFound(context);
        SetEntityTag(context.Response, calendar);
        return Results.Ok(WorkingCalendarResponse.FromDomain(calendar));
    }

    private static async Task<IResult> UpdateAsync(
        string workingCalendarId,
        PatchWorkingCalendarRequest request,
        HttpContext context,
        WorkingCalendarService service,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        if (!PlanningHttpSupport.TryReadExpectedVersion(
                context.Request.Headers.IfMatch,
                "working-calendar",
                workingCalendarId,
                out var version))
        {
            var missing = StringValues.IsNullOrEmpty(context.Request.Headers.IfMatch);
            return PlanningHttpSupport.Error(
                missing ? 428 : 412,
                missing ? "precondition_required" : "resource_version_stale",
                "A matching Working Calendar If-Match header is required.",
                context);
        }

        try
        {
            var calendar = await service.UpdateAsync(
                workingCalendarId, version, request.ToCommand(), authority!, cancellationToken);
            SetEntityTag(context.Response, calendar);
            return Results.Ok(WorkingCalendarResponse.FromDomain(calendar));
        }
        catch (Exception exception) when (TryMapError(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static async Task<IResult> DeleteAsync(
        string workingCalendarId,
        HttpContext context,
        WorkingCalendarService service,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        try
        {
            return await service.DeleteAsync(workingCalendarId, authority!, cancellationToken)
                ? Results.NoContent()
                : NotFound(context);
        }
        catch (Exception exception) when (TryMapError(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static async Task<IResult> GetSetupAsync(
        WorkingCalendarService service,
        CancellationToken cancellationToken) =>
        Results.Ok(SetupCalendarResponse.FromDomain(
            await service.GetSetupCalendarAsync(cancellationToken)));

    private static async Task<IResult> SetSetupAsync(
        SetSetupCalendarRequest request,
        HttpContext context,
        WorkingCalendarService service,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        if (string.IsNullOrWhiteSpace(request.WorkingCalendarId))
            return PlanningHttpSupport.Error(422, "validation_failed", "workingCalendarId is required.", context);
        try
        {
            var calendar = await service.SetSetupCalendarAsync(
                request.WorkingCalendarId.Trim(), authority!, cancellationToken);
            return Results.Ok(SetupCalendarResponse.FromDomain(calendar));
        }
        catch (Exception exception) when (TryMapError(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static async Task<IResult> ClearSetupAsync(
        HttpContext context,
        WorkingCalendarService service,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        try
        {
            await service.ClearSetupCalendarAsync(authority!, cancellationToken);
            return Results.NoContent();
        }
        catch (Exception exception) when (TryMapError(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static bool TryMapError(Exception exception, HttpContext context, out IResult? result)
    {
        result = exception switch
        {
            WorkingCalendarRequestException request => PlanningHttpSupport.Error(
                400, "invalid_request", request.Message, context,
                request.Issues.Select(issue => (object)new
                {
                    field = string.IsNullOrEmpty(issue.Field) ? null : issue.Field,
                    code = issue.Code,
                    message = issue.Message
                })),
            WorkingCalendarValidationException validation => PlanningHttpSupport.Error(
                422, "validation_failed", validation.Message, context,
                validation.Issues.Select(issue => (object)new
                {
                    field = issue.Field,
                    code = issue.Code,
                    message = issue.Message
                })),
            WorkingCalendarNameConflictException => PlanningHttpSupport.Error(
                409, "working_calendar_name_conflict", exception.Message, context),
            WorkingCalendarNotFoundException => NotFound(context),
            WorkingCalendarVersionConflictException => PlanningHttpSupport.Error(
                412, "resource_version_stale", exception.Message, context),
            WorkingCalendarInUseException => PlanningHttpSupport.Error(
                409, "working_calendar_in_use", exception.Message, context),
            WorkingCalendarUsageInUseException => PlanningHttpSupport.Error(
                409, "working_calendar_usage_in_use", exception.Message, context),
            EditModeMutationException edit => PlanningHttpSupport.Error(409, edit.Code, edit.Message, context),
            _ => null
        };
        return result is not null;
    }

    private static IResult NotFound(HttpContext context) => PlanningHttpSupport.Error(
        404, "resource_not_found", "The requested Working Calendar was not found.", context);

    private static void SetEntityTag(HttpResponse response, WorkingCalendar calendar) =>
        response.Headers.ETag = $"\"working-calendar:{calendar.WorkingCalendarId}:v{calendar.Version}\"";
}
