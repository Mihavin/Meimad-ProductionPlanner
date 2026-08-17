namespace Meimad.Planner.Server.Application.Kitaron;

internal sealed class KitaronConnectionMonitorService(
    IKitaronConnectionRepository repository,
    KitaronConnectionService service,
    TimeProvider timeProvider,
    ILogger<KitaronConnectionMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = await repository.GetAsync(stoppingToken);
                var now = timeProvider.GetUtcNow();
                var due = settings.LastTestAt is null
                    || now - settings.LastTestAt >= TimeSpan.FromSeconds(
                        settings.RefreshIntervalSeconds);
                if (settings.Enabled && due)
                {
                    var result = await service.TestAsync(stoppingToken);
                    if (!result.Succeeded)
                    {
                        logger.LogWarning(
                            "Periodic read-only Kitaron connection check failed: {FailureMessage}",
                            result.Message);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Periodic Kitaron connection check failed unexpectedly.");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stoppingToken);
        }
    }
}
