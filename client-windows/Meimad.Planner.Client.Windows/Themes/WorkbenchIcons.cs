using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Meimad.Planner.Client.Windows.Themes;

/// <summary>
/// Vector menu and button icons on a 24-unit grid. One glyph set is drawn two ways: Graphite
/// uses the HMI keycap style (solid shapes, details cut through), Light uses the machine-plate
/// style (square-cut engraved strokes). Icons always sit beside or behind a text label or
/// tooltip, so they never carry meaning alone.
/// </summary>
internal static class WorkbenchIcons
{
    // Each glyph has a main shape (s), free lines (l) and details (d). Keycap fills s, strokes l
    // and cuts d out of both; plate strokes all three.
    internal sealed record Glyph(string Key, string Shape, string Lines, string Details);

    internal static IReadOnlyList<Glyph> Glyphs { get; } = BuildGlyphs();

    internal static string ResourceKey(string key) => "Icon." + key;

    internal static void Apply(ResourceDictionary resources, string theme, Color ink)
    {
        foreach (var glyph in Glyphs)
            resources[ResourceKey(glyph.Key)] = Render(glyph, theme, ink);
    }

    internal static DrawingImage Render(Glyph glyph, string theme, Color ink)
    {
        var brush = new SolidColorBrush(ink);
        brush.Freeze();
        var group = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, 24, 24)) };
        // A transparent frame keeps every icon on the same 24-unit box, so glyphs keep their
        // designed size and position instead of being stretched to their own bounds.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        group.Children.Add(theme == "light" ? Plate(glyph, brush) : Keycap(glyph, brush));
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>The unclipped area a glyph inks, for checking its shape and that it stays on the grid.</summary>
    internal static Geometry Ink(Glyph glyph, string theme)
    {
        if (theme != "light") return KeycapInk(glyph);
        Geometry ink = Geometry.Empty;
        foreach (var data in new[] { glyph.Shape, glyph.Lines, glyph.Details })
            if (data.Length > 0)
                ink = Geometry.Combine(ink, Parse(data).GetWidenedPathGeometry(PlatePen(Brushes.Black)), GeometryCombineMode.Union, null);
        return ink;
    }

    private static Drawing Plate(Glyph glyph, Brush brush)
    {
        var pen = PlatePen(brush);
        var group = new DrawingGroup();
        foreach (var data in new[] { glyph.Shape, glyph.Lines, glyph.Details })
            if (data.Length > 0) group.Children.Add(new GeometryDrawing(null, pen, Parse(data)));
        return group;
    }

    private static Drawing Keycap(Glyph glyph, Brush brush) => new GeometryDrawing(brush, null, KeycapInk(glyph));

    private static Geometry KeycapInk(Glyph glyph)
    {
        Geometry ink = Geometry.Empty;
        if (glyph.Shape.Length > 0)
        {
            var shape = Parse(glyph.Shape);
            ink = Geometry.Combine(shape, shape.GetWidenedPathGeometry(RoundPen(1.5)), GeometryCombineMode.Union, null);
        }
        if (glyph.Lines.Length > 0)
            ink = Geometry.Combine(ink, Parse(glyph.Lines).GetWidenedPathGeometry(RoundPen(2.4)), GeometryCombineMode.Union, null);
        // Details are cut through the ink, so they read on any surface the key sits on.
        if (glyph.Details.Length > 0)
            ink = Geometry.Combine(ink, Parse(glyph.Details).GetWidenedPathGeometry(RoundPen(1.75)), GeometryCombineMode.Exclude, null);
        return ink;
    }

    private static Pen PlatePen(Brush brush)
    {
        var pen = new Pen(brush, 2)
        {
            StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square,
            LineJoin = PenLineJoin.Miter, MiterLimit = 4
        };
        pen.Freeze();
        return pen;
    }

    private static Pen RoundPen(double width)
    {
        var pen = new Pen(Brushes.Black, width)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    // SVG path data fills nonzero; WPF's mini-language defaults to even-odd unless told F1.
    private static Geometry Parse(string data) => Geometry.Parse("F1 " + data);

    private static string F(double n) => Math.Round(n, 2).ToString(CultureInfo.InvariantCulture);

    private static string C(double cx, double cy, double r) =>
        $"M{F(cx - r)} {F(cy)}a{F(r)} {F(r)} 0 1 0 {F(2 * r)} 0a{F(r)} {F(r)} 0 1 0 {F(-2 * r)} 0Z";

    private static string R(double x, double y, double w, double h, double r) => r == 0
        ? $"M{F(x)} {F(y)}h{F(w)}v{F(h)}h{F(-w)}Z"
        : $"M{F(x + r)} {F(y)}h{F(w - 2 * r)}a{F(r)} {F(r)} 0 0 1 {F(r)} {F(r)}v{F(h - 2 * r)}"
          + $"a{F(r)} {F(r)} 0 0 1 {F(-r)} {F(r)}h{F(2 * r - w)}a{F(r)} {F(r)} 0 0 1 {F(-r)} {F(-r)}"
          + $"v{F(2 * r - h)}a{F(r)} {F(r)} 0 0 1 {F(r)} {F(-r)}Z";

    private static string P(bool closed, params (double X, double Y)[] points) =>
        "M" + string.Join("L", points.Select(p => F(p.X) + " " + F(p.Y))) + (closed ? "Z" : "");

    private static string Gear(double cx, double cy, double outer, double inner, int teeth)
    {
        var step = 2 * Math.PI / teeth;
        var points = new List<(double, double)>();
        for (var i = 0; i < teeth; i++)
        {
            var t = i * step - Math.PI / 2;
            foreach (var (radius, offset) in new[] { (inner, -0.3), (outer, -0.16), (outer, 0.16), (inner, 0.3) })
                points.Add((cx + radius * Math.Cos(t + offset * step), cy + radius * Math.Sin(t + offset * step)));
        }
        return P(true, [.. points]);
    }

    private static string Wrench(double cx, double cy, double degrees, double k)
    {
        var t = degrees * Math.PI / 180;
        var (c, s) = (Math.Cos(t), Math.Sin(t));
        string Q(double x, double y) => F(cx + k * (x * c - y * s)) + " " + F(cy + k * (x * s + y * c));
        var r = F(3.2 * k);
        var e = F(1.25 * k);
        return $"M{Q(-7.5, -1.25)}L{Q(3.55, -1.25)}A{r} {r} 0 0 1 {Q(9.47, -1.2)}L{Q(6.2, -1.2)}L{Q(6.2, 1.2)}"
               + $"L{Q(9.47, 1.2)}A{r} {r} 0 0 1 {Q(3.55, 1.25)}L{Q(-7.5, 1.25)}A{e} {e} 0 0 1 {Q(-7.5, -1.25)}Z";
    }

    private static Glyph G(string key, string s = "", string l = "", string d = "") => new(key, s, l, d);

    private static string J(params string[] parts) => string.Concat(parts);

    private static IReadOnlyList<Glyph> BuildGlyphs()
    {
        var truck = R(2, 5.5, 12, 10.5, 1);
        var flutes = P(true, (9, 8.5), (15, 8.5), (15, 19.5), (12, 22), (9, 19.5));
        var cell = R(3, 3, 7.5, 7.5, 1.5);
        var clip = R(8.5, 2.2, 7, 3.6, 1);
        var head = C(9, 8, 3.5);
        var disc = C(12, 12, 9);
        var calendar = R(3.5, 5, 17, 16, 2);
        var tray = "M4 14.5v4a1.5 1.5 0 0 0 1.5 1.5h13a1.5 1.5 0 0 0 1.5-1.5v-4";
        return
        [
            // Menus. Cases is the part, Setup Queue a wrench, QC Queue a caliper, Tool Room an end mill.
            G("Cases", s: P(true, (12, 2.8), (20.2, 7.4), (20.2, 16.6), (12, 21.2), (3.8, 16.6), (3.8, 7.4)),
                d: "M4.6 7.85L12 12l7.4-4.15M12 12v8.3"),
            G("PlanningBoard", s: J(R(3, 4, 5, 7, 1), R(3, 13, 5, 5, 1), R(9.5, 4, 5, 10, 1), R(16, 4, 5, 5, 1), R(16, 11, 5, 8, 1)),
                l: "M3 21.5h18"),
            G("Timeline", l: "M3.5 3v17.5H21", s: J(R(7, 5, 8, 3.2, 1), R(10, 10.4, 9, 3.2, 1), R(7, 15.8, 6, 3.2, 1))),
            G("MaterialOrders", s: J(truck, P(true, (14, 8.5), (18.2, 8.5), (21.5, 12), (21.5, 16), (14, 16)), C(6.5, 18.3, 2), C(17, 18.3, 2)),
                d: J(C(5.6, 9.2, 1.3), C(9.8, 9.2, 1.3), C(7.7, 12.6, 1.3))),
            G("ShiftRoster", s: calendar, l: "M8 3v4M16 3v4", d: J("M4.4 10h15.2", C(12, 13.8, 1.7), "M8.8 19a3.2 3.2 0 0 1 6.4 0")),
            G("Reports", s: J(R(5.5, 12, 3.2, 8, 0.6), R(10.4, 6, 3.2, 14, 0.6), R(15.3, 9.5, 3.2, 10.5, 0.6)), l: "M3 21.5h18"),
            G("SetupQueue", s: Wrench(11.7, 12.3, -45, 1.05)),
            G("QcQueue", s: J(R(2.5, 4.5, 19, 4, 1), P(true, (3.5, 8.5), (7, 8.5), (7, 19.5)), P(true, (9, 8.5), (12.5, 8.5), (9, 19.5))),
                l: "M5 4.5V2.2M10.5 4.5V2.2", d: "M14.5 4.5v2M17 4.5v2M19.5 4.5v2"),
            G("MachineUsage", l: "M4 17.5a8 8 0 1 1 16 0M12 9.5V11M6.34 11.84l1.06 1.06M17.66 11.84l-1.06 1.06M12 17.5l3.8-5",
                s: C(12, 17.5, 1.6)),
            G("DprntLog", s: R(2.5, 4, 19, 16, 2), d: "M6.5 9l3 3-3 3M12 15h5.5"),
            G("ToolRoom", s: J(R(8, 2, 8, 6.5, 1), flutes), d: "M9 12.2l6-2.4M9 15.8l6-2.4M9 19.4l6-2.4"),
            G("ToolCatalog", s: J(cell, R(13.5, 3, 7.5, 7.5, 1.5), R(3, 13.5, 7.5, 7.5, 1.5), R(15.2, 13, 4.6, 3.4, 0.6),
                P(true, (15.8, 16.4), (19.2, 16.4), (19.2, 20.2), (17.5, 21.8), (15.8, 20.2)))),
            G("ToolRequirements", s: J("M8.5 4H6a1.5 1.5 0 0 0-1.5 1.5v15A1.5 1.5 0 0 0 6 22h12a1.5 1.5 0 0 0 1.5-1.5v-15A1.5 1.5 0 0 0 18 4h-2.5", clip),
                d: "M7.5 11.5l1.5 1.5 2.5-2.8M13.5 11.8h3.5M7.5 16.5l1.5 1.5 2.5-2.8M13.5 16.8h3.5"),
            G("NcCreator", s: "M14 2.5H6.5A1.5 1.5 0 0 0 5 4v16a1.5 1.5 0 0 0 1.5 1.5h11A1.5 1.5 0 0 0 19 20V7.5Z",
                d: "M14 2.5V7.5H19M8 18v-4.5h3v3h2.5V11H16"),
            G("UserTerminals", s: R(5, 2, 14, 20, 2), d: "M8 6h8M8 9.5h8M8 13h5M11 18.5h2"),
            G("Users", s: J(head, "M2.5 20.5a6.5 6.5 0 0 1 13 0Z"), l: "M15.5 4.6a3.5 3.5 0 0 1 0 6.8M18 14.2a6.5 6.5 0 0 1 3.5 6.3"),
            G("Settings", s: Gear(12, 12, 9.5, 7.2, 8), d: C(12, 12, 3)),
            G("Integrations", s: R(6.5, 7.5, 11, 7.5, 2), l: "M9.5 7.5V3M14.5 7.5V3M12 15v2.5a3 3 0 0 1-3 3H6.5"),
            // Administration pages.
            G("Machine", s: R(2.5, 3, 19, 15, 1.5), l: "M5.5 18v3M18.5 18v3",
                d: J(R(5, 5.5, 9.5, 10, 0.8), "M9.75 5.5v4.5M17.5 7v1.5M17.5 11v1.5")),
            G("Calendar", s: calendar, l: "M8 3v4M16 3v4", d: "M4.4 10h15.2M8 13.8h1M11.5 13.8h1M15 13.8h1M8 17.3h1M11.5 17.3h1"),
            G("Worker", s: J("M7 8.5a5 5 0 0 1 10 0Z", "M5 22.5a7 7 0 0 1 14 0Z"), l: "M5.5 8.5h13M9.2 10.3a2.8 2.8 0 0 0 5.6 0",
                d: "M12 4.6v2.2"),
            G("Portal", s: disc, d: "M12 3a4.5 9 0 0 1 0 18a4.5 9 0 0 1 0-18M3.9 12h16.2M5 7.5h14M5 16.5h14"),
            G("Operations", s: J(R(3.5, 4, 4.5, 4.5, 1), R(3.5, 9.75, 4.5, 4.5, 1), R(3.5, 15.5, 4.5, 4.5, 1)),
                l: "M11 6.25h9.5M11 12h9.5M11 17.75h9.5"),
            G("Orders", s: "M6 2.5h12a1.5 1.5 0 0 1 1.5 1.5v17l-2.5-1.5-2.5 1.5-2.5-1.5-2.5 1.5-2.5-1.5-2.5 1.5V4A1.5 1.5 0 0 1 6 2.5Z",
                d: "M8 7.5h8M8 11h8M8 14.5h5"),
            // Buttons.
            G("Add", l: "M12 5v14M5 12h14"),
            G("Edit", s: P(true, (15.5, 4.5), (19.5, 8.5), (9, 19), (4.5, 19.5), (5, 15)), d: "M13.5 6.5l4 4"),
            G("Save", s: "M5.5 3.5H16l3.5 3.5v12.5a1 1 0 0 1-1 1h-13a1 1 0 0 1-1-1v-15a1 1 0 0 1 1-1Z", d: "M8 3.5V8h7V3.5M7.5 20.5V14h9v6.5"),
            G("Delete", s: "M6 7.5h12l-1 12.6a1.5 1.5 0 0 1-1.5 1.4h-7A1.5 1.5 0 0 1 7 20.1Z", l: "M3.5 7.5h17M9.5 7.5v-3h5v3",
                d: "M10 11v6.5M14 11v6.5"),
            G("Cancel", s: disc, d: "M9 9l6 6M15 9l-6 6"),
            G("Pin", s: "M9 3.5h6l-.8 6.2 3.3 3.3v1.5h-11V13l3.3-3.3Z", l: "M12 14.5v6.5"),
            G("Search", l: J(C(10.5, 10.5, 6.5), "M15.3 15.3l5.2 5.2")),
            G("Filter", s: P(true, (3.5, 4.5), (20.5, 4.5), (14, 12.5), (14, 19.5), (10, 21), (10, 12.5))),
            G("Refresh", l: "M19.5 12a7.5 7.5 0 0 1-12.8 5.3M4.5 12a7.5 7.5 0 0 1 12.8-5.3M17.5 2.8v4.1h-4.1M6.5 21.2v-4.1h4.1"),
            G("Import", l: J(tray, "M12 3.5v11M7.5 10l4.5 4.5 4.5-4.5")),
            G("Export", l: J(tray, "M12 15V4M7.5 8.5L12 4l4.5 4.5")),
            G("Browse", s: "M3 6.5A1.5 1.5 0 0 1 4.5 5h4.2l2 2.2h8.8a1.5 1.5 0 0 1 1.5 1.5v9.8a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18.5Z",
                d: "M3.9 10.5h16.2"),
            G("Start", s: P(true, (7.5, 4.5), (19, 12), (7.5, 19.5))),
            G("Pause", s: J(R(6.5, 5, 3.8, 14, 1), R(13.7, 5, 3.8, 14, 1))),
            G("Finish", l: "M5 21.5V3.5", s: "M5 4h13l-2.5 4.5L18 13H5Z"),
            G("Confirm", s: disc, d: "M8 12.3l2.8 2.8 5.4-5.6"),
            G("Back", l: "M19.5 12h-15M10.5 6l-6 6 6 6"),
            G("Next", l: "M4.5 12h15M13.5 6l6 6-6 6"),
            G("OpenWindow", s: R(2.5, 9, 12, 12.5, 1.5), l: "M14 3h7v7M21 3l-6.5 6.5", d: "M3.4 12.5h10.2"),
        ];
    }
}
