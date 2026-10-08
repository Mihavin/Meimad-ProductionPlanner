using Meimad.Planner.Server.Application.Timeline;
using Meimad.Planner.Server.Configuration;
using Microsoft.AspNetCore.DataProtection;

namespace Meimad.Planner.Server.Application.Kitaron.Push;

/// <summary>
/// Pushes Planner values of Kitaron Work Order operations into Kitaron (owner decision 2026-09-28):
/// on demand from Setup → Kitaron Push, or every configured interval while automatic pushing is on.
/// A preview computes the same changes without writing. Each run and every value written is logged.
/// </summary>
internal sealed class KitaronPushService
{
    private readonly IKitaronPushRepository repository;
    private readonly IKitaronConnectionRepository connections;
    private readonly IKitaronPushTarget target;
    private readonly IKitaronPushForecast forecast;
    private readonly IDataProtector passwordProtector;
    private readonly TimeZoneInfo factoryZone;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<KitaronPushService> logger;
    private readonly SemaphoreSlim gate = new(1, 1);

    public KitaronPushService(
        IKitaronPushRepository repository,
        IKitaronConnectionRepository connections,
        IKitaronPushTarget target,
        IKitaronPushForecast forecast,
        IDataProtectionProvider dataProtectionProvider,
        TimelineOptions timelineOptions,
        TimeProvider timeProvider,
        ILogger<KitaronPushService> logger)
    {
        this.repository = repository;
        this.connections = connections;
        this.target = target;
        this.forecast = forecast;
        this.timeProvider = timeProvider;
        this.logger = logger;
        // The same protector as the Kitaron connection, which stored the password.
        passwordProtector = dataProtectionProvider.CreateProtector("Meimad.Planner.Kitaron.SqlPassword.v1");
        factoryZone = TimeZoneInfo.FindSystemTimeZoneById(timelineOptions.TimeZoneId);
    }

    internal Task<KitaronPushSettings> GetSettingsAsync(CancellationToken cancellationToken) =>
        repository.GetSettingsAsync(cancellationToken);

    internal Task<IReadOnlyList<KitaronPushRunSummary>> ListRunsAsync(CancellationToken cancellationToken) =>
        repository.ListRunsAsync(20, cancellationToken);

    internal Task<IReadOnlyList<KitaronPushChange>> ListChangesAsync(string runId, CancellationToken cancellationToken) =>
        repository.ListChangesAsync(runId, cancellationToken);

    internal Task<KitaronPushSettings> UpdateSettingsAsync(
        bool enabled, int intervalMinutes, IReadOnlyList<KitaronPushMapping>? mappings, int expectedVersion,
        string actor, CancellationToken cancellationToken)
    {
        if (intervalMinutes is < 5 or > 1440)
            throw new KitaronPushValidationException("intervalMinutes", "The interval is 5 to 1,440 minutes.");
        var normalized = new List<KitaronPushMapping>();
        foreach (var mapping in mappings ?? [])
        {
            var column = KitaronPushCatalog.Target(mapping.KitaronColumn ?? string.Empty)
                ?? throw new KitaronPushValidationException("mappings",
                    $"Kitaron column '{mapping.KitaronColumn}' cannot be pushed. Choose one of: {string.Join(", ", KitaronPushCatalog.Targets.Select(value => value.Column))}.");
            var source = KitaronPushCatalog.Source(mapping.PlannerValue ?? string.Empty)
                ?? throw new KitaronPushValidationException("mappings", $"Unknown Planner value '{mapping.PlannerValue}'.");
            if (column.Kind != source.Kind)
                throw new KitaronPushValidationException("mappings",
                    $"{column.Column} holds a {column.Kind}; \"{source.Name}\" is a {source.Kind}.");
            if (normalized.Any(value => value.KitaronColumn == column.Column))
                throw new KitaronPushValidationException("mappings", $"{column.Column} is mapped more than once.");
            normalized.Add(new KitaronPushMapping(column.Column, source.Code, mapping.Enabled));
        }

        return repository.UpdateSettingsAsync(
            enabled, intervalMinutes, normalized, expectedVersion, actor, timeProvider.GetUtcNow(), cancellationToken);
    }

    /// <summary>The changes a push would write now, without writing them.</summary>
    internal async Task<KitaronPushResult> PreviewAsync(CancellationToken cancellationToken)
    {
        var (connection, password) = await ConnectionAsync(cancellationToken);
        var (plan, stamp) = await PlanAsync(connection, password, cancellationToken);
        return new KitaronPushResult(null, false, timeProvider.GetUtcNow(), plan.OperationsMatched,
            plan.OperationsSkipped, plan.Changes, plan.Notes, stamp);
    }

