using Meimad.Planner.Server.Application.Downtimes;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Downtimes;
using Microsoft.Extensions.Primitives;

namespace Meimad.Planner.Server.Api.Downtimes;

internal static class MachineDowntimeEndpoints
{
    internal static void MapMachineDowntimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/downtimes");
        group.MapGet(string.Empty, ListAsync);
        group.MapGet("/{downtimeId}", GetAsync);
        group.MapPost(string.Empty, CreateAsync);
        group.MapPatch("/{downtimeId}", UpdateAsync);
        group.MapPost("/{downtimeId}/restore", RestoreAsync);
    }

    private static async Task<IResult> ListAsync(string? machineId, MachineDowntimeService service, CancellationToken token)
    {
        var values = await service.ListAsync(machineId, token);
        return Results.Ok(new MachineDowntimeListResponse(values.Select(MachineDowntimeResponse.FromDomain).ToArray(), null));
    }

    private static async Task<IResult> GetAsync(string downtimeId, HttpContext context, MachineDowntimeService service, CancellationToken token)
    {
        var value = await service.GetAsync(downtimeId, token);
        if (value is null) return NotFound(context);
        SetTag(context.Response, value);
        return Results.Ok(MachineDowntimeResponse.FromDomain(value));
    }

    private static async Task<IResult> CreateAsync(CreateMachineDowntimeRequest request, HttpContext context, MachineDowntimeService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        try
        {
            var value = request.ToCommand() switch
            {
                CreatePlannedMaintenanceCommand planned => await service.CreatePlannedAsync(planned, authority!, token),
                ReportBreakdownCommand breakdown => await service.ReportBreakdownAsync(breakdown, authority!, token),
                _ => throw new InvalidOperationException()
            };
            SetTag(context.Response, value);
            return Results.Created($"/api/v1/downtimes/{value.DowntimeId}", MachineDowntimeResponse.FromDomain(value));
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped)) { return mapped!; }
    }

    private static async Task<IResult> UpdateAsync(string downtimeId, UpdatePlannedMaintenanceRequest request, HttpContext context, MachineDowntimeService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        if (!TryVersion(context, downtimeId, out var version, out var versionError)) return versionError!;
        try
        {
            var value = await service.UpdatePlannedAsync(downtimeId, version, request.ToCommand(), authority!, token);
            SetTag(context.Response, value);
            return Results.Ok(MachineDowntimeResponse.FromDomain(value));
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped)) { return mapped!; }
    }

    private static async Task<IResult> RestoreAsync(string downtimeId, RestoreBreakdownRequest request, HttpContext context, MachineDowntimeService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        if (!TryVersion(context, downtimeId, out var version, out var versionError)) return versionError!;
        try
        {
            var value = await service.RestoreAsync(downtimeId, version, request.ToCommand(), authority!, token);
            SetTag(context.Response, value);
            return Results.Ok(MachineDowntimeResponse.FromDomain(value));
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped)) { return mapped!; }
    }

    private static bool TryVersion(HttpContext context, string id, out int version, out IResult? error)
    {
        if (PlanningHttpSupport.TryReadExpectedVersion(context.Request.Headers.IfMatch, "downtime", id, out version))
        {
            error = null;
            return true;
        }
        var missing = StringValues.IsNullOrEmpty(context.Request.Headers.IfMatch);
        error = PlanningHttpSupport.Error(missing ? 428 : 412,
            missing ? "precondition_required" : "resource_version_stale",
            "A matching downtime If-Match header is required.", context);
        return false;
    }

    private static bool TryMap(Exception exception, HttpContext context, out IResult? result)
    {
        result = exception switch
        {
            MachineDowntimeRequestException => PlanningHttpSupport.Error(400, "invalid_request", exception.Message, context),
            MachineDowntimeValidationException validation => PlanningHttpSupport.Error(422, "validation_failed", validation.Message, context,
                validation.Issues.Select(issue => (object)new { field = (string?)null, code = "invalid_downtime", message = issue })),
            MachineDowntimeMachineException => PlanningHttpSupport.Error(422, "machine_not_found", exception.Message, context),
            MachineDowntimeStateException => PlanningHttpSupport.Error(409, "downtime_state_conflict", exception.Message, context),
            MachineDowntimeVersionException => PlanningHttpSupport.Error(412, "resource_version_stale", exception.Message, context),
            MachineDowntimeNotFoundException => NotFound(context),
            EditModeMutationException edit => PlanningHttpSupport.Error(409, edit.Code, edit.Message, context),
            _ => null
        };
        return result is not null;
    }

    private static IResult NotFound(HttpContext context) =>
        PlanningHttpSupport.Error(404, "resource_not_found", "The requested Machine downtime was not found.", context);

    private static void SetTag(HttpResponse response, MachineDowntime value) =>
        response.Headers.ETag = $"\"downtime:{value.DowntimeId}:v{value.Version}\"";
}
