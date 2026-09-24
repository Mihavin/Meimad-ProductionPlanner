using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meimad.Planner.Server.Domain.ToolPreparations;

/// <summary>Cutter shapes the Tool Room can describe; the client previews them.</summary>
internal static class ToolShapeTypes
{
    internal const string EndMill = "END_MILL";
    internal const string BallEndMill = "BALL_END_MILL";
    internal const string BullNoseEndMill = "BULL_NOSE_END_MILL";
    internal const string ChamferMill = "CHAMFER_MILL";
    internal const string FaceMill = "FACE_MILL";
    internal const string Drill = "DRILL";
    internal const string Tap = "TAP";
    internal const string Reamer = "REAMER";
    internal const string BoringBar = "BORING_BAR";
    internal const string TurningTool = "TURNING_TOOL";
    internal const string Probe = "PROBE";
    internal const string Other = "OTHER";

    internal static readonly IReadOnlyList<string> All =
    [
        EndMill, BallEndMill, BullNoseEndMill, ChamferMill, FaceMill, Drill, Tap, Reamer, BoringBar,
        TurningTool, Probe, Other
    ];

    internal static bool IsSupported(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);
}

/// <summary>Assembled parts of one prepared tool, from the spindle side to the cutting edge.</summary>
internal static class ToolComponentTypes
{
    internal const string Holder = "HOLDER";
    internal const string Extension = "EXTENSION";
    internal const string Collet = "COLLET";
    internal const string Arbor = "ARBOR";
    internal const string Shank = "SHANK";
    internal const string Cutter = "CUTTER";
    internal const string Insert = "INSERT";
    internal const string Other = "OTHER";

    internal static readonly IReadOnlyList<string> All =
        [Holder, Extension, Collet, Arbor, Shank, Cutter, Insert, Other];

    internal static bool IsSupported(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);
}

/// <summary>Cutter dimensions in millimetres (angles in degrees); every key is optional.</summary>
internal static class ToolShapeDimensions
{
    internal static readonly IReadOnlyList<string> Keys =
    [
        "cuttingDiameter", "fluteLength", "overallLength", "shankDiameter", "cornerRadius",
        "pointAngle", "taperAngle", "neckDiameter", "neckLength", "tipDiameter"
    ];

    internal static readonly IReadOnlySet<string> Angles = new HashSet<string>(["pointAngle", "taperAngle"], StringComparer.Ordinal);

    internal const double MaximumMillimetres = 10000;
}

/// <summary>Whether the Machine's control keeps cutter compensation (D) values as radius or diameter.</summary>
internal static class ToolDiameterOffsetKinds
{
    internal const string Radius = "RADIUS";
    internal const string Diameter = "DIAMETER";

    internal static bool IsSupported(string? value) => value is Radius or Diameter;
}

internal sealed record ToolPreparationComponent(
    int Sequence,
    string ComponentType,
    string Name,
    string? CatalogNumber,
    double? Length,
    double? Diameter,
    string? Notes);

