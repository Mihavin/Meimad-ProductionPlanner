using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Meimad.Planner.Server.Domain.ToolRequirements;

namespace Meimad.Planner.Server.Application.ToolRequirements;

/// <summary>
/// The tool requirements of a period as an Excel workbook for the Tool Room and purchasing: a Tools
/// sheet (one row per tool and material group, with the copies needed and their route between
/// Machines), a Uses sheet (every planned operation that uses each tool) and a sheet of the planned
/// operations that have no released tool table. Times are in the factory's display time zone.
/// </summary>
internal static class ToolRequirementWorkbookWriter
{
    internal const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace DocumentRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";

    private static readonly IReadOnlyDictionary<string, string> MaterialLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [MaterialGroups.Aluminum] = "Aluminum",
        [MaterialGroups.Titanium] = "Titanium",
        [MaterialGroups.Stainless] = "Stainless steel",
        [MaterialGroups.Nickel] = "Nickel alloy",
        [MaterialGroups.Steel] = "Steel",
        [MaterialGroups.Copper] = "Copper alloy",
        [MaterialGroups.Plastic] = "Plastic",
        [MaterialGroups.Unknown] = "Unknown material"
    };

    internal static byte[] Write(ToolRequirementReport report)
    {
        var zone = TimeZone(report.TimeZoneId);
        string Time(DateTimeOffset value) =>
            TimeZoneInfo.ConvertTime(value, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        var tools = new List<object?[]>
        {
            new object?[] { "Material", "Type", "Diameter (mm)", "Tool", "Copies needed", "Machines", "Uses", "Machine changes", "First need", "Last need", "Route of the copies" }
        };
        tools.AddRange(report.Tools.Select(tool => new object?[]
        {
            Material(tool.MaterialGroup), TypeLabel(tool.ToolType), tool.Diameter, tool.ToolName, tool.CopiesNeeded,
            string.Join(", ", tool.Machines), tool.Uses.Count, tool.MachineChanges, Time(tool.FirstNeed), Time(tool.LastNeed),
            string.Join("; ", tool.Routes.Select(route =>
                $"Copy {route.CopyNumber}: " + string.Join(" > ", route.Stops.Select(stop =>
                    $"{stop.MachineLabel} ({Time(stop.From)} - {Time(stop.To)})"))))
        }));

        var uses = new List<object?[]>
        {
            new object?[] { "Material", "Tool", "Diameter (mm)", "Work Order", "Part", "Operation", "Operation name", "Machine", "Tool numbers", "Holder", "Stick-out (mm)", "Part material", "From", "To" }
        };
        uses.AddRange(report.Tools.SelectMany(tool => tool.Uses).Select(use => new object?[]
        {
            Material(use.MaterialGroup), use.ToolName, use.Diameter, use.WorkOrderNumber, use.PartNumber, use.OperationNumber,
            use.OperationName, use.MachineLabel, string.Join(", ", use.ToolNumbers), use.Holder, use.Length, use.Material,
            Time(use.StartsAt), Time(use.EndsAt)
        }));

        var missing = new List<object?[]>
        {
            new object?[] { "Work Order", "Part", "Operation", "Operation name", "Machine", "Material", "From", "To" }
        };
        missing.AddRange(report.OperationsWithoutToolTable.Select(operation => new object?[]
        {
            operation.WorkOrderNumber, operation.PartNumber, operation.OperationNumber, operation.OperationName,
            operation.MachineLabel, Material(operation.MaterialGroup), Time(operation.StartsAt), Time(operation.EndsAt)
        }));

        var sheets = new (string Name, List<object?[]> Rows, double[] Widths)[]
        {
            ("Tools", tools, [16, 18, 12, 26, 13, 34, 8, 15, 17, 17, 90]),
            ("Uses", uses, [16, 26, 12, 13, 22, 10, 30, 24, 16, 22, 13, 40, 17, 17]),
            ("No tool table", missing, [13, 22, 10, 30, 24, 16, 17, 17])
        };

        var strings = new SharedStrings();
        var sheetXml = sheets.Select(sheet => Sheet(sheet.Rows, sheet.Widths, strings)).ToArray();

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Save(archive, "[Content_Types].xml", new XElement(ContentTypes + "Types",
                new XElement(ContentTypes + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(ContentTypes + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                new XElement(ContentTypes + "Override", new XAttribute("PartName", "/xl/workbook.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                new XElement(ContentTypes + "Override", new XAttribute("PartName", "/xl/styles.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml")),
                new XElement(ContentTypes + "Override", new XAttribute("PartName", "/xl/sharedStrings.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml")),
                sheets.Select((_, index) => new XElement(ContentTypes + "Override",
                    new XAttribute("PartName", $"/xl/worksheets/sheet{index + 1}.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")))));
            Save(archive, "_rels/.rels", new XElement(PackageRelationships + "Relationships",
                new XElement(PackageRelationships + "Relationship", new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                    new XAttribute("Target", "xl/workbook.xml"))));
            Save(archive, "xl/workbook.xml", new XElement(Main + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", DocumentRelationships),
                new XElement(Main + "sheets", sheets.Select((sheet, index) => new XElement(Main + "sheet",
                    new XAttribute("name", sheet.Name), new XAttribute("sheetId", index + 1),
                    new XAttribute(DocumentRelationships + "id", $"rId{index + 1}"))))));
            Save(archive, "xl/_rels/workbook.xml.rels", new XElement(PackageRelationships + "Relationships",
                sheets.Select((_, index) => new XElement(PackageRelationships + "Relationship",
                    new XAttribute("Id", $"rId{index + 1}"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                    new XAttribute("Target", $"worksheets/sheet{index + 1}.xml"))),
                new XElement(PackageRelationships + "Relationship", new XAttribute("Id", $"rId{sheets.Length + 1}"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"),
                    new XAttribute("Target", "styles.xml")),
                new XElement(PackageRelationships + "Relationship", new XAttribute("Id", $"rId{sheets.Length + 2}"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings"),
                    new XAttribute("Target", "sharedStrings.xml"))));
            Save(archive, "xl/styles.xml", Styles());
            Save(archive, "xl/sharedStrings.xml", strings.ToXml());
            for (var index = 0; index < sheetXml.Length; index++)
            {
                Save(archive, $"xl/worksheets/sheet{index + 1}.xml", sheetXml[index]);
            }
        }
        return output.ToArray();
    }

    private static XElement Sheet(IReadOnlyList<object?[]> rows, double[] widths, SharedStrings strings)
    {
        var columns = rows[0].Length;
        var last = $"{Column(columns - 1)}{rows.Count}";
        return new XElement(Main + "worksheet",
            new XElement(Main + "sheetViews", new XElement(Main + "sheetView", new XAttribute("workbookViewId", 0),
                new XElement(Main + "pane", new XAttribute("ySplit", 1), new XAttribute("topLeftCell", "A2"),
                    new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")))),
            new XElement(Main + "cols", widths.Select((width, index) => new XElement(Main + "col",
                new XAttribute("min", index + 1), new XAttribute("max", index + 1),
                new XAttribute("width", width.ToString(CultureInfo.InvariantCulture)), new XAttribute("customWidth", 1)))),
            new XElement(Main + "sheetData", rows.Select((row, rowIndex) => new XElement(Main + "row",
                new XAttribute("r", rowIndex + 1),
                row.Select((value, columnIndex) => Cell($"{Column(columnIndex)}{rowIndex + 1}", value, rowIndex == 0, strings))))),
            new XElement(Main + "autoFilter", new XAttribute("ref", $"A1:{last}")));
    }

    private static XElement Cell(string reference, object? value, bool header, SharedStrings strings)
    {
        var cell = new XElement(Main + "c", new XAttribute("r", reference));
        if (header) cell.SetAttributeValue("s", 1);
        switch (value)
        {
            case null:
                break;
            case int number:
                cell.Add(new XElement(Main + "v", number.ToString(CultureInfo.InvariantCulture)));
                break;
            case double number:
                cell.Add(new XElement(Main + "v", number.ToString("R", CultureInfo.InvariantCulture)));
                break;
            default:
                cell.SetAttributeValue("t", "s");
                cell.Add(new XElement(Main + "v", strings.Index(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)));
                break;
        }
        return cell;
    }

    /// <summary>The workbook's shared-string table: each distinct text once, referenced by index.</summary>
    private sealed class SharedStrings
    {
        private readonly Dictionary<string, int> indexes = new(StringComparer.Ordinal);
        private readonly List<string> values = [];
        private int references;

        internal int Index(string text)
        {
            references++;
            if (indexes.TryGetValue(text, out var index)) return index;
            indexes[text] = values.Count;
            values.Add(text);
            return values.Count - 1;
        }

        internal XElement ToXml() => new(Main + "sst",
            new XAttribute("count", references),
            new XAttribute("uniqueCount", values.Count),
            values.Select(value => new XElement(Main + "si", new XElement(Main + "t",
                new XAttribute(XNamespace.Xml + "space", "preserve"), value))));
    }

    private static XElement Styles() => new(Main + "styleSheet",
        new XElement(Main + "fonts", new XAttribute("count", 2),
            new XElement(Main + "font", new XElement(Main + "sz", new XAttribute("val", 11)), new XElement(Main + "name", new XAttribute("val", "Calibri"))),
            new XElement(Main + "font", new XElement(Main + "b"), new XElement(Main + "sz", new XAttribute("val", 11)), new XElement(Main + "name", new XAttribute("val", "Calibri")))),
        new XElement(Main + "fills", new XAttribute("count", 2),
            new XElement(Main + "fill", new XElement(Main + "patternFill", new XAttribute("patternType", "none"))),
            new XElement(Main + "fill", new XElement(Main + "patternFill", new XAttribute("patternType", "gray125")))),
        new XElement(Main + "borders", new XAttribute("count", 1),
            new XElement(Main + "border", new XElement(Main + "left"), new XElement(Main + "right"), new XElement(Main + "top"),
                new XElement(Main + "bottom"), new XElement(Main + "diagonal"))),
        new XElement(Main + "cellStyleXfs", new XAttribute("count", 1),
            new XElement(Main + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0), new XAttribute("fillId", 0), new XAttribute("borderId", 0))),
        new XElement(Main + "cellXfs", new XAttribute("count", 2),
            new XElement(Main + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0), new XAttribute("fillId", 0), new XAttribute("borderId", 0), new XAttribute("xfId", 0)),
            new XElement(Main + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 1), new XAttribute("fillId", 0), new XAttribute("borderId", 0), new XAttribute("xfId", 0), new XAttribute("applyFont", 1))));

    private static void Save(ZipArchive archive, string path, XElement root)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).Save(writer);
    }

    private static string Column(int index)
    {
        var name = string.Empty;
        for (var value = index + 1; value > 0; value = (value - 1) / 26)
        {
            name = (char)('A' + (value - 1) % 26) + name;
        }
        return name;
    }

    internal static string Material(string group) => MaterialLabels.GetValueOrDefault(group, group);

    /// <summary>"BALL_END_MILL" reads "Ball end mill".</summary>
    internal static string TypeLabel(string type)
    {
        var words = type.Replace('_', ' ').ToLowerInvariant();
        return words.Length == 0 ? type : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static TimeZoneInfo TimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }
}
