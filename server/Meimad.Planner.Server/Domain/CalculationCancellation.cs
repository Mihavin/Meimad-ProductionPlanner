namespace Meimad.Planner.Server.Domain;

// Synchronous calculation helpers share the caller's cancellation without storing request
// state on singleton engines. Nested calculations restore the previous execution-context value.
internal static class CalculationCancellation
{
    private static readonly AsyncLocal<CancellationToken> Current = new();
    internal static IDisposable Enter(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var previous = Current.Value;
        Current.Value = token;
        return new Scope(previous);
    }
    internal static void Check() => Current.Value.ThrowIfCancellationRequested();
    private sealed class Scope(CancellationToken previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