internal sealed record ToolPreparationTool(
    int RowNumber,
    string ToolIdentifier,
    int? OffsetNumber,
    double? MeasuredLength,
    double? MeasuredDiameter,
    string ShapeType,
    IReadOnlyDictionary<string, double> Shape,
    string? Notes,
    IReadOnlyList<ToolPreparationComponent> Components)
{
    /// <summary>The offset register: the explicit number, else the digits of the tool identifier (T12 → 12).</summary>
    internal int? EffectiveOffsetNumber => OffsetNumber ?? ParseToolNumber(ToolIdentifier);

    internal static int? ParseToolNumber(string identifier)
    {
        var digits = new string(identifier.Trim().SkipWhile(character => !char.IsDigit(character))
            .TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 && digits.Length <= 4 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number > 0
            ? number
            : null;
    }
}

/// <summary>One immutable saved version of the measured tools of a Batch Operation on a Machine.</summary>
internal sealed record ToolPreparation(
    string ToolPreparationId,
    string BatchOperationId,
    string MachineId,
    string ToolTableReleaseId,
    int VersionNumber,
    DateTimeOffset SavedAt,
    string SavedBy,
    string? Comment,
    string ContentHash,
    IReadOnlyList<ToolPreparationTool> Tools);

/// <summary>What the Tool Room sends: the complete tool list of the next version.</summary>
internal sealed record ToolPreparationUpdate(
    int ExpectedVersion,
    string ToolTableReleaseId,
    string? Comment,
    IReadOnlyList<ToolPreparationTool> Tools);

/// <summary>A released tool row the preparation describes.</summary>
internal sealed record ToolPreparationReleasedTool(
    int RowNumber,
    string ToolIdentifier,
    string Description,
    bool IsRequired,
    string? MagazinePosition);

/// <summary>The Operation's preparation context: Machine, active Tool Table and the latest saved version.</summary>
internal sealed record ToolPreparationView(
    string BatchOperationId,
    string MachineId,
    string MachineNumber,
    string MachineName,
    string ProcessType,
    string NcDialect,
    string ToolDiameterOffsetKind,
    string ToolTableReleaseId,
    int ToolTableRevision,
    string ToolTableFileName,
    IReadOnlyList<ToolPreparationReleasedTool> ReleasedTools,
    ToolPreparation? Current);

internal sealed class ToolPreparationValidationException(string code, string message, string? field = null)
    : Exception(message)
{
    internal string Code { get; } = code;
    internal string? Field { get; } = field;
}

internal static class ToolPreparationValidator
{
    private const int MaximumTools = 2000;
    private const int MaximumComponents = 50;

    /// <summary>
    /// Normalizes and checks an update against the released tool rows it describes. Every prepared
    /// tool must be a released tool; measurements are optional here (the Production Package
    /// requires them for its required tools) and negative or absurd values are rejected.
    /// </summary>
    internal static IReadOnlyList<ToolPreparationTool> Validate(
        ToolPreparationUpdate update,
        IReadOnlyList<ToolPreparationReleasedTool> released)
    {
        if (update.Tools is null) throw Invalid("tool_preparation_tools_required", "tools is required.", "tools");
        if (update.Tools.Count > MaximumTools)
            throw Invalid("tool_preparation_too_many_tools", $"At most {MaximumTools} tools can be prepared.", "tools");
        if (update.Comment is { Length: > 2000 })
            throw Invalid("tool_preparation_comment_too_long", "comment must be at most 2000 characters.", "comment");

        var byIdentifier = released.ToDictionary(tool => tool.ToolIdentifier.Trim(), tool => tool, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tools = new List<ToolPreparationTool>();
        foreach (var tool in update.Tools)
        {
            var identifier = tool.ToolIdentifier?.Trim() ?? string.Empty;
            if (identifier.Length is 0 or > 80)
                throw Invalid("tool_preparation_tool_identifier_invalid", "Every tool needs its released tool identifier (at most 80 characters).", "toolIdentifier");
            if (!byIdentifier.TryGetValue(identifier, out var releasedTool))
                throw Invalid("tool_preparation_unknown_tool", $"Tool '{identifier}' is not an active row of the released Tool Table.", "toolIdentifier");
            if (!seen.Add(identifier))
                throw Invalid("tool_preparation_duplicate_tool", $"Tool '{identifier}' is listed twice.", "toolIdentifier");
            if (tool.OffsetNumber is < 1 or > 9999)
                throw Invalid("tool_preparation_offset_number_invalid", $"Tool '{identifier}': offsetNumber must be between 1 and 9999.", "offsetNumber");
            var length = Millimetres(tool.MeasuredLength, identifier, "measuredLength");
            var diameter = Millimetres(tool.MeasuredDiameter, identifier, "measuredDiameter");
            var shapeType = tool.ShapeType?.Trim().ToUpperInvariant();
            if (!ToolShapeTypes.IsSupported(shapeType))
                throw Invalid("tool_preparation_shape_type_invalid", $"Tool '{identifier}': shapeType must be one of {string.Join(", ", ToolShapeTypes.All)}.", "shapeType");
            var shape = Shape(tool.Shape, identifier);
            var notes = Text(tool.Notes, 1000, identifier, "notes");
            var components = Components(tool.Components, identifier);
            tools.Add(new ToolPreparationTool(
                releasedTool.RowNumber, releasedTool.ToolIdentifier, tool.OffsetNumber, length, diameter,
                shapeType!, shape, notes, components));
        }

        return tools.OrderBy(tool => tool.RowNumber).ToArray();
    }

    /// <summary>Canonical JSON of the tools, hashed so a package can prove which measurements it used.</summary>
    internal static string ContentHash(IReadOnlyList<ToolPreparationTool> tools)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(tools
            .OrderBy(tool => tool.RowNumber)
            .Select(tool => new
            {
                tool.RowNumber,
                tool.ToolIdentifier,
                tool.OffsetNumber,
                tool.MeasuredLength,
                tool.MeasuredDiameter,
                tool.ShapeType,
                Shape = tool.Shape.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToDictionary(entry => entry.Key, entry => entry.Value),
                tool.Notes,
                Components = tool.Components.OrderBy(component => component.Sequence)
            }), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    private static IReadOnlyList<ToolPreparationComponent> Components(IReadOnlyList<ToolPreparationComponent>? components, string identifier)
    {
        if (components is null || components.Count == 0) return [];
        if (components.Count > MaximumComponents)
            throw Invalid("tool_preparation_too_many_components", $"Tool '{identifier}': at most {MaximumComponents} components can be listed.", "components");
        var sequences = new HashSet<int>();
        var result = new List<ToolPreparationComponent>();
        foreach (var component in components)
        {
            if (component.Sequence <= 0 || !sequences.Add(component.Sequence))
                throw Invalid("tool_preparation_component_sequence_invalid", $"Tool '{identifier}': component sequences must be positive and unique.", "sequence");
            var type = component.ComponentType?.Trim().ToUpperInvariant();
            if (!ToolComponentTypes.IsSupported(type))
                throw Invalid("tool_preparation_component_type_invalid", $"Tool '{identifier}': componentType must be one of {string.Join(", ", ToolComponentTypes.All)}.", "componentType");
            var name = component.Name?.Trim() ?? string.Empty;
            if (name.Length is 0 or > 120)
                throw Invalid("tool_preparation_component_name_invalid", $"Tool '{identifier}': every component needs a name of at most 120 characters.", "name");
            result.Add(new ToolPreparationComponent(
                component.Sequence, type!, name,
                Text(component.CatalogNumber, 120, identifier, "catalogNumber"),
                Millimetres(component.Length, identifier, "length"),
                Millimetres(component.Diameter, identifier, "diameter"),
                Text(component.Notes, 500, identifier, "notes")));
        }
        return result.OrderBy(component => component.Sequence).ToArray();
    }

    private static IReadOnlyDictionary<string, double> Shape(IReadOnlyDictionary<string, double>? shape, string identifier)
    {
        var result = new SortedDictionary<string, double>(StringComparer.Ordinal);
        if (shape is null) return result;
        foreach (var (key, value) in shape)
        {
            if (!ToolShapeDimensions.Keys.Contains(key, StringComparer.Ordinal))
                throw Invalid("tool_preparation_shape_dimension_unknown", $"Tool '{identifier}': shape dimension '{key}' is not supported.", "shape");
            var maximum = ToolShapeDimensions.Angles.Contains(key) ? 180 : ToolShapeDimensions.MaximumMillimetres;
            if (!double.IsFinite(value) || value < 0 || value > maximum)
                throw Invalid("tool_preparation_shape_dimension_invalid", $"Tool '{identifier}': shape dimension '{key}' must be between 0 and {maximum}.", "shape");
            result[key] = value;
        }
        return result;
    }

    private static double? Millimetres(double? value, string identifier, string field)
    {
        if (value is null) return null;
        if (!double.IsFinite(value.Value) || value.Value < 0 || value.Value > ToolShapeDimensions.MaximumMillimetres)
            throw Invalid($"tool_preparation_{Snake(field)}_invalid", $"Tool '{identifier}': {field} must be a millimetre value between 0 and {ToolShapeDimensions.MaximumMillimetres}.", field);
        return Math.Round(value.Value, 4);
    }

    private static string? Text(string? value, int maximum, string identifier, string field)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length > maximum)
            throw Invalid($"tool_preparation_{Snake(field)}_too_long", $"Tool '{identifier}': {field} must be at most {maximum} characters.", field);
        return trimmed;
    }

    private static string Snake(string field) =>
        string.Concat(field.Select(character => char.IsUpper(character) ? "_" + char.ToLowerInvariant(character) : character.ToString()));

    private static ToolPreparationValidationException Invalid(string code, string message, string field) =>
        new(code, message, field);
}
