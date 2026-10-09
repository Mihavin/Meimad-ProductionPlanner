using System.Text.Json;
namespace Meimad.Planner.Server.Application.LocalAi;

internal static class ModelClaimValidator
{
    internal static IReadOnlyList<EvidenceClaim> Validate(string content, EvidenceBundle bundle)
    {
        if (content.Length > 16384) throw new ArgumentException("Model answer exceeds its limit.");
        using var json = JsonDocument.Parse(content);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1
            || !root.TryGetProperty("claimOrder", out var order) || order.ValueKind != JsonValueKind.Array
            || order.GetArrayLength() != bundle.Claims.Count || order.GetArrayLength() > 256)
            throw new ArgumentException("Model answer does not match the claim schema.");
        var seen = new HashSet<int>();
        var result = new List<EvidenceClaim>();
        var evidence = bundle.Facts.Select(x => x.EvidenceId).ToHashSet(StringComparer.Ordinal);
        foreach (var item in order.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var index) || index < 0 || index >= bundle.Claims.Count || !seen.Add(index))
                throw new ArgumentException("Model answer duplicates, omits or invents a claim.");
            var claim = bundle.Claims[index];
            if (claim.EvidenceIds.Count == 0 || claim.EvidenceIds.Any(id => !evidence.Contains(id)))
                throw new ArgumentException("Claim citation is outside this evidence bundle.");
            result.Add(claim);
        }
        return result;
    }
}
