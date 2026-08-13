using System.Globalization;
using Meimad.Planner.Server.Application.Timeline;
using Meimad.Planner.Server.Domain.Timeline;

namespace Meimad.Planner.Server.Api.Timeline;

internal static class TimelineEndpoints
{
    internal static void MapTimelineEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/timeline", ReadAsync);
    }

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

        var modeValue = context.Request.Query["mode"].ToString();
        var mode = modeValue switch
        {
            "" or "manual" => TimelineCalculationMode.Forward,
            "backward" => TimelineCalculationMode.Backward,
            _ => (TimelineCalculationMode?)null
        };
        if (!mode.HasValue)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status400BadRequest,
                "invalid_timeline_mode",
                "Optional query parameter 'mode' must be 'manual' or 'backward'.",
                context);
        }

        return Results.Ok(await service.CalculateAsync(
            from, to, asOf, mode.Value, cancellationToken));
    }

    private static bool TryReadInstant(string? value, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out instant);
}
