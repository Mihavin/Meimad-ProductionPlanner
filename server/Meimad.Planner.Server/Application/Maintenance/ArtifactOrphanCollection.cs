using System.Text.Json;
using Meimad.Planner.Server.Application.ProductionPackages;
using Meimad.Planner.Server.Backup;

namespace Meimad.Planner.Server.Application.Maintenance;

internal sealed record ArtifactCollectionResult(DateTimeOffset CompletedAt, int Deleted, int Retained, string Policy);

internal sealed partial class ArtifactReconciliationService
{
    internal async Task<ArtifactCollectionResult> CollectAsync(CancellationToken token)
    {
        if (packages.OrphanGraceDays is not { } days)
            throw new InvalidOperationException("Orphan cleanup is disabled by ProductionPackages:OrphanGraceDays. The approved default grace period is seven days.");
        if (!await gate.WaitAsync(0, token)) throw new InvalidOperationException("An artifact operation is already running.");
        try
        {
            using var activity = await PackageStorageActivity.TryScanAsync(packages.ResolvedPackageRoot, token)
                ?? throw new InvalidOperationException("A package build is active. Retry cleanup later.");
            // Pin backup snapshot + artifact copy before reading references. New builds cannot
            // publish while this lease is held; existing immutable reference rows are never deleted.
            using var backupLease = await RecoveryArtifactLease.AcquireAsync(token);
            var references = await inventory.ReadAsync(token);
            var production = references.Where(x => x.Store == "production").ToArray();
            var known = production.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var root = packages.ResolvedPackageRoot;
            RecoveryPaths.RejectLinks(root);
            var protectedPaths = production.Select(x => RecoveryPaths.Resolve(root, x.Path)).ToArray();
            var deleted = 0;
            var retained = 0;
            var cutoff = clock.GetUtcNow().AddDays(-days);
            if (Directory.Exists(root))
                foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(directory);
                    if (known.Contains(name)) { retained++; continue; }
                    var inspected = await InspectUnreferencedAsync(directory, known, token);
                    if (inspected.Integrity != "VERIFIED" || inspected.State is not ("STAGED" or "PUBLISHED_UNREFERENCED"))
                    { retained++; continue; }
                    // Check references by path as well as ID. A malformed cross-package path is
                    // still a reference and must prevent collection.
                    if (protectedPaths.Any(x => x.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    { retained++; continue; }
                    if (!await HasOnlyOldJournaledFilesAsync(directory, cutoff, token)) { retained++; continue; }
                    try
                    {
                        // Resolve the exact immediate child again; do not traverse links or accept
                        // unknown contents. Interrupted deletion remains unreferenced and is reported.
                        var safe = RecoveryPaths.Resolve(root, name);
                        RecoveryPaths.DeleteOwnedDirectory(root, safe);
                        deleted++;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                    { retained++; }
                }
            // The last scan is now historical. The caller should request a fresh scan.
            Volatile.Write(ref latest, Status with { Status = "RESCAN_REQUIRED" });
            return new(clock.GetUtcNow(), deleted, retained,
                $"Only complete, verified, unreferenced production publications older than {days} days are eligible. All ambiguous/referenced files, releases, evidence and backups are retained.");
        }
        finally { gate.Release(); }
    }

    private static async Task<bool> HasOnlyOldJournaledFilesAsync(string directory, DateTimeOffset cutoff, CancellationToken token)
    {
        try
        {
            var journalPath = RecoveryPaths.Resolve(directory, PackagePublicationJournal.Name);
            var journal = JsonSerializer.Deserialize<PackagePublicationJournal>(await File.ReadAllTextAsync(journalPath, token), PackagePublicationJournal.Json)!;
            if (journal.StartedAt > cutoff || journal.StartedAt == default) return false;
            var expected = journal.Files.Select(x => RecoveryPaths.Resolve(directory, x.Path)).Append(journalPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (expected.Count != journal.Files.Count + 1) return false;
            var pending = new Queue<string>(); pending.Enqueue(directory);
            var seen = 0;
            while (pending.TryDequeue(out var parent))
            {
                token.ThrowIfCancellationRequested();
                // Every directory must be part of a journaled file path; unexpected empty folders
                // are not owned publication content either.
                if (Directory.GetLastWriteTimeUtc(parent) > cutoff.UtcDateTime) return false;
                foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
                {
                    RecoveryPaths.RejectLinks(entry);
                    if (Directory.Exists(entry))
                    {
                        if (!expected.Any(x => x.StartsWith(entry + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return false;
                        pending.Enqueue(entry);
                    }
                    else if (!expected.Contains(entry) || File.GetLastWriteTimeUtc(entry) > cutoff.UtcDateTime) return false;
                    else seen++;
                }
            }
            return seen == expected.Count;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        { return false; }
    }
}
