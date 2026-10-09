using System.Globalization;
using Meimad.Planner.Server.Application.Maintenance;

namespace Meimad.Planner.Server.Api.Maintenance;

internal static class OperationalHealthEndpoints
{
    internal static void MapOperationalHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/operational-health", async (HttpContext context, OperationalHealthService service,
            string? cursor, int? limit, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, null, out _, out var error)) return error!;
            context.Response.Headers.CacheControl = "no-store";
            try { return Results.Ok(await service.ReadAsync(cursor, limit ?? 100, token)); }
            catch (ArgumentException exception) { return PlanningHttpSupport.Error(400, "health_query_invalid", exception.Message, context); }
        });
        endpoints.MapGet("/api/v1/operational-health/evidence", async (HttpContext context, OperationalEvidenceReader service,
            string from, string to, string? entityType, string? entityId, string? cursor, int? limit, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, null, out _, out var error)) return error!;
            context.Response.Headers.CacheControl = "no-store";
            if (!Instant(from, out var start) || !Instant(to, out var end))
                return PlanningHttpSupport.Error(400, "evidence_query_invalid", "from/to must be instants with an explicit UTC offset.", context);
            try { return Results.Ok(await service.ReadAsync(start, end, entityType, entityId, cursor, limit ?? 100, token)); }
            catch (ArgumentException exception) { return PlanningHttpSupport.Error(400, "evidence_query_invalid", exception.Message, context); }
        });
    }

    private static bool Instant(string text, out DateTimeOffset value)
    {
        value = default;
        return text.Length <= 64 && text.Contains('T') && (text.EndsWith('Z') || text.LastIndexOf('+') > 10 || text.LastIndexOf('-') > 10)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
    }
}
