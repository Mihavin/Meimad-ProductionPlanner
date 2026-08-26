using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Domain.ProductionRuns;
using Microsoft.Extensions.Primitives;

namespace Meimad.Planner.Server.Api.ProductionRuns;

internal static class ProductionRunEndpoints
{
    internal static void MapProductionRunEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var runs = endpoints.MapGroup("/api/v1/production-runs");
        runs.MapGet(string.Empty, ListAsync);
        runs.MapGet("/{runId}", GetAsync);
        runs.MapGet("/{runId}/readiness", GetReadinessAsync);
        endpoints.MapGet(
            "/api/v1/machines/{machineId}/production-runs/{runId}/debug-timeline",
            GetDebugTimelineAsync);
        runs.MapPost(string.Empty, CreateAsync);
        runs.MapPut("/{runId}/composition", UpdateCompositionAsync);
        runs.MapPut("/{runId}/assignment", AssignAsync);
        runs.MapDelete("/{runId}/assignment", UnassignAsync);
        runs.MapPost("/{runId}/cancel", CancelAsync);
        runs.MapPost("/{runId}/start", StartAsync);
        runs.MapPost("/{runId}/programs/{programId}/activate", ActivateProgramAsync);
        runs.MapPost("/{runId}/programs/{programId}/cycles", RecordCycleAsync);
        runs.MapPost("/{runId}/suspend", SuspendAsync);
        runs.MapPost("/{runId}/resume", ResumeAsync);
        runs.MapPost("/{runId}/reset", ResetAsync);
        endpoints.MapGet("/api/v1/batch-operations/unallocated", ListUnallocatedAsync);
    }

    private static async Task<IResult> ListAsync(ProductionRunService service, CancellationToken token) =>
        Results.Ok(new ProductionRunListResponse(
            (await service.ListAsync(token)).Select(ProductionRunResponse.FromDomain).ToArray()));

    private static async Task<IResult> ListUnallocatedAsync(
        ProductionRunService service, CancellationToken token) =>
        Results.Ok(new UnallocatedBatchOperationListResponse(await service.ListUnallocatedAsync(token)));

    private static async Task<IResult> GetAsync(
        string runId, HttpContext context, ProductionRunService service, CancellationToken token)
    {
        try
        {
            var value = await service.GetAsync(runId, token);
            SetTag(context.Response, value);
            return Results.Ok(ProductionRunResponse.FromDomain(value));
        }
        catch (Exception exception) when (TryMap(exception, context, out var result))
        {
            return result!;
        }
    }

    private static async Task<IResult> GetReadinessAsync(
        string runId, HttpContext context, ProductionRunReadinessService service, CancellationToken token)
    {
        try { return Results.Ok(await service.ReadAsync(runId, token)); }
        catch (Exception exception) when (TryMap(exception, context, out var result)) { return result!; }
    }

    private static async Task<IResult> GetDebugTimelineAsync(
        string machineId,
        string runId,
        int? limit,
        HttpContext context,
        ProductionRunDebugTimelineService service,
        CancellationToken token)
    {
        try
        {
            return Results.Ok(await service.ReadAsync(
                machineId, runId, limit ?? 200, token));
        }
        catch (ProductionRunDebugTimelineValidationException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status400BadRequest,
                "invalid_debug_timeline_request",
                exception.Message,
                context);
        }
        catch (ProductionRunDebugTimelineNotFoundException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status404NotFound,
                "production_run_debug_timeline_not_found",
                exception.Message,
                context);
        }
    }

    private static async Task<IResult> CreateAsync(
        CreateProductionRunRequest request, HttpContext context,
        ProductionRunService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error))
            return error!;
        try
        {
            var value = await service.CreateAsync(request.ToCommand(), authority!, token);
            SetTag(context.Response, value);
            return Results.Created($"/api/v1/production-runs/{value.ProductionRunId}",
                ProductionRunResponse.FromDomain(value));
        }
        catch (Exception exception) when (TryMap(exception, context, out var result))
        {
            return result!;
        }
    }

    private static async Task<IResult> AssignAsync(
        string runId, AssignProductionRunRequest request, HttpContext context,
        ProductionRunService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error))
            return error!;
        if (!TryVersion(context, runId, out var expectedVersion, out error)) return error!;
        try
        {
            var value = await service.AssignAsync(runId, expectedVersion, request.ToCommand(), authority!, token);
            SetTag(context.Response, value);
            return Results.Ok(ProductionRunResponse.FromDomain(value));
        }
        catch (Exception exception) when (TryMap(exception, context, out var result))
        {
            return result!;
        }
    }

    private static async Task<IResult> UpdateCompositionAsync(
        string runId, CreateProductionRunRequest request, HttpContext context,
        ProductionRunService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        if (!TryVersion(context, runId, out var expectedVersion, out error)) return error!;
        try
        {
            var value = await service.UpdateCompositionAsync(runId, expectedVersion, request.ToCommand(), authority!, token);
            SetTag(context.Response, value);
            return Results.Ok(ProductionRunResponse.FromDomain(value));
        }
        catch (Exception exception) when (TryMap(exception, context, out var result)) { return result!; }
    }

    private static async Task<IResult> UnassignAsync(
        string runId, HttpContext context, ProductionRunService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        if (!TryVersion(context, runId, out var expectedVersion, out error)) return error!;
        try
        {
            var value = await service.UnassignAsync(runId, expectedVersion, authority!, token);
            SetTag(context.Response, value);
            return Results.Ok(ProductionRunResponse.FromDomain(value));
        }
        catch (Exception exception) when (TryMap(exception, context, out var result)) { return result!; }
    }

    private static async Task<IResult> CancelAsync(
        string runId, CancelProductionRunRequest request, HttpContext context,
        ProductionRunService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error))
            return error!;
        if (!TryVersion(context, runId, out var expectedVersion, out error)) return error!;
        try
        {
            var value = await service.CancelAsync(runId, expectedVersion, request.Reason, authority!, token);
            SetTag(context.Response, value);
            return Results.Ok(ProductionRunResponse.FromDomain(value));
        }
        catch (Exception exception) when (TryMap(exception, context, out var result))
        {
            return result!;
        }
    }

    private static Task<IResult> StartAsync(string runId, HttpContext context, ProductionRunExecutionService service, CancellationToken token) =>
        ExecuteAsync(runId, context, (version, authority) => service.StartAsync(runId, version, authority, token));
    private static Task<IResult> ActivateProgramAsync(string runId, string programId, HttpContext context, ProductionRunExecutionService service, CancellationToken token) =>
        ExecuteAsync(runId, context, (version, authority) => service.ActivateProgramAsync(runId, programId, version, authority, token));
    private static async Task<IResult> RecordCycleAsync(string runId, string programId, RecordProductionRunCycleRequest request, HttpContext context, ProductionRunExecutionService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        if (!TryVersion(context, runId, out var version, out error)) return error!;
        try
        {
            var result = await service.RecordCycleAsync(runId, programId, version,
                new(request.Source ?? "WINDOWS", request.SourceEventId ?? string.Empty, request.ObservedAt ?? DateTimeOffset.UtcNow), authority!, token);
            SetTag(context.Response, result.Run);
            return Results.Ok(new { result.WasDuplicate, result.CompletedCycleCount, run = ProductionRunResponse.FromDomain(result.Run) });
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped)) { return mapped!; }
    }
    private static Task<IResult> SuspendAsync(string runId, ProductionRunReasonRequest request, HttpContext context, ProductionRunExecutionService service, CancellationToken token) =>
        ExecuteAsync(runId, context, (version, authority) => service.SuspendAsync(runId, version, request.Reason ?? string.Empty, authority, token));
    private static Task<IResult> ResumeAsync(string runId, HttpContext context, ProductionRunExecutionService service, CancellationToken token) =>
        ExecuteAsync(runId, context, (version, authority) => service.ResumeAsync(runId, version, authority, token));
    private static Task<IResult> ResetAsync(string runId, ProductionRunReasonRequest request, HttpContext context, ProductionRunExecutionService service, CancellationToken token) =>
        ExecuteAsync(runId, context, (version, authority) => service.ResetAsync(runId, version, request.Reason ?? string.Empty, authority, token));

    private static async Task<IResult> ExecuteAsync(string runId, HttpContext context, Func<int, EditAuthority, Task<ProductionRun>> action)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        if (!TryVersion(context, runId, out var version, out error)) return error!;
        try { var value = await action(version, authority!); SetTag(context.Response, value); return Results.Ok(ProductionRunResponse.FromDomain(value)); }
        catch (Exception exception) when (TryMap(exception, context, out var mapped)) { return mapped!; }
    }

    private static bool TryVersion(HttpContext context, string id, out int version, out IResult? error)
    {
        error = null;
        if (PlanningHttpSupport.TryReadExpectedVersion(
                context.Request.Headers.IfMatch, "production-run", id, out version)) return true;
        var missing = StringValues.IsNullOrEmpty(context.Request.Headers.IfMatch);
        error = PlanningHttpSupport.Error(missing ? 428 : 412,
            missing ? "precondition_required" : "resource_version_stale",
            "A matching Production Run If-Match header is required.", context);
        return false;
    }

    private static bool TryMap(Exception exception, HttpContext context, out IResult? result)
    {
        result = exception switch
        {
            ProductionRunValidationException value => PlanningHttpSupport.Error(422,
                "validation_failed", value.Message, context,
                [new { field = value.Field, code = value.Code, message = value.Message }]),
            ProductionRunCycleValidationException value => PlanningHttpSupport.Error(422,
                "cycle_plan_invalid", value.Message, context,
                [new { code = value.Code, message = value.Message }]),
            ProductionRunNotFoundException => PlanningHttpSupport.Error(404,
                "resource_not_found", exception.Message, context),
            ProductionRunVersionConflictException => PlanningHttpSupport.Error(412,
                "resource_version_stale", exception.Message, context),
            ProductionRunStateException value => PlanningHttpSupport.Error(409,
                value.Code, value.Message, context),
            EditModeMutationException value => PlanningHttpSupport.Error(409,
                value.Code, value.Message, context),
            _ => null
        };
        return result is not null;
    }

    private static void SetTag(HttpResponse response, ProductionRun value) =>
        response.Headers.ETag = $"\"production-run:{value.ProductionRunId}:v{value.Version}\"";
}
