namespace Meimad.Planner.Server.Api.Tablets;

using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.EInk;

/// <summary>Authenticated bootstrap for physical production tablets.</summary>
internal static class TabletEndpoints
{
    internal static void MapTabletEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/tablet/ping", PingAsync);
        endpoints.MapGet("/api/tablets/{tabletId}/status", ReadStatusAsync);
        endpoints.MapPost("/api/tablets/{tabletId}/events", SubmitEventAsync);
    }

    private static async Task<IResult> SubmitEventAsync(
        string tabletId,
        JsonElement request,
        HttpContext context,
        TabletEventService service,
        CancellationToken cancellationToken)
    {
        if (request.ValueKind != JsonValueKind.Object)
        {
            return PlanningHttpSupport.Error(422, "tablet_event_invalid",
                "The tablet event request must be a JSON object.", context);
        }

        var properties = request.EnumerateObject().ToArray();
        if (properties.Length != 1
            || properties[0].Name != "event_type"
            || properties[0].Value.ValueKind != JsonValueKind.String)
        {
            return PlanningHttpSupport.Error(422, "tablet_event_invalid",
                "The request must contain only event_type and no target or timestamp.", context);
        }

        try
        {
            var result = await service.SubmitAsync(
                new SubmitTabletEventCommand(
                    tabletId,
                    ReadBearerToken(context),
                    properties[0].Value.GetString() ?? string.Empty,
                    DecimalHeader(context, "X-Meimad-Battery-Voltage", 0m, 12m),
                    IntHeader(context, "X-Meimad-Battery-Percent", 0, 100),
                    TextHeader(context, "X-Meimad-Firmware-Version", 64),
                    TextHeader(context, "X-Meimad-Wifi-IP", 64),
                    IntHeader(context, "X-Meimad-Wifi-Rssi", -127, 0)),
                cancellationToken);
            return Results.Ok(new
            {
                tablet_id = result.TabletId,
                event_type = result.EventType,
                timestamp = result.Timestamp,
                duplicate = result.WasDuplicate
            });
        }
        catch (TabletEventValidationException exception)
        {
            return PlanningHttpSupport.Error(422, exception.Code, exception.Message, context);
        }
        catch (TabletEventResourceNotFoundException)
        {
            return PlanningHttpSupport.Error(404, "device_resource_not_found",
                "The requested tablet resource was not found.", context);
        }
        catch (TabletEventStateException exception)
        {
            return PlanningHttpSupport.Error(409, exception.Code, exception.Message, context);
        }
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
                TextHeader(context, "X-Meimad-Firmware-Version", 64),
                TextHeader(context, "X-Meimad-Wifi-IP", 64),
                IntHeader(context, "X-Meimad-Wifi-Rssi", -127, 0),
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
                TextHeader(context, "X-Meimad-Firmware-Version", 64),
                TextHeader(context, "X-Meimad-Wifi-IP", 64),
                IntHeader(context, "X-Meimad-Wifi-Rssi", -127, 0),
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
        int.TryParse(context.Request.Headers[name], NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value) && value >= minimum && value <= maximum
                ? value : null;

    private static string? TextHeader(HttpContext context, string name, int maximumLength)
    {
        var value = context.Request.Headers[name].ToString().Trim();
        return value.Length is > 0 && value.Length <= maximumLength
            && !value.Any(char.IsControl)
                ? value
                : null;
    }
}
