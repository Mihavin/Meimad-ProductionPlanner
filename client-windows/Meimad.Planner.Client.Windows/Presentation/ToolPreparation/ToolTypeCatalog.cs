using System.Collections.ObjectModel;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation.ToolPreparation;

/// <summary>A tool type the Tool Room and the tool catalog can pick; ids are the Server's codes.</summary>
internal sealed record ToolShapeOption(string Id, string Name, string Family, IReadOnlyList<string> DimensionKeys, bool HasHand = false)
{
    public string FamilyName => ToolPreparationCatalog.FamilyName(Family);
}

internal sealed record ToolHandOption(string Id, string Name);

internal sealed record ToolComponentTypeOption(string Id, string Name);

/// <summary>
/// The tool types (milling, hole making, the ISO turning families, probes), holder hands,
/// dimension keys with their labels and component kinds; ids are the Server's codes.
/// </summary>
internal static class ToolPreparationCatalog
{
    private static readonly string[] MillBody = ["cuttingDiameter", "fluteLength", "overallLength", "shankDiameter", "fluteCount"];
    private static readonly string[] SquareShank = ["shankWidth", "shankHeight", "overallLength"];

    internal static readonly IReadOnlyList<string> DimensionKeys =
    [
        "cuttingDiameter", "fluteLength", "overallLength", "shankDiameter", "cornerRadius",
        "pointAngle", "taperAngle", "neckDiameter", "neckLength", "tipDiameter",
        "cuttingWidth", "maxDepth", "minBoreDiameter", "shankWidth", "shankHeight",
        "leadAngle", "insertEdgeLength", "pitch", "fluteCount"
    ];

