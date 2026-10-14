using System.Text.Json.Serialization;
using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.ProductionBatches;
using Meimad.Planner.Server.Domain.Machines;
using Microsoft.Extensions.Primitives;

namespace Meimad.Planner.Server.Api.ProductionBatches;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WorkOrderTargetTimeRequest(double? PartPriceNis, double? MaterialTotalNis, string? Notes = null);

internal sealed record TargetTimeCalculationResponse(
    double PartPriceNis, double MaterialTotalNis, int Quantity, double MarginNis, IReadOnlyList<TargetTimeResource> Resources)
{
    internal static TargetTimeCalculationResponse FromDomain(TargetTimeCalculation value) =>
        new(value.PartPriceNis, value.MaterialTotalNis, value.Quantity, value.MarginNis, value.Resources);
}

internal sealed record WorkOrderTargetTimeResponse(
    string ProductionBatchId,
    TargetTimeCalculationResponse Calculation,
    string? Notes,
    int Version,
    string EntityTag,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy)
{
    internal static WorkOrderTargetTimeResponse FromDomain(WorkOrderTargetTime value) => new(
        value.ProductionBatchId, TargetTimeCalculationResponse.FromDomain(value.Calculation), value.Notes, value.Version,
        WorkOrderTargetTimeEndpoints.EntityTag(value), value.UpdatedAt, value.UpdatedBy);
}

/// <summary>The resources of the route with their hourly prices, before a part price and material total are entered.</summary>
internal sealed record TargetTimeResourcePrice(string Kind, string? ResourceId, string Name, double HourlyRateNis, bool DefaultRate, IReadOnlyList<int> OperationNumbers);

internal sealed record WorkOrderTargetTimeOverviewResponse(
    string ProductionBatchId,
    string BatchNumber,
    int Quantity,
    double? SuggestedPartPriceNis,
    double MachineDefaultHourlyRateNis,
    double WorkstationDefaultHourlyRateNis,
    IReadOnlyList<TargetTimeResourcePrice> Resources,
    WorkOrderTargetTimeResponse? Saved);

/// <summary>
/// <c>/api/v1/batches/{batchId}/target-time</c> (schema v122): GET (every signed-in user) gives the Work Order's quantity,
/// suggested part price, route resources with their hourly prices and the saved target time; POST <c>…/calculate</c>
/// (every signed-in user) calculates without saving; PUT and DELETE need <c>planning.workorders</c>, PUT creating with
/// <c>If-None-Match: *</c> or replacing with <c>If-Match</c>.
/// </summary>
internal static class WorkOrderTargetTimeEndpoints
{
    private const string EntityTagKind = "work-order-target-time";

    internal static void MapWorkOrderTargetTimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var route = endpoints.MapGroup("/api/v1/batches/{batchId}/target-time");
        route.MapGet(string.Empty, GetAsync);
        route.MapPost("/calculate", CalculateAsync);
        route.MapPut(string.Empty, PutAsync);
        route.MapDelete(string.Empty, DeleteAsync);
    }

    private static async Task<IResult> GetAsync(string batchId, HttpContext httpContext, WorkOrderTargetTimeService service, CancellationToken token)
    {
        var context = await service.GetContextAsync(batchId, token);
        if (context is null) return NotFound(httpContext);
        var saved = await service.GetAsync(batchId, token);
        var resources = WorkOrderTargetTimeService.Calculate(context, 0, 0).Resources
            .Select(resource => new TargetTimeResourcePrice(resource.Kind, resource.ResourceId, resource.Name, resource.HourlyRateNis,
                resource.DefaultRate, resource.OperationNumbers))
            .ToArray();
        if (saved is not null) httpContext.Response.Headers.ETag = EntityTag(saved);
        return Results.Ok(new WorkOrderTargetTimeOverviewResponse(
            context.ProductionBatchId, context.BatchNumber, context.Quantity, context.SuggestedPartPriceNis,
            ResourceHourlyRates.MachineDefaultNis, ResourceHourlyRates.WorkstationDefaultNis, resources,
            saved is null ? null : WorkOrderTargetTimeResponse.FromDomain(saved)));
    }

    private static async Task<IResult> CalculateAsync(
        string batchId, WorkOrderTargetTimeRequest request, HttpContext httpContext, WorkOrderTargetTimeService service, CancellationToken token)
    {
        try
        {
            var calculation = await service.CalculateAsync(batchId, request.PartPriceNis, request.MaterialTotalNis, token);
            return calculation is null ? NotFound(httpContext) : Results.Ok(TargetTimeCalculationResponse.FromDomain(calculation));
        }
        catch (WorkOrderTargetTimeValidationException exception)
        {
            return ValidationError(exception, httpContext);
        }
    }

    private static async Task<IResult> PutAsync(
        string batchId, WorkOrderTargetTimeRequest request, HttpContext httpContext, WorkOrderTargetTimeService service,
        IAccountRepository accounts, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryAuthorizeEdit(httpContext, Permissions.ManageWorkOrders, out var authority, out var accessError))
            return accessError!;
        var ifMatch = httpContext.Request.Headers.IfMatch;
        var ifNoneMatch = httpContext.Request.Headers.IfNoneMatch;
        int? expectedVersion;
        if (!StringValues.IsNullOrEmpty(ifMatch))
        {
            if (!StringValues.IsNullOrEmpty(ifNoneMatch))
                return PreconditionRequired(httpContext, "Send either If-None-Match: * to create or If-Match to replace, not both.");
            if (!PlanningHttpSupport.TryReadExpectedVersion(ifMatch, EntityTagKind, batchId.Trim(), out var version))
                return await Stale(httpContext, null, accounts, token);
            expectedVersion = version;
        }
        else if (ifNoneMatch.Count == 1 && ifNoneMatch[0]?.Trim() == "*")
        {
            expectedVersion = null;
        }
        else
        {
            return PreconditionRequired(httpContext,
                $"Send If-None-Match: * to create the target time or If-Match: \"{EntityTagKind}:{batchId}:v<version>\" to replace it.");
        }

        try
        {
            var result = await service.SaveAsync(batchId, request.PartPriceNis, request.MaterialTotalNis, request.Notes, expectedVersion, authority!, token);
            switch (result.Outcome)
            {
                case WorkOrderTargetTimeOutcome.Created:
                    httpContext.Response.Headers.ETag = EntityTag(result.TargetTime!);
                    return Results.Created($"/api/v1/batches/{Uri.EscapeDataString(batchId)}/target-time",
                        WorkOrderTargetTimeResponse.FromDomain(result.TargetTime!));
                case WorkOrderTargetTimeOutcome.Updated:
                    httpContext.Response.Headers.ETag = EntityTag(result.TargetTime!);
                    return Results.Ok(WorkOrderTargetTimeResponse.FromDomain(result.TargetTime!));
                case WorkOrderTargetTimeOutcome.NotFound:
                    return NotFound(httpContext);
                default:
                    return await Stale(httpContext, result.TargetTime, accounts, token);
            }
        }
        catch (WorkOrderTargetTimeValidationException exception)
        {
            return ValidationError(exception, httpContext);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, httpContext);
        }
    }

    private static async Task<IResult> DeleteAsync(
        string batchId, HttpContext httpContext, WorkOrderTargetTimeService service, IAccountRepository accounts, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryAuthorizeEdit(httpContext, Permissions.ManageWorkOrders, out var authority, out var accessError))
            return accessError!;
        var ifMatch = httpContext.Request.Headers.IfMatch;
        if (StringValues.IsNullOrEmpty(ifMatch))
            return PreconditionRequired(httpContext, $"If-Match: \"{EntityTagKind}:{batchId}:v<version>\" is required.");
        if (!PlanningHttpSupport.TryReadExpectedVersion(ifMatch, EntityTagKind, batchId.Trim(), out var expectedVersion))
            return await Stale(httpContext, null, accounts, token);
        try
        {
            var result = await service.DeleteAsync(batchId, expectedVersion, authority!, token);
            return result.Outcome switch
            {
                WorkOrderTargetTimeOutcome.Deleted => Results.NoContent(),
                WorkOrderTargetTimeOutcome.NotFound => NotFound(httpContext),
                WorkOrderTargetTimeOutcome.TargetTimeNotFound => PlanningHttpSupport.Error(StatusCodes.Status404NotFound,
                    "target_time_not_found", "This Work Order has no saved target time.", httpContext),
                _ => await Stale(httpContext, result.TargetTime, accounts, token)
            };
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, httpContext);
        }
    }

    private static async Task<IResult> Stale(HttpContext httpContext, WorkOrderTargetTime? current, IAccountRepository accounts, CancellationToken token)
    {
        const string message = "The Work Order target time changed since you opened it, so your change was not saved.";
        if (current is null)
            return PlanningHttpSupport.Error(StatusCodes.Status412PreconditionFailed, "resource_version_stale", message, httpContext);
        string? changedBy = current.UpdatedBy;
        if (!string.IsNullOrWhiteSpace(changedBy)
            && await accounts.FindDisplayNameAsync(changedBy, token) is { } displayName
            && !string.Equals(displayName, changedBy, StringComparison.OrdinalIgnoreCase))
            changedBy = $"{displayName} ({changedBy})";
        return Results.Json(new
        {
            error = new
            {
                code = "resource_version_stale",
                message,
                correlationId = httpContext.TraceIdentifier,
                details = new[] { new { currentVersion = current.Version, currentEntityTag = EntityTag(current) } },
                conflict = new
                {
                    resource = "Work Order target time",
                    changedBy,
                    changedAt = current.UpdatedAt,
                    advice = "Someone saved this Work Order's target time after you opened it, so your change was not saved. " +
                             "Refresh to load the current values, check what changed, then make your change again."
                }
            }
        }, statusCode: StatusCodes.Status412PreconditionFailed);
    }

    internal static string EntityTag(WorkOrderTargetTime value) => $"\"{EntityTagKind}:{value.ProductionBatchId}:v{value.Version}\"";

    private static IResult NotFound(HttpContext httpContext) =>
        PlanningHttpSupport.Error(StatusCodes.Status404NotFound, "resource_not_found", "The Work Order was not found.", httpContext);

    private static IResult PreconditionRequired(HttpContext httpContext, string message) =>
        PlanningHttpSupport.Error(StatusCodes.Status428PreconditionRequired, "precondition_required", message, httpContext);

    private static IResult ValidationError(WorkOrderTargetTimeValidationException exception, HttpContext httpContext) =>
        PlanningHttpSupport.Error(StatusCodes.Status422UnprocessableEntity, "validation_failed", exception.Message, httpContext,
            [new { field = exception.Field, code = "invalid", message = exception.Message }]);
}
