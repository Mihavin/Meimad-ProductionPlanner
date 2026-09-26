using System.Globalization;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>A Planning Board column for one internal station or External Resource.</summary>
internal sealed record PlanningResourceLane(string ResourceId, string Name, IReadOnlyList<PlanningResourceCard> Cards)
{
    internal static PlanningResourceLane From(TimelineResourceLane lane) => new(
        lane.ResourceId,
        lane.Name,
        lane.Intervals
            .OrderBy(interval => interval.StartsAt)
            .ThenBy(interval => interval.BatchNumber, StringComparer.Ordinal)
            .Select(PlanningResourceCard.From)
            .ToArray());
}

/// <summary>One auxiliary step as the Server placed it on the station.</summary>
internal sealed record PlanningResourceCard(
    string Title,
    string Detail,
    string TimeText,
    string PinText,
    string Explanation)
{
    internal static PlanningResourceCard From(TimelineResourceInterval interval) => new(
        interval.Label,
        $"{interval.PartNumber} · {interval.DirectionLabel}",
        $"{Format(interval.StartsAt)} – {Format(interval.EndsAt)}",
        interval.IsPinned ? "📌 Pinned" : "◌ Predicted",
        interval.Explanation);

    private static string Format(DateTimeOffset value) =>
        value.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.CurrentCulture);
}
