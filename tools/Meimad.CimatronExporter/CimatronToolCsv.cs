using System.Globalization;

namespace Meimad.CimatronExporter;

/// <summary>
/// Reads Cimatron's <c>ExportCuttersAndHoldersToCSV</c> files (Cimatron 2026.0 at Meimad, 2026-10-12): UTF-16, fields
/// separated by <c>|</c>, <c>//</c> lines with validation data and the units, a row of column names, then a row of column IDs,
/// then one row per cutter or holder. Columns are found by their ID, never by position.
/// Cutters: 1101 name, 1201 magazine number (the T number in the NC), 2102 tip type, 2105 diameter, 2106 corner radius,
/// 2109 clear length (the stick-out, as the Server reads Cimatron reports), 2108 full cutter length, 2110 cut length,
/// 2111/2112 taper and taper angle, 3101 holder name.
/// Holders: 7001 name, 7003 number of holder stages (the spindle stages follow them), per stage s the IDs
/// 7000 + 10·s + 1…4 = lower diameter, upper diameter, cone height, total height.
/// </summary>
internal static class CimatronToolCsv
{
    public static IReadOnlyList<CimatronTool> Read(string cuttersPath, string? holdersPath)
    {
        var (cutters, scale) = Table(cuttersPath, "1101");
        var holders = holdersPath is not null && File.Exists(holdersPath) ? Holders(holdersPath) : new(StringComparer.OrdinalIgnoreCase);
        var tools = new List<CimatronTool>();
        foreach (var row in cutters)
        {
            if (!int.TryParse(Value(row, "1201"), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0) continue;
            var name = Value(row, "1101") ?? $"T{number}";
            var taper = Value(row, "2111") == "1";
            var holderName = Value(row, "3101");
            (double? Diameter, double? Length) holder = holderName is not null && holders.TryGetValue(holderName, out var found) ? found : (null, null);
            tools.Add(new CimatronTool(
                number,
                name,
                taper ? "taper" : TipType(Value(row, "2102")),
                Length(row, "2105", scale),
                Length(row, "2106", scale),
                Length(row, "2110", scale),
                Length(row, "2109", scale) ?? Length(row, "2108", scale),
                holderName,
                holder.Diameter,
                holder.Length,
                taper ? Number(Value(row, "2112")) : null));
        }

        return tools;
    }

    /// <summary>Holder name → (largest stage diameter, sum of the holder stages' total heights), in millimetres.</summary>
    internal static Dictionary<string, (double? Diameter, double? Length)> Holders(string path)
    {
        var (rows, scale) = Table(path, "7001");
        var result = new Dictionary<string, (double? Diameter, double? Length)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (Value(row, "7001") is not { } name || result.ContainsKey(name)) continue;
            var stages = int.TryParse(Value(row, "7003"), NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is > 0 and <= 20 ? count : 20;
            double? diameter = null, length = null;
            for (var stage = 1; stage <= stages; stage++)
            {
                var id = 7000 + 10 * stage;
                var lower = Length(row, (id + 1).ToString(CultureInfo.InvariantCulture), scale) ?? 0;
                var upper = Length(row, (id + 2).ToString(CultureInfo.InvariantCulture), scale) ?? 0;
                var total = Length(row, (id + 4).ToString(CultureInfo.InvariantCulture), scale) ?? 0;
                if (total <= 0 && lower <= 0 && upper <= 0) continue;
                diameter = Math.Max(diameter ?? 0, Math.Max(lower, upper));
                length = (length ?? 0) + total;
            }

            result[name] = (diameter is > 0 ? diameter : null, length is > 0 ? length : null);
        }

        return result;
    }

    /// <summary>The data rows keyed by column ID, and the factor to millimetres (inch files are scaled by 25.4).</summary>
    internal static (List<Dictionary<string, string>> Rows, double Scale) Table(string path, string firstColumnId)
    {
        var lines = File.ReadAllLines(path); // the byte order mark tells UTF-16 from UTF-8
        var scale = 1.0;
        string[]? ids = null;
        var rows = new List<Dictionary<string, string>>();
        foreach (var line in lines)
        {
            var fields = line.Split('|').Select(field => field.Trim().Trim('"')).ToArray();
            if (fields.Length == 0 || fields.All(field => field.Length == 0)) continue;
            if (fields[0].StartsWith("//", StringComparison.Ordinal))
            {
                // The validation row lists "Units: | mm | inch"; the real units row comes later and wins.
                var units = Array.FindIndex(fields, field => field.Equals("Units:", StringComparison.OrdinalIgnoreCase));
                if (units >= 0 && units + 1 < fields.Length)
                    scale = fields[units + 1].Equals("inch", StringComparison.OrdinalIgnoreCase) ? 25.4 : 1.0;
                continue;
            }

            if (ids is null)
            {
                if (fields[0] == firstColumnId && fields.All(field => field.Length == 0 || field.All(char.IsAsciiDigit))) ids = fields;
                continue;
            }

            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < Math.Min(ids.Length, fields.Length); index++)
                if (ids[index].Length > 0 && fields[index].Length > 0) row.TryAdd(ids[index], fields[index]);
            if (row.Count > 0) rows.Add(row);
        }

        return (rows, scale);
    }

    private static string? Value(IReadOnlyDictionary<string, string> row, string id) =>
        row.TryGetValue(id, out var value) && value.Length > 0 ? value : null;

    private static double? Number(string? text)
    {
        if (text is null) return null;
        var normalized = text.Contains(',') && !text.Contains('.') ? text.Replace(',', '.') : text;
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static double? Length(IReadOnlyDictionary<string, string> row, string id, double scale) =>
        Number(Value(row, id)) is { } value and > 0 ? value * scale : null;

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
