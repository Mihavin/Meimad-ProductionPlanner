using System.Globalization;
using Meimad.Planner.Server.Application.LegacyImport;
using Meimad.Planner.Server.Domain.LegacyImport;
using Meimad.Planner.Server.Domain.ToolCatalog;
using Meimad.Planner.Server.Domain.ToolPreparations;

namespace Meimad.Planner.Server.Application.ToolCatalog;

/// <summary>
/// One cutter row of a Cimatron cutter workbook (NC-Process &gt; Cutters &gt; Menu &gt; Export / Import,
/// "XLS" format). Lengths are millimetres; an inch workbook is converted on read.
/// </summary>
internal sealed record CimatronCutter(
    int RowNumber,
    string Name,
    string? Comment,
    string? Technology,
    string? Tip,
    string? CatalogName,
    string? ThreadType,
    double? Diameter,
    double? CornerRadius,
    double? ClearLength,
    double? CutLength,
    bool Taper,
    double? TaperAngle,
    double? TipAngle,
    double? ShaftDiameter,
    double? Pitch,
    bool Shank,
    double? ShankTopDiameter,
    string? HolderName,
    double? Teeth);

internal sealed record CimatronCutterWorkbook(string Units, IReadOnlyList<CimatronCutter> Cutters);

/// <summary>A catalog tool described by a Cimatron cutter, before it is merged into an existing tool.</summary>
internal sealed record CimatronCatalogValues(
    string ToolType,
    string? Description,
    IReadOnlyDictionary<string, double> Shape,
    IReadOnlyDictionary<string, string> Attributes);

/// <summary>
/// The Cimatron cutter workbook: the "Cutters" sheet of Cimatron's External Cutters template,
/// whose sixth row holds Cimatron's parameter ids (1101 Cutter Name, 2105 Diameter, ...) and whose
/// cutters start two rows below it. Columns are found by id, not by position or localized name,
/// and the mapping to catalog tools lives here in both directions.
/// </summary>
internal static class CimatronCutterLibrary
{
    /// <summary>The external id system a catalog tool's Cimatron cutter name is kept under.</summary>
    internal const string ExternalSystem = "Cimatron";

    internal const string CuttersSheet = "Cutters";
    internal const int IdRow = 6;
    internal const int FirstCutterRow = 8;
    private const int MaximumCutters = 5000;
    private const double MillimetresPerInch = 25.4;

    internal const int NameId = 1101;
    internal const int CommentId = 1102;
    internal const int TechnologyId = 2101;
    internal const int TipId = 2102;
    internal const int CatalogNameId = 2103;
    internal const int ThreadTypeId = 2104;
    internal const int DiameterId = 2105;
    internal const int CornerRadiusId = 2106;
    internal const int UseFullLengthId = 2107;
    internal const int ClearLengthId = 2109;
    internal const int CutLengthId = 2110;
    internal const int TaperId = 2111;
    internal const int TaperAngleId = 2112;
    internal const int TipAngleId = 2113;
    internal const int ShaftDiameterId = 2118;
    internal const int PitchId = 2123;
    internal const int UseShaftDiameterId = 2125;
    internal const int ShankId = 2201;
    internal const int ShankTopDiameterId = 2202;
    internal const int ShankBottomDiameterId = 2203;
    internal const int UseShankConeAngleId = 2204;
    internal const int ShankConeLengthId = 2206;
    internal const int Shank2Id = 2208;
    internal const int HolderNameId = 3101;
    internal const int TeethId = 4106;
    internal const int UseShapeId = 4210;
    internal const int ReferenceHeightId = 4211;

    /// <summary>Catalog dimensions a Cimatron cutter owns; an update replaces these and keeps the others.</summary>
    internal static readonly IReadOnlySet<string> OwnedShapeKeys = new HashSet<string>(
        ["cuttingDiameter", "tipDiameter", "cornerRadius", "fluteLength", "neckLength", "shankDiameter",
         "pointAngle", "taperAngle", "pitch", "fluteCount"],
        StringComparer.Ordinal);

    /// <summary>Catalog attributes a Cimatron cutter owns (the holder and the tap's thread).</summary>
    internal static readonly IReadOnlyList<string> OwnedAttributeKeys = ["holderCode", "threadProfile"];

