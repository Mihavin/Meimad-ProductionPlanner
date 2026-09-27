namespace Meimad.Planner.Server.Domain.ToolRequirements;

/// <summary>
/// Counts the physical copies of each tool the plan needs and how they move between Machines. A tool
/// is the same tool when its name and diameter match within one material group (owner decision
/// 2026-09-27). One copy can serve several Machines when their uses do not overlap in time: it
/// migrates from the Machine that finished with it to the next one. Two uses at the same time, on
/// different Machines or twice in one tool table, need two copies. The copies needed is the largest
/// number of simultaneous uses, found by assigning uses in start order to the first free copy.
/// </summary>
internal static class ToolRequirementCalculator
{
    internal static IReadOnlyList<ToolRequirement> Calculate(IEnumerable<ToolUse> uses) =>
        uses
            .Where(use => use.EndsAt > use.StartsAt)
            .GroupBy(use => (use.MaterialGroup, Key: ToolNaming.Key(use.ToolName), Diameter: Round(use.Diameter)))
            .Select(group => Build(group.ToList()))
            .OrderBy(requirement => MaterialGroups.Rank(requirement.MaterialGroup))
            .ThenBy(requirement => requirement.ToolType, StringComparer.Ordinal)
            .ThenBy(requirement => requirement.Diameter ?? double.MaxValue)
            .ThenBy(requirement => ToolNaming.Key(requirement.ToolName), StringComparer.Ordinal)
            .ToArray();

    private static ToolRequirement Build(List<ToolUse> uses)
    {
        var ordered = uses
            .OrderBy(use => use.StartsAt)
            .ThenBy(use => use.MachineId, StringComparer.Ordinal)
            .ThenBy(use => use.OperationId, StringComparer.Ordinal)
            .ToList();
        var copies = new List<List<ToolCopyStop>>();
        var freeAt = new List<DateTimeOffset>();
        foreach (var use in ordered)
        {
            for (var unit = 0; unit < Math.Max(1, use.Copies); unit++)
            {
                // A free copy already in this Machine stays there; otherwise the one free longest moves.
                var free = Enumerable.Range(0, copies.Count).Where(index => freeAt[index] <= use.StartsAt).ToList();
                var chosen = free.FirstOrDefault(index => copies[index][^1].MachineId == use.MachineId, -1);
                if (chosen < 0 && free.Count > 0) chosen = free.OrderBy(index => freeAt[index]).First();
                if (chosen < 0)
                {
                    copies.Add([]);
                    freeAt.Add(use.EndsAt);
                    chosen = copies.Count - 1;
                }

                var stops = copies[chosen];
                if (stops.Count > 0 && stops[^1].MachineId == use.MachineId)
                {
                    stops[^1] = stops[^1] with { To = Max(stops[^1].To, use.EndsAt) };
                }
                else
                {
                    stops.Add(new ToolCopyStop(use.MachineId, use.MachineLabel, use.StartsAt, use.EndsAt));
                }
                freeAt[chosen] = Max(freeAt[chosen], use.EndsAt);
            }
        }

        var first = ordered[0];
        var typed = ordered.FirstOrDefault(use => use.ToolTypeSource == "catalog") ?? first;
        return new ToolRequirement(
            first.MaterialGroup,
            typed.ToolType,
            typed.ToolTypeSource,
            Round(first.Diameter),
            first.ToolName,
            copies.Count,
            ordered.Select(use => use.MachineLabel).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            copies.Sum(stops => stops.Count - 1),
            ordered.Min(use => use.StartsAt),
            ordered.Max(use => use.EndsAt),
            copies.Select((stops, index) => new ToolCopyRoute(index + 1, stops)).ToArray(),
            ordered);
    }

    private static double? Round(double? value) => value is { } number ? Math.Round(number, 3) : null;

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;
}
