using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Meimad.Planner.Server.Domain.LegacyImport;

namespace Meimad.Planner.Server.Application.LegacyImport;

internal sealed class OpenXmlLegacyWorkbookReader
{
    public OpenXmlLegacyWorkbookReader()
    {
    }

    internal const long MaximumWorkbookBytes = 64L * 1024 * 1024;
    private const int MaximumArchiveEntries = 4096;
    private const long MaximumXmlEntryBytes = 40L * 1024 * 1024;
    private const long MaximumRelevantXmlBytes = 160L * 1024 * 1024;
    private const int MaximumSharedStrings = 250_000;
    private const int MaximumSharedStringCharacters = 16 * 1024 * 1024;
    private const int MaximumCells = 250_000;

    internal async Task<LegacyWorkbookData> ReadAsync(
        Stream source,
        string fileName,
        CancellationToken cancellationToken)
    {
        await using var workbookStream = new MemoryStream();
        await CopyBoundedAsync(source, workbookStream, cancellationToken);
        var bytes = workbookStream.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        workbookStream.Position = 0;

        try
        {
            using var archive = new ZipArchive(workbookStream, ZipArchiveMode.Read, leaveOpen: true);
            ValidateArchive(archive);
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                var normalizedName = NormalizeEntryName(entry.FullName);
                if (!entries.TryAdd(normalizedName, entry))
                {
                    throw Format("duplicate_openxml_part", $"The workbook contains duplicate part '{normalizedName}'.");
                }
            }
            var sharedStrings = ReadSharedStrings(entries);
            var relationships = ReadWorkbookRelationships(entries);
            var sheetDescriptors = ReadSheetDescriptors(entries, relationships, out var usesDate1904);
            if (usesDate1904)
            {
                throw Format(
                    "unsupported_date_system",
                    "Workbooks using the Excel 1904 date system are not supported; convert the workbook to the 1900 date system before import.");
            }
            if (sheetDescriptors.Count == 0)
            {
                throw Format("workbook_has_no_sheets", "The workbook does not contain any readable worksheets.");
            }

            var sheets = new List<LegacySheetData>(sheetDescriptors.Count);
            var totalCells = 0;
            foreach (var descriptor in sheetDescriptors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entries.TryGetValue(descriptor.Path, out var entry))
                {
                    throw Format(
                        "worksheet_part_missing",
                        $"Worksheet '{descriptor.Name}' refers to a missing workbook part.");
                }

                var sheet = ReadSheet(entry, descriptor.Name, sharedStrings, ref totalCells);
                sheets.Add(sheet);
            }

