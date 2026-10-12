using System.Globalization;

namespace Meimad.Planner.SimulationPackage.Tools;

/// <summary>
/// Cimatron's <c>ExportCuttersAndHoldersToCSV</c> layout (Cimatron 2026.0 at Meimad, 2026-10-12): UTF-16, fields separated
/// by <c>|</c>, <c>//</c> lines with validation data and the units, a row of column names, then a row of column IDs, then one
/// row per cutter or holder. Columns are found by their ID, never by position. Holders: 7001 name, 7003 number of holder
/// stages (spindle stages follow them), per stage s the IDs 7000 + 10·s + 1…4 = lower diameter, upper diameter, cone height,
/// total height.
/// </summary>
public static class CimatronCsvTable
{
    /// <summary>
    /// The data rows keyed by column ID and the factor to millimetres (inch files 25.4), or null when the file is not in
    /// Cimatron's layout (no column-ID row starting with <paramref name="firstColumnId"/>).
    /// </summary>
    public static (IReadOnlyList<IReadOnlyDictionary<string, string>> Rows, double Scale)? Read(IEnumerable<string> lines, string firstColumnId)
    {
        var scale = 1.0;
        string[]? ids = null;
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var line in lines)
        {
            var fields = line.Split('|').Select(field => field.Trim().Trim('"')).ToArray();
            if (fields.All(field => field.Length == 0)) continue;
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

        return ids is null ? null : (rows, scale);
    }

    public static string? Value(IReadOnlyDictionary<string, string> row, string id) =>
        row.TryGetValue(id, out var value) && value.Length > 0 ? value : null;

    public static double? Number(string? text)
    {
        if (text is null) return null;
        var normalized = text.Contains(',') && !text.Contains('.') ? text.Replace(',', '.') : text;
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>A positive length in millimetres, or null.</summary>
    public static double? Length(IReadOnlyDictionary<string, string> row, string id, double scale) =>
        Number(Value(row, id)) is { } value and > 0 ? value * scale : null;

    /// <summary>
    /// A holder row's largest stage diameter and the sum of its holder stages' total heights, and whether every holder
    /// stage has a diameter and a height.
    /// </summary>
    public static (double? Diameter, double? Length, bool Complete) Holder(IReadOnlyDictionary<string, string> row, double scale)
    {
        var declared = int.TryParse(Value(row, "7003"), NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is > 0 and <= 20;
        var stages = declared ? count : 20;
        double? diameter = null, length = null;
        var complete = declared;
        for (var stage = 1; stage <= stages; stage++)
        {
            var id = 7000 + 10 * stage;
            var lower = Length(row, (id + 1).ToString(CultureInfo.InvariantCulture), scale) ?? 0;
            var upper = Length(row, (id + 2).ToString(CultureInfo.InvariantCulture), scale) ?? 0;
            var total = Length(row, (id + 4).ToString(CultureInfo.InvariantCulture), scale) ?? 0;
            if (total <= 0 && lower <= 0 && upper <= 0)
            {
                if (declared) complete = false;
                continue;
            }

            if (total <= 0 || Math.Max(lower, upper) <= 0) complete = false;
            diameter = Math.Max(diameter ?? 0, Math.Max(lower, upper));
            length = (length ?? 0) + total;
        }

        return (diameter is > 0 ? diameter : null, length is > 0 ? length : null, complete && diameter is > 0 && length is > 0);
    }
}
