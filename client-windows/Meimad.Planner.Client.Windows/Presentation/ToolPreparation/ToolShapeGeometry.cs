using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation.ToolPreparation;

/// <summary>
/// One stacked segment of the tool drawing, from the spindle gauge line downwards (a negative
/// <see cref="Top"/> lies above the gauge line: the pull stud and the adaptor taper). Kinds:
/// TAPER (spindle adaptor taper, <see cref="TopDiameter"/> at its small upper end),
/// HOLDER (taper with flange), CYLINDER, CUTTER (fluted cylinder), BALL (hemisphere tip),
/// POINT (drill point), CONE (chamfer, engraver, threading insert), DISC (face, slot and T-slot
/// cutters), INSERT (turning and boring inserts), BLADE (grooving and parting), DOVETAIL (widening
/// cutter), SPHERE (probe ball, lollipop) and GAP (the undescribed assembly between the gauge
/// line and the cutter, drawn as an axis).
/// </summary>
internal sealed record ToolShapeSegment(
    string Kind,
    double Top,
    double Height,
    double Diameter,
    string Label,
    double TipAngle = 0,
    double CornerRadius = 0,
    bool IsDefault = false,
    double TopDiameter = 0);

/// <summary>
/// The drawing of one prepared tool in millimetres, plus its measured values for the dimension lines.
/// <see cref="TotalLength"/> runs from the gauge line to the tip; <see cref="AboveGaugeLength"/> is
/// the pull stud and taper above the gauge line.
/// </summary>
internal sealed record ToolShapeGeometry(
    IReadOnlyList<ToolShapeSegment> Segments,
    double TotalLength,
    double MaximumDiameter,
    double? MeasuredLength,
    double? MeasuredDiameter,
    double AboveGaugeLength = 0);

internal sealed record ToolShapeComponent(string ComponentType, string Name, double? Length, double? Diameter);

/// <summary>
/// The spindle side of a milling tool (schema v91): the adaptor's taper above the gauge line and its
/// tool-changer flange (TCD × TCL) below it, and the pull stud on top of the taper. Millimetres.
/// </summary>
internal sealed record ToolSpindleShape(
    string AdaptorName,
    double TaperLength,
    double GaugeDiameter,
    double SmallEndDiameter,
    double ToolChangerDiameter,
    double ToolChangerLength,
    string? PullStudName = null,
    double? PullStudExposedLength = null,
    double? KnobDiameter = null,
    double? NeckDiameter = null,
    double? PilotDiameter = null)
{
    internal static ToolSpindleShape? From(PlannerSpindleAdaptor? adaptor, PlannerPullStud? pullStud) => adaptor is null
        ? null
        : new ToolSpindleShape(
            adaptor.Name, adaptor.TaperLength, adaptor.GaugeDiameter, adaptor.SmallEndDiameter,
            adaptor.ToolChangerDiameter, adaptor.ToolChangerLength,
            pullStud?.Name, pullStud?.ExposedLength, pullStud?.KnobDiameter, pullStud?.NeckDiameter, pullStud?.PilotDiameter);
}

