using System.Collections.Concurrent;
using System.Text.Json;
using Meimad.Planner.Server.Backup;

namespace Meimad.Planner.Server.Application.ProductionPackages;

internal sealed record PackageJournalFile(string Path, long Length, string Sha256);
internal sealed record PackagePublicationJournal(int Version, string PackageId, DateTimeOffset StartedAt,
    string ContextHash, string State, IReadOnlyList<PackageJournalFile> Files)
{
    internal const string Name = ".publication.json";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal async Task SaveAsync(string directory, CancellationToken token)
    {
        var temporary = RecoveryPaths.Resolve(directory, Name + ".pending");
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, this, Json, token);
            await stream.FlushAsync(token);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, RecoveryPaths.Resolve(directory, Name), overwrite: true);
    }
}

// Builders run concurrently. Reconciliation obtains exclusive access only when no builder is active.
// The registry is shared by service instances, including retries and maintenance on the same root.
internal static class PackageStorageActivity
{
    private sealed class State { internal readonly SemaphoreSlim Gate = new(1, 1); internal int Builders; }
    private static readonly ConcurrentDictionary<string, State> Roots = new(StringComparer.OrdinalIgnoreCase);
    private static State For(string root) => Roots.GetOrAdd(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), _ => new());
    internal static async Task<IDisposable> BuildAsync(string root, CancellationToken token, int maximumBuilds = int.MaxValue)
    {
        var state = For(root);
        await state.Gate.WaitAsync(token);
        try
        {
            if (state.Builders >= maximumBuilds) throw new ProductionPackageBuildException("production_package_build_busy",
                "The Server is already building the configured maximum number of packages. Retry after a build finishes.");
            state.Builders++;
        }
        finally { state.Gate.Release(); }
        return new Lease(() => { state.Gate.Wait(); try { state.Builders--; } finally { state.Gate.Release(); } });
    }
    internal static async Task<IDisposable?> TryScanAsync(string root, CancellationToken token)
    {
        var state = For(root);
        if (!await state.Gate.WaitAsync(0, token)) return null;
        if (state.Builders != 0) { state.Gate.Release(); return null; }
        return new Lease(() => state.Gate.Release());
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? action = release;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
}
