using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meimad.Planner.SimulationPackage.Manifest;

namespace Meimad.Planner.SimulationPackage.Tools;

/// <summary>tools/tool-table.json (contract: <c>Schemas/tool-table.schema.json</c>).</summary>
public sealed record ToolTableDocument
{
    public int SchemaVersion { get; init; } = 1;
    public string Units { get; init; } = PackageUnits.Millimetres;
    public ToolTableSource? Source { get; init; }
    public IReadOnlyList<ToolTableEntry> Tools { get; init; } = [];

    public static ToolTableDocument Parse(string json) =>
        JsonSerializer.Deserialize<ToolTableDocument>(json, ManifestJson.Options)
        ?? throw new JsonException("The tool table is empty.");

    public string Serialize() => JsonSerializer.Serialize(this, ManifestJson.Options);
}

public sealed record ToolTableSource
{
    public string? Application { get; init; }
    public string? NcFileName { get; init; }
    public string? ExportedAtUtc { get; init; }
}

public sealed record ToolTableEntry
{
    public int Number { get; init; }

    /// <summary>"T" + number: makes tool-table.json a structured JSON tool table the Server accepts for an NC release.</summary>
    public string? ToolIdentifier { get; init; }

    /// <summary>The release tool table description (the tool name).</summary>
    public string? Description { get; init; }

    public string Name { get; init; } = "";
    public string? Type { get; init; }
    public string? CutterId { get; init; }
    public double? DiameterMm { get; init; }
    public double? CornerRadiusMm { get; init; }
    public double? FluteLengthMm { get; init; }
    public double? GaugeLengthMm { get; init; }
    public double? TaperAngleDeg { get; init; }
    public ToolHolder? Holder { get; init; }
    public IReadOnlyList<int>? Procedures { get; init; }

    /// <summary>True when the holder is missing or lacks a diameter or length (collision checks need both).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HolderIncomplete => Holder is null || Holder.DiameterMm is null or <= 0 || Holder.LengthMm is null or <= 0;
}

public sealed record ToolHolder
{
    public string? HolderId { get; init; }
    public string? Name { get; init; }
    public double? DiameterMm { get; init; }
    public double? LengthMm { get; init; }
}

/// <summary>What a tool table file says about the tools: their T-numbers and incomplete holders.</summary>
public sealed record ToolTableFacts(IReadOnlyList<int> ToolNumbers, IReadOnlyList<int> ToolsWithIncompleteHolder);

/// <summary>
/// Reads the T-numbers of a tool table file that is not <c>tool-table.json</c> (for example the Cimatron
/// <c>*.TOOLS.mht</c> report, which the Server reads with its released-tool-table parser). Returns null
/// when the format is not one this reader handles.
/// </summary>
public interface IToolTableReader
{
    Task<ToolTableFacts?> ReadAsync(string absolutePath, CancellationToken cancellationToken);
}

/// <summary>
/// Built-in tool table readers: <c>tool-table.json</c> and a simple CSV whose header has a
/// <c>T</c>/<c>Tool</c>/<c>Number</c> column. Other formats go to the injected reader.
/// </summary>
public static partial class ToolTableReaders
{
    public static async Task<ToolTableFacts?> ReadBuiltInAsync(string path, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".json")
        {
            var document = ToolTableDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            return new ToolTableFacts(
                document.Tools.Select(tool => tool.Number).Distinct().Order().ToArray(),
                document.Tools.Where(tool => tool.HolderIncomplete).Select(tool => tool.Number).Distinct().Order().ToArray());
        }

        if (extension == ".csv")
        {
            var lines = await File.ReadAllLinesAsync(path, cancellationToken);
            if (lines.Length == 0) return new ToolTableFacts([], []);
            var header = SplitCsv(lines[0]);
            var column = header.FindIndex(name => name.Trim().ToUpperInvariant() is "T" or "TOOL" or "TOOL NUMBER" or "NUMBER" or "T NUMBER");
            if (column < 0) return null;
            var numbers = new SortedSet<int>();
            foreach (var line in lines.Skip(1))
            {
                var cells = SplitCsv(line);
                if (column >= cells.Count) continue;
                var match = Digits().Match(cells[column]);
                if (match.Success && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0)
                    numbers.Add(number);
            }

            return new ToolTableFacts(numbers.ToArray(), []);
        }

        return null;
    }

    /// <summary>The first 1–5 digit run of a tool identifier such as "T17" or "T17A"; null when none.</summary>
    public static int? ToolNumber(string identifier)
    {
        var match = Digits().Match(identifier);
        return match.Success && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : null;
    }

    /// <summary>
    /// Counts holder rows with a blank or zero dimension in a holders CSV export: Cimatron's own layout
    /// (<see cref="CimatronCsvTable"/>, holders whose stages lack a diameter or height), else a plain CSV with a header row.
    /// </summary>
    public static async Task<int> CountIncompleteHolderRowsAsync(string path, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        if (CimatronCsvTable.Read(lines, "7001") is var (rows, scale))
            return rows.Count(row => CimatronCsvTable.Value(row, "7001") is not null && !CimatronCsvTable.Holder(row, scale).Complete);
        if (lines.Length < 2) return 0;
        var header = SplitCsv(lines[0]);
        var dimensionColumns = header
            .Select((name, index) => (name: name.ToUpperInvariant(), index))
            .Where(item => item.name.Contains("DIAM", StringComparison.Ordinal) || item.name.Contains("LENGTH", StringComparison.Ordinal))
            .Select(item => item.index)
            .ToArray();
        if (dimensionColumns.Length == 0) return 0;
        var incomplete = 0;
        foreach (var line in lines.Skip(1).Where(line => line.Trim().Length > 0))
        {
            var cells = SplitCsv(line);
            if (dimensionColumns.Any(index => index >= cells.Count
                    || !double.TryParse(cells[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    || value <= 0))
            {
                incomplete++;
            }
        }

        return incomplete;
    }

    private static List<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if ((c == ',' || c == ';' || c == '\t') && !quoted)
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        cells.Add(current.ToString().Trim());
        return cells;
    }

    [GeneratedRegex(@"\d{1,5}", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();
}
