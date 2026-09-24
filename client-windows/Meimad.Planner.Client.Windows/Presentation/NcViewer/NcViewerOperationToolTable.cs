using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.NcEngine;

namespace Meimad.Planner.Client.Windows.Presentation.NcViewer;

/// <summary>One released tool of the Operation's tool table as the NC engine understands it (millimetres).</summary>
/// <param name="Number">The program's T number (from the released tool identifier, else the offset number).</param>
/// <param name="ShapeKnown">True when the Tool Room described the cutter shape; false leaves the type to the description.</param>
internal sealed record NcViewerOperationTool(
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
/// The Operation's released tool table for the NC viewer, with the Tool Room's cutter shapes and
/// measurements when they exist. It replaces the values the viewer would otherwise read from the
/// program's comments: descriptions come from the table, type and diameter from the Tool Room
/// shape (or, for rows without one, from the engine's reading of the table description).
/// Tools the program does not use are ignored.
/// </summary>
internal sealed record NcViewerOperationToolTable(
    string Source,
    int Revision,
    int PreparationVersion,
    IReadOnlyList<NcViewerOperationTool> Tools)
{
    /// <summary>The Tool Room's table of a Batch Operation: every released row plus its measurements and shape.</summary>
    internal static NcViewerOperationToolTable? From(PlannerToolPreparation? preparation)
    {
        if (preparation is null) return null;
        var tools = new List<NcViewerOperationTool>();
        foreach (var tool in preparation.Tools)
        {
            var number = ToolNumber(tool.ToolIdentifier) ?? tool.OffsetNumber;
            if (number is null) continue;
            var shape = tool.ShapeType.ToUpperInvariant();
            var shapeKnown = shape != "OTHER";
            var diameter = tool.MeasuredDiameter ?? Value(tool.Shape, "cuttingDiameter");
            var length = tool.MeasuredLength ?? Value(tool.Shape, "overallLength");
            tools.Add(new NcViewerOperationTool(
                number.Value, tool.ToolIdentifier, tool.Description.Trim(), shapeKnown,
                MillType(shape), LatheType(shape), diameter, length, CornerRadius(shape, tool.Shape, diameter)));
        }
        if (tools.Count == 0) return null;
        var source = string.Create(CultureInfo.InvariantCulture,
            $"Tool table r{preparation.ToolTableRevision} ({preparation.ToolTableFileName})");
        if (preparation.Version > 0)
        {
            source = string.Create(CultureInfo.InvariantCulture, $"{source} + Tool Room v{preparation.Version}");
        }
        return new NcViewerOperationToolTable(source, preparation.ToolTableRevision, preparation.Version, tools);
    }

    /// <summary>The released rows of a tool table alone (no Batch Operation, so no Tool Room measurements).</summary>
    internal static NcViewerOperationToolTable? FromRows(IReadOnlyList<PlannerReleasedTool>? rows, int revision, string fileName)
    {
        if (rows is null) return null;
        var tools = new List<NcViewerOperationTool>();
        foreach (var row in rows.Where(row => row.IsActive).OrderBy(row => row.RowNumber))
        {
            var number = ToolNumber(row.ToolIdentifier);
            if (number is null) continue;
            tools.Add(new NcViewerOperationTool(number.Value, row.ToolIdentifier, row.Description.Trim(), false, "other", "other", null, null, 0));
        }
        return tools.Count == 0
            ? null
            : new NcViewerOperationToolTable(
                string.Create(CultureInfo.InvariantCulture, $"Tool table r{revision} ({fileName})"), revision, 0, tools);
    }

    /// <summary>The rows of the tool table a release was made with, from the Operation's G-code catalog.</summary>
    internal static NcViewerOperationToolTable? FromCatalog(PlannerGCodeCatalog? catalog, string? toolTableReleaseId)
    {
        if (catalog is null || toolTableReleaseId is null) return null;
        var revisions = new List<PlannerProcessRevision>(catalog.ProcessRevisions);
        if (catalog.ActiveProcessRevision is not null) revisions.Add(catalog.ActiveProcessRevision);
        var table = revisions.Select(revision => revision.ToolTable)
            .FirstOrDefault(value => value is not null && value.ToolTableReleaseId == toolTableReleaseId);
        return table is null ? null : FromRows(table.Tools, table.RevisionNumber, table.OriginalFileName);
    }

    /// <summary>The rows whose type or diameter must come from the engine's reading of the description.</summary>
    internal IReadOnlyList<NcEngineToolDescription> DescriptionsToInfer =>
        Tools.Where(tool => (!tool.ShapeKnown || tool.Diameter is null) && tool.Description.Length > 0)
            .Select(tool => new NcEngineToolDescription(tool.Number, tool.Description))
            .ToArray();

    /// <summary>
    /// The engine's editable table with the Operation's tools written into the program's rows, in
    /// the table's units. <paramref name="inferred"/> holds the engine's reading of the table
    /// descriptions (type, diameter, corner radius) for rows the Tool Room has not described.
    /// <paramref name="warnings"/> names the program tools the table does not list; their values
    /// inferred from the program stay.
    /// </summary>
    internal JsonObject ApplyTo(JsonElement editable, IReadOnlyDictionary<int, NcEngineInferredTool>? inferred, out IReadOnlyList<string> warnings)
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
            NcEngineInferredTool? reading = null;
            if (inferred is not null && inferred.TryGetValue(number.Value, out var found)) reading = found;
            if (tool.Description.Length > 0) row["description"] = tool.Description;
            if (tool.ShapeKnown)
            {
                var type = lathe ? tool.LatheType : tool.MillType;
                if (allowedTypes.Count == 0 || allowedTypes.Contains(type)) row["type"] = type;
                row["cornerRadius"] = Round(tool.CornerRadius * scale);
            }
            else if (reading is not null)
            {
                if (reading.Type != "other" && (allowedTypes.Count == 0 || allowedTypes.Contains(reading.Type))) row["type"] = reading.Type;
                if (reading.CornerRadius > 0) row["cornerRadius"] = Round(reading.CornerRadius * scale);
                if (lathe && reading.Tip > 0) row["tip"] = reading.Tip;
                if (reading.Width is { } width) row["width"] = Round(width * scale);
            }
            var diameter = tool.Diameter ?? reading?.Diameter;
            if (diameter is { } value) row["diameter"] = Round(value * scale);
            var length = tool.Length ?? reading?.Length;
            if (length is { } measured) row["length"] = Round(measured * scale);
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
