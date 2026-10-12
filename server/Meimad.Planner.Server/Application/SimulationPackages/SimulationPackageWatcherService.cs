using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.SimulationPackage.Geometry;
using Meimad.Planner.SimulationPackage.Manifest;
using Meimad.Planner.SimulationPackage.Validation;

namespace Meimad.Planner.Server.Application.SimulationPackages;

/// <summary>
/// Folder watcher of exporter definition §12 D, placed in the Server (decision recorded in the
/// implementation plan): the Server is the only component that reads approved roots and writes
/// planning data, so a client-side watcher would duplicate both. It is off by default
/// (<c>SimulationPackages:Watcher:Enabled</c>). It polls — no FileSystemWatcher, which is unreliable on
/// SMB shares — the configured roots that are themselves approved, and considers only folders that
/// contain both <c>COMPLETE</c> and <c>manifest.json</c>. Each new manifest is imported once with the
/// Case/Operation the package folder and manifest identify; it never chooses a stock candidate or a
/// Machine and never overwrites a manually entered Work CS, so such packages stay <c>blocked</c> with a
/// stored report until a planner imports them from the Case. A package already imported or already
/// recorded as blocked (same manifest hash and folder) is skipped.
/// </summary>
internal sealed class SimulationPackageWatcherService(
    SimulationPackageImportService importer,
    SqliteSimulationPackageRepository repository,
    SimulationPackageOptions options,
    TimeProvider timeProvider,
    ILogger<SimulationPackageWatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Watcher.Enabled || options.Watcher.Roots.Length == 0) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var (imported, blocked) = await RunOnceAsync(stoppingToken);
                    if (imported > 0 || blocked > 0)
                        logger.LogInformation("Simulation package watcher: {Imported} imported, {Blocked} blocked.", imported, blocked);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Simulation package watcher scan failed.");
                }

                await Task.Delay(TimeSpan.FromSeconds(options.Watcher.PollIntervalSeconds), timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One scan of the watch roots; returns how many packages were imported and blocked.</summary>
    internal async Task<(int Imported, int Blocked)> RunOnceAsync(CancellationToken cancellationToken)
    {
        var network = await repository.ReadNetworkFolderAsync(cancellationToken);
        var approved = new List<string>(options.ApprovedRoots);
        if (network.IsConfigured) approved.Add(network.RootPath!);
        var imported = 0;
        var blocked = 0;
        foreach (var configuredRoot in options.Watcher.Roots)
        {
            var root = importer.ResolvePath(configuredRoot, network);
            if (!approved.Any(item => PackagePaths.IsUnder(root, item)))
            {
                logger.LogWarning("Simulation package watch root {Root} is not under an approved root and is skipped.", root);
                continue;
            }

            if (!Directory.Exists(root)) continue;
            foreach (var folder in CompletePackages(root, options.Watcher.MaximumDepth))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string manifestSha;
                try
                {
                    manifestSha = PackageHashing.Sha256OfFile(Path.Combine(folder, ManifestJson.ManifestFileName));
                }
                catch (IOException)
                {
                    continue; // being replaced right now; the next scan sees it
                }

                if (await repository.HasBlockedAttemptAsync(manifestSha, network.ToStored(folder) ?? folder, cancellationToken)) continue;
                var outcome = await importer.ImportCoreAsync(
                    new PackageImportRequest(folder, SimulationPackageImportService.WatcherActor),
                    checkActiveImport: false,
                    expectedActiveImportId: null,
                    recordBlocked: true,
                    cancellationToken);
                switch (outcome.Result.Outcome)
                {
                    case PackageImportOutcome.Imported: imported++; break;
                    case PackageImportOutcome.Blocked: blocked++; break;
                }
            }
        }

        return (imported, blocked);
    }

    private static IEnumerable<string> CompletePackages(string root, int maximumDepth)
    {
        var pending = new Stack<(string Folder, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (folder, depth) = pending.Pop();
            if (File.Exists(Path.Combine(folder, ManifestJson.CompleteMarkerFileName))
                && File.Exists(Path.Combine(folder, ManifestJson.ManifestFileName)))
            {
                yield return folder;
                continue;
            }

            if (depth >= maximumDepth) continue;
            string[] children;
            try
            {
                children = Directory.GetDirectories(folder);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                // Staging and superseded exports are never packages; generated caches are not either.
                if (name.StartsWith('.') || name.Equals("_MeimadPlanner", StringComparison.OrdinalIgnoreCase)) continue;
                pending.Push((child, depth + 1));
            }
        }
    }
}
