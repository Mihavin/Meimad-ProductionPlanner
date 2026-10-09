using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Meimad.Planner.Server.Application.LocalAi;

/// <summary>
/// OCR with the engine built into Windows (no downloaded model or language data): Windows renders the
/// PDF page and recognizes it in the installed OCR language, English first. Hebrew is recognized only
/// when the Windows Hebrew OCR language capability is installed on the Server PC.
/// </summary>
internal sealed class WindowsPageTextRecognizer : IPageTextRecognizer
{
    /// <summary>Keeps a rendered page under ~36 megapixels (about 140 MB) even for A0 drawings.</summary>
    private const double MaxPixels = 36_000_000;
    private readonly Lazy<OcrEngine?> engine = new(Create);

    public bool Available => engine.Value is not null;

    private static OcrEngine? Create()
    {
        try
        {
            var languages = OcrEngine.AvailableRecognizerLanguages;
            var language = languages.FirstOrDefault(x => x.LanguageTag.Equals("en-US", StringComparison.OrdinalIgnoreCase))
                ?? languages.FirstOrDefault(x => x.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                ?? languages.FirstOrDefault();
            return language is null ? null : OcrEngine.TryCreateFromLanguage(language);
        }
        catch (Exception) { return null; }
    }

    public async Task<IReadOnlyList<string>> RecognizePdfPagesAsync(byte[] pdf, IReadOnlyList<int> pageIndexes, CancellationToken token)
    {
        if (engine.Value is not { } ocr) return [];
        using var source = new InMemoryRandomAccessStream();
        await source.WriteAsync(pdf.AsBuffer()).AsTask(token);
        source.Seek(0);
        var document = await PdfDocument.LoadFromStreamAsync(source).AsTask(token);
        var result = new List<string>(pageIndexes.Count);
        foreach (var index in pageIndexes)
        {
            token.ThrowIfCancellationRequested();
            using var page = document.GetPage((uint)index);
            var width = Math.Max(1, page.Size.Width); var height = Math.Max(1, page.Size.Height);
            // 300 dpi, limited by the engine's largest side and the pixel budget.
            var scale = Math.Min(300.0 / 72, Math.Min(OcrEngine.MaxImageDimension / Math.Max(width, height), Math.Sqrt(MaxPixels / (width * height))));
            using var image = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(image, new PdfPageRenderOptions
            { DestinationWidth = (uint)Math.Max(1, width * scale), DestinationHeight = (uint)Math.Max(1, height * scale) }).AsTask(token);
            var decoder = await BitmapDecoder.CreateAsync(image).AsTask(token);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(token);
            var recognized = await ocr.RecognizeAsync(bitmap).AsTask(token);
            result.Add(string.Join('\n', recognized.Lines.Select(line => line.Text)));
        }
        return result;
    }
}
