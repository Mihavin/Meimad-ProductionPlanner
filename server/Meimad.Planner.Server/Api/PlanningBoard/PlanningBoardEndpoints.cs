using Meimad.Planner.Server.Application.PlanningBoard;

namespace Meimad.Planner.Server.Api.PlanningBoard;

internal static class PlanningBoardEndpoints
{
    internal static void MapPlanningBoardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/planning-board", ReadAsync);
    }

    private static async Task<IResult> ReadAsync(
        PlanningBoardService service,
        CancellationToken cancellationToken)
    {
        var snapshot = await service.ReadAsync(cancellationToken);
        return Results.Ok(PlanningBoardResponse.FromApplication(snapshot));
    }
}
