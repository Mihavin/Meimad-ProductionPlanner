using Meimad.Planner.Server.Application.PlanningBoard;
using Meimad.Planner.Server.Application.Projections;

namespace Meimad.Planner.Server.Api.PlanningBoard;

internal static class PlanningBoardEndpoints
{
    internal static void MapPlanningBoardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/planning-board", ReadAsync);
    }

    private static async Task<IResult> ReadAsync(
        HttpContext context,
        PlanningBoardService service,
        ProjectionResponseCache responses,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        var json = context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        var bytes = await responses.ReadAsync("board:live", cacheCompleted: false, async token =>
            PlannerPerformanceMetrics.Serialize(PlanningBoardResponse.FromApplication(await service.ReadAsync(token)), json), cancellationToken);
        return Results.Bytes(bytes, "application/json; charset=utf-8");
    }
}
