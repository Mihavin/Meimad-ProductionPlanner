namespace Meimad.Planner.Server.Api.Tablets;

/// <summary>Minimal network reachability probe for physical production tablets.
/// It publishes no planning data and is deliberately usable before a device is registered.</summary>
internal static class TabletEndpoints
{
    internal static void MapTabletEndpoints(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/tablet/ping", (string? hardwareId) => Results.Ok(new
        {
            status = "ok",
            hardwareId = string.IsNullOrWhiteSpace(hardwareId) ? null : hardwareId.Trim(),
            tabletId = (string?)null
        }));
}