    /// <summary>Writes the changes into Kitaron in one transaction and logs the run.</summary>
    internal async Task<KitaronPushResult> RunAsync(string trigger, string? requestedBy, CancellationToken cancellationToken, string? expectedPreviewStamp = null)
    {
        if (!await gate.WaitAsync(0, cancellationToken))
            throw new KitaronPushBlockedException("A push to Kitaron is already running. Try again in a moment.");
        var runId = Guid.NewGuid().ToString("N");
        try
        {
            await repository.StartRunAsync(runId, trigger, requestedBy, timeProvider.GetUtcNow(), cancellationToken);
            KitaronPushPlanner.Plan? plan = null;
            try
            {
                var (connection, password) = await ConnectionAsync(cancellationToken);
                var planned = await PlanAsync(connection, password, cancellationToken);
                plan = planned.Plan;
                if (expectedPreviewStamp is not null && !string.Equals(expectedPreviewStamp, planned.Stamp, StringComparison.Ordinal))
                    throw new KitaronPushConflictException("The Kitaron push preview changed. Nothing was written. Refresh Preview and review the current values.");
                if (plan.Writes.Count > 0)
                    await target.WriteAsync(connection, password, plan.Writes, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var message = exception is KitaronPushBlockedException
                    ? exception.Message
                    : $"Kitaron did not accept the changes; nothing was written. {exception.Message}";
                await repository.FinishRunAsync(runId, false, plan?.OperationsMatched ?? 0, plan?.OperationsSkipped ?? 0,
                    message, [], timeProvider.GetUtcNow(), CancellationToken.None);
                if (exception is KitaronPushBlockedException) throw;
                logger.LogError(exception, "Pushing to Kitaron failed.");
                throw new KitaronPushBlockedException(message);
            }

            var summary = plan.Changes.Count == 0
                ? "Kitaron already has the Planner's values."
                : $"{plan.Changes.Count} values written.";
            await repository.FinishRunAsync(runId, true, plan.OperationsMatched, plan.OperationsSkipped,
                string.Join(" ", plan.Notes.Prepend(summary)), plan.Changes, timeProvider.GetUtcNow(), CancellationToken.None);
            return new KitaronPushResult(runId, true, timeProvider.GetUtcNow(), plan.OperationsMatched,
                plan.OperationsSkipped, plan.Changes, plan.Notes);
        }
        finally
        {
            gate.Release();
        }
    }

    internal Task<DateTimeOffset?> LastAutomaticRunStartedAtAsync(CancellationToken cancellationToken) =>
        repository.LastAutomaticRunStartedAtAsync(cancellationToken);

    private async Task<(KitaronPushPlanner.Plan Plan, string Stamp)> PlanAsync(
        StoredKitaronConnectionSettings connection, string password, CancellationToken cancellationToken)
    {
        var settings = await repository.GetSettingsAsync(cancellationToken);
        var active = settings.Mappings.Where(mapping => mapping.Enabled).ToArray();
        var operations = await repository.ReadOperationsAsync(cancellationToken);
        if (active.Length == 0 || operations.Count == 0)
        {
            var empty = KitaronPushPlanner.Build(settings.Mappings, operations, new Dictionary<string, KitaronPushForecast>(), [], factoryZone);
            return (empty, KitaronPushComparison.Stamp(settings, empty, connection));
        }

        var forecasts = active.Any(mapping => mapping.PlannerValue.StartsWith("forecast_", StringComparison.Ordinal))
            ? await forecast.ReadAsync(cancellationToken)
            : new Dictionary<string, KitaronPushForecast>();
        var rows = await target.ReadAsync(
            connection, password,
            operations.Select(operation => operation.WorkOrderNumber).Distinct().ToArray(),
            active.Select(mapping => mapping.KitaronColumn).ToArray(),
            cancellationToken);
        var plan = KitaronPushPlanner.Build(settings.Mappings, operations, forecasts, rows, factoryZone);
        return (plan, KitaronPushComparison.Stamp(settings, plan, connection));
    }

    private async Task<(StoredKitaronConnectionSettings Connection, string Password)> ConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = await connections.GetAsync(cancellationToken);
        if (!connection.Enabled)
            throw new KitaronPushBlockedException("The Kitaron connector is switched off on the Server's Kitaron setup page.");
        if (string.IsNullOrWhiteSpace(connection.ProtectedPassword))
            throw new KitaronPushBlockedException("No Kitaron password is configured on the Server's Kitaron setup page.");
        try
        {
            return (connection, passwordProtector.Unprotect(connection.ProtectedPassword));
        }
        catch (Exception)
        {
            throw new KitaronPushBlockedException(
                "The stored Kitaron password cannot be decrypted on this Server. Save it again on the Kitaron setup page.");
        }
    }
}

/// <summary>Runs the automatic push at the configured interval while it is switched on.</summary>
internal sealed class KitaronPushHostedService(
    IKitaronPushRepository repository,
    KitaronPushService pushService,
    TimeProvider timeProvider,
    ILogger<KitaronPushHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = await repository.GetSettingsAsync(stoppingToken);
                var last = await pushService.LastAutomaticRunStartedAtAsync(stoppingToken);
                if (settings.Enabled
                    && (last is null || timeProvider.GetUtcNow() - last >= TimeSpan.FromMinutes(settings.IntervalMinutes)))
                {
                    await pushService.RunAsync("automatic", null, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (KitaronPushBlockedException exception)
            {
                logger.LogWarning("Automatic push to Kitaron did not run: {Reason}", exception.Message);
            }
            catch (Exception exception) { logger.LogError(exception, "Automatic push to Kitaron failed."); }

            try { await Task.Delay(TimeSpan.FromSeconds(60), timeProvider, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}

/// <summary>The Timeline's current placement of each operation, from now over the next 180 days.</summary>
internal sealed class TimelineKitaronPushForecast(TimelineProjectionService timeline, TimeProvider timeProvider)
    : IKitaronPushForecast
{
    public async Task<IReadOnlyDictionary<string, KitaronPushForecast>> ReadAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var projection = await timeline.CalculateAsync(now, now.AddDays(180), now, cancellationToken);
        return projection.Machines
            .SelectMany(machine => machine.Intervals)
            .Where(interval => interval.Type == "operation" && interval.OperationId is not null)
            .GroupBy(interval => interval.OperationId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new KitaronPushForecast(group.Min(interval => interval.StartsAt), group.Max(interval => interval.EndsAt)),
                StringComparer.Ordinal);
    }
}
