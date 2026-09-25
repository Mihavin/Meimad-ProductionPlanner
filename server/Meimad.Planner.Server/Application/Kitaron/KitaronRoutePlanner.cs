using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meimad.Planner.Server.Application.Kitaron;

/// <summary>
/// Turns the Kitaron route master into Meimad route facts (OD-038). One route header is chosen per
/// part; its steps are ordered as Kitaron orders them and classified by the Setup decision for their
/// station: MACHINE steps become Case Operations, WORKSTATION and EXTERNAL steps become auxiliary
/// requirements of the nearest machining step (before the first one: BACKWARD chained towards the
/// Machine; after one: FORWARD chained away from it), IGNORE steps vanish, and UNDECIDED steps are
/// skipped and reported. Pure and deterministic so the rules are unit-testable.
/// </summary>
internal static class KitaronRoutePlanner
{
    internal const string RequirementKeyMarker = "step";

    internal sealed record Result(
        IReadOnlyList<KitaronSyncOperation> Operations,
        IReadOnlyList<KitaronSyncRequirement> Requirements,
        IReadOnlySet<string> PartsWithRoute,
        int StepsSkipped);

    internal static Result Plan(
        IReadOnlyList<KitaronSourceRouteStep> steps,
        IReadOnlyDictionary<int, KitaronStationRecord> stations,
        IReadOnlySet<string> parts,
        IReadOnlySet<string> parentParts,
        ICollection<string> warnings)
    {
        var operations = new List<KitaronSyncOperation>();
        var requirements = new List<KitaronSyncRequirement>();
        var partsWithRoute = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        var undecidedStations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unknownStationSteps = 0;

        foreach (var partGroup in steps
                     .Where(step => parts.Contains(step.PartNumber))
                     .GroupBy(step => step.PartNumber, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var part = partGroup.Key;
            var route = ChooseHeader(partGroup);
            if (route.Count == 0) continue;
            partsWithRoute.Add(part);
            if (parentParts.Contains(part))
            {
                AddWarning(warnings, $"{part} is a parent Case; its Kitaron route steps were skipped.");
                continue;
            }

            var ordered = OrderSteps(route, part, warnings);
            var classified = new List<ClassifiedStep>();
            foreach (var step in ordered)
            {
                if (step.StationId is null || !stations.TryGetValue(step.StationId.Value, out var station))
                {
                    unknownStationSteps++;
                    skipped++;
                    continue;
                }
                switch (station.ImportRole)
                {
                    case KitaronStationRoles.Machine:
                    case KitaronStationRoles.Workstation:
                    case KitaronStationRoles.External:
                        classified.Add(new ClassifiedStep(step, station));
                        break;
                    case KitaronStationRoles.Ignore:
                        break;
                    default:
                        undecidedStations[station.StationName] = undecidedStations.GetValueOrDefault(station.StationName) + 1;
                        skipped++;
                        break;
                }
            }

            if (!classified.Any(item => item.Station.ImportRole == KitaronStationRoles.Machine))
            {
                if (classified.Count > 0)
                {
                    AddWarning(warnings,
                        $"{part}: the Kitaron route has no machining step, so its {classified.Count} auxiliary step(s) were not imported.");
                    skipped += classified.Count;
                }
                continue;
            }

            // The machining steps form one sequence: each operation follows the one before it.
            var machiningIndex = 0;
            string? previousOperationKey = null;
            foreach (var item in classified.Where(item => item.Station.ImportRole == KitaronStationRoles.Machine))
            {
                var step = item.Step;
                var number = ActionNumber(step)!.Value;
                var name = StepName(step, number);
                var key = OperationKey(part, number);
                var setup = Seconds(step.DirectionTimeMinutes);
                var cycle = Seconds(step.TimeProductionMinutes);
                operations.Add(new KitaronSyncOperation(
                    key, part, number, machiningIndex, name, item.Station.MachineType, setup, cycle,
                    Hash(key, machiningIndex, name, item.Station.MachineType, setup, cycle, previousOperationKey),
                    previousOperationKey));
                previousOperationKey = key;
                machiningIndex++;
            }

            // Auxiliary steps attach to the nearest machining step: the ones before the first
            // machining step are BACKWARD from it (the last of them anchored to the Machine start,
            // earlier ones chained before it); the ones after a machining step are FORWARD from it.
            var pending = new List<ClassifiedStep>();
            string? lastMachining = null;
            string? lastForwardKey = null;
            var sequence = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in classified)
            {
                if (item.Station.ImportRole == KitaronStationRoles.Machine)
                {
                    var operationKey = OperationKey(part, ActionNumber(item.Step)!.Value);
                    if (lastMachining is null)
                    {
                        string? predecessor = null;
                        for (var index = pending.Count - 1; index >= 0; index--)
                        {
                            var requirement = Requirement(part, operationKey, pending[index], "BACKWARD", predecessor, sequence);
                            requirements.Add(requirement);
                            predecessor = requirement.SourceKey;
                        }
                        pending.Clear();
                    }
                    lastMachining = operationKey;
                    lastForwardKey = null;
                    continue;
                }

                if (lastMachining is null)
                {
                    pending.Add(item);
                    continue;
                }
                var forward = Requirement(part, lastMachining, item, "FORWARD", lastForwardKey, sequence);
                requirements.Add(forward);
                lastForwardKey = forward.SourceKey;
            }
        }

        foreach (var (station, count) in undecidedStations.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            AddWarning(warnings,
                $"Kitaron station '{station}' has no import role yet; {count} route step(s) were skipped. Decide it in Setup, Kitaron Stations.");
        }
        if (unknownStationSteps > 0)
            AddWarning(warnings, $"{unknownStationSteps} Kitaron route step(s) reference a station that does not exist and were skipped.");

        // Requirements stay in construction order, which is predecessor-first per chain; the apply
        // relies on that to resolve predecessor ids in one pass.
        return new Result(
            operations.OrderBy(item => item.SourceKey, StringComparer.OrdinalIgnoreCase).ToArray(),
            requirements,
            partsWithRoute,
            skipped);
    }

