using Meimad.Planner.Server.Application.Preparation;
using Meimad.Planner.Server.Application.Projections;

namespace Meimad.Planner.Server.Api.Preparation;

internal static class PreparationQueueEndpoints
{
    internal static void MapPreparationQueueEndpoints(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/preparation-queues/{stage}", ListAsync);

    private static async Task<IResult> ListAsync(
        string stage,
        PreparationQueueService service,
        ProjectionResponseCache responses,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            context.Response.Headers.CacheControl = "no-store";
            var json = context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
            var bytes = await responses.ReadAsync("preparation:" + stage, cacheCompleted: false,
                async token => PlannerPerformanceMetrics.Serialize(new { items = await service.ListAsync(stage, token) }, json), cancellationToken);
            return Results.Bytes(bytes, "application/json; charset=utf-8");
        }
        catch (PreparationQueueValidationException exception)
        {
            return PlanningHttpSupport.Error(422, exception.Code, exception.Message, context);
        }
    }
}