    internal static readonly IReadOnlyDictionary<string, string> DimensionLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["cuttingDiameter"] = "Cutting diameter",
        ["fluteLength"] = "Flute length",
        ["overallLength"] = "Overall cutter length",
        ["shankDiameter"] = "Shank diameter",
        ["cornerRadius"] = "Corner / nose radius",
        ["pointAngle"] = "Point angle (degrees)",
        ["taperAngle"] = "Chamfer / taper angle (degrees)",
        ["neckDiameter"] = "Neck diameter",
        ["neckLength"] = "Neck length",
        ["tipDiameter"] = "Tip diameter",
        ["cuttingWidth"] = "Cutting width",
        ["maxDepth"] = "Max cutting depth",
        ["minBoreDiameter"] = "Min bore diameter",
        ["shankWidth"] = "Shank width",
        ["shankHeight"] = "Shank height",
        ["leadAngle"] = "Lead angle (degrees)",
        ["insertEdgeLength"] = "Insert edge length",
        ["pitch"] = "Pitch",
        ["fluteCount"] = "Number of flutes"
    };

    internal static readonly IReadOnlyList<ToolShapeOption> Shapes =
    [
        new("END_MILL", "Flat end mill", "MILLING", MillBody),
        new("BALL_END_MILL", "Ball end mill", "MILLING", MillBody),
        new("BULL_NOSE_END_MILL", "Bull-nose end mill", "MILLING", [.. MillBody, "cornerRadius"]),
        new("CHAMFER_MILL", "Chamfer mill", "MILLING", [.. MillBody, "taperAngle", "tipDiameter"]),
        new("FACE_MILL", "Face mill", "MILLING", ["cuttingDiameter", "overallLength", "shankDiameter", "cornerRadius", "leadAngle", "fluteCount"]),
        new("SLOT_MILL", "Slot / disc cutter", "MILLING", ["cuttingDiameter", "cuttingWidth", "overallLength", "shankDiameter", "cornerRadius", "fluteCount"]),
        new("T_SLOT_MILL", "T-slot cutter", "MILLING", ["cuttingDiameter", "cuttingWidth", "neckDiameter", "neckLength", "overallLength", "shankDiameter", "cornerRadius"]),
        new("THREAD_MILL", "Thread mill", "MILLING", ["cuttingDiameter", "fluteLength", "overallLength", "shankDiameter", "pitch", "neckDiameter", "neckLength", "fluteCount"]),
        new("DOVETAIL_MILL", "Dovetail cutter", "MILLING", ["cuttingDiameter", "fluteLength", "overallLength", "shankDiameter", "taperAngle", "neckDiameter"]),
        new("LOLLIPOP_MILL", "Lollipop (undercut) mill", "MILLING", ["cuttingDiameter", "neckDiameter", "neckLength", "overallLength", "shankDiameter"]),
        new("ENGRAVER", "Engraver", "MILLING", ["cuttingDiameter", "tipDiameter", "taperAngle", "fluteLength", "overallLength", "shankDiameter"]),
        new("DRILL", "Drill", "HOLE_MAKING", ["cuttingDiameter", "fluteLength", "overallLength", "shankDiameter", "pointAngle"]),
        new("SPOT_DRILL", "Spot drill", "HOLE_MAKING", ["cuttingDiameter", "fluteLength", "overallLength", "shankDiameter", "pointAngle"]),
        new("CENTER_DRILL", "Center drill", "HOLE_MAKING", ["cuttingDiameter", "tipDiameter", "fluteLength", "overallLength", "shankDiameter", "pointAngle"]),
        new("TAP", "Tap", "HOLE_MAKING", ["cuttingDiameter", "pitch", "fluteLength", "overallLength", "shankDiameter"]),
        new("REAMER", "Reamer", "HOLE_MAKING", ["cuttingDiameter", "fluteLength", "overallLength", "shankDiameter"]),
        new("BORING_HEAD", "Boring head", "HOLE_MAKING", ["cuttingDiameter", "fluteLength", "overallLength", "shankDiameter", "cornerRadius"]),
        new("COUNTERSINK", "Countersink", "HOLE_MAKING", ["cuttingDiameter", "tipDiameter", "pointAngle", "overallLength", "shankDiameter"]),
        new("COUNTERBORE", "Counterbore", "HOLE_MAKING", ["cuttingDiameter", "tipDiameter", "fluteLength", "overallLength", "shankDiameter"]),
        new("TURNING_TOOL", "External turning tool", "TURNING", ["cornerRadius", "leadAngle", "insertEdgeLength", .. SquareShank], true),
        new("BORING_BAR", "Boring bar (internal turning)", "TURNING", ["cornerRadius", "leadAngle", "insertEdgeLength", "minBoreDiameter", "shankDiameter", "overallLength"], true),
        new("EXTERNAL_GROOVING", "External grooving tool", "TURNING", ["cuttingWidth", "maxDepth", "cornerRadius", .. SquareShank], true),
        new("INTERNAL_GROOVING", "Internal grooving tool", "TURNING", ["cuttingWidth", "maxDepth", "cornerRadius", "minBoreDiameter", "shankDiameter", "overallLength"], true),
        new("FACE_GROOVING", "Face grooving tool", "TURNING", ["cuttingWidth", "maxDepth", "cornerRadius", .. SquareShank], true),
        new("PARTING", "Parting (cut-off) tool", "TURNING", ["cuttingWidth", "maxDepth", "cornerRadius", .. SquareShank], true),
        new("EXTERNAL_THREADING", "External threading tool", "TURNING", ["pitch", "insertEdgeLength", .. SquareShank], true),
        new("INTERNAL_THREADING", "Internal threading tool", "TURNING", ["pitch", "minBoreDiameter", "shankDiameter", "overallLength"], true),
        new("PROBE", "Probe", "OTHER", ["tipDiameter", "shankDiameter", "overallLength"]),
        new("OTHER", "Other", "OTHER", DimensionKeys)
    ];

    internal static readonly IReadOnlyList<ToolHandOption> Hands =
    [
        new("NEUTRAL", "Neutral"),
        new("RIGHT", "Right hand"),
        new("LEFT", "Left hand")
    ];

    internal static readonly IReadOnlyList<ToolComponentTypeOption> ComponentTypes =
    [
        new("HOLDER", "Holder"),
        new("EXTENSION", "Extension"),
        new("COLLET", "Collet"),
        new("ARBOR", "Arbor"),
        new("SHANK", "Shank"),
        new("CUTTER", "Cutter"),
        new("INSERT", "Insert"),
        new("OTHER", "Other")
    ];

    internal static ToolShapeOption Shape(string? id) =>
        Shapes.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Shapes[^1];

    internal static ToolHandOption Hand(string? id) =>
        Hands.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Hands[0];

    internal static ToolComponentTypeOption ComponentType(string? id) =>
        ComponentTypes.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase)) ?? ComponentTypes[^1];

    internal static string FamilyName(string family) => family switch
    {
        "MILLING" => "Milling",
        "HOLE_MAKING" => "Hole making",
        "TURNING" => "Turning",
        _ => "Other"
    };

    internal static string DimensionLabel(string key) => DimensionLabels.TryGetValue(key, out var label) ? label : key;
}

