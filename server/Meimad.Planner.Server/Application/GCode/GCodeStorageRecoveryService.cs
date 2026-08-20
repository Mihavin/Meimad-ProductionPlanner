namespace Meimad.Planner.Server.Application.GCode;

/// <summary>
/// Removes only recognisable incomplete/orphaned release publications. Unknown files are never
/// touched: a directory must either use the staging prefix or contain Meimad's release marker.
/// </summary>
internal sealed class GCodeStorageRecoveryService : IHostedService
{
    private const string MarkerName = ".meimad-release-id";

    private readonly GCodeArtifactStore store;
    private readonly IGCodeRepository repository;
    private readonly ILogger<GCodeStorageRecoveryService> logger;

    public GCodeStorageRecoveryService(
        GCodeArtifactStore store,
        IGCodeRepository repository,
        ILogger<GCodeStorageRecoveryService> logger)
    {
        this.store = store;
        this.repository = repository;
        this.logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var root = store.RootPath;
        if (!Directory.Exists(root))
        {
            return;
        }

        var knownIds = await repository.ListStoredArtifactIdsAsync(cancellationToken);
        foreach (var stagingDirectory in Directory.EnumerateDirectories(
                     root,
                     ".staging-*",
                     SearchOption.AllDirectories))
        {
            TryDelete(stagingDirectory, "incomplete staging publication");
        }

        foreach (var markerPath in Directory.EnumerateFiles(root, MarkerName, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string artifactId;
            try
            {
                artifactId = (await File.ReadAllTextAsync(markerPath, cancellationToken)).Trim();
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Could not inspect a G-code release recovery marker.");
                continue;
            }
            catch (UnauthorizedAccessException exception)
            {
                logger.LogWarning(exception, "Could not inspect a G-code release recovery marker.");
                continue;
            }

            if (!knownIds.Contains(artifactId))
            {
                var directory = Path.GetDirectoryName(markerPath);
                if (directory is not null)
                {
                    TryDelete(directory, "orphaned immutable publication");
                }
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void TryDelete(string directory, string reason)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
            logger.LogInformation("Removed {Reason} from G-code release storage.", reason);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Could not remove {Reason} from G-code release storage.", reason);
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "Could not remove {Reason} from G-code release storage.", reason);
        }
    }
}
