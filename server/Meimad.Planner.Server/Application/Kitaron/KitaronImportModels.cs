using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meimad.Planner.Server.Application.Kitaron;

internal interface IKitaronImportContextReader
{
    Task<string> ReadAsync(CancellationToken token);
}

internal sealed record KitaronImportEvidence(string RunId, string SourceSystem, string SourceEndpoint,
    int ConnectionVersion, int MappingVersion, string SourceFingerprint, KitaronSourceConsistency? Consistency,
    string RequestedBy = "kitaron-sync", bool ReviewedPreview = false);
internal sealed record KitaronImportFieldChange(string Field, string? Before, string? After);
internal sealed record KitaronImportChange(string Entity, string Key, string Action,
    IReadOnlyList<KitaronImportFieldChange> Fields);
internal sealed record KitaronImportLocalPreview(KitaronSyncStatus PredictedStatus,
    IReadOnlyList<KitaronImportChange> Changes, int TotalChanges, int Unchanged, bool Truncated, bool Destructive);
internal sealed record KitaronImportPreview(string PreviewId, DateTimeOffset ExpiresAt, string LocalRevision,
    KitaronImportEvidence Evidence, KitaronImportLocalPreview Local, IReadOnlyList<string> Warnings);
internal sealed record KitaronImportApplyRequest(string PreviewId);
internal sealed record KitaronImportReceipt(KitaronImportEvidence Evidence, string? LocalRevision, KitaronSyncStatus Status);

/// <summary>
/// Whether an unreviewed (automatic or one-click) import may remove or deactivate records, change
/// their status, or import under a mapping other than the last receipt's. Off is the policy in force
/// (functional spec, Reviewed ERP import: the previous automatic-sync policy remains until the owner
/// decides); on is the D03 gate, which then needs one reviewed apply on an installed database.
/// </summary>
internal sealed record KitaronImportPolicy(bool ReviewedApplyRequired)
{
    public static readonly KitaronImportPolicy Default = new(ReviewedApplyRequired: false);
}

internal static class KitaronImportFingerprint
{
    // Source result ordering is not source identity. Object property and row-array order are canonicalized;
    // route order is carried explicitly by route-position/sequence fields, not SQL result order.
    internal static string Of<T>(T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(document.RootElement))));
    }
    private static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonical).OrderBy(x => x, StringComparer.Ordinal)) + "]",
        _ => value.GetRawText()
    };
}
