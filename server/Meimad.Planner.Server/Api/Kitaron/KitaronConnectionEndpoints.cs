using System.Net;
using Meimad.Planner.Server.Application.Kitaron;

namespace Meimad.Planner.Server.Api.Kitaron;

internal static class KitaronConnectionEndpoints
{
    internal static void MapKitaronConnectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/kitaron/connection", GetAsync);
        endpoints.MapPut("/api/v1/kitaron/connection", UpdateAsync);
        endpoints.MapPost("/api/v1/kitaron/connection/test", TestAsync);
        endpoints.MapGet("/api/v1/kitaron/mapping", GetMappingAsync);
        endpoints.MapPut("/api/v1/kitaron/mapping", UpdateMappingAsync);
        endpoints.MapGet("/api/v1/kitaron/sync", GetSyncAsync);
        endpoints.MapPost("/api/v1/kitaron/sync", RunSyncAsync);
    }

    private static async Task<IResult> GetSyncAsync(
        HttpContext context,
        KitaronSyncService service,
        CancellationToken cancellationToken)
    {
        if (!IsLocalRequest(context))
        {
            return PlanningHttpSupport.Error(StatusCodes.Status403Forbidden,
                "kitaron_setup_local_only", "Kitaron synchronization can be managed only from the Server computer.", context);
        }
        return Results.Ok(await service.GetStatusAsync(cancellationToken));
    }

    private static async Task<IResult> RunSyncAsync(
        HttpContext context,
        KitaronSyncService service,
        CancellationToken cancellationToken)
    {
        if (!IsLocalRequest(context))
        {
            return PlanningHttpSupport.Error(StatusCodes.Status403Forbidden,
                "kitaron_setup_local_only", "Kitaron synchronization can be managed only from the Server computer.", context);
        }
        try { return Results.Ok(await service.RunAsync(cancellationToken)); }
        catch (KitaronSyncBlockedException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict,
                "kitaron_sync_blocked", exception.Message, context);
        }
    }

    internal static bool IsLocalRequest(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        return address is null
            || IPAddress.IsLoopback(address)
            || (address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4()));
    }

    private static async Task<IResult> GetAsync(
        HttpContext context,
        KitaronConnectionService service,
        CancellationToken cancellationToken)
    {
        if (!IsLocalRequest(context))
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status403Forbidden,
                "kitaron_setup_local_only",
                "Kitaron connection settings can be managed only from the Server computer.",
                context);
        }
        return Results.Ok(Response(await service.GetAsync(cancellationToken)));
    }

    private static async Task<IResult> UpdateAsync(
        KitaronConnectionUpdateRequest request,
        HttpContext context,
        KitaronConnectionService service,
        CancellationToken cancellationToken)
    {
        if (!IsLocalRequest(context))
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status403Forbidden,
                "kitaron_setup_local_only",
                "Kitaron connection settings can be managed only from the Server computer.",
                context);
        }
        try
        {
            var value = await service.UpdateAsync(new KitaronConnectionUpdate(
                request.ServerHost,
                request.ServerPort,
                request.DatabaseName,
                request.ViewSchema,
                request.ViewName,
                request.Username,
                request.Password,
                request.ClearPassword,
                request.Enabled,
                request.RefreshIntervalSeconds,
                request.Version), cancellationToken);
            return Results.Ok(Response(value));
        }
        catch (KitaronConnectionValidationException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status422UnprocessableEntity,
                "validation_failed",
                exception.Message,
                context,
                [new { field = exception.Field }]);
        }
        catch (KitaronConnectionConcurrencyException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status412PreconditionFailed,
                "kitaron_connection_stale",
                exception.Message,
                context);
        }
    }

    private static async Task<IResult> TestAsync(
        HttpContext context,
        KitaronConnectionService service,
        CancellationToken cancellationToken)
    {
        if (!IsLocalRequest(context))
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status403Forbidden,
                "kitaron_setup_local_only",
                "Kitaron connection settings can be managed only from the Server computer.",
                context);
        }
        var result = await service.TestAsync(cancellationToken);
        var response = new KitaronConnectionTestResponse(
            result.Succeeded,
            result.Message,
            result.Columns.Select(column => new KitaronSourceColumnResponse(
                column.Name, column.DataType)).ToArray(),
            Response(result.Settings));
        return result.Succeeded
            ? Results.Ok(response)
            : Results.Json(response, statusCode: StatusCodes.Status502BadGateway);
    }

    private static KitaronConnectionResponse Response(KitaronConnectionSettings value) => new(
        value.ServerHost,
        value.ServerPort,
        value.DatabaseName,
        value.ViewSchema,
        value.ViewName,
        value.Username,
        value.PasswordConfigured,
        value.Enabled,
        value.RefreshIntervalSeconds,
        value.LastTestStatus,
        value.LastTestAt,
        value.LastTestMessage,
        value.LastTestColumnCount,
        value.Version,
        value.UpdatedAt);

    private static async Task<IResult> GetMappingAsync(
        HttpContext context,
        KitaronMappingService service,
        CancellationToken cancellationToken)
    {
        if (!IsLocalRequest(context))
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status403Forbidden,
                "kitaron_setup_local_only",
                "Kitaron mapping settings can be managed only from the Server computer.",
                context);
        }
        return Results.Ok(await service.GetAsync(cancellationToken));
    }

    private static async Task<IResult> UpdateMappingAsync(
        KitaronMappingUpdateRequest request,
        HttpContext context,
        KitaronMappingService service,
        CancellationToken cancellationToken)
    {
        if (!IsLocalRequest(context))
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status403Forbidden,
                "kitaron_setup_local_only",
                "Kitaron mapping settings can be managed only from the Server computer.",
                context);
        }
        try
        {
            return Results.Ok(await service.UpdateAsync(
                new KitaronMappingUpdate(
                    request.ModelMode,
                    request.Status,
                    request.Fields?.Select(field => new KitaronMappingFieldUpdate(
                        field.TargetEntity,
                        field.TargetField,
                        field.Enabled,
                        field.SourceColumn,
                        field.Confidence,
                        field.Transform,
                        field.Notes)).ToArray(),
                    request.Notes,
                    request.Version),
                cancellationToken));
        }
        catch (KitaronMappingValidationException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status422UnprocessableEntity,
                "validation_failed",
                exception.Message,
                context,
                [new { field = exception.Field }]);
        }
        catch (KitaronMappingConcurrencyException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status412PreconditionFailed,
                "kitaron_mapping_stale",
                exception.Message,
                context);
        }
    }
}

