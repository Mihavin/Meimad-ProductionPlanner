namespace Meimad.Planner.Client.Windows.Presentation.ToolPreparation;

/// <summary>
/// One stacked segment of the tool drawing, from the spindle gauge line downwards. Kinds:
/// HOLDER (taper with flange), CYLINDER, CUTTER (fluted cylinder), BALL (hemisphere tip),
/// POINT (drill point), CONE (chamfer), DISC (face mill), INSERT (turning insert) and SPHERE (probe).
/// </summary>
internal sealed record ToolShapeSegment(
    string Kind,
    double Top,
    double Height,
    double Diameter,
    string Label,
    double TipAngle = 0,
    double CornerRadius = 0,
    bool IsDefault = false);

/// <summary>The drawing of one prepared tool in millimetres, plus its measured values for the dimension lines.</summary>
internal sealed record ToolShapeGeometry(
    IReadOnlyList<ToolShapeSegment> Segments,
    double TotalLength,
    double MaximumDiameter,
    double? MeasuredLength,
    double? MeasuredDiameter);

internal sealed record ToolShapeComponent(string ComponentType, string Name, double? Length, double? Diameter);

/// <summary>
/// Builds the schematic from the assembled components (holder, extension, collet, shank, ...) and
/// the cutter shape and dimensions. Missing dimensions get typical defaults that are marked, so the
/// picture is always drawable and the Tool Room sees what is still unspecified.
/// </summary>
internal static class ToolShapeBuilder
{
    private const double DefaultHolderLength = 60;
    private const double DefaultHolderDiameter = 63;
    private const double DefaultCylinderLength = 40;
    private const double DefaultCylinderDiameter = 25;
    private const double DefaultCuttingDiameter = 10;

