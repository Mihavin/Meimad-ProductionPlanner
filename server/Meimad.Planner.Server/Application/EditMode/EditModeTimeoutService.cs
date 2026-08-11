namespace Meimad.Planner.Server.Application.EditMode;

internal sealed class EditModeTimeoutService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);
    private readonly IEditModeRepository repository;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<EditModeTimeoutService> logger;

    public EditModeTimeoutService(
        IEditModeRepository repository,
        TimeProvider timeProvider,
        ILogger<EditModeTimeoutService> logger)
    {
        this.repository = repository;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, timeProvider, stoppingToken);
                await repository.ProcessTimeoutAsync(timeProvider.GetUtcNow(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Edit Mode timeout processing failed; it will be retried.");
            }
        }
    }
}
