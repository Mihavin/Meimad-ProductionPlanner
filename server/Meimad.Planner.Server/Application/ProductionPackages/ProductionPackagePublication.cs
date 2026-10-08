using System.Security.Cryptography;
using System.Text.Json;

namespace Meimad.Planner.Server.Application.ProductionPackages;

internal sealed record ProductionPackageRequest(string ActorId, string RequestId, string RequestHash)
{
    internal static ProductionPackageRequest? Create(string actor, string? key, string operation,
        string offsetMode, ProductionPackageSelection? selection)
    {
        if (key is null) return null; // Legacy unkeyed calls retain CAS, but cannot replay a lost response.
        key = key.Trim();
        if (key.Length is < 1 or > 128 || key.Any(value => value < 33 || value > 126))
            throw new ProductionPackageBuildException("production_package_request_key_invalid",
                "Idempotency-Key must contain 1 to 128 printable ASCII characters without spaces.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { command = "CREATE_PRODUCTION_PACKAGE", operation, offsetMode, selection });
        return new(actor, key, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}

internal static class ProductionPackagePublication
{
    internal static string Fingerprint(ProductionPackageBuildContext context, string offsetMode)
    {
        // The build DTO contains the exact generation inputs. Readiness's material/operational
        // projection is not generated content; retain its engineering eligibility facts separately.
        var snapshot = JsonSerializer.SerializeToNode(context)!.AsObject();
        snapshot.Remove(nameof(context.ReadinessContext));
        snapshot.Remove(nameof(context.CurrentPackageId));
        snapshot.Remove(nameof(context.PublicationVersion));
        if (offsetMode == "MANUAL_DUMMY")
        {
            snapshot.Remove(nameof(context.ToolPreparation));
            snapshot.Remove(nameof(context.ReleasedTools));
        }
        snapshot["engineeringEligibility"] = JsonSerializer.SerializeToNode(new
        {
            context.ReadinessContext.UsableToolPositions,
            context.ReadinessContext.RequiredToolCount,
            supportedPostprocessors = context.ReadinessContext.SupportedPostprocessorIds.Order(StringComparer.Ordinal).ToArray()
        });
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot)));
    }
}
