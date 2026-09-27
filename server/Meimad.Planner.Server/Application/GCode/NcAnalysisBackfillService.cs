using Meimad.Planner.Server.Configuration;

namespace Meimad.Planner.Server.Application.GCode;

/// <summary>
/// Gives every existing immutable G-code release an analysis from the current NC analyzer (the
/// NC engine), so releases made before the engine was installed get the same cycle-time basis
/// as new ones. The release file is never modified; the previous analysis stays as history and
/// the newer analysis becomes current. Runs once after startup, one release at a time, and
/// skips (until the next start) a release the engine could not analyze.
/// </summary>
internal sealed class NcAnalysisBackfillService : BackgroundService
{
    private const int BatchSize = 25;
    private readonly INcAnalysisRepository repository;
    private readonly INcProgramAnalyzer analyzer;
    private readonly GCodeArtifactStore artifactStore;
    private readonly GCodeOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<NcAnalysisBackfillService> logger;

    public NcAnalysisBackfillService(
        INcAnalysisRepository repository,
        INcProgramAnalyzer analyzer,
        GCodeArtifactStore artifactStore,
        GCodeOptions options,
        TimeProvider timeProvider,
        ILogger<NcAnalysisBackfillService> logger)
    {
        this.repository = repository;
        this.analyzer = analyzer;
        this.artifactStore = artifactStore;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.NcEngineBackfillEnabled) return;
        try
        {
            await Task.Delay(options.NcEngineBackfillStartDelay, timeProvider, stoppingToken);
            var count = await RunOnceAsync(stoppingToken);
            if (count > 0)
            {
                logger.LogInformation("NC engine analysis added to {Count} existing G-code release(s).", count);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The NC engine analysis backfill stopped.");
        }
    }

    /// <summary>Analyzes every release without a current-version analysis; returns how many were added.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var added = 0;
        while (true)
        {
            var batch = await repository.ListReleasesWithoutAnalysisAsync(
                analyzer.CurrentVersion, BatchSize, failed, cancellationToken);
            if (batch.Count == 0) return added;
            foreach (var release in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await TryAnalyzeAsync(release, cancellationToken)) added++;
                else failed.Add(release.GCodeReleaseId);
            }
        }
    }

    private async Task<bool> TryAnalyzeAsync(NcAnalysisCandidate release, CancellationToken cancellationToken)
    {
        string path;
        try
        {
            path = artifactStore.ResolveStoredPath(release.StoredRelativePath);
        }
        catch (Exception exception) when (exception is GCodeStorageException or ArgumentException
            or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "G-code release {ReleaseId} has no readable stored file.", release.GCodeReleaseId);
            return false;
        }
        if (!File.Exists(path))
        {
            logger.LogWarning("G-code release {ReleaseId} has no stored file at {Path}.", release.GCodeReleaseId, path);
            return false;
        }

        var interpretations = await repository.ListPostprocessorMachineInterpretationsAsync(release.PostprocessorId, cancellationToken);
        var analysis = await analyzer.AnalyzeAsync(path, interpretations, timeProvider.GetUtcNow(), cancellationToken);
        if (!string.Equals(analysis.ParserVersion, analyzer.CurrentVersion, StringComparison.Ordinal))
        {
            // The engine failed and the analyzer fell back; the release keeps its existing analysis.
            return false;
        }
        return await repository.AddAnalysisAsync(release, analysis, cancellationToken);
    }
}
