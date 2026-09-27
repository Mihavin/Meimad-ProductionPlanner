using System.Globalization;
using Meimad.Planner.Server.Application.Timeline;

namespace Meimad.Planner.Server.Api.Timeline;

internal static class TimelineEndpoints
{
    internal static void MapTimelineEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/timeline", ReadAsync);
        endpoints.MapPut("/api/v1/timeline/auxiliary-pins", SetPinAsync);
        endpoints.MapDelete("/api/v1/timeline/auxiliary-pins/{batchOperationId}/{requirementId}", ClearPinAsync);
    }

    /// <summary>
    /// Pins one auxiliary work item (Batch Operation plus requirement) to a Workstation and/or
    /// Employee and optionally to its start. The pin is a constraint for the next calculation; it
    /// never moves Machine assignments or backlog order.
    /// </summary>
    private static async Task<IResult> SetPinAsync(
        TimelineAuxiliaryPinRequest request,
        HttpContext context,
        ITimelineAuxiliaryPinRepository repository,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        if (string.IsNullOrWhiteSpace(request.BatchOperationId) || string.IsNullOrWhiteSpace(request.RequirementId))
        {
            return PlanningHttpSupport.Error(StatusCodes.Status422UnprocessableEntity, "validation_failed",
                "batchOperationId and requirementId are required.", context);
        }
        var userId = context.Request.Headers["X-Meimad-User-Id"].ToString().Trim();
        try
        {
            var pin = await repository.SetAsync(new TimelineAuxiliaryPin(
                request.BatchOperationId.Trim(), request.RequirementId.Trim(),
                Blank(request.WorkstationId), Blank(request.EmployeeId),
                request.PlannedStartsAt, request.PlannedEndsAt, request.PinStart, Blank(request.Reason)),
                authority!, string.IsNullOrEmpty(userId) ? null : userId, cancellationToken);
            return Results.Ok(new TimelineAuxiliaryPinResponse(
                pin.BatchOperationId, pin.RequirementId, pin.WorkstationId, pin.EmployeeId, pin.StartsAt));
        }
        catch (TimelineAuxiliaryPinException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status422UnprocessableEntity, exception.Code, exception.Message, context);
        }
        catch (Application.EditMode.EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, context);
        }
    }

    private static async Task<IResult> ClearPinAsync(
        string batchOperationId,
        string requirementId,
        HttpContext context,
        ITimelineAuxiliaryPinRepository repository,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        try
        {
            return await repository.ClearAsync(batchOperationId, requirementId, authority!, cancellationToken)
                ? Results.NoContent()
                : PlanningHttpSupport.Error(StatusCodes.Status404NotFound, "auxiliary_pin_not_found",
                    "No pin exists for that Batch Operation and requirement.", context);
        }
        catch (Application.EditMode.EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, context);
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<IResult> ReadAsync(
        HttpContext context,
        TimelineProjectionService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadInstant(context.Request.Query["from"], out var from)
            || !TryReadInstant(context.Request.Query["to"], out var to)
            || to <= from)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status400BadRequest,
                "invalid_timeline_horizon",
                "Query parameters 'from' and 'to' must be RFC 3339 instants and 'to' must be after 'from'.",
                context);
        }

        DateTimeOffset? asOf = null;
        if (context.Request.Query.TryGetValue("asOf", out var asOfValues))
        {
            if (!TryReadInstant(asOfValues, out var parsedAsOf))
            {
                return PlanningHttpSupport.Error(
                    StatusCodes.Status400BadRequest,
                    "invalid_timeline_as_of",
                    "Optional query parameter 'asOf' must be an RFC 3339 instant.",
                    context);
            }
            asOf = parsedAsOf;
            if (asOf < from || asOf >= to)
            {
                return PlanningHttpSupport.Error(
                    StatusCodes.Status400BadRequest,
                    "timeline_as_of_outside_horizon",
                    "Optional query parameter 'asOf' must fall inside the requested Timeline horizon.",
                    context);
            }
        }

        if (context.Request.Query.ContainsKey("mode"))
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status400BadRequest,
                "timeline_mode_is_assignment_owned",
                "Timeline planning mode is configured per Machine assignment and cannot be supplied as a global query parameter.",
                context);
        }

        return Results.Ok(await service.CalculateAsync(
            from, to, asOf, cancellationToken, recordDiagnostics: true));
    }

    private static bool TryReadInstant(string? value, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out instant);
}

internal sealed record TimelineAuxiliaryPinRequest(
    string? BatchOperationId,
    string? RequirementId,
    string? WorkstationId,
    string? EmployeeId,
    DateTimeOffset PlannedStartsAt,
    DateTimeOffset PlannedEndsAt,
    bool PinStart,
    string? Reason);

internal sealed record TimelineAuxiliaryPinResponse(
    string BatchOperationId,
    string RequirementId,
    string? WorkstationId,
    string? EmployeeId,
    DateTimeOffset? StartsAt);