    internal static string OperationKey(string part, int operationNumber) => $"{part}\u001f{operationNumber}";

    internal static string RequirementKey(string part, int stepNumber) => $"{part}\u001f{RequirementKeyMarker}\u001f{stepNumber}";

    /// <summary>
    /// One route per part: the header for the part's current revision wins, then a chart master,
    /// then a master header, then an unexpired one, then the newest header.
    /// </summary>
    private static IReadOnlyList<KitaronSourceRouteStep> ChooseHeader(IEnumerable<KitaronSourceRouteStep> steps)
    {
        var headers = steps.GroupBy(step => step.DirectionHeaderId).ToArray();
        if (headers.Length == 0) return [];
        var chosen = headers
            .OrderByDescending(header => RevisionMatches(header.First()) ? 1 : 0)
            .ThenByDescending(header => header.First().ChartMaster ? 1 : 0)
            .ThenByDescending(header => header.First().IsMaster ? 1 : 0)
            .ThenByDescending(header => header.First().ExpiredDate is null ? 1 : 0)
            .ThenByDescending(header => header.Key)
            .First();
        return chosen.ToArray();
    }

    private static bool RevisionMatches(KitaronSourceRouteStep step) =>
        !string.IsNullOrWhiteSpace(step.PartRevision)
        && string.Equals(step.PartRevision.Trim(), step.HeaderRevision?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The process sequence of a Kitaron route is its operation number (ActionNumber 10, 20, 30,
    /// ...). NumOrder is not: in 4,157 of the 4,335 commissioned routes it disagrees with the
    /// operation numbers, for example the purchasing step 10 of 16W1120-13 has NumOrder 27. The row
    /// id breaks ties. Rows without a numeric ActionNumber and repeated ActionNumbers cannot become
    /// stable Meimad identities and are reported.
    /// </summary>
    private static IReadOnlyList<KitaronSourceRouteStep> OrderSteps(
        IReadOnlyList<KitaronSourceRouteStep> steps, string part, ICollection<string> warnings)
    {
        var ordered = steps
            .OrderBy(step => ActionNumber(step) ?? int.MaxValue)
            .ThenBy(step => step.DirectionId)
            .ToList();
        var result = new List<KitaronSourceRouteStep>(ordered.Count);
        var seen = new HashSet<int>();
        var invalid = 0;
        var duplicates = 0;
        foreach (var step in ordered)
        {
            var number = ActionNumber(step);
            if (number is null) { invalid++; continue; }
            if (!seen.Add(number.Value)) { duplicates++; continue; }
            result.Add(step);
        }
        if (invalid > 0)
            AddWarning(warnings, $"{part}: {invalid} Kitaron route step(s) have no positive numeric ActionNumber and were skipped.");
        if (duplicates > 0)
            AddWarning(warnings, $"{part}: {duplicates} Kitaron route step(s) repeat an ActionNumber; only the first was kept.");
        return result;
    }

    private static KitaronSyncRequirement Requirement(
        string part, string operationKey, ClassifiedStep item, string direction, string? predecessorKey,
        IDictionary<string, int> sequence)
    {
        var step = item.Step;
        var station = item.Station;
        var number = ActionNumber(step)!.Value;
        var position = sequence.TryGetValue(operationKey, out var used) ? used : 0;
        sequence[operationKey] = position + 1;
        var isExternal = station.ImportRole == KitaronStationRoles.External;
        var perBatch = isExternal ? 0 : Seconds(step.DirectionTimeMinutes) ?? Seconds(station.DefaultMinutesPerBatch) ?? 0;
        var perUnit = isExternal ? 0 : Seconds(step.TimeProductionMinutes) ?? Seconds(station.DefaultMinutesPerPart) ?? 0;
        var key = RequirementKey(part, number);
        var name = StepName(step, number);
        return new KitaronSyncRequirement(
            key, part, operationKey, number, position, name,
            isExternal ? "EXTERNAL" : "WORKSTATION",
            isExternal ? null : station.WorkstationTypeId,
            isExternal ? station.ExternalResourceId : null,
            isExternal ? 1 : station.CapacityRequired,
            perBatch, perUnit, direction, predecessorKey,
            Hash(key, operationKey, number, position, name, isExternal, station.WorkstationTypeId,
                station.ExternalResourceId, station.CapacityRequired, perBatch, perUnit, direction, predecessorKey));
    }

    private static string StepName(KitaronSourceRouteStep step, int number)
    {
        var description = KitaronTextNormalization.Clean(step.Description)
            ?? KitaronTextNormalization.Clean(step.OperationName);
        var name = description ?? $"Operation {number}";
        return name.Length <= 200 ? name : name[..200];
    }

    internal static int? ActionNumber(KitaronSourceRouteStep step) =>
        int.TryParse(step.ActionNumber?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;

    private static int? Seconds(double? minutes) =>
        minutes is > 0 && double.IsFinite(minutes.Value) && minutes.Value * 60 <= int.MaxValue
            ? (int)Math.Round(minutes.Value * 60, MidpointRounding.AwayFromZero)
            : null;

    private static void AddWarning(ICollection<string> warnings, string message)
    {
        if (warnings.Count < 500) warnings.Add(message);
    }

    private static string Hash(params object?[] values) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values)))).ToLowerInvariant();

    private sealed record ClassifiedStep(KitaronSourceRouteStep Step, KitaronStationRecord Station);
}
