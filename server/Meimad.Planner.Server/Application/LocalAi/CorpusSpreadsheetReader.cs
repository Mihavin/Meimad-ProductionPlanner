using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using ExcelDataReader;
using Meimad.Planner.Server.Application.LegacyImport;
using Meimad.Planner.Server.Domain.LegacyImport;

namespace Meimad.Planner.Server.Application.LocalAi;

/// <summary>
/// Turns an Excel workbook into searchable text, sheet by sheet: .xlsx/.xlsm through the bounded OpenXML
/// import reader, Excel 97-2003 .xls through ExcelDataReader. Every row names its column headers (the first
/// of the sheet's first ten rows with at least two text cells), so a search excerpt stays readable without
/// the header row. Cells show the value Excel last saved; formulas are not recalculated. Cells formatted as
/// dates are written as ISO dates. Hidden sheets are included.
/// </summary>
internal static class CorpusSpreadsheetReader
{
    private const int HeaderSearchRows = 10;
    private const int MaxLegacyCells = 250_000;

    private sealed record Cell(string Value, bool IsText);
    private sealed record Sheet(string Name, SortedDictionary<int, SortedDictionary<int, Cell>> Rows);

    // ExcelDataReader reads the Windows code pages of Excel 97-2003 strings through the registered provider.
    static CorpusSpreadsheetReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Reads an .xlsx/.xlsm workbook.</summary>
    internal static async Task<string> ReadAsync(byte[] workbook, int maxCharacters, CancellationToken token)
    {
        LegacyWorkbookData data;
        try
        {
            using var stream = new MemoryStream(workbook, writable: false);
            data = await new OpenXmlLegacyWorkbookReader().ReadAsync(stream, "workbook.xlsx", token, allowDate1904: true);
        }
        catch (LegacyWorkbookFormatException exception) { throw new InvalidDataException("Workbook could not be read: " + exception.Message, exception); }
        var dateStyles = ReadDateStyles(workbook);
        var sheets = data.Sheets.Select(sheet => new Sheet(sheet.Name, new SortedDictionary<int, SortedDictionary<int, Cell>>(
            sheet.Rows.ToDictionary(row => row.Key, row => new SortedDictionary<int, Cell>(row.Value
                .Select(cell => (cell.Key, Cell: new Cell(Display(cell.Value, dateStyles, data.UsesDate1904), IsText(cell.Value))))
                .Where(cell => cell.Cell.Value.Length > 0).ToDictionary(cell => cell.Key, cell => cell.Cell)))))).ToList();
        return Render(sheets, maxCharacters, token);
    }

