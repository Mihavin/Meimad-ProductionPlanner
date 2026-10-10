using System.Security.Cryptography;
using System.Text;
using Meimad.Planner.Server.Application.LegacyImport;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace Meimad.Planner.Server.Application.LocalAi;

/// <summary>Recognizes the text of rendered PDF pages that carry no usable text layer (scanned drawings).</summary>
internal interface IPageTextRecognizer
{
    /// <summary>False when the Server PC has no OCR engine/language; pages are then indexed from their text layer only.</summary>
    bool Available { get; }
    Task<IReadOnlyList<string>> RecognizePdfPagesAsync(byte[] pdf, IReadOnlyList<int> pageIndexes, CancellationToken token);
}

/// <summary>
/// Extracted document text. <c>Extraction</c> is TEXT, CSV, PDF_TEXT, PDF_OCR, PDF_MIXED, XLSX, XLS, STEP, DMIS or RTF.
/// </summary>
internal sealed record CorpusFileContent(string Hash, string Text, long Bytes, string Extraction, int OcrPages);

/// <summary>
/// Reads approved corpus files: text/Markdown, CSV and DMIS inspection programs (.dmi) as UTF-8 or
/// byte-order-marked UTF-16 text; PDF drawings, parts lists and specifications page by page, with OCR for a
/// page whose text layer has too little text; Excel workbooks (.xlsx/.xlsm/.xls) sheet by sheet; STEP models
/// (.stp/.step) as their header, product, assembly, unit and property records; and RTF documents such as CMM
/// reports as plain text (owner decisions 2026-10-09/10). The hash of a PDF, workbook, STEP or RTF file is the
/// SHA-256 of its bytes, so stored extracted text stays valid exactly while the file is unchanged and never has
/// to be extracted (or recognized) again to be verified.
/// </summary>
internal sealed class CorpusDocumentReader(IPageTextRecognizer recognizer)
{
    internal const long MaxPdfBytes = 64L * 1024 * 1024;
    internal const int MaxPdfPages = 200;
    internal const int MaxTextCharacters = 400_000;
    /// <summary>A page with fewer letters/digits than this in its text layer is treated as an image.</summary>
    internal const int MinimumPageCharacters = 40;

    private enum Format { Text, Csv, Dmis, Pdf, Workbook, LegacyWorkbook, Step, Rtf }

    internal bool OcrAvailable => recognizer.Available;

    internal static bool Supported(string path) => FormatOf(path) is not null;

    /// <summary>Extracted text of these kinds is cached by file hash; plain text, CSV and DMIS are read directly.</summary>
    internal static bool IsCached(string extraction) => extraction is not ("TEXT" or "CSV" or "DMIS");

    private static bool IsText(Format format) => format is Format.Text or Format.Csv or Format.Dmis;

