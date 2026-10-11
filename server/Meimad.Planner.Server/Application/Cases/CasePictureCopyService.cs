using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Persistence;

namespace Meimad.Planner.Server.Application.Cases;

/// <summary>
/// Copies every Case picture that is still only a link (a file on the share or a PC drive) to the Server's picture store
/// once the Server can read it, so the Planning Board, the TV and job packages show it whichever PC asks and whatever
/// happens to the link. The link stays on the Case as the picture's source; no Case data changes. Runs after startup;
/// a picture the Server cannot read (a drive only PCs map, a missing file, not an image) is tried again at the next start.
/// </summary>
internal sealed class CasePictureCopyService(
    SqliteCasePictureRepository repository,
    CasePictureStore store,
    ServerFilePathResolver pathResolver,
    TimeProvider timeProvider,
    ILogger<CasePictureCopyService> logger) : BackgroundService
{
    internal static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, timeProvider, stoppingToken);
            var (copied, unreadable) = await RunOnceAsync(stoppingToken);
            if (copied > 0 || unreadable > 0)
                logger.LogInformation("Case pictures copied to the Server: {Copied}; links the Server could not read: {Unreadable}.", copied, unreadable);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Copying Case pictures to the Server stopped.");
        }
    }

    /// <summary>Copies the readable linked pictures; returns how many were copied and how many links could not be read.</summary>
    internal async Task<(int Copied, int Unreadable)> RunOnceAsync(CancellationToken cancellationToken)
    {
        var copied = 0;
        var unreadable = 0;
        foreach (var (caseId, link, workingFolder) in await repository.ListLinkedAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var path = pathResolver.ResolveExistingFile(link, workingFolder ?? string.Empty);
                if (path is null || new FileInfo(path).Length is 0 or > CasePictureStore.MaximumBytes)
                {
                    unreadable++;
                    continue;
                }
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                if (CasePictureStore.Detect(bytes) is null)
                {
                    unreadable++;
                    continue;
                }
                var picture = await store.WriteAsync(caseId, Path.GetFileName(path), bytes, link, "system", timeProvider.GetUtcNow(), cancellationToken);
                if (await repository.AddCopyAsync(picture, link, timeProvider.GetUtcNow(), cancellationToken)) copied++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or CasePictureException)
            {
                unreadable++;
                logger.LogDebug(exception, "The picture of Case {CaseId} could not be copied.", caseId);
            }
        }
        return (copied, unreadable);
    }
}
