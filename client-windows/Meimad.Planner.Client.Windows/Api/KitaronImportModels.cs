namespace Meimad.Planner.Client.Windows.Api;

internal sealed record KitaronImportFieldChange(string Field, string? Before, string? After);
internal sealed record KitaronImportChange(string Entity, string Key, string Action, IReadOnlyList<KitaronImportFieldChange> Fields);
internal sealed record KitaronImportLocalPreview(IReadOnlyList<KitaronImportChange> Changes, int TotalChanges,
    int Unchanged, bool Truncated, bool Destructive);
internal sealed record KitaronImportEvidence(string RunId, string SourceSystem, string SourceEndpoint,
    int ConnectionVersion, int MappingVersion, string SourceFingerprint);
internal sealed record KitaronImportPreview(string PreviewId, DateTimeOffset ExpiresAt, string LocalRevision,
    KitaronImportEvidence Evidence, KitaronImportLocalPreview Local, IReadOnlyList<string> Warnings);
internal sealed record KitaronImportApplyResult(string Status, string? Message);
