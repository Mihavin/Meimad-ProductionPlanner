using System.Security.Cryptography;
using System.Text.Json;
using Meimad.Planner.Server.Application.ProductionPackages;
using Meimad.Planner.Server.Backup;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Persistence;

namespace Meimad.Planner.Server.Application.Maintenance;

internal sealed record StoredArtifact(string Store, string Id, string Path, long? Length, string Hash, string State);
internal sealed record ArtifactHealthItem(string Store, string Id, string State, string Integrity, string Detail);
internal sealed record ArtifactHealthReport(DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string Status,
    int Total, int Problems, IReadOnlyList<ArtifactHealthItem> Items, bool Truncated, string Retention, int? OrphanGraceDays = null,
    DateTimeOffset? LastSuccessfulScanAt = null);

internal sealed partial class ArtifactReconciliationService(SqliteArtifactInventory inventory, ProductionPackageOptions packages,
    GCodeOptions gcode, EInkOptions eink, TimeProvider clock)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ArtifactHealthReport latest = new(null, null, "NOT_SCANNED", 0, 0, [], false, RetentionPolicy);
    internal const string RetentionPolicy = "Scans retain all files. Manual collection uses the configured grace period. Referenced artifacts, production evidence and backups are retained; raw telemetry retention is unchanged.";
    internal ArtifactHealthReport Status => Volatile.Read(ref latest) with { OrphanGraceDays = packages.OrphanGraceDays };
    private static bool NeedsReview(ArtifactHealthItem item) => item.Integrity != "VERIFIED"
        || item.State is not ("ACTIVE" or "SUPERSEDED" or "REFERENCED");

    internal async Task<ArtifactHealthReport> ScanAsync(CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token)) return Status with { Status = "BUSY" };
        var started = clock.GetUtcNow();
        try
        {
            using var activity = await PackageStorageActivity.TryScanAsync(packages.ResolvedPackageRoot, token);
            if (activity is null) return Status with { Status = "BUILD_IN_PROGRESS" };
            using var backupLease = await RecoveryArtifactLease.AcquireAsync(token);
            var references = await inventory.ReadAsync(token);
            var roots = new Dictionary<string, string> { ["production"] = packages.ResolvedPackageRoot,
                ["gcode"] = gcode.ResolvedReleaseRoot, ["eink"] = eink.ResolvedPackageRoot };
            var items = new List<ArtifactHealthItem>();
            var total = 0;
            var problems = 0;
            void Add(ArtifactHealthItem item)
            {
                total++;
                if (NeedsReview(item)) problems++;
                // Keep problems before healthy rows so a large healthy catalog cannot hide an error.
                if (items.Count < 500) items.Add(item);
                else if (NeedsReview(item))
                {
                    var healthy = items.FindLastIndex(x => !NeedsReview(x));
                    if (healthy >= 0) items[healthy] = item;
                }
            }
            foreach (var group in references.GroupBy(x => (x.Store, x.Id)))
            {
                var integrity = "VERIFIED";
                var detail = "All referenced files match recorded lengths and SHA-256 hashes.";
                foreach (var file in group.DistinctBy(x => (x.Path, x.Length, x.Hash)))
                {
                    var result = await VerifyAsync(roots[file.Store], file.Path, file.Length, file.Hash, token);
                    if (result != "VERIFIED") { integrity = result; detail = $"{file.Path}: {result}. Retained; restore or investigate before use."; break; }
                }
                Add(new(group.Key.Store, group.Key.Id, group.First().State, integrity, detail));
            }
            var known = references.Where(x => x.Store == "production").Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            RecoveryPaths.RejectLinks(packages.ResolvedPackageRoot);
            if (Directory.Exists(packages.ResolvedPackageRoot))
                foreach (var entry in Directory.EnumerateFileSystemEntries(packages.ResolvedPackageRoot).Order(StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(entry);
                    if (known.Contains(name)) continue;
                    Add(await InspectUnreferencedAsync(entry, known, token));
                }
            // Release markers are older than the package journal. They prove ownership, not
            // completeness, so abandoned releases are reported and never inferred safe to delete.
            var releaseIds = references.Where(x => x.Store == "gcode").Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            RecoveryPaths.RejectLinks(gcode.ResolvedReleaseRoot);
            var pending = new Queue<(string Path, int Depth)>();
            if (Directory.Exists(gcode.ResolvedReleaseRoot)) pending.Enqueue((gcode.ResolvedReleaseRoot, 0));
            var inspected = 0;
            while (pending.TryDequeue(out var folder))
            {
                token.ThrowIfCancellationRequested();
                if (++inspected > 100_000 || folder.Depth > 64) throw new InvalidDataException("Release inventory bound exceeded.");
                foreach (var child in Directory.EnumerateDirectories(folder.Path).Order(StringComparer.Ordinal))
                {
                    var relative = Path.GetRelativePath(gcode.ResolvedReleaseRoot, child);
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    { Add(new("gcode", relative, "AMBIGUOUS", "UNVERIFIED", "Linked directory retained and not traversed.")); continue; }
                    if (Path.GetFileName(child).StartsWith(".staging-", StringComparison.Ordinal))
                    { Add(new("gcode", relative, "STAGED", "INCOMPLETE", "Release staging retained for investigation.")); continue; }
                    var marker = RecoveryPaths.Resolve(child, ".meimad-release-id");
                    if (File.Exists(marker))
                    {
                        var id = new FileInfo(marker).Length <= 256 ? (await File.ReadAllTextAsync(marker, token)).Trim() : "";
                        if (!releaseIds.Contains(id))
                            Add(new("gcode", relative, "PUBLISHED_UNREFERENCED", "UNVERIFIED", "Release marker has no database reference; retained for investigation."));
                    }
                    pending.Enqueue((child, folder.Depth + 1));
                }
            }
            var report = new ArtifactHealthReport(started, clock.GetUtcNow(), "COMPLETE", total, problems,
                items.OrderBy(x => !NeedsReview(x)).ThenBy(x => x.Store, StringComparer.Ordinal)
                    .ThenBy(x => x.Id, StringComparer.Ordinal).ToArray(), total > items.Count, RetentionPolicy, packages.OrphanGraceDays);
            report = report with { LastSuccessfulScanAt = report.CompletedAt };
            Volatile.Write(ref latest, report);
            return report;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or Microsoft.Data.Sqlite.SqliteException or JsonException)
        {
            var report = Status with { StartedAt = started, CompletedAt = null, Status = "SCAN_FAILED" };
            Volatile.Write(ref latest, report);
            return report;
        }
        finally { gate.Release(); }
    }

    private static async Task<ArtifactHealthItem> InspectUnreferencedAsync(string directory, IReadOnlySet<string> known, CancellationToken token)
    {
        var name = Path.GetFileName(directory);
        ArtifactHealthItem Result(string state, string integrity, string detail) => new("production", name, state, integrity, detail);
        try
        {
            RecoveryPaths.RejectLinks(directory);
            if (!Directory.Exists(directory)) return Result("UNRECOGNIZED", "UNVERIFIED", "Unexpected file retained for investigation.");
            var path = RecoveryPaths.Resolve(directory, PackagePublicationJournal.Name);
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024)
                return Result("UNRECOGNIZED", "UNVERIFIED", "Missing or oversized publication journal; retained for investigation.");
            var journal = JsonSerializer.Deserialize<PackagePublicationJournal>(await File.ReadAllTextAsync(path, token), PackagePublicationJournal.Json);
            if (journal is null || journal.Version != 1 || !Guid.TryParseExact(journal.PackageId, "N", out _)
                || (name != journal.PackageId && name != ".staging-" + journal.PackageId)
                || journal.State is not ("STAGING" or "READY") || journal.Files is null || journal.Files.Count > 1024
                || journal.ContextHash is null || journal.ContextHash.Length != 64
                || journal.Files.Any(x => x is null || string.IsNullOrWhiteSpace(x.Path) || x.Length < 0 || x.Sha256 is null || x.Sha256.Length != 64))
                return Result("AMBIGUOUS", "UNVERIFIED", "Journal identity or format does not match; retained for investigation.");
            if (known.Contains(journal.PackageId)) return Result("AMBIGUOUS", "UNVERIFIED", "Staging directory collides with a referenced package; retained.");
            if (journal.State == "STAGING") return Result("STAGED", "INCOMPLETE", "Interrupted before the complete manifest was journaled; retained.");
            if (!journal.Files.Any(x => x.Path == "manifest.json")) return Result("AMBIGUOUS", "UNVERIFIED", "Journal has no manifest; retained.");
            foreach (var file in journal.Files)
            {
                var integrity = await VerifyAsync(directory, file.Path, file.Length, file.Sha256, token);
                if (integrity != "VERIFIED") return Result("AMBIGUOUS", integrity, "Journaled files do not verify; retained for investigation.");
            }
            return Result(name.StartsWith(".staging-", StringComparison.Ordinal) ? "STAGED" : "PUBLISHED_UNREFERENCED", "VERIFIED",
                "Complete journal and files verified, but no package record exists. Retained; never auto-activated.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException
            or ArgumentException)
        { return Result("AMBIGUOUS", "UNVERIFIED", "Unable to safely inspect this publication; retained for investigation."); }
    }

    private static async Task<string> VerifyAsync(string root, string relative, long? length, string hash, CancellationToken token)
    {
        try
        {
            var path = RecoveryPaths.Resolve(root, relative);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            if (length is not null && stream.Length != length) return "CORRUPT";
            return string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)), hash, StringComparison.OrdinalIgnoreCase)
                ? "VERIFIED" : "CORRUPT";
        }
        catch (FileNotFoundException) { return "MISSING"; }
        catch (DirectoryNotFoundException) { return "MISSING"; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { return "UNVERIFIED"; }
    }
}

internal sealed class ArtifactReconciliationStartup(ArtifactReconciliationService service,
    ILogger<ArtifactReconciliationStartup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var report = await service.ScanAsync(stoppingToken);
        if (report.Status != "COMPLETE" || report.Problems > 0)
            logger.LogWarning("Artifact reconciliation: {Status}; {Problems} integrity issues. Inspect Server maintenance.", report.Status, report.Problems);
    }
}
