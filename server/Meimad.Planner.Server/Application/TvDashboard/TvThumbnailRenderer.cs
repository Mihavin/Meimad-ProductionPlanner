using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Meimad.Planner.Server.Application.TvDashboard;

/// <summary>
/// The small image a TV row shows: the stored picture scaled to fit a fixed <see cref="Size"/> x <see cref="Size"/>
/// transparent square (aspect kept, never enlarged past the square) and re-encoded as PNG with the imaging codecs built
/// into Windows, so a TV never downloads a large original, a BMP or a model file. Rendered copies are kept in memory by
/// the content hash of the source (the TV thumbnail token); the source files are content-addressed, so a copy never goes
/// stale.
/// </summary>
internal sealed class TvThumbnailRenderer
{
    /// <summary>Twice the 96 px TV column, so the picture stays sharp on a 4K panel.</summary>
    internal const uint Size = 192;

    /// <summary>Changes when the rendering changes, so TVs fetch new copies (it is part of every thumbnail token).</summary>
    internal const string RenderRevision = "1";

    private const int MaximumCachedImages = 256;
    private readonly ConcurrentDictionary<string, byte[]> cache = new(StringComparer.Ordinal);

    /// <summary>The PNG for <paramref name="key"/>, rendering <paramref name="sourcePath"/> once; null when it cannot be decoded.</summary>
    internal async Task<byte[]?> RenderAsync(string key, string sourcePath, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(key, out var cached)) return cached;
        byte[] source;
        try
        {
            source = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        var rendered = await RenderAsync(source, cancellationToken);
        if (rendered is null) return null;
        if (cache.Count >= MaximumCachedImages) cache.Clear();
        cache[key] = rendered;
        return rendered;
    }

    /// <summary>Scales and re-encodes <paramref name="image"/> (PNG, JPEG, BMP or GIF); null when Windows cannot decode it.</summary>
    internal static async Task<byte[]?> RenderAsync(byte[] image, CancellationToken cancellationToken)
    {
        try
        {
            using var input = new InMemoryRandomAccessStream();
            await input.WriteAsync(image.AsBuffer()).AsTask(cancellationToken);
            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken);
            var width = decoder.PixelWidth;
            var height = decoder.PixelHeight;
            if (width == 0 || height == 0) return null;
            var scale = Math.Min((double)Size / width, (double)Size / height);
            var scaledWidth = (uint)Math.Clamp(Math.Round(width * scale), 1, Size);
            var scaledHeight = (uint)Math.Clamp(Math.Round(height * scale), 1, Size);
            var transform = new BitmapTransform
            {
                ScaledWidth = scaledWidth,
                ScaledHeight = scaledHeight,
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken);
            var scaled = pixels.DetachPixelData();

            // Centre the scaled picture on a transparent square.
            var canvas = new byte[Size * Size * 4];
            var left = (Size - scaledWidth) / 2;
            var top = (Size - scaledHeight) / 2;
            for (var row = 0u; row < scaledHeight; row++)
            {
                System.Buffer.BlockCopy(scaled, (int)(row * scaledWidth * 4), canvas, (int)(((top + row) * Size + left) * 4), (int)(scaledWidth * 4));
            }

            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask(cancellationToken);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, Size, Size, 96, 96, canvas);
            await encoder.FlushAsync().AsTask(cancellationToken);
            var result = new byte[output.Size];
            using var reader = new DataReader(output.GetInputStreamAt(0));
            await reader.LoadAsync((uint)output.Size).AsTask(cancellationToken);
            reader.ReadBytes(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Not an image Windows can decode (or a damaged file): the TV shows the placeholder.
            return null;
        }
    }
}
