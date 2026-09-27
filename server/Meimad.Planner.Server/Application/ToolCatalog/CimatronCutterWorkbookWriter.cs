using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Meimad.Planner.Server.Application.LegacyImport;

namespace Meimad.Planner.Server.Application.ToolCatalog;

/// <summary>
/// Writes cutters into a copy of Cimatron's own External Cutters workbook (an empty Cimatron
/// 2026 "Export to XLS" file embedded in the Server), so the result opens in Cimatron's
/// NC-Process &gt; Cutters &gt; Menu &gt; Import like any file Cimatron exported itself. Only the
/// rows below the parameter-id and name rows of the "Cutters" sheet are replaced; the macros,
/// validation lists and the other sheets stay as Cimatron wrote them.
/// </summary>
internal static class CimatronCutterWorkbookWriter
{
    internal const string TemplateResource = "Meimad.Planner.Server.ToolCatalog.CimatronCutters.xlsm";
    internal const string ContentType = "application/vnd.ms-excel.sheet.macroEnabled.12";

    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    internal static byte[] Write(IReadOnlyList<IReadOnlyDictionary<int, object>> cutters)
    {
        using var output = new MemoryStream();
        using (var template = typeof(CimatronCutterWorkbookWriter).Assembly.GetManifestResourceStream(TemplateResource)
            ?? throw new InvalidOperationException($"The Cimatron cutter template '{TemplateResource}' is not embedded in the Server."))
        {
            template.CopyTo(output);
        }

        using (var archive = new ZipArchive(output, ZipArchiveMode.Update, leaveOpen: true))
        {
            var sheetPath = CuttersSheetPath(archive);
            var strings = Load(archive, "xl/sharedStrings.xml");
            var sheet = Load(archive, sheetPath);
            var table = new SharedStrings(strings.Root!);

            var sheetData = sheet.Root!.Element(Main + "sheetData") ?? throw new InvalidDataException("The Cimatron template's Cutters sheet has no sheetData.");
            var columns = new Dictionary<int, int>();
            foreach (var cell in sheetData.Elements(Main + "row").Where(row => RowNumber(row) == CimatronCutterLibrary.IdRow).Elements(Main + "c"))
            {
                if (OpenXmlLegacyWorkbookReader.TryParseCellReference((string?)cell.Attribute("r"), out var column, out _)
                    && int.TryParse(cell.Element(Main + "v")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    columns.TryAdd(id, column);
                }
            }
            if (!columns.ContainsKey(CimatronCutterLibrary.NameId))
                throw new InvalidDataException("The Cimatron template's Cutters sheet has no parameter id row.");

            foreach (var old in sheetData.Elements(Main + "row").Where(row => RowNumber(row) >= CimatronCutterLibrary.FirstCutterRow).ToList()) old.Remove();

            var rowNumber = CimatronCutterLibrary.FirstCutterRow;
            foreach (var cutter in cutters)
            {
                var row = new XElement(Main + "row", new XAttribute("r", rowNumber));
                foreach (var (column, value) in cutter
                             .Where(entry => columns.ContainsKey(entry.Key))
                             .Select(entry => (Column: columns[entry.Key], entry.Value))
                             .OrderBy(entry => entry.Column))
                {
                    var reference = OpenXmlLegacyWorkbookReader.ToColumnName(column) + rowNumber.ToString(CultureInfo.InvariantCulture);
                    row.Add(value switch
                    {
                        bool flag => new XElement(Main + "c", new XAttribute("r", reference), new XAttribute("t", "b"), new XElement(Main + "v", flag ? "1" : "0")),
                        double number => new XElement(Main + "c", new XAttribute("r", reference), new XElement(Main + "v", number.ToString("R", CultureInfo.InvariantCulture))),
                        _ => new XElement(Main + "c", new XAttribute("r", reference), new XAttribute("t", "s"), new XElement(Main + "v", table.Index(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)))
                    });
                }
                sheetData.Add(row);
                rowNumber++;
            }

            var dimension = sheet.Root.Element(Main + "dimension");
            var last = Math.Max(CimatronCutterLibrary.FirstCutterRow - 1, rowNumber - 1);
            if (dimension?.Attribute("ref")?.Value is { } range && range.Split(':') is [var first, var end]
                && OpenXmlLegacyWorkbookReader.TryParseCellReference(end, out var lastColumn, out _))
            {
                dimension.SetAttributeValue("ref", $"{first}:{OpenXmlLegacyWorkbookReader.ToColumnName(lastColumn)}{last}");
            }

            table.Commit();
            Save(archive, "xl/sharedStrings.xml", strings);
            Save(archive, sheetPath, sheet);
        }
        return output.ToArray();
    }

    private static string CuttersSheetPath(ZipArchive archive)
    {
        var workbook = Load(archive, "xl/workbook.xml");
        var relationshipId = workbook.Root!.Element(Main + "sheets")?.Elements(Main + "sheet")
            .FirstOrDefault(sheet => string.Equals((string?)sheet.Attribute("name"), CimatronCutterLibrary.CuttersSheet, StringComparison.Ordinal))
            ?.Attribute(Relationships + "id")?.Value
            ?? throw new InvalidDataException("The Cimatron template has no Cutters sheet.");
        var target = Load(archive, "xl/_rels/workbook.xml.rels").Root!.Elements(PackageRelationships + "Relationship")
            .First(relationship => (string?)relationship.Attribute("Id") == relationshipId)
            .Attribute("Target")!.Value;
        return "xl/" + target.TrimStart('/').Replace("xl/", string.Empty, StringComparison.Ordinal);
    }

    private static int RowNumber(XElement row) =>
        int.TryParse((string?)row.Attribute("r"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static XDocument Load(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path) ?? throw new InvalidDataException($"The Cimatron template has no '{path}' part.");
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static void Save(ZipArchive archive, string path, XDocument document)
    {
        archive.GetEntry(path)?.Delete();
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        document.Save(writer);
    }

    /// <summary>The workbook's shared-string table, reusing an existing entry for equal text.</summary>
    private sealed class SharedStrings
    {
        private readonly XElement root;
        private readonly Dictionary<string, int> indexes = new(StringComparer.Ordinal);
        private int count;
        private int added;
        private int references;

        internal SharedStrings(XElement root)
        {
            this.root = root;
            foreach (var item in root.Elements(Main + "si"))
            {
                var text = item.Element(Main + "t");
                if (text is not null && item.Elements().Count() == 1) indexes.TryAdd(text.Value, count);
                count++;
            }
        }

        internal int Index(string text)
        {
            references++;
            if (indexes.TryGetValue(text, out var index)) return index;
            var element = new XElement(Main + "t", text);
            if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1])))
                element.SetAttributeValue(XNamespace.Xml + "space", "preserve");
            root.Add(new XElement(Main + "si", element));
            indexes[text] = count;
            added++;
            return count++;
        }

        internal void Commit()
        {
            root.SetAttributeValue("uniqueCount", count);
            var total = int.TryParse((string?)root.Attribute("count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var existing) ? existing : count - added;
            root.SetAttributeValue("count", total + references);
        }
    }
}
