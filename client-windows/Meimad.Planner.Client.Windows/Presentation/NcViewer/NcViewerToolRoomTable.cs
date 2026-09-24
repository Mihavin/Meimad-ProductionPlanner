using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation.NcViewer;

/// <summary>One Tool Room tool as the NC engine's tool table understands it (millimetres).</summary>
/// <param name="Number">The program's T number (from the released tool identifier, else the offset number).</param>
/// <param name="ShapeKnown">False for shape OTHER: the engine keeps the type it inferred from the program.</param>
internal sealed record NcViewerToolRoomTool(
    int Number,
    string Identifier,
    string Description,
    bool ShapeKnown,
    string MillType,
    string LatheType,
    double? Diameter,
    double? Length,
    double CornerRadius);

/// <summary>
/// The Tool Room's saved tool table (measured length and diameter, cutter shape) for the NC
/// viewer. It replaces the values the viewer would otherwise infer from program comments, so the
/// toolpath simulation cuts with the prepared tools. Tools the program does not use are ignored.
/// </summary>
internal sealed record NcViewerToolRoomTable(string Source, int Version, IReadOnlyList<NcViewerToolRoomTool> Tools)
{
    internal static NcViewerToolRoomTable? From(PlannerToolPreparation? preparation)
    {
        if (preparation is null || preparation.Version == 0) return null;
        var tools = new List<NcViewerToolRoomTool>();
        foreach (var tool in preparation.Tools)
        {
            var number = ToolNumber(tool.ToolIdentifier) ?? tool.OffsetNumber;
            if (number is null) continue;
            var shape = tool.ShapeType.ToUpperInvariant();
            var diameter = tool.MeasuredDiameter ?? Value(tool.Shape, "cuttingDiameter");
            var length = tool.MeasuredLength ?? Value(tool.Shape, "overallLength");
            var shapeKnown = shape != "OTHER";
            if (diameter is null && length is null && !shapeKnown) continue; // nothing prepared yet
            tools.Add(new NcViewerToolRoomTool(
                number.Value, tool.ToolIdentifier, tool.Description, shapeKnown,
                MillType(shape), LatheType(shape), diameter, length, CornerRadius(shape, tool.Shape, diameter)));
        }
        return tools.Count == 0
            ? null
            : new NcViewerToolRoomTable(
                string.Create(CultureInfo.InvariantCulture, $"Tool Room tool table v{preparation.Version}"),
                preparation.Version, tools);
    }

    /// <summary>
    /// The engine's editable table with the Tool Room values written into the program's tools, in
    /// the table's units. <paramref name="warnings"/> names the program tools the Tool Room has
    /// not described; their inferred values stay.
    /// </summary>
    internal JsonObject ApplyTo(JsonElement editable, out IReadOnlyList<string> warnings)
    {
        var node = JsonNode.Parse(editable.GetRawText())!.AsObject();
        var lathe = string.Equals(Text(node["machineType"]), "lathe", StringComparison.Ordinal);
        var scale = string.Equals(Text(node["units"]), "inch", StringComparison.Ordinal) ? 1 / 25.4 : 1;
        var allowedTypes = (node["toolTypes"] as JsonArray)?
            .Select(Text).Where(type => type is not null).Select(type => type!)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var byNumber = Tools.GroupBy(tool => tool.Number).ToDictionary(group => group.Key, group => group.First());
        var unmatched = new List<string>();
        foreach (var row in (node["tools"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var number = Number(row["number"]);
            if (number is null) continue;
            if (!byNumber.TryGetValue(number.Value, out var tool))
            {
                unmatched.Add($"T{number.Value}");
                continue;
            }
            var type = lathe ? tool.LatheType : tool.MillType;
            if (tool.ShapeKnown && (allowedTypes.Count == 0 || allowedTypes.Contains(type)))
            {
                row["type"] = type;
                row["cornerRadius"] = Round(tool.CornerRadius * scale);
            }
            if (tool.Diameter is { } diameter) row["diameter"] = Round(diameter * scale);
            if (tool.Length is { } length) row["length"] = Round(length * scale);
            if (tool.Description.Length > 0) row["description"] = tool.Description;
        }
        warnings = unmatched.Count == 0
            ? []
            : [$"{Source} has no entry for {string.Join(", ", unmatched)}; the viewer keeps the values inferred from the program."];
        return node;
    }

    private static int? ToolNumber(string identifier)
    {
        var digits = new string(identifier.Trim().SkipWhile(character => !char.IsDigit(character)).TakeWhile(char.IsDigit).ToArray());
        return digits.Length is > 0 and <= 4 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : null;
    }

    private static string MillType(string shape) => shape switch
    {
        "END_MILL" => "end-mill",
        "BALL_END_MILL" => "ball-mill",
        "BULL_NOSE_END_MILL" => "bull-nose-mill",
        "CHAMFER_MILL" => "chamfer-mill",
        "FACE_MILL" => "face-mill",
        "DRILL" => "drill",
        "TAP" => "tap",
        "REAMER" => "reamer",
        "BORING_BAR" => "boring-head",
        "PROBE" => "probe",
        _ => "other"
    };

    private static string LatheType(string shape) => shape switch
    {
        "TURNING_TOOL" => "external-cutter",
        "BORING_BAR" => "internal-cutter",
        "DRILL" => "drill",
        "TAP" => "tap",
        "REAMER" => "reamer",
        "PROBE" => "non-cutting",
        _ => "other"
    };

    private static double CornerRadius(string shape, IReadOnlyDictionary<string, double> dimensions, double? diameter) => shape switch
    {
        "BALL_END_MILL" => Round((diameter ?? 0) / 2),
        "BULL_NOSE_END_MILL" or "TURNING_TOOL" or "BORING_BAR" => Math.Max(0, Value(dimensions, "cornerRadius") ?? 0),
        _ => 0
    };

    private static double? Value(IReadOnlyDictionary<string, double> dimensions, string key) =>
        dimensions.TryGetValue(key, out var value) && value > 0 && double.IsFinite(value) ? value : null;

    private static double Round(double value) => Math.Round(value, 4);

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) && number >= 1 && number <= 999 && Math.Abs(number - Math.Round(number)) < 0.0001
            ? (int)Math.Round(number)
            : null;
}
