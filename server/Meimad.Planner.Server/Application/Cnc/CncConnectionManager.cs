using System.Collections.Concurrent;
using System.Diagnostics;
using Meimad.Planner.Server.Domain.Cnc;

namespace Meimad.Planner.Server.Application.Cnc;

internal sealed class CncConnectionManager(
    ICncConnectionRepository repository,
    ICncAdapterFactory adapterFactory,
    IEnumerable<ICncSnapshotConsumer> consumers,
    ICncLivePublisher livePublisher,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory,
    ILogger<CncConnectionManager> logger) : BackgroundService, ICncConnectionManager
{
    private readonly ConcurrentDictionary<string, WorkerLease> workers = new(StringComparer.Ordinal);
    private CancellationToken managerToken;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        managerToken = stoppingToken;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var enabled = await repository.ListConnectionsAsync(true, stoppingToken);
                var currentIds = enabled.Select(value => value.MachineId).ToHashSet(StringComparer.Ordinal);
                foreach (var stale in workers.Keys.Where(id => !currentIds.Contains(id)).ToArray())
                    StopWorker(stale);
                foreach (var connection in enabled)
                {
                    if (!workers.TryGetValue(connection.MachineId, out var lease)
                        || lease.Version != connection.Version)
                    {
                        StopWorker(connection.MachineId);
                        StartWorker(connection, stoppingToken);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Unable to reconcile configured CNC connection workers.");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
        }
        foreach (var machineId in workers.Keys.ToArray()) StopWorker(machineId);
        await Task.WhenAll(workers.Values.Select(value => value.Task));
    }

    public Task<MachineSnapshot?> GetCurrentSnapshotAsync(
        string machineId, CancellationToken cancellationToken = default) =>
        repository.GetCurrentSnapshotAsync(machineId, cancellationToken);

    public async Task RequestReconnectAsync(string machineId, CancellationToken token = default)
    {
        var connection = await repository.GetConnectionAsync(machineId, token)
            ?? throw new CncConnectionNotFoundException(machineId);
        if (!connection.Enabled) throw new CncValidationException("enabled", "Enable the CNC connection before reconnecting it.");
        StopWorker(machineId);
        StartWorker(connection, managerToken);
    }

    public async Task<CncConnectionTestResult> TestConnectionAsync(
        string machineId, CancellationToken token = default)
    {
        var connection = await repository.GetConnectionAsync(machineId, token)
            ?? throw new CncConnectionNotFoundException(machineId);
        await using var adapter = adapterFactory.CreateAdapter(connection);
        return await adapter.TestConnectionAsync(token);
    }

    private void StartWorker(MachineConnection connection, CancellationToken hostToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        var worker = new MachineConnectionWorker(
            connection, repository, adapterFactory, consumers.ToArray(), livePublisher,
            timeProvider, loggerFactory.CreateLogger<MachineConnectionWorker>());
        var task = worker.RunAsync(cancellation.Token);
        workers[connection.MachineId] = new(connection.Version, cancellation, task);
        _ = task.ContinueWith(completed =>
        {
            if (completed.Exception is not null)
                logger.LogError(completed.Exception, "CNC worker terminated unexpectedly for Machine {MachineId}.", connection.MachineId);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void StopWorker(string machineId)
    {
        if (!workers.TryRemove(machineId, out var lease)) return;
        lease.Cancellation.Cancel();
        lease.Cancellation.Dispose();
    }

    private sealed record WorkerLease(int Version, CancellationTokenSource Cancellation, Task Task);
}

internal sealed class MachineConnectionWorker(
    MachineConnection connection,
    ICncConnectionRepository repository,
    ICncAdapterFactory adapterFactory,
    IReadOnlyList<ICncSnapshotConsumer> consumers,
    ICncLivePublisher livePublisher,
    TimeProvider timeProvider,
    ILogger<MachineConnectionWorker> logger)
{
    private static readonly int[] BackoffSteps = [1000, 2000, 5000, 10000, 30000];

    internal async Task RunAsync(CancellationToken token)
    {
        var failureCount = 0;
        while (!token.IsCancellationRequested)
        {
            await using var adapter = adapterFactory.CreateAdapter(connection);
            var operationStarted = Stopwatch.GetTimestamp();
            try
            {
                var now = timeProvider.GetUtcNow();
                await repository.UpdateConnectionStateAsync(
                    connection.Id, CncConnectionStates.Connecting, now, false, null, token);
                await livePublisher.PublishAsync(new("MachineConnectionChanged", connection.MachineId, now,
                    new { connectionStatus = CncConnectionStates.Connecting }), token);
                await adapter.ConnectAsync(token);
                failureCount = 0;
                while (!token.IsCancellationRequested)
                {
                    operationStarted = Stopwatch.GetTimestamp();
                    var observed = await adapter.ReadSnapshotAsync(token);
                    var snapshot = observed.Snapshot;
                    await repository.UpdateConnectionStateAsync(connection.Id, snapshot.ConnectionStatus,
                        snapshot.Timestamp, true, snapshot.LastError, token);
                    var changed = await repository.SaveSnapshotAsync(connection, snapshot, observed.RawTelemetry, token);
                    var events = new List<string>();
                    foreach (var consumer in consumers)
                    {
                        var result = await consumer.ConsumeAsync(snapshot, token);
                        events.AddRange(result.DomainEvents);
                    }
                    if (changed)
                    {
                        await livePublisher.PublishAsync(new(
                            "MachineSnapshotUpdated", connection.MachineId, snapshot.Timestamp, snapshot), token);
                    }
                    if (events.Count > 0)
                    {
                        await livePublisher.PublishAsync(new(
                            "BenchStateChanged", connection.MachineId, snapshot.Timestamp,
                            new { eventTypes = events.Distinct().ToArray() }), token);
                    }
                    logger.LogDebug(
                        "CNC operation completed. MachineId={MachineId} AdapterType={AdapterType} ConnectionId={ConnectionId} Operation={Operation} DurationMs={DurationMs} Success={Success}",
                        connection.MachineId, CncAdapterTypes.Serialize(connection.AdapterType), connection.Id,
                        "poll", Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds, true);
                    await Task.Delay(TimeSpan.FromMilliseconds(connection.PollingIntervalMs), timeProvider, token);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failureCount++;
                var now = timeProvider.GetUtcNow();
                var previous = await repository.GetCurrentSnapshotAsync(connection.MachineId, token);
                var offline = Offline(previous, connection, now, exception.Message);
                await repository.UpdateConnectionStateAsync(connection.Id, CncConnectionStates.Offline,
                    now, false, exception.Message, token);
                var changed = await repository.SaveSnapshotAsync(connection, offline,
                    [new(connection.MachineId, connection.Id,
                        CncAdapterTypes.Serialize(connection.AdapterType), now, "POLL_ERROR", Safe(exception.Message))], token);
                if (changed)
                    await livePublisher.PublishAsync(new("MachineSnapshotUpdated", connection.MachineId, now, offline), token);
                logger.LogWarning(exception,
                    "CNC poll failed. MachineId={MachineId} AdapterType={AdapterType} ConnectionId={ConnectionId} Operation={Operation} DurationMs={DurationMs} Success={Success}",
                    connection.MachineId, CncAdapterTypes.Serialize(connection.AdapterType), connection.Id,
                    "poll", Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds, false);
                try { await adapter.DisconnectAsync(token); } catch { }
                var backoff = BackoffSteps[Math.Min(failureCount - 1, BackoffSteps.Length - 1)];
                backoff = Math.Min(backoff, connection.MaximumReconnectBackoffMs);
                await Task.Delay(TimeSpan.FromMilliseconds(backoff), timeProvider, token);
            }
        }
    }

    private static MachineSnapshot Offline(
        MachineSnapshot? previous, MachineConnection connection, DateTimeOffset at, string error)
    {
        var adapter = CncAdapterTypes.Serialize(connection.AdapterType);
        var machineState = previous is null
            ? new CncFreshValue<string>(null, null, true)
            : previous.MachineState with { Stale = previous.MachineState.Value is not null };
        var partCounter = previous is null
            ? new CncFreshValue<int?>(null, null, true)
            : previous.PartCounter with { Stale = previous.PartCounter.Value is not null };
        return new(connection.MachineId, connection.Id, adapter, at, CncConnectionStates.Offline,
            previous?.LastSeenAt,
            machineState,
            previous is null
                ? new(new(null, null, true), new(null, null, true), new(null, null, true))
                : new(previous.Program.ProgramNumber with { Stale = previous.Program.ProgramNumber.Value is not null },
                    previous.Program.PartName with { Stale = previous.Program.PartName.Value is not null },
                    previous.Program.HeaderSourcePath with { Stale = previous.Program.HeaderSourcePath.Value is not null }),
            previous?.Production is { } production
                ? production with { ModeVariableValue = production.ModeVariableValue with { Stale = production.ModeVariableValue.Value is not null } }
                : new(null, null, new(null, null, true)),
            partCounter,
            previous?.Telemetry ?? new(null, null, null, null),
            new Dictionary<string, string> { ["CONNECTION"] = CncComponentStates.Unavailable },
            previous?.CapabilityHealth ?? new Dictionary<string, string>(),
            Safe(error), previous?.Version + 1 ?? 1);
    }

    private static string Safe(string value) => value.Length <= 500 ? value : value[..500];
}