    private static Format? FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" or ".md" => Format.Text,
        ".csv" => Format.Csv,
        ".dmi" => Format.Dmis,
        ".pdf" => Format.Pdf,
        ".xlsx" or ".xlsm" => Format.Workbook,
        ".xls" => Format.LegacyWorkbook,
        ".stp" or ".step" => Format.Step,
        ".rtf" => Format.Rtf,
        _ => null,
    };

    /// <summary>The current content hash without extracting text; used to verify stored extracted text.</summary>
    internal static async Task<string> HashAsync(string path, CancellationToken token)
    {
        var format = FormatOf(path) ?? throw new InvalidDataException("Unsupported document format.");
        if (IsText(format)) return (await ReadTextAsync(path, token)).Hash;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }

    /// <summary>Reads a file; <paramref name="cached"/> may return earlier extracted text for an unchanged file hash.</summary>
    internal async Task<CorpusFileContent> ReadAsync(string path, Func<string, Task<(string Text, string Extraction)?>>? cached, CancellationToken token)
    {
        var format = FormatOf(path) ?? throw new InvalidDataException("Unsupported document format.");
        if (IsText(format))
        {
            var text = await ReadTextAsync(path, token);
            return new(text.Hash, text.Text, text.Bytes, format switch { Format.Csv => "CSV", Format.Dmis => "DMIS", _ => "TEXT" }, 0);
        }
        var length = new FileInfo(path).Length;
        if (format == Format.Step)
        {
            if (length > CorpusStepReader.MaxStepBytes) throw new InvalidDataException("STEP file exceeds the indexing size limit.");
            var stepHash = await HashAsync(path, token);
            if (cached is not null && await cached(stepHash) is { } earlierStep) return new(stepHash, earlierStep.Text, length, earlierStep.Extraction, 0);
            return new(stepHash, await CorpusStepReader.ReadAsync(path, MaxTextCharacters, token), length, "STEP", 0);
        }
        if (length > format switch { Format.Pdf => MaxPdfBytes, Format.Rtf => CorpusRtfReader.MaxRtfBytes, _ => OpenXmlLegacyWorkbookReader.MaximumWorkbookBytes })
            throw new InvalidDataException("Document exceeds the indexing size limit.");
        var bytes = await File.ReadAllBytesAsync(path, token);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (cached is not null && await cached(hash) is { } earlier) return new(hash, earlier.Text, bytes.LongLength, earlier.Extraction, 0);
        if (format == Format.Workbook)
            return new(hash, await CorpusSpreadsheetReader.ReadAsync(bytes, MaxTextCharacters, token), bytes.LongLength, "XLSX", 0);
        if (format == Format.LegacyWorkbook)
            return new(hash, CorpusSpreadsheetReader.ReadLegacy(bytes, MaxTextCharacters, token), bytes.LongLength, "XLS", 0);
        if (format == Format.Rtf)
            return new(hash, CorpusRtfReader.Read(bytes, MaxTextCharacters, token), bytes.LongLength, "RTF", 0);
        var (content, extraction, ocrPages) = await ExtractPdfAsync(bytes, token);
        return new(hash, content, bytes.LongLength, extraction, ocrPages);
    }

    private async Task<(string Text, string Extraction, int OcrPages)> ExtractPdfAsync(byte[] bytes, CancellationToken token)
    {
        var pages = new List<string>();
        try
        {
            using var document = PdfDocument.Open(bytes);
            if (document.NumberOfPages > MaxPdfPages) throw new InvalidDataException("PDF has too many pages to index.");
            foreach (var page in document.GetPages())
            {
                token.ThrowIfCancellationRequested();
                // The nearest-neighbour extractor also groups rotated drawing text into words. It returns
                // spaces as words of their own; dropping them keeps phrases searchable with single spaces.
                pages.Add(string.Join(' ', NearestNeighbourWordExtractor.Instance.GetWords(page.Letters)
                    .Select(word => word.Text.Trim()).Where(word => word.Length > 0)));
            }
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or InvalidDataException))
        { throw new InvalidDataException("PDF could not be read: " + exception.Message, exception); }
        var textPages = pages.Count(page => Characters(page) >= MinimumPageCharacters);
        var imagePages = Enumerable.Range(0, pages.Count).Where(index => Characters(pages[index]) < MinimumPageCharacters).ToArray();
        var ocrPages = 0;
        if (imagePages.Length > 0 && recognizer.Available)
        {
            IReadOnlyList<string> recognized;
            try { recognized = await recognizer.RecognizePdfPagesAsync(bytes, imagePages, token); }
            catch (Exception exception) when (exception is not OperationCanceledException) { recognized = []; }
            for (var i = 0; i < Math.Min(imagePages.Length, recognized.Count); i++)
                if (Characters(recognized[i]) > Characters(pages[imagePages[i]]))
                { pages[imagePages[i]] = recognized[i]; ocrPages++; }
        }
        var text = new StringBuilder();
        for (var i = 0; i < pages.Count && text.Length < MaxTextCharacters; i++)
            text.Append("[Page ").Append(i + 1).Append("]\n").Append(pages[i].Trim()).Append("\n\n");
        if (text.Length > MaxTextCharacters) text.Length = MaxTextCharacters;
        if (pages.Sum(Characters) == 0) throw new InvalidDataException("PDF has no readable text.");
        return (text.ToString(), ocrPages == 0 ? "PDF_TEXT" : textPages == 0 ? "PDF_OCR" : "PDF_MIXED", ocrPages);
    }

    private static int Characters(string text) => text.Count(char.IsLetterOrDigit);

    private static async Task<(string Hash, string Text, long Bytes)> ReadTextAsync(string path, CancellationToken token)
    {
        var text = await File.ReadAllTextAsync(path, new UTF8Encoding(false, true), token);
        if (text.Contains('\0')) throw new InvalidDataException("Binary content is unsupported.");
        var bytes = Encoding.UTF8.GetBytes(text);
        return (Convert.ToHexStringLower(SHA256.HashData(bytes)), text, bytes.LongLength);
    }
}
