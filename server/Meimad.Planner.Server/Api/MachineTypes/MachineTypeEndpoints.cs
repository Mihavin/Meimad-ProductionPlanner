using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.MachineTypes;
using Meimad.Planner.Server.Domain.MachineTypes;
using Microsoft.Extensions.Primitives;

namespace Meimad.Planner.Server.Api.MachineTypes;

internal static class MachineTypeEndpoints
{
    internal static void MapMachineTypeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var types = endpoints.MapGroup("/api/v1/machine-types");
        types.MapGet(string.Empty, ListAsync);
        types.MapPost(string.Empty, CreateAsync);
        types.MapGet("/{machineTypeId}", GetAsync);
        types.MapPatch("/{machineTypeId}", UpdateAsync);
        types.MapDelete("/{machineTypeId}", DeleteAsync);
    }

    private static async Task<IResult> ListAsync(MachineTypeService service, CancellationToken token)
    {
        var values = await service.ListAsync(token);
        return Results.Ok(new MachineTypeListResponse(values.Select(MachineTypeResponse.FromDomain).ToArray(), null));
    }

    private static async Task<IResult> GetAsync(string machineTypeId, HttpContext context, MachineTypeService service, CancellationToken token)
    {
        var value = await service.GetByIdAsync(machineTypeId, token);
        if (value is null) return NotFound(context);
        SetTag(context.Response, value);
        return Results.Ok(MachineTypeResponse.FromDomain(value));
    }

    private static async Task<IResult> CreateAsync(CreateMachineTypeRequest request, HttpContext context, MachineTypeService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        try
        {
            var value = await service.CreateAsync(request.ToCommand(), authority!, token);
            SetTag(context.Response, value);
            return Results.Created($"/api/v1/machine-types/{value.MachineTypeId}", MachineTypeResponse.FromDomain(value));
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped)) { return mapped!; }
    }

    private static async Task<IResult> UpdateAsync(string machineTypeId, PatchMachineTypeRequest request, HttpContext context, MachineTypeService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        if (!PlanningHttpSupport.TryReadExpectedVersion(context.Request.Headers.IfMatch, "machine-type", machineTypeId, out var version))
        {
            var missing = StringValues.IsNullOrEmpty(context.Request.Headers.IfMatch);
            return PlanningHttpSupport.Error(missing ? 428 : 412,
                missing ? "precondition_required" : "resource_version_stale",
                "A matching Machine Type If-Match header is required.", context);
        }
        try
        {
            var value = await service.UpdateAsync(machineTypeId, version, request.ToCommand(), authority!, token);
            SetTag(context.Response, value);
            return Results.Ok(MachineTypeResponse.FromDomain(value));
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped)) { return mapped!; }
    }

    private static async Task<IResult> DeleteAsync(string machineTypeId, HttpContext context, MachineTypeService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        try
        {
            return await service.DeleteAsync(machineTypeId, authority!, token)
                ? Results.NoContent()
                : NotFound(context);
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped)) { return mapped!; }
    }

    private static bool TryMap(Exception exception, HttpContext context, out IResult? result)
    {
        result = exception switch
        {
            MachineTypeRequestException request => PlanningHttpSupport.Error(400, "invalid_request", request.Message, context,
                request.Issues.Select(issue => (object)new { field = string.IsNullOrEmpty(issue.Field) ? null : issue.Field, code = issue.Code, message = issue.Message })),
            MachineTypeValidationException validation => PlanningHttpSupport.Error(422, "validation_failed", validation.Message, context,
                validation.Issues.Select(issue => (object)new { field = issue.Field, code = issue.Code, message = issue.Message })),
            MachineTypeNameConflictException => PlanningHttpSupport.Error(409, "machine_type_name_conflict", exception.Message, context),
            MachineTypeNotFoundException => NotFound(context),
            MachineTypeVersionConflictException => PlanningHttpSupport.Error(412, "resource_version_stale", exception.Message, context),
            MachineTypeInUseException => PlanningHttpSupport.Error(409, "machine_type_in_use", exception.Message, context),
            MachineTypeNameInUseException => PlanningHttpSupport.Error(409, "machine_type_name_in_use", exception.Message, context),
            MachineTypeCompatibilityException => PlanningHttpSupport.Error(409, "assigned_operation_incompatible", exception.Message, context),
            EditModeMutationException edit => PlanningHttpSupport.Error(409, edit.Code, edit.Message, context),
            _ => null
        };
        return result is not null;
    }

    private static IResult NotFound(HttpContext context) =>
        PlanningHttpSupport.Error(404, "resource_not_found", "The requested Machine Type was not found.", context);

    private static void SetTag(HttpResponse response, MachineType value) =>
        response.Headers.ETag = $"\"machine-type:{value.MachineTypeId}:v{value.Version}\"";
}