internal sealed record KitaronConnectionUpdateRequest(
    string? ServerHost,
    int ServerPort,
    string? DatabaseName,
    string? ViewSchema,
    string? ViewName,
    string? Username,
    string? Password,
    bool ClearPassword,
    bool Enabled,
    int RefreshIntervalSeconds,
    int Version);

internal sealed record KitaronConnectionResponse(
    string ServerHost,
    int ServerPort,
    string DatabaseName,
    string ViewSchema,
    string ViewName,
    string Username,
    bool PasswordConfigured,
    bool Enabled,
    int RefreshIntervalSeconds,
    string LastTestStatus,
    DateTimeOffset? LastTestAt,
    string? LastTestMessage,
    int? LastTestColumnCount,
    int Version,
    DateTimeOffset UpdatedAt);

internal sealed record KitaronConnectionTestResponse(
    bool Succeeded,
    string Message,
    IReadOnlyList<KitaronSourceColumnResponse> Columns,
    KitaronConnectionResponse Settings);

internal sealed record KitaronSourceColumnResponse(string Name, string DataType);

internal sealed record KitaronMappingUpdateRequest(
    string? ModelMode,
    string? Status,
    IReadOnlyList<KitaronMappingFieldUpdateRequest>? Fields,
    string? Notes,
    int Version);

internal sealed record KitaronMappingFieldUpdateRequest(
    string? TargetEntity,
    string? TargetField,
    bool Enabled,
    string? SourceColumn,
    string? Confidence,
    string? Transform,
    string? Notes);
