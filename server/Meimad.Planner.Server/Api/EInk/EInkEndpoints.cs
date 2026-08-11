using Meimad.Planner.Server.Application.EInk;

namespace Meimad.Planner.Server.Api.EInk;

internal static class EInkEndpoints
{
    internal static void MapEInkEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/eink/devices/{deviceId}/version", ReadVersionAsync);
        endpoints.MapGet("/api/v1/eink/devices/{deviceId}/machine-screen", ReadMachineScreenAsync);
        endpoints.MapGet("/api/v1/eink/devices/{deviceId}/package-manifest", ReadCurrentManifestAsync);
        endpoints.MapGet(
            "/api/v1/eink/devices/{deviceId}/packages/{packageId}/revisions/{revision}/manifest",
            ReadExactManifestAsync);
        endpoints.MapGet(
            "/api/v1/eink/devices/{deviceId}/packages/{packageId}/revisions/{revision}/files/{fileId}",
            ReadFileAsync);
        endpoints.MapGet("/api/v1/eink/devices/{deviceId}/time-config", ReadTimeConfigAsync);
    }

    private static async Task<IResult> ReadVersionAsync(
        string deviceId,
        HttpContext context,
        EInkDeviceService service,
        CancellationToken cancellationToken) => await ReadResourceAsync(
        context,
        () => service.ReadVersionAsync(
            deviceId,
            ReadToken(context),
            cancellationToken),
        "no-cache");

    private static async Task<IResult> ReadMachineScreenAsync(
        string deviceId,
        HttpContext context,
        EInkDeviceService service,
        CancellationToken cancellationToken) => await ReadResourceAsync(
        context,
        () => service.ReadMachineScreenAsync(
            deviceId,
            ReadToken(context),
            cancellationToken),
        "no-cache");

    private static async Task<IResult> ReadCurrentManifestAsync(
        string deviceId,
        HttpContext context,
        EInkDeviceService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var resource = await service.ReadCurrentManifestAsync(
                deviceId,
                ReadToken(context),
                cancellationToken);
            context.Response.Headers.ContentLocation = ExactManifestPath(deviceId, resource.Value);
            return Conditional(context, resource, "no-cache");
        }
        catch (Exception exception) when (Known(exception))
        {
            return Error(context, exception);
        }
    }

    private static async Task<IResult> ReadExactManifestAsync(
        string deviceId,
        string packageId,
        string revision,
        HttpContext context,
        EInkDeviceService service,
        CancellationToken cancellationToken) => await ReadResourceAsync(
        context,
        () => service.ReadExactManifestAsync(
            deviceId,
            packageId,
            revision,
            ReadToken(context),
            cancellationToken),
        "private, max-age=31536000, immutable");

    private static async Task<IResult> ReadFileAsync(
        string deviceId,
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
                deviceId,
                packageId,
                revision,
                fileId,
                ReadToken(context),
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
        string deviceId,
        HttpContext context,
        EInkDeviceService service,
        CancellationToken cancellationToken) => await ReadResourceAsync(
        context,
        () => service.ReadTimeConfigAsync(
            deviceId,
            ReadToken(context),
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

    private static string ReadToken(HttpContext context)
    {
        var value = context.Request.Headers.Authorization.ToString();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? value[7..].Trim()
            : string.Empty;
    }

    private static string ExactManifestPath(
        string deviceId,
        EInkManifestResponse manifest) =>
        $"/api/v1/eink/devices/{Uri.EscapeDataString(deviceId)}/packages/{Uri.EscapeDataString(manifest.PackageId)}/revisions/{Uri.EscapeDataString(manifest.Revision)}/manifest";

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
