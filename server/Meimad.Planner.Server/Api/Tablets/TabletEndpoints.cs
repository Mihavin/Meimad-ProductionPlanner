namespace Meimad.Planner.Server.Api.Tablets;

using System.Globalization;
using Meimad.Planner.Server.Application.EInk;

/// <summary>Authenticated bootstrap for physical production tablets.</summary>
internal static class TabletEndpoints
{
    internal static void MapTabletEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/tablet/ping", PingAsync);
        endpoints.MapGet("/api/tablets/{tabletId}/status", ReadStatusAsync);
    }

    private static async Task<IResult> ReadStatusAsync(
        string tabletId,
        HttpContext context,
        TabletStatusService service,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            var value = await service.ReadAsync(
                tabletId,
                ReadBearerToken(context),
                timeProvider.GetUtcNow(),
                DecimalHeader(context, "X-Meimad-Battery-Voltage", 0m, 12m),
                IntHeader(context, "X-Meimad-Battery-Percent", 0, 100),
                cancellationToken);
            context.Response.Headers.CacheControl = "no-cache";
            return Results.Ok(value);
        }
        catch (TabletStatusResourceNotFoundException)
        {
            return PlanningHttpSupport.Error(404, "device_resource_not_found",
                "The requested tablet resource was not found.", context);
        }
        catch (TabletStatusUnavailableException exception)
        {
            return PlanningHttpSupport.Error(409, exception.Code, exception.Message, context);
        }
    }

    private static async Task<IResult> PingAsync(
        string? hardwareId,
        HttpContext context,
        EInkDeviceRegistrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var registration = await service.BootstrapAsync(
                ReadBearerToken(context), hardwareId,
                DecimalHeader(context, "X-Meimad-Battery-Voltage", 0m, 12m),
                IntHeader(context, "X-Meimad-Battery-Percent", 0, 100),
                cancellationToken);
            return Results.Ok(new
            {
                status = "ok",
                tabletId = registration.TabletId,
                deviceId = registration.DeviceId,
                machineId = registration.MachineId
            });
        }
        catch (EInkDeviceRegistrationValidationException exception)
        {
            return PlanningHttpSupport.Error(422, "tablet_bootstrap_invalid", exception.Message, context);
        }
        catch (EInkDeviceRegistrationNotFoundException)
        {
            // Deliberately do not reveal whether the token, MAC, or registration failed.
            return PlanningHttpSupport.Error(404, "device_resource_not_found",
                "The requested tablet resource was not found.", context);
        }
    }

    private static string ReadBearerToken(HttpContext context)
    {
        var value = context.Request.Headers.Authorization.ToString();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? value[7..].Trim() : string.Empty;
    }

    private static decimal? DecimalHeader(HttpContext context, string name, decimal minimum, decimal maximum) =>
        decimal.TryParse(context.Request.Headers[name], NumberStyles.Number,
            CultureInfo.InvariantCulture, out var value) && value >= minimum && value <= maximum
                ? value : null;

    private static int? IntHeader(HttpContext context, string name, int minimum, int maximum) =>
        int.TryParse(context.Request.Headers[name], NumberStyles.None,
            CultureInfo.InvariantCulture, out var value) && value >= minimum && value <= maximum
                ? value : null;
}
