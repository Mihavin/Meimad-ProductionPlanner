namespace Meimad.Planner.Client.Windows.Api;

internal sealed record ArtifactHealthItem(string Store, string Id, string State, string Integrity, string Detail);
internal sealed record ArtifactHealthReport(DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string Status,
    int Total, int Problems, IReadOnlyList<ArtifactHealthItem> Items, bool Truncated, string Retention, int? OrphanGraceDays = null);
internal sealed record ArtifactCollectionResult(DateTimeOffset CompletedAt, int Deleted, int Retained, string Policy);