    /// <summary>
    /// Reads an Excel 97-2003 .xls workbook (or an .xlsx saved with the .xls extension). Unlike .xlsx, a sheet
    /// over the text or cell limit is cut off rather than refused.
    /// </summary>
    internal static string ReadLegacy(byte[] workbook, int maxCharacters, CancellationToken token)
    {
        var sheets = new List<Sheet>();
        try
        {
            using var stream = new MemoryStream(workbook, writable: false);
            using var reader = ExcelReaderFactory.CreateReader(stream);
            int cells = 0, characters = 0;
            do
            {
                var rows = new SortedDictionary<int, SortedDictionary<int, Cell>>();
                var row = 0;
                while (cells <= MaxLegacyCells && characters <= maxCharacters && reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    row++;
                    for (var column = 0; column < reader.FieldCount; column++)
                    {
                        var value = reader.GetValue(column);
                        var display = value switch
                        {
                            null => "",
                            string text => Clean(text),
                            DateTime date when date.Year < 1900 => date.ToString("HH:mm", CultureInfo.InvariantCulture),
                            DateTime date => date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                            double number => number.ToString("G15", CultureInfo.InvariantCulture),
                            bool flag => flag ? "true" : "false",
                            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                            _ => Clean(value.ToString() ?? ""),
                        };
                        if (display.Length == 0) continue;
                        if (!rows.TryGetValue(row, out var line)) rows[row] = line = [];
                        line[column + 1] = new(display, value is string);
                        cells++; characters += display.Length;
                    }
                }
                sheets.Add(new(reader.Name ?? "Sheet" + (sheets.Count + 1).ToString(CultureInfo.InvariantCulture), rows));
            }
            while (cells <= MaxLegacyCells && characters <= maxCharacters && reader.NextResult());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { throw new InvalidDataException("Workbook could not be read: " + exception.Message, exception); }
        return Render(sheets, maxCharacters, token);
    }

    private static string Render(IReadOnlyList<Sheet> sheets, int maxCharacters, CancellationToken token)
    {
        if (!sheets.Any(sheet => sheet.Rows.Count > 0)) throw new InvalidDataException("Workbook has no cell values.");
        var text = new StringBuilder();
        foreach (var sheet in sheets)
        {
            token.ThrowIfCancellationRequested();
            if (text.Length >= maxCharacters) break;
            text.Append("[Sheet: ").Append(sheet.Name).Append("]\n");
            var headerRow = sheet.Rows.Keys.Take(HeaderSearchRows).FirstOrDefault(row => sheet.Rows[row].Values.Count(cell => cell.IsText) >= 2);
            var headers = headerRow == 0 ? [] : sheet.Rows[headerRow].ToDictionary(pair => pair.Key, pair => pair.Value.Value);
            foreach (var (row, cells) in sheet.Rows)
            {
                if (text.Length >= maxCharacters) break;
                text.Append("Row ").Append(row.ToString(CultureInfo.InvariantCulture)).Append(": ");
                if (row == headerRow) text.Append("(headers) ").AppendJoin(" | ", cells.Values.Select(cell => cell.Value));
                else text.AppendJoin("; ", cells.Select(pair =>
                    (row > headerRow && headers.TryGetValue(pair.Key, out var header) ? header : OpenXmlLegacyWorkbookReader.ToColumnName(pair.Key)) + " = " + pair.Value.Value));
                text.Append('\n');
            }
            text.Append('\n');
        }
        if (text.Length > maxCharacters) text.Length = maxCharacters;
        return text.ToString();
    }

    // Numbers keep their stored invariant text; the OpenXML reader does not expose the cell type, so only a
    // shared string (whose raw value is its index) or a non-numeric value is known to be text.
    private static bool IsText(LegacyCellData cell) =>
        !string.IsNullOrWhiteSpace(cell.Value) && (cell.Raw != cell.Value || !double.TryParse(cell.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _));

    private static string Display(LegacyCellData cell, IReadOnlySet<int> dateStyles, bool date1904)
    {
        if (cell.Value is null || cell.Kind == "error") return "";
        if (cell.Raw == cell.Value && cell.StyleIndex is { } style && dateStyles.Contains(style)
            && double.TryParse(cell.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial)
            && serial is >= 0 and < 2_958_466)
        {
            var date = (date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30)).AddDays(serial);
            return serial % 1 == 0 ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : serial < 1 ? date.ToString("HH:mm", CultureInfo.InvariantCulture)
                : date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        // Excel stores binary doubles with up to 17 digits (0.30000000000000004); 15 digits read as typed.
        if (cell.Raw == cell.Value && cell.Value.Length > 15
            && double.TryParse(cell.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return number.ToString("G15", CultureInfo.InvariantCulture);
        return Clean(cell.Value);
    }

    private static string Clean(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Style indexes whose number format is a date or time (built-in 14–22 and 45–47, or a custom format with date/time codes).</summary>
    private static IReadOnlySet<int> ReadDateStyles(byte[] workbook)
    {
        var result = new HashSet<int>();
        try
        {
            using var archive = new ZipArchive(new MemoryStream(workbook, writable: false), ZipArchiveMode.Read);
            var entry = archive.GetEntry("xl/styles.xml");
            if (entry is null || entry.Length > 40L * 1024 * 1024) return result;
            var custom = new Dictionary<int, string>();
            var formats = new List<int>();
            using var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true,
                MaxCharactersInDocument = Math.Max(entry.Length * 4, 1024),
            });
            var inCellFormats = false;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "cellXfs") inCellFormats = false;
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName == "numFmt" && int.TryParse(reader.GetAttribute("numFmtId"), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                    custom[id] = reader.GetAttribute("formatCode") ?? "";
                else if (reader.LocalName == "cellXfs") inCellFormats = !reader.IsEmptyElement;
                else if (inCellFormats && reader.LocalName == "xf")
                    formats.Add(int.TryParse(reader.GetAttribute("numFmtId"), NumberStyles.None, CultureInfo.InvariantCulture, out var format) ? format : 0);
            }
            for (var i = 0; i < formats.Count; i++)
                if (formats[i] is >= 14 and <= 22 or >= 45 and <= 47 || custom.TryGetValue(formats[i], out var code) && IsDateFormat(code))
                    result.Add(i);
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException) { }
        return result;
    }

    private static bool IsDateFormat(string code)
    {
        // Ignore quoted literals, escaped characters and [colour]/[$-locale] sections before looking for d, m, y, h or s.
        var plain = new StringBuilder();
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '"') { i = code.IndexOf('"', i + 1); if (i < 0) break; }
            else if (c is '\\' or '_' or '*') i++;
            else if (c == '[') { var end = code.IndexOf(']', i); if (end < 0) break; var section = code[(i + 1)..end]; if (section is "h" or "hh" or "m" or "mm" or "s" or "ss") plain.Append('h'); i = end; }
            else plain.Append(char.ToLowerInvariant(c));
        }
        var text = plain.ToString();
        return !text.Contains("general", StringComparison.Ordinal) && text.IndexOfAny(['d', 'm', 'y', 'h', 's']) >= 0;
    }
}
