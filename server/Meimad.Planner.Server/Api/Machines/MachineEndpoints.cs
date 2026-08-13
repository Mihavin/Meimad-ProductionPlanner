using Meimad.Planner.Server.Api.MachineAssignments;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.MachineAssignments;
using Meimad.Planner.Server.Application.Machines;
using Meimad.Planner.Server.Domain.Machines;
using Microsoft.Extensions.Primitives;

namespace Meimad.Planner.Server.Api.Machines;

internal static class MachineEndpoints
{
    internal static void MapMachineEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var machines = endpoints.MapGroup("/api/v1/machines");
        machines.MapPost(string.Empty, CreateAsync);
        machines.MapGet(string.Empty, ListAsync);
        machines.MapGet("/{machineId}", GetByIdAsync);
        machines.MapGet("/{machineId}/picture", GetPictureAsync);
        machines.MapPatch("/{machineId}", UpdateAsync);
        machines.MapGet("/{machineId}/backlog", GetBacklogAsync);
    }

    private static async Task<IResult> CreateAsync(
        CreateMachineRequest request,
        HttpContext httpContext,
        MachineService service,
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
            var created = await service.CreateAsync(request.ToCommand(), authority!, cancellationToken);
            SetEntityTag(httpContext.Response, created);
            return Results.Created(
                $"/api/v1/machines/{created.MachineId}",
                MachineResponse.FromDomain(created));
        }
        catch (Exception exception) when (TryMapDomainError(exception, httpContext, out var error))
        {
            return error!;
        }
    }

    private static async Task<IResult> ListAsync(
        MachineService service,
        CancellationToken cancellationToken)
    {
        var machines = await service.ListAsync(cancellationToken);
        return Results.Ok(new MachineListResponse(
            machines.Select(MachineResponse.FromDomain).ToArray(),
            null));
    }

    private static async Task<IResult> GetByIdAsync(
        string machineId,
        HttpContext httpContext,
        MachineService service,
        CancellationToken cancellationToken)
    {
        var machine = await service.GetByIdAsync(machineId, cancellationToken);
        if (machine is null)
        {
            return NotFound(httpContext);
        }

        SetEntityTag(httpContext.Response, machine);
        return Results.Ok(MachineResponse.FromDomain(machine));
    }

    private static async Task<IResult> UpdateAsync(
        string machineId,
        PatchMachineRequest request,
        HttpContext httpContext,
        MachineService service,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(
                httpContext,
                out var authority,
                out var accessError))
        {
            return accessError!;
        }

        if (!PlanningHttpSupport.TryReadExpectedVersion(
                httpContext.Request.Headers.IfMatch,
                "machine",
                machineId,
                out var expectedVersion))
        {
            var missing = StringValues.IsNullOrEmpty(httpContext.Request.Headers.IfMatch);
            return PlanningHttpSupport.Error(
                missing ? StatusCodes.Status428PreconditionRequired : StatusCodes.Status412PreconditionFailed,
                missing ? "precondition_required" : "resource_version_stale",
                "A matching Machine If-Match header is required.",
                httpContext);
        }

        try
        {
            var updated = await service.UpdateAsync(
                machineId,
                expectedVersion,
                request.ToCommand(),
                authority!,
                cancellationToken);
            SetEntityTag(httpContext.Response, updated);
            return Results.Ok(MachineResponse.FromDomain(updated));
        }
        catch (MachineRequestException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status400BadRequest,
                "invalid_request",
                exception.Message,
                httpContext,
                exception.Issues.Select(issue => (object)new
                {
                    field = string.IsNullOrEmpty(issue.Field) ? null : issue.Field,
                    code = issue.Code,
                    message = issue.Message
                }));
        }
        catch (Exception exception) when (TryMapDomainError(exception, httpContext, out var error))
        {
            return error!;
        }
    }

    private static async Task<IResult> GetPictureAsync(
        string machineId,
        HttpContext httpContext,
        MachineService service,
        CancellationToken cancellationToken)
    {
        var machine = await service.GetByIdAsync(machineId, cancellationToken);
        if (machine?.PicturePath is null || !File.Exists(machine.PicturePath))
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status404NotFound,
                "picture_not_found",
                "No picture is available for the requested Machine.",
                httpContext);
        }

        var contentType = Path.GetExtension(machine.PicturePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            _ => null
        };
        if (contentType is null)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status415UnsupportedMediaType,
                "picture_format_unsupported",
                "The Machine picture format is not supported.",
                httpContext);
        }

        return Results.File(machine.PicturePath, contentType, enableRangeProcessing: true);
    }

    private static async Task<IResult> GetBacklogAsync(
        string machineId,
        HttpContext httpContext,
        MachineService machineService,
        MachineAssignmentService assignmentService,
        CancellationToken cancellationToken)
    {
        if (await machineService.GetByIdAsync(machineId, cancellationToken) is null)
        {
            return NotFound(httpContext);
        }

        var items = await assignmentService.GetBacklogAsync(machineId, cancellationToken);
        return Results.Ok(new MachineBacklogResponse(
            machineId,
            items.Select(MachineBacklogItemResponse.FromDomain).ToArray()));
    }

    private static bool TryMapDomainError(
        Exception exception,
        HttpContext httpContext,
        out IResult? result)
    {
        result = exception switch
        {
            MachineValidationException validation => PlanningHttpSupport.Error(
                StatusCodes.Status422UnprocessableEntity,
                "validation_failed",
                validation.Message,
                httpContext,
                validation.Issues.Select(issue => (object)new
                {
                    field = issue.Field,
                    code = issue.Code,
                    message = issue.Message
                })),
            WorkingCalendarNotFoundException => PlanningHttpSupport.Error(
                StatusCodes.Status422UnprocessableEntity,
                "invalid_working_calendar",
                exception.Message,
                httpContext),
            WorkingCalendarUsageException => PlanningHttpSupport.Error(
                StatusCodes.Status422UnprocessableEntity,
                "invalid_working_calendar_usage",
                exception.Message,
                httpContext),
            MachineTypeReferenceNotFoundException => PlanningHttpSupport.Error(
                StatusCodes.Status422UnprocessableEntity,
                "invalid_machine_type",
                exception.Message,
                httpContext),
            MachineNumberConflictException => PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict,
                "machine_number_conflict",
                exception.Message,
                httpContext),
            MachineNotFoundException => NotFound(httpContext),
            MachineVersionConflictException => PlanningHttpSupport.Error(
                StatusCodes.Status412PreconditionFailed,
                "resource_version_stale",
                exception.Message,
                httpContext),
            MachineBacklogCompatibilityException => PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict,
                "assigned_operation_incompatible",
                exception.Message,
                httpContext),
            EditModeMutationException edit => PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict,
                edit.Code,
                edit.Message,
                httpContext),
            _ => null
        };
        return result is not null;
    }

    private static IResult NotFound(HttpContext context) => PlanningHttpSupport.Error(
        StatusCodes.Status404NotFound,
        "resource_not_found",
        "The requested Machine was not found.",
        context);

    private static void SetEntityTag(HttpResponse response, Machine machine)
    {
        response.Headers.ETag = $"\"machine:{machine.MachineId}:v{machine.Version}\"";
    }
}
