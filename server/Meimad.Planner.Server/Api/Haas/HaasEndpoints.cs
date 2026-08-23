using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Haas;
using Meimad.Planner.Server.Domain.Haas;

namespace Meimad.Planner.Server.Api.Haas;

internal static class HaasEndpoints
{
    internal static void MapHaasEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/machines/{machineId}/haas");
        group.MapGet("/connection", GetConnectionAsync);
        group.MapPut("/connection", UpdateConnectionAsync);
        group.MapPost("/test-mtconnect", TestMtConnectAsync);
        group.MapPost("/test-mdc", TestMdcAsync);
        group.MapPost("/test-net-share", TestNetShareAsync);
        group.MapGet("/production-variable", ReadVariableAsync);
        group.MapGet("/monitor", ReadMonitorAsync);
        group.MapPost("/tool-table-reset", ResetAfterToolTableAsync);
    }

    private static async Task<IResult> GetConnectionAsync(
        string machineId, HaasIntegrationService service, CancellationToken token)
    {
        var value = await service.GetSettingsAsync(machineId, token);
        return value is null ? Results.Ok(new HaasConnectionResponse(machineId, string.Empty,
            5051, 8082, 8080, false, null, null, 10605, 605, HaasPartCounterSources.Q500,
            2000, 3000, 2, 50, 32768, NcHeaderParser.DefaultPartPatterns, false, 0, null))
            : Results.Ok(Response(value));
    }

    private static async Task<IResult> UpdateConnectionAsync(
        string machineId, HaasConnectionUpdateRequest request, HttpContext context,
        HaasIntegrationService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var accessError))
            return accessError!;
        try
        {
            var value = await service.UpdateSettingsAsync(machineId, new HaasSettingsUpdate(
                request.Host, request.MdcPort, request.MtConnectPort,
                request.DprntPort is > 0 ? request.DprntPort : 8080,
                request.LocalNetShareEnabled, request.LocalNetSharePath, request.CredentialsReference,
                request.ProductionModeVariable, request.LegacyVariableAlias,
                request.PartCounterSource, request.PollingIntervalMs, request.ConnectionTimeoutMs,
             request.StableProgramPolls, request.HeaderLineLimit, request.HeaderByteLimit,
                request.HeaderPartPatterns, request.Enabled, request.Version,
                request.TelemetryProvider), authority!, token);
            return Results.Ok(Response(value));
        }
        catch (HaasValidationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status422UnprocessableEntity,
                "validation_failed", exception.Message, context, [new { field = exception.Field }]);
        }
        catch (HaasSettingsConcurrencyException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status412PreconditionFailed,
                "haas_connection_stale", exception.Message, context);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict,
                exception.Code, exception.Message, context);
        }
    }

    private static async Task<IResult> TestMdcAsync(
        string machineId, HaasIntegrationService service, CancellationToken token) =>
        TestResult(await service.TestMdcAsync(machineId, token));

    private static async Task<IResult> TestMtConnectAsync(
        string machineId, HaasIntegrationService service, CancellationToken token) =>
        TestResult(await service.TestMtConnectAsync(machineId, token));

    private static async Task<IResult> TestNetShareAsync(
        string machineId, HaasIntegrationService service, CancellationToken token) =>
        TestResult(await service.TestNetShareAsync(machineId, token));

    private static async Task<IResult> ReadVariableAsync(
        string machineId, HttpContext context, HaasIntegrationService service, CancellationToken token)
    {
        try { return Results.Ok(await service.ReadProductionVariableAsync(machineId, token)); }
        catch (HaasSettingsNotFoundException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status404NotFound,
                "haas_settings_not_found", exception.Message, context);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status502BadGateway,
                "haas_read_failed", exception.Message, context);
        }
    }

    private static async Task<IResult> ReadMonitorAsync(
        string machineId, HttpContext context, HaasIntegrationService service, CancellationToken token)
    {
        var value = await service.ReadMonitorAsync(machineId, token);
        return value is null
            ? PlanningHttpSupport.Error(StatusCodes.Status404NotFound,
                "haas_settings_not_found", "Haas NGC is not configured for this Machine.", context)
            : Results.Ok(value);
    }

    private static async Task<IResult> ResetAfterToolTableAsync(
        string machineId, HaasToolTableResetRequest request, HttpContext context,
        EditModeService editMode, HaasIntegrationService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var accessError))
            return accessError!;
        var snapshot = await editMode.GetStatusAsync(authority!.ClientId, token);
        if (snapshot.CallerState != EditClientState.Editor || snapshot.Generation != authority.Generation)
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict,
                "edit_generation_stale", "The active edit authority is no longer valid.", context);
        if (!request.ToolTableTransferConfirmed)
            return PlanningHttpSupport.Error(StatusCodes.Status422UnprocessableEntity,
                "tool_table_transfer_not_confirmed",
                "The production variable can be reset only after Tool Table transfer succeeds.", context);
        var result = await service.ResetProductionVariableAfterToolTableAsync(machineId,
            request.ToolTableId ?? string.Empty, snapshot.Holder?.UserId ?? authority.ClientId, token);
        return result.Succeeded ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status502BadGateway);
    }

    private static IResult TestResult(HaasConnectionTest value) => value.Succeeded
        ? Results.Ok(value) : Results.Json(value, statusCode: StatusCodes.Status502BadGateway);

    private static HaasConnectionResponse Response(HaasConnectionSettings value) => new(
        value.MachineId, value.Host, value.MdcPort, value.MtConnectPort, value.DprntPort,
        value.LocalNetShareEnabled, value.LocalNetSharePath, value.CredentialsReference,
        value.ProductionModeVariable, value.LegacyVariableAlias, value.PartCounterSource,
        value.PollingIntervalMs, value.ConnectionTimeoutMs, value.StableProgramPolls,
        value.HeaderLineLimit, value.HeaderByteLimit, value.HeaderPartPatterns,
        value.Enabled, value.Version, value.UpdatedAt, value.TelemetryProvider);
}

internal sealed record HaasConnectionUpdateRequest(
    string? Host, int MdcPort, int MtConnectPort, int DprntPort, bool LocalNetShareEnabled,
    string? LocalNetSharePath, string? CredentialsReference,
    int ProductionModeVariable, int LegacyVariableAlias, string? PartCounterSource,
    int PollingIntervalMs, int ConnectionTimeoutMs, int StableProgramPolls,
    int HeaderLineLimit, int HeaderByteLimit, IReadOnlyList<string>? HeaderPartPatterns,
    bool Enabled, int Version, string? TelemetryProvider);

internal sealed record HaasConnectionResponse(
    string MachineId, string Host, int MdcPort, int MtConnectPort, int DprntPort,
    bool LocalNetShareEnabled, string? LocalNetSharePath, string? CredentialsReference,
    int ProductionModeVariable, int LegacyVariableAlias, string PartCounterSource,
    int PollingIntervalMs, int ConnectionTimeoutMs, int StableProgramPolls,
    int HeaderLineLimit, int HeaderByteLimit, IReadOnlyList<string> HeaderPartPatterns,
    bool Enabled, int Version, DateTimeOffset? UpdatedAt, string TelemetryProvider = HaasTelemetryProviders.Mdc);

internal sealed record HaasToolTableResetRequest(
    string? ToolTableId,
    bool ToolTableTransferConfirmed);
