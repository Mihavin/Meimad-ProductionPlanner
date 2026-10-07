namespace Meimad.Planner.Server.Application.Cnc;

/// <summary>One line of a Machine's DPRNT output as the Server received it.</summary>
internal sealed record DprntLogLine(long Id, DateTimeOffset ReceivedAt, string Line);

/// <summary>
/// A page of a Machine's DPRNT log in the order the lines arrived. <see cref="HasMore"/> says more
/// lines match after the last one; ask again with its <see cref="DprntLogLine.Id"/> as <c>afterId</c>.
/// </summary>
internal sealed record DprntLogPage(
    string MachineId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Search,
    IReadOnlyList<DprntLogLine> Lines,
    bool HasMore);

internal sealed record DprntLogQuery(
    string MachineId, DateTimeOffset? From, DateTimeOffset? To, string? Search, long AfterId, int Limit);

internal interface IDprntLogRepository
{
    /// <summary>The matching lines after <see cref="DprntLogQuery.AfterId"/>, at most limit + 1; null for an unknown Machine.</summary>
    Task<IReadOnlyList<DprntLogLine>?> ReadAsync(DprntLogQuery query, CancellationToken cancellationToken);
}

internal sealed class DprntLogValidationException(string message) : Exception(message);

/// <summary>
/// The permanent DPRNT log (schema v96): every non-blank line each Machine's DPRNT output sent since
/// the log exists, whatever it says, with the time the Server received it. Read-only.
/// </summary>
internal sealed class DprntLogService(IDprntLogRepository repository)
{
    internal const int DefaultLimit = 5000;
    internal const int MaximumLimit = 50000;

    internal async Task<DprntLogPage?> ReadAsync(
        string machineId, DateTimeOffset? from, DateTimeOffset? to, string? search, long? afterId, int? limit,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(machineId)) throw new DprntLogValidationException("machineId is required.");
        if (from is not null && to is not null && to <= from) throw new DprntLogValidationException("to must be after from.");
        if (afterId is < 0) throw new DprntLogValidationException("afterId must not be negative.");
        var size = limit ?? DefaultLimit;
        if (size is < 1 or > MaximumLimit) throw new DprntLogValidationException($"limit is between 1 and {MaximumLimit}.");
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        if (search?.Length > 200) throw new DprntLogValidationException("search is limited to 200 characters.");

        var lines = await repository.ReadAsync(
            new DprntLogQuery(machineId.Trim(), from, to, search, afterId ?? 0, size), cancellationToken);
        if (lines is null) return null;
        var hasMore = lines.Count > size;
        return new DprntLogPage(machineId.Trim(), from, to, search, hasMore ? lines.Take(size).ToArray() : lines, hasMore);
    }
}
