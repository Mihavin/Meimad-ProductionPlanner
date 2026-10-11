using System.Security.Cryptography;
using System.Text;
using Meimad.Planner.Server.Configuration;

namespace Meimad.Planner.Server.Application.Cases;

/// <summary>A Case picture kept on the Server (schema v115).</summary>
internal sealed record CasePicture(
    string CaseId,
    string FileName,
    string MediaType,
    long SizeBytes,
    string Sha256,
    string StoredRelativePath,
    string? SourceReference,
    DateTimeOffset CreatedAt,
    string? CreatedBy);

internal sealed class CasePictureException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}

/// <summary>
/// The Server's Case picture files: PNG, JPEG, BMP or GIF up to 20 MB, checked by content, stored once per hash below the
/// release root as <c>case-pictures/&lt;case&gt;/&lt;sha256&gt;.&lt;ext&gt;</c> so the recovery set backs them up.
/// </summary>
internal sealed class CasePictureStore(GCodeOptions options)
{
    internal const long MaximumBytes = 20L * 1024 * 1024;
    internal const string Folder = "case-pictures";

    /// <summary>Checks and writes <paramref name="bytes"/>; returns the picture record (not yet saved).</summary>
    internal async Task<CasePicture> WriteAsync(
        string caseId, string? fileName, byte[] bytes, string? source, string? actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (bytes.Length == 0) throw new CasePictureException("picture_empty", "The picture is empty.");
        if (bytes.Length > MaximumBytes)
            throw new CasePictureException("picture_too_large", $"A Case picture may be at most {MaximumBytes / (1024 * 1024)} MB.");
        var (mediaType, extension) = Detect(bytes)
            ?? throw new CasePictureException("picture_format_unsupported", "Use a PNG, JPEG, BMP or GIF picture.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var folder = Path.Combine(Root(), FolderName(caseId));
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, hash + extension);
        if (!File.Exists(target))
        {
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, target, overwrite: true);
        }
        var name = Path.GetFileName((fileName ?? string.Empty).Trim());
        if (name.Length is 0 or > 255) name = "picture" + extension;
        return new CasePicture(caseId, name, mediaType, bytes.LongLength, hash,
            Path.GetRelativePath(options.ResolvedReleaseRoot, target).Replace('\\', '/'), source, now, actor);
    }

    /// <summary>The stored file of a picture, or null when it is missing on disk.</summary>
    internal string? PathOf(CasePicture picture) => PathOf(picture.StoredRelativePath);

    internal string? PathOf(string storedRelativePath)
    {
        var root = Path.GetFullPath(options.ResolvedReleaseRoot);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, storedRelativePath));
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }

    /// <summary>The image type of the content (magic bytes), or null when it is not a supported picture.</summary>
    internal static (string MediaType, string Extension)? Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return ("image/png", ".png");
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ("image/jpeg", ".jpg");
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8))) return ("image/gif", ".gif");
        if (bytes.Length >= 26 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M') return ("image/bmp", ".bmp");
        return null;
    }

    private string Root() => Path.GetFullPath(Path.Combine(options.ResolvedReleaseRoot, Folder));

    private static string FolderName(string caseId) =>
        caseId.Length is > 0 and <= 64 && caseId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? caseId
            : "c-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(caseId)))[..32];
}