            return new LegacyWorkbookData(fileName, sha256, sheets);
        }
        catch (InvalidDataException exception)
        {
            throw Format("invalid_xlsx", $"The uploaded file is not a valid .xlsx workbook: {exception.Message}");
        }
        catch (XmlException exception)
        {
            throw Format("invalid_openxml", $"The workbook contains invalid OpenXML: {exception.Message}");
        }
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaximumWorkbookBytes)
            {
                throw Format(
                    "workbook_too_large",
                    $"The workbook exceeds the {MaximumWorkbookBytes / 1024 / 1024} MiB upload limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (total == 0)
        {
            throw Format("workbook_empty", "The uploaded workbook is empty.");
        }
    }

    private static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count > MaximumArchiveEntries)
        {
            throw Format("archive_entry_limit_exceeded", "The workbook contains too many ZIP entries.");
        }

        long relevantXmlBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var normalized = NormalizeEntryName(entry.FullName);
            if (!normalized.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && !normalized.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.Length > MaximumXmlEntryBytes)
            {
                throw Format("xml_part_too_large", $"Workbook XML part '{normalized}' is too large.");
            }

            relevantXmlBytes += entry.Length;
            if (relevantXmlBytes > MaximumRelevantXmlBytes)
            {
                throw Format("xml_expansion_limit_exceeded", "The workbook XML expansion limit was exceeded.");
            }
        }
    }

    private static string NormalizeEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains('\\', StringComparison.Ordinal)
            || name.StartsWith("/", StringComparison.Ordinal)
            || name.Contains(':', StringComparison.Ordinal))
        {
            throw Format("unsafe_openxml_path", "The workbook contains an unsafe ZIP entry path.");
        }

        var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw Format("unsafe_openxml_path", "The workbook contains a traversing ZIP entry path.");
        }

        return string.Join('/', segments);
    }

    private static IReadOnlyList<string> ReadSharedStrings(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        if (!entries.TryGetValue("xl/sharedStrings.xml", out var entry))
        {
            return [];
        }

        var result = new List<string>();
        var characterCount = 0;
        using var reader = CreateReader(entry);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si")
            {
                continue;
            }

            using var subtree = reader.ReadSubtree();
            subtree.MoveToContent();
            var item = XElement.Load(subtree, LoadOptions.None);
            var value = new StringBuilder(string.Concat(
                item.Descendants().Where(node => node.Name.LocalName == "t").Select(node => node.Value)));

            characterCount += value.Length;
            if (result.Count >= MaximumSharedStrings
                || characterCount > MaximumSharedStringCharacters)
            {
                throw Format("shared_string_limit_exceeded", "The workbook shared-string limit was exceeded.");
            }

            result.Add(value.ToString());
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ReadWorkbookRelationships(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        if (!entries.TryGetValue("xl/_rels/workbook.xml.rels", out var entry))
        {
            throw Format("workbook_relationships_missing", "The workbook relationships part is missing.");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = CreateReader(entry);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship")
            {
                continue;
            }

            var id = reader.GetAttribute("Id");
            var target = reader.GetAttribute("Target");
            var targetMode = reader.GetAttribute("TargetMode");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            if (string.Equals(targetMode, "External", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalized = NormalizeRelationshipTarget(target);
            if (!result.TryAdd(id, normalized))
            {
                throw Format("duplicate_relationship", $"The workbook contains duplicate relationship ID '{id}'.");
            }
        }

        return result;
    }

    private static string NormalizeRelationshipTarget(string target)
    {
        if (target.StartsWith("/", StringComparison.Ordinal)
            || target.Contains('\\', StringComparison.Ordinal)
            || Uri.TryCreate(target, UriKind.Absolute, out _))
        {
            throw Format("unsafe_relationship_target", "The workbook contains an unsafe relationship target.");
        }

        var segments = new List<string> { "xl" };
        foreach (var segment in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count <= 1)
                {
                    throw Format("unsafe_relationship_target", "The workbook contains a traversing relationship target.");
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return NormalizeEntryName(string.Join('/', segments));
    }

    private static IReadOnlyList<SheetDescriptor> ReadSheetDescriptors(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, string> relationships,
        out bool usesDate1904)
    {
        if (!entries.TryGetValue("xl/workbook.xml", out var entry))
        {
            throw Format("workbook_part_missing", "The workbook part is missing.");
        }

        usesDate1904 = false;
        var result = new List<SheetDescriptor>();
        using var reader = CreateReader(entry);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "workbookPr")
            {
                usesDate1904 = reader.GetAttribute("date1904") is "1" or "true";
                continue;
            }

            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "sheet")
            {
                continue;
            }

            var name = reader.GetAttribute("name");
            var relationshipId = reader.GetAttribute(
                "id",
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
            if (string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(relationshipId)
                || !relationships.TryGetValue(relationshipId, out var path)
                || !path.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(new SheetDescriptor(name, path));
        }

        return result;
    }

    private static LegacySheetData ReadSheet(
        ZipArchiveEntry entry,
        string sheetName,
        IReadOnlyList<string> sharedStrings,
        ref int totalCells)
    {
        var rows = new SortedDictionary<int, IReadOnlyDictionary<int, LegacyCellData>>();
        var maximumRow = 0;
        var maximumColumn = 0;
        using var reader = CreateReader(entry);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row")
            {
                continue;
            }

            var rowNumber = ParsePositiveInt(reader.GetAttribute("r"));
            if (rowNumber is null)
            {
                continue;
            }

            var cells = new SortedDictionary<int, LegacyCellData>();
            using var rowReader = reader.ReadSubtree();
            while (rowReader.Read())
            {
                if (rowReader.NodeType != XmlNodeType.Element || rowReader.LocalName != "c")
                {
                    continue;
                }

                var reference = rowReader.GetAttribute("r");
                var styleIndex = ParsePositiveInt(rowReader.GetAttribute("s"));
                if (!TryParseCellReference(reference, out var column, out _))
                {
                    continue;
                }

                var type = rowReader.GetAttribute("t");
                using var cellReader = rowReader.ReadSubtree();
                cellReader.MoveToContent();
                var element = XElement.Load(cellReader, LoadOptions.None);
                var formula = element.Elements().FirstOrDefault(node => node.Name.LocalName == "f")?.Value;
                var value = element.Elements().FirstOrDefault(node => node.Name.LocalName == "v")?.Value
                    ?? element.Descendants().FirstOrDefault(node => node.Name.LocalName == "t")?.Value;
                if ((formula?.Length ?? 0) > 1_000_000 || (value?.Length ?? 0) > 1_000_000)
                {
                    throw Format("cell_text_limit_exceeded", $"Cell '{reference}' exceeds the text limit.");
                }

                var decoded = DecodeValue(type, value, sharedStrings);
                var kind = type == "e"
                    ? "error"
                    : formula is not null && value is null
                        ? "formula_missing_cache"
                        : formula is not null
                            ? "formula_cached"
                            : "value";
                cells[column] = new LegacyCellData(
                    reference ?? $"{ToColumnName(column)}{rowNumber.Value}",
                    decoded,
                    value,
                    formula,
                    kind,
                    styleIndex);
                maximumColumn = Math.Max(maximumColumn, column);
                totalCells++;
                if (totalCells > MaximumCells)
                {
                    throw Format("cell_limit_exceeded", "The workbook cell limit was exceeded.");
                }
            }

            if (cells.Count > 0)
            {
                rows[rowNumber.Value] = cells;
                maximumRow = Math.Max(maximumRow, rowNumber.Value);
            }
        }

        return new LegacySheetData(sheetName, maximumRow, maximumColumn, rows);
    }

    private static string? DecodeValue(
        string? type,
        string? value,
        IReadOnlyList<string> sharedStrings)
    {
        if (value is null)
        {
            return null;
        }

        if (type == "s")
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index < 0
                || index >= sharedStrings.Count)
            {
                throw Format("invalid_shared_string", "A worksheet refers to an invalid shared-string index.");
            }

            return sharedStrings[index];
        }

        if (type == "b")
        {
            return value == "1" ? "true" : "false";
        }

        return value;
    }

    private static XmlReader CreateReader(ZipArchiveEntry entry)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = Math.Max(entry.Length * 4, 1024)
        };
        return XmlReader.Create(entry.Open(), settings);
    }

    internal static bool TryParseCellReference(
        string? reference,
        out int column,
        out int row)
    {
        column = 0;
        row = 0;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var index = 0;
        try
        {
            while (index < reference.Length && char.IsLetter(reference[index]))
            {
                var letter = char.ToUpperInvariant(reference[index]);
                if (letter is < 'A' or > 'Z')
                {
                    return false;
                }
                column = checked((column * 26) + letter - 'A' + 1);
                if (column > 16_384)
                {
                    return false;
                }
                index++;
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        return column > 0
            && index > 0
            && int.TryParse(reference.AsSpan(index), NumberStyles.None, CultureInfo.InvariantCulture, out row)
            && row is > 0 and <= 1_048_576;
    }

    internal static string ToColumnName(int column)
    {
        if (column <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }

        var result = new StringBuilder();
        while (column > 0)
        {
            column--;
            result.Insert(0, (char)('A' + (column % 26)));
            column /= 26;
        }

        return result.ToString();
    }

    private static int? ParsePositiveInt(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;

    private static LegacyWorkbookFormatException Format(string code, string message) => new(code, message);

    private sealed record SheetDescriptor(string Name, string Path);
}

internal sealed record LegacyWorkbookData(
    string FileName,
    string Sha256,
    IReadOnlyList<LegacySheetData> Sheets);

internal sealed record LegacySheetData(
    string Name,
    int MaximumRow,
    int MaximumColumn,
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, LegacyCellData>> Rows)
{
    internal LegacyCellData? Cell(int row, int column) =>
        Rows.TryGetValue(row, out var cells) && cells.TryGetValue(column, out var cell)
            ? cell
            : null;
}

internal sealed record LegacyCellData(
    string Address,
    string? Value,
    string? Raw,
    string? Formula,
    string Kind,
    int? StyleIndex);
