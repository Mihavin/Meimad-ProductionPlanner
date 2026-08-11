using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.MachineAssignments;

namespace Meimad.Planner.Server.Api.MachineAssignments;

internal static class MachineAssignmentEndpoints
{
    internal static void MapMachineAssignmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var operations = endpoints.MapGroup("/api/v1/batch-operations");
        operations.MapPut("/{batchOperationId}/assignment", AssignOrMoveAsync);
        operations.MapDelete("/{batchOperationId}/assignment", UnassignAsync);
        operations.MapPost("/{batchOperationId}/start", StartAsync);
        operations.MapPost("/{batchOperationId}/suspend", SuspendAsync);
        operations.MapPost("/{batchOperationId}/finish", FinishAsync);
    }

    private static async Task<IResult> AssignOrMoveAsync(
        string batchOperationId,
        AssignMachineRequest request,
        HttpContext httpContext,
        MachineAssignmentService service,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(
                httpContext,
                out var authority,
                out var accessError))
        {
            return accessError!;
        }

        try
        {
            var result = await service.AssignOrMoveAsync(
                batchOperationId,
                request.MachineId ?? string.Empty,
                request.BacklogPosition,
                authority!,
                cancellationToken);
            var response = MachineAssignmentResponse.FromDomain(result.Assignment);
            return result.WasCreated
                ? Results.Created(
                    $"/api/v1/machine-assignments/{result.Assignment.MachineAssignmentId}",
                    response)
                : Results.Ok(response);
        }
        catch (Exception exception) when (TryMapError(exception, httpContext, out var error))
        {
            return error!;
        }
    }

    private static async Task<IResult> UnassignAsync(
        string batchOperationId,
        HttpContext httpContext,
        MachineAssignmentService service,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(
                httpContext,
                out var authority,
                out var accessError))
        {
            return accessError!;
        }

        try
        {
            await service.UnassignAsync(batchOperationId, authority!, cancellationToken);
            return Results.NoContent();
        }
        catch (Exception exception) when (TryMapError(exception, httpContext, out var error))
        {
            return error!;
        }
    }

    private static Task<IResult> StartAsync(
        string batchOperationId,
        HttpContext context,
        MachineAssignmentService service,
        CancellationToken cancellationToken) =>
        ChangeExecutionStatusAsync(
            batchOperationId,
            BatchOperationExecutionAction.Start,
            context,
            service,
            cancellationToken);

    private static Task<IResult> SuspendAsync(
        string batchOperationId,
        HttpContext context,
        MachineAssignmentService service,
        CancellationToken cancellationToken) =>
        ChangeExecutionStatusAsync(
            batchOperationId,
            BatchOperationExecutionAction.Suspend,
            context,
            service,
            cancellationToken);

    private static Task<IResult> FinishAsync(
        string batchOperationId,
        HttpContext context,
        MachineAssignmentService service,
        CancellationToken cancellationToken) =>
        ChangeExecutionStatusAsync(
            batchOperationId,
            BatchOperationExecutionAction.Finish,
            context,
            service,
            cancellationToken);

    private static async Task<IResult> ChangeExecutionStatusAsync(
        string batchOperationId,
        BatchOperationExecutionAction action,
        HttpContext context,
        MachineAssignmentService service,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(
                context, out var authority, out var accessError))
        {
            return accessError!;
        }

        try
        {
            var result = await service.ChangeExecutionStatusAsync(
                batchOperationId, action, authority!, cancellationToken);
            return Results.Ok(BatchOperationExecutionResponse.FromApplication(result));
        }
        catch (Exception exception) when (TryMapError(exception, context, out var error))
        {
            return error!;
        }
    }

    private static bool TryMapError(
        Exception exception,
        HttpContext context,
        out IResult? result)
    {
        result = exception switch
        {
            MachineAssignmentValidationException validation => PlanningHttpSupport.Error(
                StatusCodes.Status422UnprocessableEntity,
                "validation_failed",
                validation.Message,
                context,
                [new { field = validation.Field, code = validation.Code, message = validation.Message }]),
            BatchOperationNotFoundException or AssignmentMachineNotFoundException =>
                PlanningHttpSupport.Error(
                    StatusCodes.Status404NotFound,
                    "resource_not_found",
                    exception.Message,
                    context),
            IncompatibleMachineException => PlanningHttpSupport.Error(
                StatusCodes.Status422UnprocessableEntity,
                "incompatible_machine",
                exception.Message,
                context),
            BacklogPositionOutOfRangeException => PlanningHttpSupport.Error(
                StatusCodes.Status422UnprocessableEntity,
                "backlog_position_out_of_range",
                exception.Message,
                context),
            CompletedBatchOperationCannotBeAssignedException => PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict,
                "operation_completed",
                exception.Message,
                context),
            RunningBatchOperationCannotMoveException => PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict,
                "operation_in_progress",
                exception.Message,
                context),
            BatchOperationNotAssignedException => PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict,
                "operation_not_assigned",
                exception.Message,
                context),
            BatchOperationTransitionException => PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict,
                "invalid_operation_transition",
                exception.Message,
                context),
            BatchOperationNotFirstException => PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict,
                "operation_not_first_in_backlog",
                exception.Message,
                context),
            MachineAlreadyRunningOperationException => PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict,
                "machine_operation_already_in_progress",
                exception.Message,
                context),
            EditModeMutationException edit => PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict,
                edit.Code,
                edit.Message,
                context),
            _ => null
        };
        return result is not null;
    }
}