    internal static ToolShapeGeometry Build(
        string shapeType,
        IReadOnlyDictionary<string, double> shape,
        IReadOnlyList<ToolShapeComponent> components,
        double? measuredLength,
        double? measuredDiameter)
    {
        var segments = new List<ToolShapeSegment>();
        var top = 0.0;
        var hasHolder = false;
        foreach (var component in components)
        {
            var componentType = component.ComponentType.ToUpperInvariant();
            if (componentType is "CUTTER" or "INSERT") continue; // drawn from the shape below
            var isHolder = componentType == "HOLDER";
            hasHolder |= isHolder;
            var length = Positive(component.Length) ?? (isHolder ? DefaultHolderLength : DefaultCylinderLength);
            var diameter = Positive(component.Diameter) ?? (isHolder ? DefaultHolderDiameter : DefaultCylinderDiameter);
            segments.Add(new ToolShapeSegment(
                isHolder ? "HOLDER" : "CYLINDER", top, length, diameter,
                component.Name.Length == 0 ? Capitalized(componentType) : component.Name,
                IsDefault: component.Length is null || component.Diameter is null));
            top += length;
        }
        if (!hasHolder)
        {
            segments.Insert(0, new ToolShapeSegment("HOLDER", 0, DefaultHolderLength, DefaultHolderDiameter, "Holder", IsDefault: true));
            for (var index = 1; index < segments.Count; index++)
            {
                segments[index] = segments[index] with { Top = segments[index].Top + DefaultHolderLength };
            }
            top += DefaultHolderLength;
        }

        var cuttingDiameter = Positive(Value(shape, "cuttingDiameter")) ?? Positive(measuredDiameter) ?? DefaultCuttingDiameter;
        var shankDiameter = Positive(Value(shape, "shankDiameter")) ?? cuttingDiameter;
        var fluteLength = Positive(Value(shape, "fluteLength")) ?? Math.Round(cuttingDiameter * 2.2, 1);
        var overallLength = Positive(Value(shape, "overallLength")) ?? Math.Round(fluteLength + Math.Max(cuttingDiameter * 2.5, 20), 1);
        var shankLength = Math.Max(0, overallLength - fluteLength);
        var cornerRadius = Math.Max(0, Value(shape, "cornerRadius") ?? 0);
        var pointAngle = Positive(Value(shape, "pointAngle")) ?? 118;
        var taperAngle = Positive(Value(shape, "taperAngle")) ?? 45;
        // The shank is drawn from the overall cutter length; without it the shank is a marked guess.
        var shankDefault = Positive(Value(shape, "overallLength")) is null;
        var cutterName = components.FirstOrDefault(component => component.ComponentType.Equals("CUTTER", StringComparison.OrdinalIgnoreCase))?.Name;
        var type = shapeType.ToUpperInvariant();
        var defaults = Value(shape, "cuttingDiameter") is null && measuredDiameter is null;

        switch (type)
        {
            case "FACE_MILL":
            {
                var body = Positive(Value(shape, "overallLength")) ?? Math.Max(30, cuttingDiameter * 0.5);
                var arbor = Math.Max(10, body * 0.5);
                segments.Add(new ToolShapeSegment("CYLINDER", top, arbor, shankDiameter < cuttingDiameter ? shankDiameter : cuttingDiameter * 0.4, "Arbor", IsDefault: shankDefault));
                top += arbor;
                segments.Add(new ToolShapeSegment("DISC", top, body - arbor, cuttingDiameter, cutterName ?? "Face mill", IsDefault: defaults));
                top += body - arbor;
                break;
            }
            case "TURNING_TOOL":
            {
                var shank = Positive(Value(shape, "overallLength")) ?? 100;
                var width = Positive(Value(shape, "shankDiameter")) ?? 20;
                segments.Add(new ToolShapeSegment("CYLINDER", top, shank, width, cutterName ?? "Shank", IsDefault: shankDefault));
                top += shank;
                segments.Add(new ToolShapeSegment("INSERT", top, Math.Max(4, width * 0.4), Math.Max(width, cuttingDiameter), "Insert", TipAngle: 80, IsDefault: defaults));
                top += Math.Max(4, width * 0.4);
                break;
            }
            case "PROBE":
            {
                var stylus = Positive(Value(shape, "overallLength")) ?? 50;
                segments.Add(new ToolShapeSegment("CYLINDER", top, stylus, Positive(Value(shape, "shankDiameter")) ?? 4, cutterName ?? "Stylus", IsDefault: shankDefault));
                top += stylus;
                var ball = Positive(Value(shape, "tipDiameter")) ?? 6;
                segments.Add(new ToolShapeSegment("SPHERE", top, ball, ball, "Ball", IsDefault: defaults));
                top += ball;
                break;
            }
            case "BORING_BAR":
            {
                segments.Add(new ToolShapeSegment("CYLINDER", top, shankLength, shankDiameter, cutterName ?? "Bar", IsDefault: shankDefault));
                top += shankLength;
                segments.Add(new ToolShapeSegment("INSERT", top, Math.Max(fluteLength, 6), cuttingDiameter, "Boring head", TipAngle: 80, IsDefault: defaults));
                top += Math.Max(fluteLength, 6);
                break;
            }
            case "CHAMFER_MILL":
            {
                segments.Add(new ToolShapeSegment("CYLINDER", top, shankLength, shankDiameter, cutterName ?? "Shank", IsDefault: shankDefault));
                top += shankLength;
                segments.Add(new ToolShapeSegment("CONE", top, fluteLength, cuttingDiameter, "Chamfer", TipAngle: taperAngle, IsDefault: defaults));
                top += fluteLength;
                break;
            }
            case "DRILL":
            {
                segments.Add(new ToolShapeSegment("CYLINDER", top, shankLength, shankDiameter, cutterName ?? "Shank", IsDefault: shankDefault));
                top += shankLength;
                var point = PointHeight(cuttingDiameter, pointAngle);
                segments.Add(new ToolShapeSegment("CUTTER", top, Math.Max(0, fluteLength - point), cuttingDiameter, "Drill", IsDefault: defaults));
                top += Math.Max(0, fluteLength - point);
                segments.Add(new ToolShapeSegment("POINT", top, point, cuttingDiameter, "Drill point", TipAngle: pointAngle, IsDefault: defaults));
                top += point;
                break;
            }
            case "BALL_END_MILL":
            {
                segments.Add(new ToolShapeSegment("CYLINDER", top, shankLength, shankDiameter, cutterName ?? "Shank", IsDefault: shankDefault));
                top += shankLength;
                var ball = cuttingDiameter / 2;
                segments.Add(new ToolShapeSegment("CUTTER", top, Math.Max(0, fluteLength - ball), cuttingDiameter, "Ball end mill", IsDefault: defaults));
                top += Math.Max(0, fluteLength - ball);
                segments.Add(new ToolShapeSegment("BALL", top, ball, cuttingDiameter, "Ball", IsDefault: defaults));
                top += ball;
                break;
            }
            default:
            {
                segments.Add(new ToolShapeSegment("CYLINDER", top, shankLength, shankDiameter, cutterName ?? "Shank", IsDefault: shankDefault));
                top += shankLength;
                var label = type switch
                {
                    "BULL_NOSE_END_MILL" => "Bull-nose end mill",
                    "TAP" => "Tap",
                    "REAMER" => "Reamer",
                    "END_MILL" => "End mill",
                    _ => "Cutter"
                };
                segments.Add(new ToolShapeSegment("CUTTER", top, fluteLength, cuttingDiameter, label,
                    CornerRadius: type == "BULL_NOSE_END_MILL" ? Math.Min(cornerRadius > 0 ? cornerRadius : cuttingDiameter * 0.15, cuttingDiameter / 2) : 0,
                    IsDefault: defaults));
                top += fluteLength;
                break;
            }
        }

        var maximum = segments.Count == 0 ? 0 : segments.Max(segment => segment.Diameter);
        return new ToolShapeGeometry(segments, top, maximum, measuredLength, measuredDiameter);
    }

    /// <summary>Axial height of a drill point of the given included angle.</summary>
    internal static double PointHeight(double diameter, double pointAngle)
    {
        var half = Math.Clamp(pointAngle, 1, 179) / 2 * Math.PI / 180;
        return Math.Round(diameter / 2 / Math.Tan(half), 2);
    }

    private static double? Value(IReadOnlyDictionary<string, double> shape, string key) =>
        shape.TryGetValue(key, out var value) ? value : null;

    private static double? Positive(double? value) =>
        value is > 0 && double.IsFinite(value.Value) ? value : null;

    private static string Capitalized(string type) =>
        type.Length == 0 ? type : char.ToUpperInvariant(type[0]) + type[1..].ToLowerInvariant();
}
