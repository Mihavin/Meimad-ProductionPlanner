using Meimad.Planner.Server.Application.Cnc;
using Meimad.Planner.Server.Application.EditMode;

namespace Meimad.Planner.Server.Api.Cnc;

internal static class CncVerificationEndpoints
{
    internal static void MapCncVerificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/production-runs/{runId}/offset-loader-releases", ListReleasesAsync);
        endpoints.MapPost("/api/v1/production-runs/{runId}/offset-loader-releases", CreateReleaseAsync);
        endpoints.MapGet("/api/v1/machines/{machineId}/verification-configuration", GetSettingsAsync);
        endpoints.MapPut("/api/v1/machines/{machineId}/verification-configuration", UpdateSettingsAsync);
        endpoints.MapPost(
            "/api/v1/production-runs/{runId}/verification/invalidate",
            InvalidateVerificationAsync);
        endpoints.MapPost(
            "/api/v1/production-runs/{runId}/offset-loader/current/revoke",
            RevokeCurrentOffsetLoaderAsync);
    }

    private static async Task<IResult> ListReleasesAsync(
        string runId, CncVerificationFoundationService service, CancellationToken token)
    {
        try { return Results.Ok(new { items = await service.ListOffsetLoaderReleasesAsync(runId, token) }); }
        catch (Exception exception) when (Map(exception, null, out var result)) { return result!; }
    }

    private static async Task<IResult> CreateReleaseAsync(
        string runId, CreateOffsetLoaderReleaseRequest request, HttpContext context,
        CncVerificationFoundationService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        try
        {
            var value = await service.CreateOffsetLoaderReleaseAsync(runId,
                new(request.MachineId ?? string.Empty, request.NcReleaseId ?? string.Empty,
                    request.ToolTableReleaseId ?? string.Empty, request.ArtifactHash,
                    request.MetadataJson ?? "{}"), authority!, token);
            return Results.Created($"/api/v1/production-runs/{runId}/offset-loader-releases/{value.OffsetLoaderReleaseId}", value);
        }
        catch (Exception exception) when (Map(exception, context, out var result)) { return result!; }
    }

    private static async Task<IResult> GetSettingsAsync(
        string machineId, HttpContext context, CncVerificationFoundationService service,
        CancellationToken token)
    {
        try
        {
            var value = await service.GetSettingsAsync(machineId, token);
            if (value is null) return PlanningHttpSupport.Error(404, "verification_settings_not_found",
                "CNC verification settings are not configured for this Machine.", context);
            return Results.Ok(value);
        }
        catch (Exception exception) when (Map(exception, context, out var result)) { return result!; }
    }

    private static async Task<IResult> UpdateSettingsAsync(
        string machineId, UpdateCncVerificationSettingsRequest request, HttpContext context,
        CncVerificationFoundationService service, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        try
        {
            var value = await service.UpdateSettingsAsync(machineId, new(
                request.DprintTransport ?? string.Empty, request.DprintPort,
                request.ChallengeProgramNumber, request.VerifyProgramNumber,
                request.CustomGcodeAlias, request.NonceVariable, request.ResponseVariable,
                request.VerificationStateVariable, request.ReleaseTokenVariable,
                request.VerificationSecret, request.ExpectedMacroVersion,
                request.ResponseCodeDigits, request.VerificationTimeoutSeconds,
                request.Enabled), request.Version, authority!, token);
            return Results.Ok(value);
        }
        catch (Exception exception) when (Map(exception, context, out var result)) { return result!; }
    }

    private static async Task<IResult> InvalidateVerificationAsync(
        string runId,
        CncRecoveryRequest request,
        HttpContext context,
        CncVerificationFoundationService service,
        CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error))
            return error!;
        try
        {
            return Results.Ok(await service.InvalidateVerificationAsync(
                runId, request.MachineId ?? string.Empty, request.Reason ?? string.Empty,
                authority!, token));
        }
        catch (Exception exception) when (Map(exception, context, out var result))
        {
            return result!;
        }
    }

    private static async Task<IResult> RevokeCurrentOffsetLoaderAsync(
        string runId,
        CncRecoveryRequest request,
        HttpContext context,
        CncVerificationFoundationService service,
        CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error))
            return error!;
        try
        {
            return Results.Ok(await service.RevokeCurrentOffsetLoaderAsync(
                runId, request.MachineId ?? string.Empty, request.Reason ?? string.Empty,
                authority!, token));
        }
        catch (Exception exception) when (Map(exception, context, out var result))
        {
            return result!;
        }
    }

    private static bool Map(Exception exception, HttpContext? context, out IResult? result)
    {
        context ??= new DefaultHttpContext();
        result = exception switch
        {
            CncVerificationValidationException value => PlanningHttpSupport.Error(422,
                "validation_failed", value.Message, context,
                [new { field = value.Field, code = value.Code, message = value.Message }]),
            CncVerificationTargetException value => PlanningHttpSupport.Error(409,
                value.Code, value.Message, context),
            CncVerificationConcurrencyException => PlanningHttpSupport.Error(412,
                "resource_version_stale", exception.Message, context),
            EditModeMutationException value => PlanningHttpSupport.Error(409,
                value.Code, value.Message, context),
            _ => null
        };
        return result is not null;
    }
}

internal sealed record CreateOffsetLoaderReleaseRequest(
    string? MachineId, string? NcReleaseId, string? ToolTableReleaseId,
    string? ArtifactHash, string? MetadataJson);

internal sealed record UpdateCncVerificationSettingsRequest(
    string? DprintTransport, int DprintPort, int ChallengeProgramNumber,
    int VerifyProgramNumber, int? CustomGcodeAlias, int NonceVariable,
    int ResponseVariable, int VerificationStateVariable, int ReleaseTokenVariable,
    string? VerificationSecret, int ExpectedMacroVersion, int ResponseCodeDigits,
    int VerificationTimeoutSeconds, bool Enabled, int Version);

internal sealed record CncRecoveryRequest(string? MachineId, string? Reason);