    internal static CimatronCutterWorkbook Read(LegacyWorkbookData workbook)
    {
        var sheet = workbook.Sheets.FirstOrDefault(entry => entry.Name.Equals(CuttersSheet, StringComparison.OrdinalIgnoreCase))
            ?? throw Invalid("cimatron_cutters_sheet_missing",
                "The workbook has no 'Cutters' sheet. Export the cutters from Cimatron (NC-Process > Cutters, Menu > Export, XLS format).");

        var columns = new Dictionary<int, int>();
        if (sheet.Rows.TryGetValue(IdRow, out var ids))
        {
            foreach (var (column, cell) in ids)
            {
                if (int.TryParse(cell.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) columns.TryAdd(id, column);
            }
        }
        if (!columns.ContainsKey(NameId) || !columns.ContainsKey(DiameterId))
            throw Invalid("cimatron_cutter_ids_missing",
                "The 'Cutters' sheet has no Cimatron parameter id row (row 6 with 1101 Cutter Name and 2105 Diameter).");

        var inch = false;
        if (sheet.Rows.TryGetValue(3, out var header))
        {
            var cells = header.OrderBy(entry => entry.Key).Select(entry => entry.Value.Value?.Trim()).ToList();
            var units = cells.FindIndex(text => text is not null && text.StartsWith("Units", StringComparison.OrdinalIgnoreCase));
            inch = units >= 0 && units + 1 < cells.Count && string.Equals(cells[units + 1], "inch", StringComparison.OrdinalIgnoreCase);
        }
        var scale = inch ? MillimetresPerInch : 1;

        var cutters = new List<CimatronCutter>();
        foreach (var (rowNumber, row) in sheet.Rows.Where(entry => entry.Key >= FirstCutterRow).OrderBy(entry => entry.Key))
        {
            string? Text(int id) => columns.TryGetValue(id, out var column) && row.TryGetValue(column, out var cell)
                ? string.IsNullOrWhiteSpace(cell.Value) ? null : cell.Value.Trim()
                : null;
            double? Number(int id) => double.TryParse(Text(id), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
                ? value
                : null;
            double? Length(int id) => Number(id) is { } value ? Math.Round(value * scale, 4) : null;
            bool Flag(int id) => Text(id) is { } value && (value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

            var name = Text(NameId);
            if (name is null) continue;
            if (cutters.Count >= MaximumCutters)
                throw Invalid("cimatron_too_many_cutters", $"A Cimatron cutter workbook may hold at most {MaximumCutters} cutters.");
            var comment = Text(CommentId);
            cutters.Add(new CimatronCutter(
                rowNumber,
                name,
                comment is null || comment.Equals("No comment", StringComparison.OrdinalIgnoreCase) ? null : comment,
                Text(TechnologyId),
                Text(TipId),
                Text(CatalogNameId),
                Text(ThreadTypeId),
                Length(DiameterId),
                Length(CornerRadiusId),
                Length(ClearLengthId),
                Length(CutLengthId),
                Flag(TaperId),
                Number(TaperAngleId),
                Number(TipAngleId),
                Length(ShaftDiameterId),
                Length(PitchId),
                Flag(ShankId),
                Length(ShankTopDiameterId),
                Text(HolderNameId),
                Number(TeethId)));
        }
        return new CimatronCutterWorkbook(inch ? "inch" : "mm", cutters);
    }

    /// <summary>The catalog type of a Cimatron technology and tip.</summary>
    internal static string ToolType(CimatronCutter cutter)
    {
        var technology = (cutter.Technology ?? "Milling").Trim().ToLowerInvariant();
        var tip = (cutter.Tip ?? string.Empty).Trim().ToLowerInvariant();
        return technology switch
        {
            "special lollipop" => "LOLLIPOP_MILL",
            "special slot mill" => "SLOT_MILL",
            "special dove mill" => "DOVETAIL_MILL",
            "special counter sink" => "COUNTERSINK",
            "probe" => "PROBE",
            "thread mill" => "THREAD_MILL",
            "drilling" => tip switch
            {
                "ream" => "REAMER",
                "tap" => "TAP",
                "center" => "CENTER_DRILL",
                _ => "DRILL"
            },
            "milling" => tip switch
            {
                "ball" or "full radius" => "BALL_END_MILL",
                "bull" or "corner radius" => "BULL_NOSE_END_MILL",
                "center" => "CENTER_DRILL",
                "drilling" => "DRILL",
                "ream" => "REAMER",
                "tap" => "TAP",
                _ => cutter.Taper ? "CHAMFER_MILL" : "END_MILL"
            },
            _ => "OTHER"
        };
    }

    /// <summary>The catalog description of a Cimatron cutter.</summary>
    internal static CimatronCatalogValues ToCatalog(CimatronCutter cutter)
    {
        var type = ToolType(cutter);
        var shape = new SortedDictionary<string, double>(StringComparer.Ordinal);
        void Put(string key, double? value)
        {
            if (value is { } number && number > 0) shape[key] = Math.Round(number, 4);
        }

        if (type == "CHAMFER_MILL")
        {
            // Cimatron draws a chamfer mill as a tapered flat cutter: a small tip diameter that
            // widens at the taper angle up to the shaft diameter.
            Put("cuttingDiameter", cutter.ShaftDiameter ?? cutter.Diameter);
            Put("tipDiameter", cutter.Diameter);
        }
        else
        {
            Put("cuttingDiameter", cutter.Diameter);
        }
        if (type is not ("DRILL" or "REAMER" or "TAP" or "CENTER_DRILL")) Put("cornerRadius", cutter.CornerRadius);
        Put("fluteLength", cutter.CutLength);
        Put("neckLength", cutter.ClearLength);
        if (cutter.Shank) Put("shankDiameter", cutter.ShankTopDiameter);
        else if (type == "CHAMFER_MILL") Put("shankDiameter", cutter.ShaftDiameter);
        if (type is "DRILL" or "CENTER_DRILL") Put("pointAngle", cutter.TipAngle);
        if (cutter.Taper) Put("taperAngle", cutter.TaperAngle);
        if (type == "THREAD_MILL") Put("pitch", cutter.Pitch);
        if (cutter.Teeth is { } teeth && teeth >= 1 && teeth <= 100 && Math.Abs(teeth - Math.Round(teeth)) < 1e-9) shape["fluteCount"] = Math.Round(teeth);

        var attributes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (cutter.HolderName is { } holder) attributes["holderCode"] = holder;
        if (type == "TAP" && cutter.ThreadType is { } thread) attributes["threadProfile"] = thread;

        return new CimatronCatalogValues(type, cutter.Comment, shape, attributes);
    }

    /// <summary>
    /// The Cimatron cutter row of a catalog tool, keyed by Cimatron parameter id; null for tools
    /// Cimatron's milling cutter table cannot hold (turning tools and "other").
    /// </summary>
    internal static IReadOnlyDictionary<int, object>? ToCimatron(CatalogTool tool)
    {
        if (CutterKind(tool.ToolType) is not { } kind) return null;
        var (technology, tip, chamfer) = kind;

        double? Value(string key) => tool.Shape.TryGetValue(key, out var value) && value > 0 ? value : null;
        var name = tool.ExternalIds.FirstOrDefault(entry => entry.System.Equals(ExternalSystem, StringComparison.OrdinalIgnoreCase))?.Value ?? tool.Name;
        var row = new Dictionary<int, object>
        {
            [NameId] = name,
            [TechnologyId] = technology,
            [UseFullLengthId] = false,
            [ShankId] = false,
            [Shank2Id] = 0d,
            [UseShapeId] = false,
            [ReferenceHeightId] = 0d
        };
        if (tip is not null) row[TipId] = tip;
        if (tool.Description is { } description) row[CommentId] = description;

        var cutting = Value("cuttingDiameter");
        if (chamfer)
        {
            // Cimatron's tapered flat cutter; a tool without a tip diameter gets Cimatron's own 0.1 mm point.
            row[DiameterId] = Value("tipDiameter") ?? 0.1;
            row[TaperId] = true;
            row[TaperAngleId] = Value("taperAngle") ?? 45d;
            if (cutting is { } shaft)
            {
                row[ShaftDiameterId] = shaft;
                row[UseShaftDiameterId] = 1d;
            }
        }
        else
        {
            if (cutting is { } diameter) row[DiameterId] = diameter;
            if (tip is "Flat" or "Ball" or "Bull") row[TaperId] = false;
        }

        // A shank wider than the cutter: Cimatron's first shank, a cone from the cutter diameter up to
        // the shank. The catalog keeps no cone length, so the cone rises at about 26.6 degrees
        // (its length is the diameter step); Cimatron's own lengths are not round-tripped.
        if (!chamfer && Value("shankDiameter") is { } shank && cutting is { } body && shank > body + 1e-6)
        {
            row[ShankId] = true;
            row[ShankTopDiameterId] = shank;
            row[ShankBottomDiameterId] = body;
            row[UseShankConeAngleId] = false;
            row[ShankConeLengthId] = Math.Round(shank - body, 4);
        }

        var corner = Value("cornerRadius");
        if (tool.ToolType == "BALL_END_MILL") corner ??= cutting / 2;
        if (tip is "Flat" or "Ball" or "Bull") row[CornerRadiusId] = corner ?? 0d;

        var flute = Value("fluteLength");
        if (flute is { } cut) row[CutLengthId] = cut;
        if ((Value("neckLength") ?? flute) is { } clear) row[ClearLengthId] = clear;
        if (tool.ToolType is "DRILL" or "SPOT_DRILL" or "CENTER_DRILL" && Value("pointAngle") is { } point) row[TipAngleId] = point;
        if (tool.ToolType == "THREAD_MILL" && Value("pitch") is { } pitch) row[PitchId] = pitch;
        if (Value("fluteCount") is { } teeth) row[TeethId] = teeth;
        if (tool.Attributes.TryGetValue("holderCode", out var holder)) row[HolderNameId] = holder;
        if (tool.ToolType == "TAP" && tool.Attributes.TryGetValue("threadProfile", out var thread))
        {
            row[ThreadTypeId] = thread;
            if (ThreadCatalog(thread) is { } catalog) row[CatalogNameId] = catalog;
        }
        return row;
    }

    /// <summary>
    /// The type a Cimatron cutter gives the catalog tool it updates. Cimatron has one cutter kind
    /// for several catalog types (a face mill, counterbore or boring head is a flat mill, an
    /// engraver a tapered flat mill, a T-slot mill a slot mill, a spot drill a center drill), so a
    /// cutter of the kind the tool already exports as keeps the tool's own type; a cutter of another
    /// kind (a ball tip for an end mill) sets the type Cimatron describes.
    /// </summary>
    internal static string MergedToolType(string existingToolType, string cutterToolType) =>
        CutterKind(existingToolType) is { } kind && kind == CutterKind(cutterToolType) ? existingToolType : cutterToolType;

    /// <summary>
    /// Cimatron's technology, tip and taper for a catalog type; null for types Cimatron's milling
    /// cutter table cannot hold (turning tools and "other").
    /// </summary>
    private static (string Technology, string? Tip, bool Chamfer)? CutterKind(string toolType) => toolType switch
    {
        "END_MILL" or "FACE_MILL" or "COUNTERBORE" or "BORING_HEAD" => ("Milling", "Flat", false),
        "CHAMFER_MILL" or "ENGRAVER" => ("Milling", "Flat", true),
        "BALL_END_MILL" => ("Milling", "Ball", false),
        "BULL_NOSE_END_MILL" => ("Milling", "Bull", false),
        "SLOT_MILL" or "T_SLOT_MILL" => ("Special Slot Mill", null, false),
        "DOVETAIL_MILL" => ("Special Dove Mill", null, false),
        "LOLLIPOP_MILL" => ("Special Lollipop", null, false),
        "THREAD_MILL" => ("Thread mill", null, false),
        "COUNTERSINK" => ("Special Counter Sink", null, false),
        "DRILL" => ("Drilling", "Drilling", false),
        "SPOT_DRILL" or "CENTER_DRILL" => ("Drilling", "Center", false),
        "REAMER" => ("Drilling", "Ream", false),
        "TAP" => ("Drilling", "Tap", false),
        "PROBE" => ("Probe", null, false),
        _ => null
    };

    /// <summary>Cimatron's thread catalog of a thread designation: "M6" is metric, "#0-80 UNF" is UNF.</summary>
    private static string? ThreadCatalog(string thread)
    {
        var text = thread.Trim().ToUpperInvariant();
        foreach (var family in new[] { "UNEF", "UNF", "UNC", "NPT", "BSPT", "BSP" })
        {
            if (text.Contains(family, StringComparison.Ordinal)) return family;
        }
        return text.Length > 1 && text[0] == 'M' && (char.IsDigit(text[1]) || text[1] == ' ') ? "M" : null;
    }

    private static LegacyWorkbookFormatException Invalid(string code, string message) => new(code, message);
}
