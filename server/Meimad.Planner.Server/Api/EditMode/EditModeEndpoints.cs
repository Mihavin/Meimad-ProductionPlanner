using Meimad.Planner.Server.Application.EditMode;

namespace Meimad.Planner.Server.Api.EditMode;

internal static class EditModeEndpoints
{
    internal static void MapEditModeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var editMode = endpoints.MapGroup("/api/v1/edit-mode");
        editMode.MapGet(string.Empty, GetStatusAsync);
        editMode.MapPost("/requests", RequestEditAsync);
        editMode.MapGet("/requests/{requestId}", GetRequestAsync);
        editMode.MapPost("/requests/{requestId}/decision", DecideAsync);
        editMode.MapPost("/release", ReleaseAsync);
    }

    private static async Task<IResult> GetStatusAsync(
        HttpContext context,
        EditModeService service,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadClientId(context, out var clientId, out var error))
        {
            return error!;
        }

        try
        {
            var snapshot = await service.GetStatusAsync(clientId!, cancellationToken);
            return Results.Ok(EditModeResponse.FromDomain(snapshot));
        }
        catch (Exception exception) when (TryMapError(exception, context, out error))
        {
            return error!;
        }
    }

    private static async Task<IResult> RequestEditAsync(
        HttpContext context,
        EditModeService service,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadClientIdentity(
                context,
                out var clientId,
                out var userId,
                out var error))
        {
            return error!;
        }

        try
        {
            var snapshot = await service.RequestEditAsync(clientId!, userId!, cancellationToken);
            var response = EditModeResponse.FromDomain(snapshot);
            return snapshot.CallerState == EditClientState.RequestingEdit
                ? Results.Accepted(
                    $"/api/v1/edit-mode/requests/{snapshot.PendingRequest!.RequestId}",
                    response)
                : Results.Created("/api/v1/edit-mode", response);
        }
        catch (Exception exception) when (TryMapError(exception, context, out error))
        {
            return error!;
        }
    }

    private static async Task<IResult> GetRequestAsync(
        string requestId,
        HttpContext context,
        EditModeService service,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadClientId(context, out var clientId, out var error))
        {
            return error!;
        }

        try
        {
            var request = await service.GetRequestAsync(requestId, clientId!, cancellationToken);
            return request is null
                ? PlanningHttpSupport.Error(
                    StatusCodes.Status404NotFound,
                    "edit_request_not_found",
                    "The Edit Mode request was not found.",
                    context)
                : Results.Ok(EditTransferRequestResponse.FromDomain(request));
        }
        catch (Exception exception) when (TryMapError(exception, context, out error))
        {
            return error!;
        }
    }

    private static async Task<IResult> DecideAsync(
        string requestId,
        EditDecisionRequest request,
        HttpContext context,
        EditModeService service,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error))
        {
            return error!;
        }

        try
        {
            var snapshot = await service.DecideAsync(
                requestId,
                authority!,
                request.ToDomain(),
                cancellationToken);
            return Results.Ok(EditModeResponse.FromDomain(snapshot));
        }
        catch (Exception exception) when (TryMapError(exception, context, out error))
        {
            return error!;
        }
    }

    private static async Task<IResult> ReleaseAsync(
        HttpContext context,
        EditModeService service,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error))
        {
            return error!;
        }

        try
        {
            var snapshot = await service.ReleaseAsync(authority!, cancellationToken);
            return Results.Ok(EditModeResponse.FromDomain(snapshot));
        }
        catch (Exception exception) when (TryMapError(exception, context, out error))
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
            EditModeMutationException mutation => PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict,
                mutation.Code,
                mutation.Message,
                context),
            EditModeCommandException command => PlanningHttpSupport.Error(
                StatusFor(command.Code),
                command.Code,
                command.Message,
                context),
            _ => null
        };
        return result is not null;
    }

    private static int StatusFor(string code) => code switch
    {
        "edit_request_not_found" => StatusCodes.Status404NotFound,
        "edit_request_forbidden" => StatusCodes.Status403Forbidden,
        "invalid_edit_mode_request" => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status409Conflict
    };
}