/// <summary>One dimension text box of a tool: the value lives in the owning <see cref="ToolDimensionSet"/>.</summary>
internal sealed class ToolDimensionFieldViewModel(string key, ToolDimensionSet owner) : ToolPreparationObservable
{
    public string Key => key;
    public string Label => ToolPreparationCatalog.DimensionLabel(key);
    public new string Text { get => owner.Get(key); set => owner.Set(key, value); }

    internal void Refresh() => Raise(nameof(Text));
}

/// <summary>
/// The dimension values of one tool (every known key) and the fields shown for its type. Values
/// entered for a hidden key are kept until the tool is saved, when only the shown keys count.
/// </summary>
internal sealed class ToolDimensionSet(Action changed)
{
    private readonly Dictionary<string, string> values =
        ToolPreparationCatalog.DimensionKeys.ToDictionary(key => key, _ => string.Empty, StringComparer.Ordinal);

    public ObservableCollection<ToolDimensionFieldViewModel> Fields { get; } = [];

    internal string Get(string key) => values.TryGetValue(key, out var text) ? text : string.Empty;

    internal void Set(string key, string? text)
    {
        var value = text ?? string.Empty;
        if (!values.ContainsKey(key) || values[key] == value) return;
        values[key] = value;
        Fields.FirstOrDefault(field => field.Key == key)?.Refresh();
        changed();
    }

    /// <summary>Replaces every value with the given dimensions (missing keys become empty).</summary>
    internal void Load(IReadOnlyDictionary<string, double> shape)
    {
        foreach (var key in values.Keys.ToArray())
        {
            values[key] = shape.TryGetValue(key, out var value) ? ToolPreparationObservable.Text(value) : string.Empty;
        }
        foreach (var field in Fields) field.Refresh();
    }

    /// <summary>Shows the fields of the type, in the type's order.</summary>
    internal void ShowFor(ToolShapeOption shape)
    {
        Fields.Clear();
        foreach (var key in shape.DimensionKeys) Fields.Add(new ToolDimensionFieldViewModel(key, this));
    }

    /// <summary>The shown dimensions that parse, for the preview.</summary>
    internal IReadOnlyDictionary<string, double> Preview()
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            if (ToolPreparationObservable.TryParseNumber(values[field.Key]) is { } value) result[field.Key] = value;
        }
        return result;
    }

    /// <summary>The shown dimensions for saving; a value that is not a number names the tool and the dimension.</summary>
    internal IReadOnlyDictionary<string, double> Parse(string toolLabel)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            var value = ToolPreparationObservable.ParseNumber(values[field.Key], $"{toolLabel}: {field.Label}");
            if (value is not null) result[field.Key] = value.Value;
        }
        return result;
    }
}
