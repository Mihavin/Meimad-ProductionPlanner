using Meimad.Planner.Server.Application.Deletion;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Kitaron;

namespace Meimad.Planner.Server.Api.Deletion;

internal static class PlanningDeletionEndpoints
{
    internal static void MapPlanningDeletionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDelete("/api/v1/cases/{caseId}", (string caseId, HttpContext c, PlanningDeletionService s, CancellationToken t) => DeleteAsync(c, a => s.DeleteCaseAsync(caseId, a, t)));
        endpoints.MapDelete("/api/v1/cases/{caseId}/operations/{operationId}", (string caseId, string operationId, HttpContext c, PlanningDeletionService s, CancellationToken t) => DeleteAsync(c, a => s.DeleteCaseOperationAsync(caseId, operationId, a, t)));
        endpoints.MapDelete("/api/v1/orders/{orderId}", (string orderId, HttpContext c, PlanningDeletionService s, CancellationToken t) => DeleteAsync(c, a => s.DeleteOrderAsync(orderId, a, t)));
        endpoints.MapDelete("/api/v1/batches/{batchId}", (string batchId, HttpContext c, PlanningDeletionService s, CancellationToken t) => DeleteAsync(c, a => s.DeleteBatchAsync(batchId, a, t)));
        endpoints.MapDelete("/api/v1/machines/{machineId}", (string machineId, HttpContext c, PlanningDeletionService s, CancellationToken t) => DeleteAsync(c, a => s.DeleteMachineAsync(machineId, a, t)));
    }

    private static async Task<IResult> DeleteAsync(HttpContext context, Func<EditAuthority, Task<bool>> delete)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        try
        {
            return await delete(authority!)
                ? Results.NoContent()
                : PlanningHttpSupport.Error(404, "resource_not_found", "The requested resource was not found.", context);
        }
        catch (PlanningDeletionBlockedException exception)
        {
            return PlanningHttpSupport.Error(409, "delete_blocked", exception.Message, context);
        }
        catch (KitaronManagedResourceException exception)
        {
            return PlanningHttpSupport.Error(409, "kitaron_managed_read_only", exception.Message, context);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(409, exception.Code, exception.Message, context);
        }
    }
}
