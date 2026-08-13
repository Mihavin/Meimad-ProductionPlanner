using System.Text.Json;
using Meimad.Planner.Server.Application.EventLogging;

namespace Meimad.Planner.Server.Api.EventLogging;

internal static class StructuredEventLogEndpoints
{
    internal static void MapStructuredEventLogEndpoints(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/event-log", ListAsync);

    private static async Task<IResult> ListAsync(
        string? from, string? to, string? eventType, int? limit, HttpContext context,
        IStructuredEventLogRepository repository, CancellationToken token)
    {
        if (!Parse(from, out var startsAt) || !Parse(to, out var endsAt) || (startsAt.HasValue && endsAt <= startsAt))
            return PlanningHttpSupport.Error(400,"invalid_event_log_range","from/to must be RFC3339 instants with from < to.",context);
        var values = await repository.ListAsync(startsAt,endsAt,eventType,limit ?? 1000,token);
        return Results.Ok(new { items = values.Select(value => new
        {
            eventId=value.EventId,eventType=value.EventType,timestamp=value.Timestamp,user=value.User,
            relatedEntityIds=value.RelatedEntityIds,reasonCode=value.ReasonCode,comment=value.Comment,
            beforeData=Read(value.BeforeDataJson),afterData=Read(value.AfterDataJson)
        }).ToArray() });
    }

    private static bool Parse(string? value,out DateTimeOffset? parsed)
    { parsed=null;if(string.IsNullOrWhiteSpace(value))return true;if(DateTimeOffset.TryParse(value,out var instant)){parsed=instant;return true;}return false; }
    private static JsonElement? Read(string? json) => json is null ? null : JsonSerializer.Deserialize<JsonElement>(json);
}
