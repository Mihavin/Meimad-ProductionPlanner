using Meimad.Planner.Server.Application.TvDashboard;

namespace Meimad.Planner.Server.Api.TvDashboard;

internal static class TvDashboardEndpoints
{
    internal static void MapTvDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/tv-dashboard", ReadAsync);
    }

    private static async Task<IResult> ReadAsync(
        HttpContext context,
        TvDashboardService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ReadAsync(cancellationToken);
        context.Response.Headers.ETag = result.EntityTag;
        context.Response.Headers.CacheControl = "no-cache";
        if (context.Request.Headers.IfNoneMatch.Any(value =>
                string.Equals(value, result.EntityTag, StringComparison.Ordinal)))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Ok(result.Projection);
    }
}
