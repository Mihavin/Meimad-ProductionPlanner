using Meimad.Planner.Server.Domain.Projections;

namespace Meimad.Planner.Server.Application.Projections;

internal interface IProjectionRevisionReader
{
    Task<string> ReadAsync(CancellationToken token);
}

internal sealed class ProjectionChangedException : Exception
{
    internal ProjectionChangedException() : base("Planning data changed during three projection attempts. Refresh to read a coherent result.") { }
}

// A scope fixes the evaluation instant across nested board/timeline/readiness reads.
// It holds no connection or transaction while projection CPU work runs.
internal sealed class ProjectionConsistency(IProjectionRevisionReader revisions, TimeProvider clock)
{
    private readonly string runtimeId = Guid.NewGuid().ToString("N");
    private sealed record ReadScope(DateTimeOffset Instant)
    {
        internal List<Func<Task>> AfterValidation { get; } = [];
    }
    private static readonly AsyncLocal<ReadScope?> scope = new();
    internal static DateTimeOffset? EvaluationAt => scope.Value?.Instant;
    internal static Task AfterValidationAsync(Func<Task> action)
    {
        if (scope.Value is not { } current) return action();
        current.AfterValidation.Add(action);
        return Task.CompletedTask;
    }

    internal async Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> read,
        Func<T, ProjectionFreshness, T> stamp, CancellationToken token, DateTimeOffset? asOf = null,
        IProjectionRevisionReader? revisionReader = null)
    {
        if (scope.Value is not null) return await read(token); // The outer read owns validation and retry.
        var instant = (asOf ?? clock.GetUtcNow()).ToUniversalTime();
        var inputs = revisionReader ?? revisions;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var before = await inputs.ReadAsync(token);
            T result;
            Exception? failure = null;
            var current = new ReadScope(instant);
            scope.Value = current;
            try { result = await read(token); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { failure = exception; result = default!; }
            finally { scope.Value = null; }
            var after = await inputs.ReadAsync(token);
            if (before != after) continue;
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            var stamped = stamp(result, new(before, runtimeId, instant, clock.GetUtcNow().ToUniversalTime(), attempt));
            foreach (var action in current.AfterValidation) await action();
            return stamped;
        }
        throw new ProjectionChangedException();
    }
}
