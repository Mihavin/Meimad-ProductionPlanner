using Meimad.Planner.Server.Backup;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Maintenance;
using Meimad.Planner.Server.Application.Accounts;

namespace Meimad.Planner.Server.Api.Maintenance;

internal static class ServerMaintenanceEndpoints
{
    internal static void MapServerMaintenanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/server-maintenance");
        group.MapGet("/database", GetDatabaseAsync);
        group.MapPost("/collected-data/preview", PreviewAsync);
        group.MapPost("/collected-data/purge", PurgeAsync);
        group.MapPost("/backups/download", DownloadBackupAsync);
        group.MapGet("/recovery-sets", async (RecoverySetService service, CancellationToken token) => Results.Ok(await service.StatusAsync(token)));
        group.MapPost("/recovery-sets", async (HttpContext context, RecoverySetService service, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, Permissions.ManageSetup, out var user, out var error)) return error!;
            try { return Results.Ok(await service.CreateAsync(user!.UserName, token)); }
            catch (InvalidOperationException exception) { return PlanningHttpSupport.Error(409, "recovery_not_configured", exception.Message, context); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            { return PlanningHttpSupport.Error(409, "recovery_set_failed", "Recovery set could not be completed. Check storage access, the recovery password file, keys and referenced artifacts.", context); }
        });
    }

    private static async Task<IResult> GetDatabaseAsync(
        HttpContext context,
        ServerMaintenanceService service,
        CancellationToken token)
    {
        if (!PlanningHttpSupport.TryAuthorizeIdentity(context, Permissions.ManageSetup, out _, out _, out var error))
        {
            return error!;
        }
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(ServerMaintenanceCatalogResponse.FromStatus(
            await service.ReadStatusAsync(token)));
    }

    private static async Task<IResult> PreviewAsync(
        CollectedDataPreviewRequest request,
        HttpContext context,
        ServerMaintenanceService service,
        CancellationToken token)
    {
        if (!PlanningHttpSupport.TryAuthorizeIdentity(context, Permissions.ManageSetup, out _, out _, out var error))
        {
            return error!;
        }
        try
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.PreviewAsync(
                request.FromInclusive,
                request.ToExclusive,
                request.Types,
                request.MachineId,
                token));
        }
        catch (ServerMaintenanceValidationException exception)
        {
            return PlanningHttpSupport.Error(400, exception.Code, exception.Message, context);
        }
    }

    private static async Task<IResult> PurgeAsync(
        CollectedDataPurgeRequest request,
        HttpContext context,
        ServerMaintenanceService service,
        CancellationToken token)
    {
        if (!TryReadMutationIdentity(context, out var authority, out var userId, out var error))
        {
            return error!;
        }
        try
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.PurgeAsync(
                request.FromInclusive,
                request.ToExclusive,
                request.Types,
                request.MachineId,
                request.ExpectedTotalRows,
                request.Reason,
                userId!,
                authority!,
                token));
        }
        catch (ServerMaintenanceValidationException exception)
        {
            return PlanningHttpSupport.Error(400, exception.Code, exception.Message, context);
        }
        catch (CollectedDataPreviewChangedException exception)
        {
            return PlanningHttpSupport.Error(
                409,
                "collected_data_preview_changed",
                exception.Message,
                context,
                [new { expectedRows = exception.ExpectedRows, actualRows = exception.ActualRows }]);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(409, exception.Code, exception.Message, context);
        }
    }

    private static async Task<IResult> DownloadBackupAsync(
        HttpContext context,
        ServerMaintenanceService service,
        CancellationToken token)
    {
        if (!TryReadMutationIdentity(context, out var authority, out var userId, out var error))
        {
            return error!;
        }
        try
        {
            var artifact = await service.CreateHttpBackupAsync(userId!, authority!, token);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Meimad-Checksum-SHA256"] = artifact.Backup.Sha256;
            context.Response.Headers["X-Meimad-Backup-Created-At"] = artifact.Backup.CreatedAt.ToString("O");
            context.Response.Headers["X-Meimad-Integrity-Verified"] = artifact.Backup.IntegrityVerified ? "true" : "false";
            context.Response.Headers["X-Meimad-Restore-Verified"] = artifact.Backup.RestoreVerified ? "true" : "false";
            return Results.Stream(
                artifact.Content,
                "application/vnd.sqlite3",
                artifact.Backup.FileName,
                enableRangeProcessing: false);
        }
        catch (ServerMaintenanceValidationException exception)
        {
            return PlanningHttpSupport.Error(400, exception.Code, exception.Message, context);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(409, exception.Code, exception.Message, context);
        }
    }

    private static bool TryReadMutationIdentity(
        HttpContext context,
        out EditAuthority? authority,
        out string? userId,
        out IResult? error)
    {
        authority = null;
        userId = null;
        if (!PlanningHttpSupport.TryAuthorizeEdit(context, Permissions.ManageSetup, out authority, out error))
        {
            return false;
        }
        return PlanningHttpSupport.TryAuthorizeIdentity(context, Permissions.ManageSetup, out _, out userId, out error);
    }
}
