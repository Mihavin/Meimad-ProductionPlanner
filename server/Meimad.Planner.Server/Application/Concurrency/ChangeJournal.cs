using System.Collections.Concurrent;

namespace Meimad.Planner.Server.Application.Concurrency;

/// <summary>
/// Who last saved a change through each API resource path, kept in memory so a version conflict can
/// name the user and time of the change the refused request did not see. Best effort: it forgets on a
/// Server restart, knows nothing about changes made by background work (Kitaron synchronization, CNC
/// events), and only reports a change from the last <see cref="Horizon"/>.
/// </summary>
internal sealed class ChangeJournal(TimeProvider timeProvider)
{
    internal static readonly TimeSpan Horizon = TimeSpan.FromHours(12);
    private const int Capacity = 5000;

    private readonly ConcurrentDictionary<string, JournalEntry> entries = new(StringComparer.Ordinal);

    internal sealed record JournalEntry(string ResourcePath, string UserName, DateTimeOffset ChangedAt);

    internal void Record(string path, string userName)
    {
        if (ResourcePath(path) is not { } key) return;
        entries[key] = new JournalEntry(key, userName, timeProvider.GetUtcNow());
        if (entries.Count > Capacity)
        {
            foreach (var stale in entries.Values.OrderBy(entry => entry.ChangedAt).Take(entries.Count - Capacity))
                entries.TryRemove(stale.ResourcePath, out _);
        }
    }

    /// <summary>The latest recorded change of the resource at <paramref name="path"/>, of a resource that contains it, or of one it contains.</summary>
    internal JournalEntry? LatestRelatedTo(string path)
    {
        if (ResourcePath(path) is not { } key) return null;
        var since = timeProvider.GetUtcNow() - Horizon;
        return entries.Values
            .Where(entry => entry.ChangedAt >= since && Related(entry.ResourcePath, key))
            .OrderByDescending(entry => entry.ChangedAt)
            .FirstOrDefault();
    }

    /// <summary>The collection and item part of an API path, e.g. <c>/api/v1/cases/c1/operations/o1</c>; null for a bare collection.</summary>
    internal static string? ResourcePath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // api, v1, collection, id: a POST to the bare collection creates a new item nobody else saw.
        return segments.Length >= 4 && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            ? "/" + string.Join('/', segments).ToLowerInvariant()
            : null;
    }

    private static bool Related(string recorded, string requested) =>
        recorded == requested
        || requested.StartsWith(recorded + "/", StringComparison.Ordinal)
        || recorded.StartsWith(requested + "/", StringComparison.Ordinal);
}
