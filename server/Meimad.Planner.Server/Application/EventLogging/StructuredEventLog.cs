namespace Meimad.Planner.Server.Application.EventLogging;

internal sealed record StructuredEvent(
    string EventId, string EventType, DateTimeOffset Timestamp, string User,
    IReadOnlyDictionary<string, string> RelatedEntityIds, string? ReasonCode,
    string? Comment, string? BeforeDataJson, string? AfterDataJson);

internal sealed record StructuredEventWrite(
    string EventType, DateTimeOffset Timestamp, string User,
    IReadOnlyDictionary<string, string> RelatedEntityIds, string? ReasonCode = null,
    string? Comment = null, object? BeforeData = null, object? AfterData = null,
    string? EventKey = null);

internal interface IStructuredEventLogRepository
{
    Task AppendAsync(StructuredEventWrite value, CancellationToken token);
    Task<IReadOnlyList<StructuredEvent>> ListAsync(
        DateTimeOffset? from, DateTimeOffset? to, string? eventType, int limit, CancellationToken token);
}