/// <summary>
/// Builds the schematic from the assembled components (holder, extension, collet, shank, ...) and
/// the cutter shape and dimensions. Without components only the cutter hangs from the gauge line.
/// Missing dimensions get typical defaults that are marked, so the picture is always drawable and
/// the Tool Room sees what is still unspecified.
/// With a spindle adaptor (owner decisions 2026-09-29) a milling tool is drawn as it sits in the
/// spindle: the pull stud and the taper above the gauge line, the tool-changer flange (TCD × TCL)
/// below it, then the holder as a cylinder of its diameter HD whose length follows from the
/// measured length: HL = measured length − OHL − TCL (− extensions). The tool shows only its
/// outside-holder length OHL.
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
        double? measuredDiameter,
        ToolSpindleShape? spindle = null)
    {
        var segments = new List<ToolShapeSegment>();
        var top = 0.0;
        var above = 0.0;
        var outsideHolder = Positive(Value(shape, "outsideHolderLength"));
        if (spindle is not null && !IsTurning(shapeType))
        {
            above = AddSpindle(segments, spindle);
            top = AddHolder(segments, spindle, components, outsideHolder, measuredLength);
            components = components
                .Where(component => component.ComponentType.ToUpperInvariant() is not ("HOLDER" or "COLLET" or "SHANK"))
                .ToArray();
        }
        foreach (var component in components)
        {
            var componentType = component.ComponentType.ToUpperInvariant();
            if (componentType is "CUTTER" or "INSERT") continue; // drawn from the shape below
            var isHolder = componentType == "HOLDER";
            var length = Positive(component.Length) ?? (isHolder ? DefaultHolderLength : DefaultCylinderLength);
            var diameter = Positive(component.Diameter) ?? (isHolder ? DefaultHolderDiameter : DefaultCylinderDiameter);
            segments.Add(new ToolShapeSegment(
                isHolder ? "HOLDER" : "CYLINDER", top, length, diameter,
                component.Name.Length == 0 ? Capitalized(componentType) : component.Name,
                IsDefault: component.Length is null || component.Diameter is null));
            top += length;
        }
        var assemblyDrawn = segments.Count > 0;

        var cuttingDiameter = Positive(Value(shape, "cuttingDiameter")) ?? Positive(measuredDiameter) ?? DefaultCuttingDiameter;
        var shankDiameter = Positive(Value(shape, "shankDiameter")) ?? cuttingDiameter;
        var fluteLength = Positive(Value(shape, "fluteLength")) ?? Math.Round(cuttingDiameter * 2.2, 1);
        var overallLength = Positive(Value(shape, "overallLength")) ?? Math.Round(fluteLength + Math.Max(cuttingDiameter * 2.5, 20), 1);
        // Only the part outside the holder (OHL) is seen; without it, the whole shank.
        var shankLength = outsideHolder is { } visible ? Math.Max(0, visible - fluteLength) : Math.Max(0, overallLength - fluteLength);
        var cornerRadius = Math.Max(0, Value(shape, "cornerRadius") ?? 0);
        var pointAngle = Positive(Value(shape, "pointAngle")) ?? 118;
        var taperAngle = Positive(Value(shape, "taperAngle")) ?? 45;
        // The shank is drawn from the overall cutter length; without it the shank is a marked guess.
        var shankDefault = Positive(Value(shape, "overallLength")) is null;
        var cutterName = components.FirstOrDefault(component => component.ComponentType.Equals("CUTTER", StringComparison.OrdinalIgnoreCase))?.Name;
        var type = shapeType.ToUpperInvariant();
        var defaults = Value(shape, "cuttingDiameter") is null && measuredDiameter is null;

        var neckDiameter = Positive(Value(shape, "neckDiameter"));
        var neckLength = Positive(Value(shape, "neckLength"));
        var cuttingWidth = Positive(Value(shape, "cuttingWidth"));
        var tipDiameter = Positive(Value(shape, "tipDiameter"));

        switch (type)
        {
            case "FACE_MILL":
            case "SLOT_MILL":
            {
                var body = Positive(Value(shape, "overallLength")) ?? Math.Max(30, cuttingDiameter * 0.5);
                var disc = type == "SLOT_MILL" ? Math.Min(cuttingWidth ?? Math.Max(2, cuttingDiameter * 0.1), body) : Math.Max(body * 0.5, 2);
                var arbor = Math.Max(0, body - disc);
                segments.Add(new ToolShapeSegment("CYLINDER", top, arbor, shankDiameter < cuttingDiameter ? shankDiameter : cuttingDiameter * 0.4, "Arbor", IsDefault: shankDefault));
                top += arbor;
                segments.Add(new ToolShapeSegment("DISC", top, disc, cuttingDiameter, cutterName ?? (type == "SLOT_MILL" ? "Slot cutter" : "Face mill"), IsDefault: defaults));
                top += disc;
                break;
            }
            case "T_SLOT_MILL":
            {
                var disc = cuttingWidth ?? Math.Max(2, cuttingDiameter * 0.2);
                var neck = neckLength ?? Math.Max(disc, cuttingDiameter * 0.5);
                var shank = Math.Max(0, overallLength - disc - neck);
                segments.Add(new ToolShapeSegment("CYLINDER", top, shank, shankDiameter, cutterName ?? "Shank", IsDefault: shankDefault));
                top += shank;
                segments.Add(new ToolShapeSegment("CYLINDER", top, neck, neckDiameter ?? Math.Max(1, cuttingDiameter * 0.4), "Neck", IsDefault: neckDiameter is null || neckLength is null));
                top += neck;
                segments.Add(new ToolShapeSegment("DISC", top, disc, cuttingDiameter, "T-slot cutter", IsDefault: defaults));
                top += disc;
                break;
            }
            case "LOLLIPOP_MILL":
            {
                var ball = cuttingDiameter;
                var neck = neckLength ?? Math.Max(ball, 10);
                var shank = Math.Max(0, overallLength - ball - neck);
                segments.Add(new ToolShapeSegment("CYLINDER", top, shank, shankDiameter, cutterName ?? "Shank", IsDefault: shankDefault));
                top += shank;
                segments.Add(new ToolShapeSegment("CYLINDER", top, neck, neckDiameter ?? Math.Max(1, ball * 0.6), "Neck", IsDefault: neckDiameter is null || neckLength is null));
                top += neck;
                segments.Add(new ToolShapeSegment("SPHERE", top, ball, ball, "Lollipop", IsDefault: defaults));
                top += ball;
                break;
            }
            case "THREAD_MILL":
            {
                var neck = neckLength ?? 0;
                var shank = Math.Max(0, shankLength - neck);
                segments.Add(new ToolShapeSegment("CYLINDER", top, shank, shankDiameter, cutterName ?? "Shank", IsDefault: shankDefault));
                top += shank;
                if (neck > 0)
                {
                    segments.Add(new ToolShapeSegment("CYLINDER", top, neck, neckDiameter ?? Math.Max(1, cuttingDiameter * 0.7), "Neck", IsDefault: neckDiameter is null));
                    top += neck;
                }
                segments.Add(new ToolShapeSegment("CUTTER", top, fluteLength, cuttingDiameter, "Thread mill", IsDefault: defaults));
                top += fluteLength;
                break;
            }
            case "DOVETAIL_MILL":
            {
                var neck = Math.Max(0, shankLength * 0.3);
                segments.Add(new ToolShapeSegment("CYLINDER", top, shankLength - neck, shankDiameter, cutterName ?? "Shank", IsDefault: shankDefault));
                top += shankLength - neck;
                segments.Add(new ToolShapeSegment("CYLINDER", top, neck, neckDiameter ?? Math.Max(1, cuttingDiameter * 0.5), "Neck", IsDefault: neckDiameter is null));
                top += neck;
                segments.Add(new ToolShapeSegment("DOVETAIL", top, fluteLength, cuttingDiameter, "Dovetail", TipAngle: taperAngle, IsDefault: defaults));
                top += fluteLength;
                break;
            }
            case "ENGRAVER":
            {
                segments.Add(new ToolShapeSegment("CYLINDER", top, shankLength, shankDiameter, cutterName ?? "Shank", IsDefault: shankDefault));
                top += shankLength;
                segments.Add(new ToolShapeSegment("CONE", top, fluteLength, cuttingDiameter, "Engraver", TipAngle: taperAngle, IsDefault: defaults));
                top += fluteLength;
                break;
            }
            case "SPOT_DRILL":
            case "CENTER_DRILL":
            case "COUNTERSINK":
            {
                var angle = Positive(Value(shape, "pointAngle")) ?? 90;
                var point = PointHeight(cuttingDiameter, angle);
                var body = type == "COUNTERSINK" ? Math.Max(0, fluteLength - point) : Math.Max(0, fluteLength - point);
                segments.Add(new ToolShapeSegment("CYLINDER", top, shankLength, shankDiameter, cutterName ?? "Shank", IsDefault: shankDefault));
                top += shankLength;
                if (body > 0)
                {
                    segments.Add(new ToolShapeSegment("CUTTER", top, body, cuttingDiameter, type == "COUNTERSINK" ? "Countersink" : type == "CENTER_DRILL" ? "Center drill" : "Spot drill", IsDefault: defaults));
                    top += body;
                }
                segments.Add(new ToolShapeSegment("POINT", top, point, cuttingDiameter, "Point", TipAngle: angle, IsDefault: defaults));
                top += point;
                if (type == "CENTER_DRILL" && tipDiameter is { } pilotDiameter && pilotDiameter < cuttingDiameter)
                {
                    var pilot = Math.Max(1, pilotDiameter * 1.5);
                    segments.Add(new ToolShapeSegment("CYLINDER", top, pilot, pilotDiameter, "Pilot", IsDefault: false));
                    top += pilot;
                }
                break;
            }
            case "COUNTERBORE":
            {
                segments.Add(new ToolShapeSegment("CYLINDER", top, shankLength, shankDiameter, cutterName ?? "Shank", IsDefault: shankDefault));
                top += shankLength;
                segments.Add(new ToolShapeSegment("CUTTER", top, fluteLength, cuttingDiameter, "Counterbore", IsDefault: defaults));
                top += fluteLength;
                var pilotSize = tipDiameter ?? cuttingDiameter * 0.5;
                var pilotLength = Math.Max(2, pilotSize);
                segments.Add(new ToolShapeSegment("CYLINDER", top, pilotLength, pilotSize, "Pilot", IsDefault: tipDiameter is null));
                top += pilotLength;
                break;
            }
            case "TURNING_TOOL":
            case "EXTERNAL_GROOVING":
            case "FACE_GROOVING":
            case "PARTING":
            case "EXTERNAL_THREADING":
            {
                // A square-shank holder: drawn by its width, the insert or blade below it.
                var shank = Positive(Value(shape, "overallLength")) ?? 100;
                var width = Positive(Value(shape, "shankWidth")) ?? Positive(Value(shape, "shankDiameter")) ?? 20;
                var holderDefault = Positive(Value(shape, "overallLength")) is null || (Positive(Value(shape, "shankWidth")) is null && Positive(Value(shape, "shankDiameter")) is null);
                segments.Add(new ToolShapeSegment("CYLINDER", top, shank, width, cutterName ?? "Shank", IsDefault: holderDefault));
                top += shank;
                var insertDefault = type == "TURNING_TOOL" ? Value(shape, "cornerRadius") is null : cuttingWidth is null;
                switch (type)
                {
                    case "TURNING_TOOL":
                        var edge = Positive(Value(shape, "insertEdgeLength")) ?? Math.Max(4, width * 0.4);
                        segments.Add(new ToolShapeSegment("INSERT", top, edge, Math.Max(width, edge), "Insert", TipAngle: Positive(Value(shape, "leadAngle")) ?? 80, IsDefault: insertDefault));
                        top += edge;
                        break;
                    case "EXTERNAL_THREADING":
                        var thread = Positive(Value(shape, "insertEdgeLength")) ?? Math.Max(4, width * 0.4);
                        segments.Add(new ToolShapeSegment("CONE", top, thread, Math.Max(width * 0.5, thread), "Threading insert", TipAngle: 60, IsDefault: Value(shape, "pitch") is null));
                        top += thread;
                        break;
                    default:
                        var depth = Positive(Value(shape, "maxDepth")) ?? Math.Max(6, width * 0.6);
                        segments.Add(new ToolShapeSegment("BLADE", top, depth, cuttingWidth ?? Math.Max(1, width * 0.15),
                            type == "PARTING" ? "Parting blade" : "Grooving insert", IsDefault: insertDefault));
                        top += depth;
                        break;
                }
                break;
            }
            case "INTERNAL_GROOVING":
            case "INTERNAL_THREADING":
            {
                var bar = Positive(Value(shape, "overallLength")) ?? 100;
                var barDiameter = Positive(Value(shape, "shankDiameter")) ?? Positive(Value(shape, "minBoreDiameter")) ?? 16;
                segments.Add(new ToolShapeSegment("CYLINDER", top, bar, barDiameter, cutterName ?? "Bar", IsDefault: Positive(Value(shape, "overallLength")) is null));
                top += bar;
                var head = type == "INTERNAL_THREADING"
                    ? new ToolShapeSegment("CONE", top, Math.Max(4, barDiameter * 0.4), barDiameter, "Threading insert", TipAngle: 60, IsDefault: Value(shape, "pitch") is null)
                    : new ToolShapeSegment("BLADE", top, Positive(Value(shape, "maxDepth")) ?? Math.Max(4, barDiameter * 0.4), cuttingWidth ?? Math.Max(1, barDiameter * 0.2), "Grooving insert", IsDefault: cuttingWidth is null);
                segments.Add(head);
                top += head.Height;
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
                var bar = Positive(Value(shape, "overallLength")) ?? 100;
                var barDiameter = Positive(Value(shape, "shankDiameter")) ?? Positive(Value(shape, "minBoreDiameter")) ?? 16;
                segments.Add(new ToolShapeSegment("CYLINDER", top, bar, barDiameter, cutterName ?? "Bar", IsDefault: Positive(Value(shape, "overallLength")) is null));
                top += bar;
                var edge = Positive(Value(shape, "insertEdgeLength")) ?? Math.Max(4, barDiameter * 0.4);
                segments.Add(new ToolShapeSegment("INSERT", top, edge, Math.Max(barDiameter, edge), "Insert", TipAngle: Positive(Value(shape, "leadAngle")) ?? 80, IsDefault: Value(shape, "cornerRadius") is null));
                top += edge;
                break;
            }
            case "BORING_HEAD":
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

        // No assembly described: only the cutter hangs from the gauge line. A measured length that
        // is longer than the cutter leaves the unspecified part of the assembly as an empty axis.
        if (!assemblyDrawn && measuredLength is { } measured && measured > top + 0.005)
        {
            var gap = measured - top;
            for (var index = 0; index < segments.Count; index++)
            {
                segments[index] = segments[index] with { Top = segments[index].Top + gap };
            }
            segments.Insert(0, new ToolShapeSegment("GAP", 0, gap, 0, string.Empty, IsDefault: true));
            top = measured;
        }

        var maximum = segments.Count == 0 ? 0 : segments.Max(segment => segment.Diameter);
        return new ToolShapeGeometry(segments, top, maximum, measuredLength, measuredDiameter, above);
    }

    /// <summary>Pull stud and taper above the gauge line; returns their height.</summary>
    private static double AddSpindle(List<ToolShapeSegment> segments, ToolSpindleShape spindle)
    {
        var taper = spindle.TaperLength;
        var above = taper;
        if (spindle.PullStudExposedLength is { } exposed && exposed > 0)
        {
            var knob = Positive(spindle.KnobDiameter) ?? Math.Max(1, spindle.SmallEndDiameter * 0.6);
            var neck = Positive(spindle.NeckDiameter) ?? knob * 0.65;
            var pilot = Positive(spindle.PilotDiameter) ?? knob * 1.1;
            var knobHeight = Math.Min(exposed * 0.3, knob * 0.6);
            var pilotHeight = Math.Min(exposed * 0.25, 4);
            var neckHeight = Math.Max(0, exposed - knobHeight - pilotHeight);
            var stud = -(taper + exposed);
            var name = string.IsNullOrWhiteSpace(spindle.PullStudName) ? "Pull stud" : $"Pull stud {spindle.PullStudName}";
            segments.Add(new ToolShapeSegment("CYLINDER", stud, knobHeight, knob, name, IsDefault: spindle.KnobDiameter is null));
            segments.Add(new ToolShapeSegment("CYLINDER", stud + knobHeight, neckHeight, neck, "Neck", IsDefault: spindle.NeckDiameter is null));
            segments.Add(new ToolShapeSegment("CYLINDER", stud + knobHeight + neckHeight, pilotHeight, pilot, "Collar", IsDefault: spindle.PilotDiameter is null));
            above += exposed;
        }
        segments.Add(new ToolShapeSegment("TAPER", -taper, taper, spindle.GaugeDiameter, $"{spindle.AdaptorName} taper",
            TopDiameter: spindle.SmallEndDiameter));
        segments.Add(new ToolShapeSegment("CYLINDER", 0, spindle.ToolChangerLength, spindle.ToolChangerDiameter, "Tool changer flange"));
        return above;
    }

    /// <summary>
    /// The holder below the flange: its diameter HD from the HOLDER component, its length HL driven by
    /// the measured length (HL = L − OHL − TCL − extensions); returns the height reached.
    /// </summary>
    private static double AddHolder(
        List<ToolShapeSegment> segments, ToolSpindleShape spindle, IReadOnlyList<ToolShapeComponent> components,
        double? outsideHolder, double? measuredLength)
    {
        var holder = components.FirstOrDefault(component => component.ComponentType.Equals("HOLDER", StringComparison.OrdinalIgnoreCase));
        var extensions = components
            .Where(component => component.ComponentType.ToUpperInvariant() is not ("HOLDER" or "COLLET" or "SHANK" or "CUTTER" or "INSERT"))
            .Sum(component => Positive(component.Length) ?? DefaultCylinderLength);
        var diameter = Positive(holder?.Diameter);
        var driven = measuredLength is { } measured && outsideHolder is { } ohl
            ? measured - ohl - spindle.ToolChangerLength - extensions
            : (double?)null;
        var length = driven is > 0 ? Math.Round(driven.Value, 3) : Positive(holder?.Length) ?? DefaultHolderLength;
        var top = spindle.ToolChangerLength;
        var label = holder is null || holder.Name.Length == 0 ? "Holder" : holder.Name;
        segments.Add(new ToolShapeSegment("CYLINDER", top, length, diameter ?? Math.Round(spindle.ToolChangerDiameter * 0.6, 1),
            driven is > 0 ? $"{label} (HL)" : label, IsDefault: diameter is null || driven is not > 0));
        return top + length;
    }

    private static bool IsTurning(string shapeType) => shapeType.ToUpperInvariant() is
        "TURNING_TOOL" or "BORING_BAR" or "EXTERNAL_GROOVING" or "INTERNAL_GROOVING" or "FACE_GROOVING"
        or "PARTING" or "EXTERNAL_THREADING" or "INTERNAL_THREADING";

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
