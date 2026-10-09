namespace Meimad.Planner.Client.Windows.Api;

internal sealed record OperationalHealthItem(string Category, string EntityId, string Name, string Status,
    string Freshness, DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSucceededAt,
    DateTimeOffset? ObservedAt, DateTimeOffset? ReceivedAt, string Detail, string? SourceVersion = null,
    string? ContentRevision = null, string? ObservedState = null);
internal sealed record OperationalHealthPage(DateTimeOffset ReadAt, string Revision,
    IReadOnlyList<OperationalHealthItem> Items, string? NextCursor);
