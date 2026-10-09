using Meimad.Planner.Server.Application.Projections;

namespace Meimad.Planner.Server.Tests.Timeline;

public sealed class ProjectionResponseCacheTests
{
    [Fact]
    public async Task Same_revision_and_explicit_time_share_work_and_cached_result_but_new_revision_recalculates()
    {
        var revision = new Revision();
        var cache = new ProjectionResponseCache(revision, TimeProvider.System);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<byte[]> Calculate(CancellationToken token) { Interlocked.Increment(ref calls); started.SetResult(); await release.Task.WaitAsync(token); return [1, 2, 3]; }
        var first = cache.ReadAsync("explicit:time", true, Calculate, default);
        await started.Task;
        var second = cache.ReadAsync("explicit:time", true, Calculate, default);
        release.SetResult();
        Assert.Equal(await first, await second);
        Assert.Equal(new byte[] { 1, 2, 3 }, await cache.ReadAsync("explicit:time", true, _ => throw new Exception("Cache miss"), default));
        Assert.Equal(1, calls);
        revision.Value = "next";
        Assert.Equal(new byte[] { 9 }, await cache.ReadAsync("explicit:time", true, _ => Task.FromResult(new byte[] { 9 }), default));
    }

    [Fact]
    public async Task One_cancelled_viewer_does_not_cancel_another_but_last_viewer_cancels_shared_work()
    {
        var cache = new ProjectionResponseCache(new Revision(), TimeProvider.System);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<byte[]> Calculate(CancellationToken token) { started.SetResult(token); await finish.Task.WaitAsync(token); return [1]; }
        using var a = new CancellationTokenSource(); using var b = new CancellationTokenSource();
        var first = cache.ReadAsync("live", false, Calculate, a.Token);
        var shared = await started.Task;
        var second = cache.ReadAsync("live", false, Calculate, b.Token);
        a.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(shared.IsCancellationRequested);
        b.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.True(shared.IsCancellationRequested);
    }

    [Fact]
    public async Task Changed_revision_during_calculation_is_not_cached_and_live_reads_are_not_retained()
    {
        var revision = new Revision(); var cache = new ProjectionResponseCache(revision, TimeProvider.System);
        await cache.ReadAsync("explicit", true, _ => { revision.Value = "changed"; return Task.FromResult(new byte[] { 1 }); }, default);
        revision.Value = "initial";
        Assert.Equal(new byte[] { 2 }, await cache.ReadAsync("explicit", true, _ => Task.FromResult(new byte[] { 2 }), default));
        await cache.ReadAsync("live", false, _ => Task.FromResult(new byte[] { 1 }), default);
        // Wait for the completion continuation to retire the flight (no retained live cache).
        await Task.Delay(20);
        Assert.Equal(new byte[] { 2 }, await cache.ReadAsync("live", false, _ => Task.FromResult(new byte[] { 2 }), default));
    }

    [Fact]
    public async Task Distinct_requests_are_bounded_and_cancellation_releases_capacity()
    {
        var cache = new ProjectionResponseCache(new Revision(), TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        var entered = new SemaphoreSlim(0);
        async Task<byte[]> Calculate(CancellationToken token) { entered.Release(); await Task.Delay(Timeout.Infinite, token); return []; }
        var tasks = Enumerable.Range(0, 4).Select(i => cache.ReadAsync(i.ToString(), false, Calculate, cancellation.Token)).ToArray();
        for (var i = 0; i < 4; i++) await entered.WaitAsync();
        await Assert.ThrowsAsync<ProjectionBusyException>(() => cache.ReadAsync("fifth", false, Calculate, default));
        cancellation.Cancel();
        foreach (var task in tasks) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await Task.Delay(20);
        Assert.Equal(new byte[] { 1 }, await cache.ReadAsync("new", false, _ => Task.FromResult(new byte[] { 1 }), default));
    }

    [Fact]
    public async Task Expiry_and_size_limit_do_not_return_stale_or_oversized_cached_payloads()
    {
        var clock = new Clock(); var cache = new ProjectionResponseCache(new Revision(), clock);
        await cache.ReadAsync("time", true, _ => Task.FromResult(new byte[] { 1 }), default);
        clock.Now = clock.Now.AddSeconds(11);
        Assert.Equal(new byte[] { 2 }, await cache.ReadAsync("time", true, _ => Task.FromResult(new byte[] { 2 }), default));
        await cache.ReadAsync("large", true, _ => Task.FromResult(new byte[8 * 1024 * 1024 + 1]), default);
        await Task.Delay(20);
        Assert.Equal(new byte[] { 3 }, await cache.ReadAsync("large", true, _ => Task.FromResult(new byte[] { 3 }), default));
    }

    private sealed class Revision : IProjectionRevisionReader
    {
        internal string Value = "initial";
        public Task<string> ReadAsync(CancellationToken token) => Task.FromResult(Value);
    }
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
