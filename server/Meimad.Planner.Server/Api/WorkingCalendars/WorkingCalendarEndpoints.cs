using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.WorkingCalendars;
using Meimad.Planner.Server.Domain.WorkingCalendars;

namespace Meimad.Planner.Server.Api.WorkingCalendars;

internal static class WorkingCalendarEndpoints
{
    internal static void MapWorkingCalendarEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var calendars = endpoints.MapGroup("/api/v1/working-calendars");
        calendars.MapGet(string.Empty, ListAsync);
        calendars.MapPost(string.Empty, CreateAsync);
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
}
