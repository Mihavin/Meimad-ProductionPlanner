using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Qc;

namespace Meimad.Planner.Server.Api.Qc;

internal static class QcWorkflowEndpoints
{
    internal static void MapQcWorkflowEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/qc-queue", ListAsync);
        endpoints.MapPost("/api/v1/qc-queue/{productionRunId}/decision", DecideAsync);
    }

    private static async Task<IResult> ListAsync(
        QcWorkflowService service,
        CancellationToken cancellationToken) =>
        Results.Ok(new { items = await service.ListQueueAsync(cancellationToken) });

    private static async Task<IResult> DecideAsync(
        string productionRunId,
        QcDecisionRequest request,
        HttpContext context,
        QcWorkflowService service,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(
                context, out var authority, out var authorityError))
            return authorityError!;
        if (!PlanningHttpSupport.TryReadClientIdentity(
                context, out _, out var userId, out var identityError))
            return identityError!;

        try
        {
            return Results.Ok(await service.DecideAsync(
                new(productionRunId, request.Decision, userId!, request.Reason),
                authority!,
                cancellationToken));
        }
        catch (QcWorkflowValidationException exception)
        {
            return PlanningHttpSupport.Error(422, exception.Code, exception.Message, context);
        }
        catch (QcWorkflowNotFoundException exception)
        {
            return PlanningHttpSupport.Error(
                404, "qc_production_run_not_found", exception.Message, context);
        }
        catch (QcWorkflowStateException exception)
        {
            return PlanningHttpSupport.Error(
                409, "qc_decision_not_allowed", exception.Message, context);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(409, exception.Code, exception.Message, context);
        }
    }
}

internal sealed record QcDecisionRequest(string Decision, string? Reason);
