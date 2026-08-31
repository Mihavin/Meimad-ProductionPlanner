using Meimad.Planner.Server.Application.EInk;

namespace Meimad.Planner.Server.Api.EInk;

internal static class EInkEndpoints
{
    internal static void MapEInkEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/eink/tablets/{tabletId}/version", ReadVersionAsync);
        endpoints.MapGet("/api/v1/eink/tablets/{tabletId}/machine-screen", ReadMachineScreenAsync);
        endpoints.MapGet("/api/v1/eink/tablets/{tabletId}/package-manifest", ReadCurrentManifestAsync);
        endpoints.MapGet(
            "/api/v1/eink/tablets/{tabletId}/packages/{packageId}/revisions/{revision}/manifest",
            ReadExactManifestAsync);
        endpoints.MapGet(
            "/api/v1/eink/tablets/{tabletId}/packages/{packageId}/revisions/{revision}/files/{fileId}",
            ReadFileAsync);
        endpoints.MapGet("/api/v1/eink/tablets/{tabletId}/time-config", ReadTimeConfigAsync);
    }

    private static async Task<IResult> ReadVersionAsync(
        string tabletId,
        HttpContext context,
        EInkDeviceService service,
        CancellationToken cancellationToken) => await ReadResourceAsync(
        context,
        () => service.ReadVersionAsync(
            tabletId,
            cancellationToken),
        "no-cache");

    private static async Task<IResult> ReadMachineScreenAsync(
        string tabletId,
        HttpContext context,
        EInkDeviceService service,
        CancellationToken cancellationToken) => await ReadResourceAsync(
        context,
        () => service.ReadMachineScreenAsync(
            tabletId,
            cancellationToken),
        "no-cache");

    private static async Task<IResult> ReadCurrentManifestAsync(
        string tabletId,
        HttpContext context,
        EInkDeviceService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var resource = await service.ReadCurrentManifestAsync(
                tabletId,
                cancellationToken);
            context.Response.Headers.ContentLocation = ExactManifestPath(tabletId, resource.Value);
            return Conditional(context, resource, "no-cache");
        }
        catch (Exception exception) when (Known(exception))
        {
            return Error(context, exception);
        }
    }

    private static async Task<IResult> ReadExactManifestAsync(
        string tabletId,
        string packageId,
        string revision,
        HttpContext context,
        EInkDeviceService service,
        CancellationToken cancellationToken) => await ReadResourceAsync(
        context,
        () => service.ReadExactManifestAsync(
            tabletId,
            packageId,
            revision,
            cancellationToken),
        "private, max-age=31536000, immutable");

    private static async Task<IResult> ReadFileAsync(
        string tabletId,
        string packageId,
        string revision,
        string fileId,
        HttpContext context,
        EInkDeviceService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var file = await service.ResolveFileAsync(
                tabletId,
                packageId,
                revision,
                fileId,
                cancellationToken);
            context.Response.Headers.ETag = $"\"sha256:{file.Sha256}\"";
            context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            context.Response.Headers["X-Meimad-Checksum-SHA256"] = file.Sha256;
            return Results.File(
                file.FullPath,
                file.MediaType,
                enableRangeProcessing: false);
        }
        catch (Exception exception) when (Known(exception))
        {
            return Error(context, exception);
        }
    }

    private static async Task<IResult> ReadTimeConfigAsync(
        string tabletId,
        HttpContext context,
        EInkDeviceService service,
        CancellationToken cancellationToken) => await ReadResourceAsync(
        context,
        () => service.ReadTimeConfigAsync(
            tabletId,
            cancellationToken),
        "no-cache");

    private static async Task<IResult> ReadResourceAsync<T>(
        HttpContext context,
        Func<Task<EInkResource<T>>> read,
        string cacheControl)
    {
        try
        {
            return Conditional(context, await read(), cacheControl);
        }
        catch (Exception exception) when (Known(exception))
        {
            return Error(context, exception);
        }
    }

    private static IResult Conditional<T>(
        HttpContext context,
        EInkResource<T> resource,
        string cacheControl)
    {
        context.Response.Headers.ETag = resource.EntityTag;
        context.Response.Headers.CacheControl = cacheControl;
        return context.Request.Headers.IfNoneMatch.Any(value =>
            string.Equals(value, resource.EntityTag, StringComparison.Ordinal))
                ? Results.StatusCode(StatusCodes.Status304NotModified)
                : Results.Ok(resource.Value);
    }

    private static string ExactManifestPath(
        string tabletId,
        EInkManifestResponse manifest) =>
        $"/api/v1/eink/tablets/{Uri.EscapeDataString(tabletId)}/packages/{Uri.EscapeDataString(manifest.PackageId)}/revisions/{Uri.EscapeDataString(manifest.Revision)}/manifest";

    private static bool Known(Exception exception) => exception is
        EInkDeviceResourceNotFoundException or
        EInkPackageNotAssignedException or
        EInkPackageFileIntegrityException;

    private static IResult Error(HttpContext context, Exception exception) => exception switch
    {
        EInkPackageNotAssignedException => PlanningHttpSupport.Error(
            StatusCodes.Status404NotFound,
            "package_not_assigned",
            "No published package is assigned to the device's current operation.",
            context),
        EInkPackageFileIntegrityException integrity => PlanningHttpSupport.Error(
            StatusCodes.Status409Conflict,
            "package_integrity_failed",
            integrity.Message,
            context),
        _ => PlanningHttpSupport.Error(
            StatusCodes.Status404NotFound,
            "device_resource_not_found",
            "The requested device resource was not found.",
            context)
    };
}
