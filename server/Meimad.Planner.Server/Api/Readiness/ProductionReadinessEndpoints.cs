using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.MachineAssignments;
using Meimad.Planner.Server.Application.Readiness;
using Meimad.Planner.Server.Domain.Readiness;

namespace Meimad.Planner.Server.Api.Readiness;

internal static class ProductionReadinessEndpoints
{
    internal static void MapProductionReadinessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var operations = endpoints.MapGroup("/api/v1/batch-operations");
        operations.MapGet("/{batchOperationId}/readiness", ReadAsync);
        operations.MapPut("/{batchOperationId}/readiness-inputs", UpdateAsync);
    }

    private static async Task<IResult> ReadAsync(
        string batchOperationId,
        HttpContext context,
        ProductionReadinessService service,
        CancellationToken token)
    {
        try
        {
            return Results.Ok(ProductionReadinessResponse.FromDomain(
                await service.ReadAsync(batchOperationId, token)));
        }
        catch (BatchOperationNotFoundException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status404NotFound, "resource_not_found",
                exception.Message, context);
        }
    }

    private static async Task<IResult> UpdateAsync(
        string batchOperationId,
        ProductionReadinessInputRequest request,
        HttpContext context,
        ProductionReadinessService service,
        CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(
                context, out var authority, out var accessError))
        {
            return accessError!;
        }

        try
        {
            var result = await service.UpdateInputsAsync(
                batchOperationId,
                new ProductionReadinessInputUpdate(
                    request.SelectedGCodeReleaseId,
                    request.MaterialStatus ?? string.Empty,
                    request.MaterialComment,
                    request.ToolOffsetStatus ?? string.Empty,
                    request.ToolOffsetComment),
                authority!, token);
            return Results.Ok(ProductionReadinessResponse.FromDomain(result));
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
            ProductionReadinessValidationException validation =>
                PlanningHttpSupport.Error(
                    StatusCodes.Status422UnprocessableEntity,
                    "validation_failed", validation.Message, context,
                    [new
                    {
                        field = validation.Field,
                        code = validation.Code,
                        message = validation.Message
                    }]),
            BatchOperationNotFoundException => PlanningHttpSupport.Error(
                StatusCodes.Status404NotFound, "resource_not_found",
                exception.Message, context),
            EditModeMutationException edit => PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict, edit.Code, edit.Message, context),
            _ => null
        };
        return result is not null;
    }
}

internal sealed record ProductionReadinessInputRequest(
    string? SelectedGCodeReleaseId,
    string? MaterialStatus,
    string? MaterialComment,
    string? ToolOffsetStatus,
    string? ToolOffsetComment);

internal sealed record ProductionReadinessResponse(
    string OverallState,
    bool IsReadyForProduction,
    bool IsManaged,
    string Summary,
    IReadOnlyList<ReadinessComponentResponse> Components,
    string? EffectiveGCodeReleaseId,
    bool RequiresExplicitGCodeSelection,
    IReadOnlyList<ReadinessReleaseResponse> CompatibleGCodeReleases)
{
    internal static ProductionReadinessResponse FromDomain(
        ProductionReadinessResult value) => new(
            value.OverallState,
            value.IsReadyForProduction,
            value.IsManaged,
            value.Summary,
            value.Components.Select(ReadinessComponentResponse.FromDomain).ToArray(),
            value.EffectiveGCodeReleaseId,
            value.RequiresExplicitGCodeSelection,
            value.CompatibleGCodeReleases.Select(ReadinessReleaseResponse.FromDomain).ToArray());
}

internal sealed record ReadinessComponentResponse(
    string Key,
    string Label,
    string State,
    string Message,
    bool IsBlocking)
{
    internal static ReadinessComponentResponse FromDomain(ReadinessComponent value) =>
        new(value.Key, value.Label, value.State, value.Message, value.IsBlocking);
}

internal sealed record ReadinessReleaseResponse(
    string GCodeReleaseId,
    string ProcessRevisionId,
    string PostprocessorId,
    string PostprocessorName,
    string OriginalFileName,
    int PostSpecificRevision)
{
    internal static ReadinessReleaseResponse FromDomain(ReadinessRelease value) =>
        new(value.GCodeReleaseId, value.ProcessRevisionId, value.PostprocessorId,
            value.PostprocessorName, value.OriginalFileName, value.PostSpecificRevision);
}
