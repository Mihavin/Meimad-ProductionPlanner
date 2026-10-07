using System.Globalization;

namespace Meimad.Planner.Client.Windows.Api;

/// <summary>One line of a Machine's DPRNT output as the Server received it.</summary>
internal sealed record DprntLogLineInfo(long Id, DateTimeOffset ReceivedAt, string Line)
{
    public string TimeText => ReceivedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
}

/// <summary>
/// A page of the permanent DPRNT log (GET /api/v1/machines/{id}/dprnt-log) in arrival order;
/// <see cref="HasMore"/> says more lines follow the last one.
/// </summary>
internal sealed record DprntLogPageInfo(
    string MachineId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Search,
    IReadOnlyList<DprntLogLineInfo> Lines,
    bool HasMore);
