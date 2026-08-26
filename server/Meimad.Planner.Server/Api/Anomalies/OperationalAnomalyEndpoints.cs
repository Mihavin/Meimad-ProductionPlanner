using Meimad.Planner.Server.Application.Anomalies;

namespace Meimad.Planner.Server.Api.Anomalies;

internal static class OperationalAnomalyEndpoints
{
    internal static void MapOperationalAnomalyEndpoints(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/operational-anomalies", ListAsync);

    private static async Task<IResult> ListAsync(
        string? machineId,
        string? productionRunId,
        string? anomalyType,
        int? limit,
        HttpContext context,
        OperationalAnomalyService service,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(new
            {
                items = await service.ListAsync(
                    machineId, productionRunId, anomalyType, limit ?? 200, cancellationToken)
            });
        }
        catch (OperationalAnomalyValidationException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status400BadRequest,
                exception.Code,
                exception.Message,
                context,
                [new { field = exception.Field }]);
        }
    }
}
