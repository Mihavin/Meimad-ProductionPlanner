using System.Collections.Concurrent;
using Meimad.Planner.Server.Application.GCode;
using Meimad.Planner.Server.Application.Timeline;
using Meimad.Planner.Server.Application.ToolCatalog;
using Meimad.Planner.Server.Domain.GCode;
using Meimad.Planner.Server.Domain.ToolRequirements;

namespace Meimad.Planner.Server.Application.ToolRequirements;

/// <summary>A planned operation on the Timeline and where its tools and material come from.</summary>
internal sealed record PlannedOperationSource(
    string OperationId,
    string WorkOrderNumber,
    string PartNumber,
    int OperationNumber,
    string OperationName,
    string? ToolTableReleaseId,
    string? StoredRelativePath,
    string? OriginalFileName,
    string? CaseMaterial,
    string? RawMaterial);

/// <summary>An active row of a released tool table.</summary>
internal sealed record ReleasedToolRow(string ToolTableReleaseId, string ToolIdentifier, string Description);

internal interface IToolRequirementSourceRepository
{
    Task<IReadOnlyList<PlannedOperationSource>> ReadOperationsAsync(
        IReadOnlyCollection<string> operationIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReleasedToolRow>> ReadToolRowsAsync(
        IReadOnlyCollection<string> toolTableReleaseIds, CancellationToken cancellationToken);
}

/// <summary>A planned operation whose tools are unknown because it has no released tool table.</summary>
internal sealed record PlannedOperationWithoutTools(
    string OperationId,
    string WorkOrderNumber,
    string PartNumber,
    int OperationNumber,
    string OperationName,
    string MachineLabel,
    string MaterialGroup,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt);

internal sealed record ToolRequirementReport(
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset CalculatedAt,
    string TimeZoneId,
    int PlannedOperationCount,
    IReadOnlyList<ToolRequirement> Tools,
    IReadOnlyList<PlannedOperationWithoutTools> OperationsWithoutToolTable);

/// <summary>
/// The tools the plan needs in a period. The Timeline gives the operations that hold a Machine in the
/// period; each operation's released tool table (the one it would run now, or the one it runs) gives
/// its tools, with size and holder read again from the stored Cimatron report or CSV/JSON table. The
/// tool catalog gives the type of a tool it knows by Cimatron id or name, the name prefix otherwise,
/// and the part material separates the tools by material group.
/// </summary>
internal sealed class ToolRequirementService(
    TimelineProjectionService timeline,
    IToolRequirementSourceRepository sources,
    IToolCatalogRepository catalog,
    GCodeArtifactStore artifacts,
    TimeProvider timeProvider,
    ILogger<ToolRequirementService> logger)
{
    internal static readonly TimeSpan MaximumPeriod = TimeSpan.FromDays(92);

    // Released tool tables are immutable, so their size columns are read once per release.
    private readonly ConcurrentDictionary<string, IReadOnlyList<ReleasedToolGeometry>> geometry =
        new(StringComparer.Ordinal);

    internal async Task<ToolRequirementReport> CalculateAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var projection = await timeline.CalculateAsync(from, to, timeProvider.GetUtcNow(), cancellationToken);
        var windows = projection.Machines
            .SelectMany(machine => machine.Intervals
                .Where(interval => interval.Type == "operation" && interval.OperationId is not null)
                .Select(interval => (Machine: machine, Interval: interval)))
            .GroupBy(entry => (entry.Machine.MachineId, OperationId: entry.Interval.OperationId!))
            .Select(group => new Window(
                group.Key.MachineId,
                $"{group.First().Machine.Number} {group.First().Machine.Name}".Trim(),
                group.Key.OperationId,
                Max(from, group.Min(entry => entry.Interval.StartsAt)),
                Min(to, group.Max(entry => entry.Interval.EndsAt))))
            .Where(window => window.EndsAt > window.StartsAt)
            .ToArray();

        var operations = (await sources.ReadOperationsAsync(
                windows.Select(window => window.OperationId).Distinct(StringComparer.Ordinal).ToArray(), cancellationToken))
            .ToDictionary(operation => operation.OperationId, StringComparer.Ordinal);
        var rows = (await sources.ReadToolRowsAsync(
                operations.Values.Select(operation => operation.ToolTableReleaseId).OfType<string>()
                    .Distinct(StringComparer.Ordinal).ToArray(),
                cancellationToken))
            .GroupBy(row => row.ToolTableReleaseId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var known = await ReadCatalogAsync(cancellationToken);

        var uses = new List<ToolUse>();
        var withoutTools = new List<PlannedOperationWithoutTools>();
        foreach (var window in windows)
        {
            if (!operations.TryGetValue(window.OperationId, out var operation)) continue;
            var material = string.IsNullOrWhiteSpace(operation.CaseMaterial) ? operation.RawMaterial : operation.CaseMaterial;
            var group = MaterialGroups.Classify(material);
            if (operation.ToolTableReleaseId is not { } releaseId || !rows.TryGetValue(releaseId, out var toolRows))
            {
                withoutTools.Add(new PlannedOperationWithoutTools(
                    operation.OperationId, operation.WorkOrderNumber, operation.PartNumber, operation.OperationNumber,
                    operation.OperationName, window.MachineLabel, group, window.StartsAt, window.EndsAt));
                continue;
            }

            var sizes = (await ReadGeometryAsync(operation, cancellationToken))
                .GroupBy(size => size.ToolIdentifier, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(sizeGroup => sizeGroup.Key, sizeGroup => sizeGroup.First(), StringComparer.OrdinalIgnoreCase);
            var tools = toolRows.Select(row =>
            {
                var size = sizes.GetValueOrDefault(row.ToolIdentifier);
                var name = size?.Name ?? row.Description;
                var entry = known.GetValueOrDefault(ToolNaming.Key(name));
                return (Row: row, Size: size, Name: name, Known: entry,
                    Diameter: size?.Diameter ?? entry?.Diameter ?? ToolNaming.DiameterFromName(name));
            });
            foreach (var tool in tools.GroupBy(tool => (ToolNaming.Key(tool.Name), tool.Diameter is { } value ? Math.Round(value, 3) : (double?)null)))
            {
                var first = tool.First();
                uses.Add(new ToolUse(
                    group,
                    first.Name,
                    first.Diameter,
                    first.Known?.ToolType ?? ToolNaming.TypeFromName(first.Name),
                    first.Known is null ? "name" : "catalog",
                    tool.Count(),
                    tool.Select(entry => entry.Row.ToolIdentifier).ToArray(),
                    first.Size?.Holder,
                    first.Size?.Length,
                    window.MachineId,
                    window.MachineLabel,
                    operation.OperationId,
                    operation.WorkOrderNumber,
                    operation.PartNumber,
                    operation.OperationNumber,
                    operation.OperationName,
                    material,
                    window.StartsAt,
                    window.EndsAt));
            }
        }

        return new ToolRequirementReport(
            from,
            to,
            projection.ReadAt,
            projection.DisplayTimeZoneId,
            windows.Select(window => window.OperationId).Distinct(StringComparer.Ordinal).Count(),
            ToolRequirementCalculator.Calculate(uses),
            withoutTools
                .OrderBy(operation => operation.StartsAt)
                .ThenBy(operation => operation.MachineLabel, StringComparer.Ordinal)
                .ToArray());
    }

    private async Task<IReadOnlyList<ReleasedToolGeometry>> ReadGeometryAsync(
        PlannedOperationSource operation, CancellationToken cancellationToken)
    {
        if (operation.ToolTableReleaseId is not { } releaseId
            || operation.StoredRelativePath is not { } relativePath
            || operation.OriginalFileName is not { } fileName)
        {
            return [];
        }
        if (geometry.TryGetValue(releaseId, out var cached)) return cached;
        try
        {
            var read = await ReleasedToolTableParser.ReadGeometryAsync(
                artifacts.ResolveStoredPath(relativePath), fileName, cancellationToken);
            geometry[releaseId] = read;
            return read;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException or System.Text.Json.JsonException
                                              or GCodeValidationException)
        {
            // Sizes then come from the catalog or the tool name; the list still names every tool.
            logger.LogWarning(exception, "Tool sizes of released tool table {ReleaseId} could not be read.", releaseId);
            return [];
        }
    }

    private async Task<IReadOnlyDictionary<string, KnownTool>> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        var known = new Dictionary<string, KnownTool>(StringComparer.Ordinal);
        foreach (var tool in await catalog.ListAsync(null, null, true, cancellationToken))
        {
            var entry = new KnownTool(
                tool.ToolType,
                tool.Shape.TryGetValue("cuttingDiameter", out var diameter) && diameter > 0 ? diameter : null);
            foreach (var id in tool.ExternalIds.Where(id =>
                         id.System.Equals(CimatronCutterLibrary.ExternalSystem, StringComparison.OrdinalIgnoreCase)))
            {
                known.TryAdd(ToolNaming.Key(id.Value), entry);
            }
            known.TryAdd(ToolNaming.Key(tool.Name), entry);
        }
        return known;
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    private sealed record Window(string MachineId, string MachineLabel, string OperationId, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

    private sealed record KnownTool(string ToolType, double? Diameter);
}
