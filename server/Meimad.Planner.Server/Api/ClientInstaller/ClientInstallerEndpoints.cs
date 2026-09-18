using Meimad.Planner.Server.Application.ClientInstaller;

namespace Meimad.Planner.Server.Api.ClientInstaller;

/// <summary>
/// Read-only distribution of the Windows client installer bundled with the Server. A client
/// compares its own version with <c>clientVersion</c> at start and installs the download when
/// the Server's client package is newer.
/// </summary>
internal static class ClientInstallerEndpoints
{
    internal static void MapClientInstallerEndpoints(this WebApplication application)
    {
        application.MapGet("/api/v1/client-installer", (ClientInstallerService service) =>
        {
            var manifest = service.GetManifest();
            return Results.Ok(new
            {
                serverVersion = manifest.ServerVersion,
                clientVersion = manifest.ClientVersion,
                installerAvailable = manifest.InstallerAvailable,
                fileName = manifest.FileName,
                byteLength = manifest.ByteLength,
                sha256 = manifest.Sha256,
                builtAt = manifest.BuiltAt
            });
        });

        application.MapGet("/api/v1/client-installer/download", (ClientInstallerService service, HttpContext context) =>
        {
            var installer = service.GetInstaller();
            if (installer is null)
            {
                return PlanningHttpSupport.Error(
                    StatusCodes.Status404NotFound,
                    "client_installer_unavailable",
                    "The Server has no client installer to distribute.",
                    context);
            }

            context.Response.Headers["X-Meimad-Checksum-SHA256"] = installer.Sha256;
            context.Response.Headers["X-Meimad-Client-Version"] = installer.ClientVersion;
            return Results.File(
                installer.Path,
                "application/octet-stream",
                installer.FileName,
                enableRangeProcessing: true);
        });
    }
}
