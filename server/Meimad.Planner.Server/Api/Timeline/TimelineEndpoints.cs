using System.Globalization;
using Meimad.Planner.Server.Application.Timeline;

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

        return Results.Ok(await service.CalculateAsync(from, to, cancellationToken));
    }

    private static bool TryReadInstant(string? value, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out instant);
}
