namespace Meimad.Planner.Server.Application.Projections;

internal sealed class ProjectionBusyException : Exception
{
    internal ProjectionBusyException() : base("The Server is calculating other projections. Retry shortly.") { }
}

// Live reads share only work already in flight. Completed caching requires an explicit
// evaluation time in the caller's key. Revision and runtime-instance identity are automatic.
internal sealed class ProjectionResponseCache(IProjectionRevisionReader revisions, TimeProvider clock)
{
    private const int MaximumInFlight = 4;
    private const int MaximumEntries = 32;
    private const long MaximumBytes = 32 * 1024 * 1024;
    private const int MaximumEntryBytes = 8 * 1024 * 1024;
    private readonly object sync = new();
    private readonly Dictionary<string, Flight> flights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Cached> cache = new(StringComparer.Ordinal);
    private long cachedBytes;
    private sealed record Cached(byte[] Bytes, DateTimeOffset ExpiresAt);
    private sealed class Flight
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource<byte[]> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Waiters;
    }

    internal async Task<byte[]> ReadAsync(string requestKey, bool cacheCompleted,
        Func<CancellationToken, Task<byte[]>> calculate, CancellationToken token)
    {
        var revision = await revisions.ReadAsync(token);
        token.ThrowIfCancellationRequested();
        var key = revision + ":" + requestKey;
        Flight flight;
        lock (sync)
        {
            foreach (var expired in cache.Where(x => x.Value.ExpiresAt <= clock.GetUtcNow()).Select(x => x.Key).ToArray()) Remove(expired);
            if (cacheCompleted && cache.TryGetValue(key, out var cached))
            { PlannerPerformanceMetrics.ProjectionCacheHits.Add(1); return cached.Bytes; }
            if (!flights.TryGetValue(key, out flight!))
            {
                if (flights.Count >= MaximumInFlight) throw new ProjectionBusyException();
                flight = new();
                flights.Add(key, flight);
                _ = Task.Run(() => RunAsync(key, revision, flight, cacheCompleted, calculate));
            }
            else
            {
                if (flight.Cancellation.IsCancellationRequested) throw new ProjectionBusyException();
                PlannerPerformanceMetrics.ProjectionShared.Add(1);
            }
            flight.Waiters++;
        }
        try { return await flight.Completion.Task.WaitAsync(token); }
        finally
        {
            lock (sync)
                if (--flight.Waiters == 0 && !flight.Completion.Task.IsCompleted) flight.Cancellation.Cancel();
        }
    }

    private async Task RunAsync(string key, string revision, Flight flight, bool cacheCompleted,
        Func<CancellationToken, Task<byte[]>> calculate)
    {
        try
        {
            var bytes = await calculate(flight.Cancellation.Token);
            var after = await revisions.ReadAsync(flight.Cancellation.Token);
            lock (sync)
            {
                if (!flight.Cancellation.IsCancellationRequested && cacheCompleted && revision == after && bytes.Length <= MaximumEntryBytes)
                {
                    while (cache.Count >= MaximumEntries || cachedBytes + bytes.Length > MaximumBytes)
                        Remove(cache.MinBy(x => x.Value.ExpiresAt).Key);
                    cache.Add(key, new(bytes, clock.GetUtcNow().AddSeconds(10)));
                    cachedBytes += bytes.Length;
                }
                flight.Completion.TrySetResult(bytes);
            }
        }
        catch (OperationCanceledException) { flight.Completion.TrySetCanceled(); }
        catch (Exception exception) { flight.Completion.TrySetException(exception); }
        finally
        {
            lock (sync) { flights.Remove(key); flight.Cancellation.Dispose(); }
        }
    }

    private void Remove(string key)
    {
        if (cache.Remove(key, out var previous)) cachedBytes -= previous.Bytes.Length;
    }
}
