using Meimad.Planner.Server.Application.Preparation;

namespace Meimad.Planner.Server.Api.Preparation;

internal static class PreparationQueueEndpoints
{
    internal static void MapPreparationQueueEndpoints(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/preparation-queues/{stage}", ListAsync);

    private static async Task<IResult> ListAsync(
        string stage,
        PreparationQueueService service,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(new { items = await service.ListAsync(stage, cancellationToken) });
        }
        catch (PreparationQueueValidationException exception)
        {
            return PlanningHttpSupport.Error(422, exception.Code, exception.Message, context);
        }
    }
}
