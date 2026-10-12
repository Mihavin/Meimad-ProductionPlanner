using System.Globalization;
using Meimad.Planner.SimulationPackage.Tools;

namespace Meimad.CimatronExporter;

/// <summary>
/// Reads Cimatron's <c>ExportCuttersAndHoldersToCSV</c> files (layout in <see cref="CimatronCsvTable"/>). Cutters: 1101 name,
/// 1201 magazine number (the T number in the NC), 2102 tip type, 2105 diameter, 2106 corner radius, 2109 clear length (the
/// stick-out, as the Server reads Cimatron reports), 2108 full cutter length, 2110 cut length, 2111/2112 taper and taper
/// angle, 3101 holder name.
/// </summary>
internal static class CimatronToolCsv
{
    public static IReadOnlyList<CimatronTool> Read(string cuttersPath, string? holdersPath)
    {
        if (CimatronCsvTable.Read(File.ReadAllLines(cuttersPath), "1101") is not var (cutters, scale)) return [];
        var holders = holdersPath is not null && File.Exists(holdersPath) ? Holders(holdersPath) : new(StringComparer.OrdinalIgnoreCase);
        var tools = new List<CimatronTool>();
        foreach (var row in cutters)
        {
            if (!int.TryParse(CimatronCsvTable.Value(row, "1201"), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0) continue;
            var name = CimatronCsvTable.Value(row, "1101") ?? $"T{number}";
            var taper = CimatronCsvTable.Value(row, "2111") == "1";
            var holderName = CimatronCsvTable.Value(row, "3101");
            (double? Diameter, double? Length) holder = holderName is not null && holders.TryGetValue(holderName, out var found) ? found : (null, null);
            tools.Add(new CimatronTool(
                number,
                name,
                taper ? "taper" : TipType(CimatronCsvTable.Value(row, "2102")),
                CimatronCsvTable.Length(row, "2105", scale),
                CimatronCsvTable.Length(row, "2106", scale),
                CimatronCsvTable.Length(row, "2110", scale),
                CimatronCsvTable.Length(row, "2109", scale) ?? CimatronCsvTable.Length(row, "2108", scale),
                holderName,
                holder.Diameter,
                holder.Length,
                taper ? CimatronCsvTable.Number(CimatronCsvTable.Value(row, "2112")) : null));
        }

        return tools;
    }

    /// <summary>Holder name → (largest stage diameter, sum of the holder stages' total heights), in millimetres.</summary>
    internal static Dictionary<string, (double? Diameter, double? Length)> Holders(string path)
    {
        var result = new Dictionary<string, (double? Diameter, double? Length)>(StringComparer.OrdinalIgnoreCase);
        if (CimatronCsvTable.Read(File.ReadAllLines(path), "7001") is not var (rows, scale)) return result;
        foreach (var row in rows)
        {
            if (CimatronCsvTable.Value(row, "7001") is not { } name || result.ContainsKey(name)) continue;
            var (diameter, length, _) = CimatronCsvTable.Holder(row, scale);
            result[name] = (diameter, length);
        }

        return result;
    }

    // Option IDs of column 2102 "Tip / Type".
    private static string? TipType(string? code) => code switch
    {
        "210201" => "flat",
        "210202" => "ball",
        "210203" => "bull",
        "210204" => "drill",
        "210205" => "ream",
        "210206" => "tap",
        "210207" => "center",
        "210208" => "full radius",
        "210209" => "corner radius",
        "210210" => "cylinder",
        _ => null
    };
}
