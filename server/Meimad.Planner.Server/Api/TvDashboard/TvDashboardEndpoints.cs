using Meimad.Planner.Server.Application.TvDashboard;
using Meimad.Planner.Server.Persistence;

namespace Meimad.Planner.Server.Api.TvDashboard;

/// <summary>
/// The read-only TV routes; like the page they need no sign-in (the <c>/api/v1/tv-dashboard</c> prefix is exempt), so
/// only GET is mapped here. Every change to the pictures goes through the signed-in Case Operation routes.
/// </summary>
internal static class TvDashboardEndpoints
{
    /// <summary>A rendered TV picture never changes for its version; a TV revalidates it at most hourly.</summary>
    internal const string ThumbnailCacheControl = "public, max-age=3600";

    internal static void MapTvDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/tv-dashboard", ReadAsync);
        endpoints.MapGet(TvDashboardService.ThumbnailRoute + "{version}", ReadThumbnailAsync);
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

    /// <summary>
    /// The small PNG (<see cref="TvThumbnailRenderer.Size"/> px square) of a picture the projection names. Only pictures of
    /// parts assigned to, or last worked on, a display-enabled Machine are served; the version is the only identifier.
    /// </summary>
    private static async Task<IResult> ReadThumbnailAsync(
        string version,
        HttpContext context,
        SqliteOperationThumbnailRepository repository,
        OperationThumbnailStore store,
        TvThumbnailRenderer renderer,
        CancellationToken cancellationToken)
    {
        if (TvDashboardService.HashOfThumbnailVersion(version) is not { } hash
            || await repository.FindDisplayableAsync(hash, cancellationToken) is not { } storedPath
            || store.PathOf(storedPath) is not { } file)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status404NotFound, "thumbnail_not_found",
                "This part picture is not shown on the TV dashboard.", context);
        }

        var entityTag = $"\"tv-thumbnail:{version}\"";
        context.Response.Headers.CacheControl = ThumbnailCacheControl;
        context.Response.Headers.ETag = entityTag;
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (context.Request.Headers.IfNoneMatch.Any(value => string.Equals(value, entityTag, StringComparison.Ordinal)))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var png = await renderer.RenderAsync(version, file, cancellationToken);
        if (png is null)
        {
            context.Response.Headers.Remove("ETag");
            context.Response.Headers.CacheControl = "no-cache";
            return PlanningHttpSupport.Error(StatusCodes.Status404NotFound, "thumbnail_unavailable",
                "This part picture cannot be shown.", context);
        }

        return Results.File(png, "image/png");
    }
}
