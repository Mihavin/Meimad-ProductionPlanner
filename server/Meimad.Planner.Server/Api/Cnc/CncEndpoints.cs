using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.Cnc;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Cnc;

namespace Meimad.Planner.Server.Api.Cnc;

internal static class CncEndpoints
{
    internal static void MapCncEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/cnc-adapters", (CncConnectionService service) =>
            Results.Ok(service.ListAdapterDefinitions()));
        var group = endpoints.MapGroup("/api/v1/machines/{machineId}");
        group.MapGet("/cnc-connection", GetConnectionAsync);
        group.MapPut("/cnc-connection", UpdateConnectionAsync);
        group.MapPost("/cnc-connection/test", TestConnectionAsync);
        group.MapPost("/cnc-connection/reconnect", ReconnectAsync);
        group.MapGet("/snapshot", GetSnapshotAsync);
        group.MapGet("/cnc-diagnostics", GetDiagnosticsAsync);
        endpoints.Map("/api/v1/machines/live", HandleWebSocketAsync);
        endpoints.Map("/machines/live", HandleWebSocketAsync);
    }

    private static async Task<IResult> GetConnectionAsync(
        string machineId, CncConnectionService service, CancellationToken token)
    {
        var value = await service.GetConnectionAsync(machineId, token);
        return value is null ? Results.NotFound() : Results.Ok(Public(value));
    }

    private static async Task<IResult> UpdateConnectionAsync(
        string machineId, CncConnectionUpdateRequest request, HttpContext context,
        CncConnectionService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var accessError))
            return accessError!;
        try
        {
            var value = await service.UpdateConnectionAsync(machineId, new(
                request.AdapterType ?? string.Empty, request.Enabled, request.PollingIntervalMs,
                request.ConnectionTimeoutMs, request.MaximumReconnectBackoffMs,
                request.AllowRead, request.AllowWrite, request.RawTelemetryRetentionDays,
                request.Configuration, request.UsernameSecretId, request.PasswordSecretId,
                request.Version), authority!, token);
            return Results.Ok(Public(value));
        }
        catch (CncValidationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status422UnprocessableEntity,
                "validation_failed", exception.Message, context, [new { field = exception.Field }]);
        }
        catch (CncConnectionConcurrencyException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status412PreconditionFailed,
                "cnc_connection_stale", exception.Message, context);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict,
                exception.Code, exception.Message, context);
        }
    }

    private static async Task<IResult> TestConnectionAsync(
        string machineId, HttpContext context, CncConnectionService service, CancellationToken token)
    {
        try
        {
            var value = await service.TestConnectionAsync(machineId, token);
            return value.OverallSuccess ? Results.Ok(value)
                : Results.Json(value, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (CncConnectionNotFoundException exception)
        { return PlanningHttpSupport.Error(404, "cnc_connection_not_found", exception.Message, context); }
        catch (CncAdapterUnsupportedException exception)
        { return PlanningHttpSupport.Error(422, "cnc_adapter_unsupported", exception.Message, context); }
    }

    private static async Task<IResult> ReconnectAsync(
        string machineId, HttpContext context, CncConnectionService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out _, out var accessError)) return accessError!;
        try
        {
            await service.ReconnectAsync(machineId, token);
            return Results.Accepted(value: new { machineId, status = CncConnectionStates.Connecting });
        }
        catch (CncConnectionNotFoundException exception)
        { return PlanningHttpSupport.Error(404, "cnc_connection_not_found", exception.Message, context); }
        catch (CncValidationException exception)
        { return PlanningHttpSupport.Error(422, "validation_failed", exception.Message, context); }
    }

    private static async Task<IResult> GetSnapshotAsync(
        string machineId, CncConnectionService service, CancellationToken token)
    {
        var value = await service.GetSnapshotAsync(machineId, token);
        return value is null ? Results.NotFound() : Results.Ok(value);
    }

    private static async Task<IResult> GetDiagnosticsAsync(
        string machineId, int? limit, CncConnectionService service, CancellationToken token) =>
        Results.Ok(await service.GetDiagnosticsAsync(machineId, limit ?? 50, token));

    private static async Task HandleWebSocketAsync(
        HttpContext context, ICncConnectionManager manager, ICncLivePublisher publisher)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var request = await ReceiveSubscriptionAsync(socket, context.RequestAborted);
        if (request is null || request.MachineIds.Count is < 1 or > 100)
        {
            await socket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData,
                "Send a subscribe message with 1-100 machineIds.", context.RequestAborted);
            return;
        }
        var ids = request.MachineIds.Where(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 200)
            .ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return;
        await using var subscription = publisher.Subscribe(ids);
        foreach (var id in ids)
        {
            var snapshot = await manager.GetCurrentSnapshotAsync(id, context.RequestAborted);
            if (snapshot is not null)
                await SendAsync(socket, new CncLiveMessage("MachineSnapshotUpdated", id,
                    snapshot.Timestamp, snapshot), context.RequestAborted);
        }
        await foreach (var message in subscription.Reader.ReadAllAsync(context.RequestAborted))
        {
            if (socket.State != WebSocketState.Open) break;
            await SendAsync(socket, message, context.RequestAborted);
        }
    }

    private static async Task<CncWebSocketSubscription?> ReceiveSubscriptionAsync(
        WebSocket socket, CancellationToken token)
    {
        var bytes = new byte[8192];
        var result = await socket.ReceiveAsync(bytes, token);
        if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage) return null;
        try
        {
            var value = JsonSerializer.Deserialize<CncWebSocketSubscription>(
                bytes.AsSpan(0, result.Count), CncJson.Options);
            return value?.Type.Equals("subscribe", StringComparison.OrdinalIgnoreCase) == true ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static Task SendAsync(WebSocket socket, CncLiveMessage message, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, CncJson.Options));
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
    }

    private static CncConnectionResponse Public(MachineConnection value)
    {
        object configuration = value.AdapterType == CncAdapterType.HaasNgc
            ? PublicHaas(value.ConfigurationJson)
            : new { };
        return new(value.Id, value.MachineId, CncAdapterTypes.Serialize(value.AdapterType), value.Enabled,
            value.ConnectionStatus, value.LastConnectionAttemptAt, value.LastConnectedAt,
            value.LastDisconnectedAt, value.LastSuccessfulPollAt, value.PollingIntervalMs,
            value.ConnectionTimeoutMs, value.MaximumReconnectBackoffMs, value.AllowRead,
            value.AllowWrite, value.RawTelemetryRetentionDays, configuration,
            value.UsernameSecretId is not null, value.PasswordSecretId is not null,
            value.Version, value.UpdatedAt);
    }

    private static object PublicHaas(string json)
    {
        var value = JsonSerializer.Deserialize<HaasNgcConnectionConfiguration>(json, CncJson.Options)!;
        return new
        {
            value.Host,
            value.MacAddress,
            value.Mdc,
            value.MtConnect,
            value.TelemetryProvider,
            programAccess = new
            {
                value.ProgramAccess.Provider,
                value.ProgramAccess.Enabled,
                value.ProgramAccess.SharePath,
                value.ProgramAccess.HeaderLineLimit,
                value.ProgramAccess.HeaderByteLimit,
                value.ProgramAccess.HeaderPartPatterns,
                usernameSecretConfigured = value.ProgramAccess.UsernameSecretId is not null,
                passwordSecretConfigured = value.ProgramAccess.PasswordSecretId is not null
            },
            value.Production,
            value.Monitoring
        };
    }
}

internal sealed record CncConnectionUpdateRequest(
    string? AdapterType,
    bool Enabled,
    int PollingIntervalMs,
    int ConnectionTimeoutMs,
    int MaximumReconnectBackoffMs,
    bool AllowRead,
    bool AllowWrite,
    int RawTelemetryRetentionDays,
    JsonElement Configuration,
    string? UsernameSecretId,
    string? PasswordSecretId,
    int Version);

internal sealed record CncConnectionResponse(
    string Id,
    string MachineId,
    string AdapterType,
    bool Enabled,
    string ConnectionStatus,
    DateTimeOffset? LastConnectionAttemptAt,
    DateTimeOffset? LastConnectedAt,
    DateTimeOffset? LastDisconnectedAt,
    DateTimeOffset? LastSuccessfulPollAt,
    int PollingIntervalMs,
    int ConnectionTimeoutMs,
    int MaximumReconnectBackoffMs,
    bool AllowRead,
    bool AllowWrite,
    int RawTelemetryRetentionDays,
    object Configuration,
    bool UsernameSecretConfigured,
    bool PasswordSecretConfigured,
    int Version,
    DateTimeOffset UpdatedAt);

internal sealed record CncWebSocketSubscription(string Type, IReadOnlyList<string> MachineIds);
