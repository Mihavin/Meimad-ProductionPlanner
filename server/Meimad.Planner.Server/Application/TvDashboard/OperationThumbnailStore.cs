using System.Security.Cryptography;
using System.Text;
using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Configuration;

namespace Meimad.Planner.Server.Application.TvDashboard;

/// <summary>The two kinds of Case Operation thumbnail (schema v118), in TV priority order after the Case picture.</summary>
internal static class OperationThumbnailSources
{
    /// <summary>The finished-part thumbnail of the Cimatron simulation package.</summary>
    internal const string Package = "package";

    /// <summary>A screenshot or preview stored at import or uploaded from the NC viewer or the Case Workspace.</summary>
    internal const string Snapshot = "snapshot";

    internal static readonly IReadOnlyList<string> All = [Package, Snapshot];

    internal static bool IsKnown(string? source) => source is Package or Snapshot;
}

/// <summary>A Case Operation thumbnail kept on the Server (schema v118).</summary>
internal sealed record OperationThumbnail(
    string CaseOperationId,
    string Source,
    string FileName,
    string MediaType,
    long SizeBytes,
    string Sha256,
    string StoredRelativePath,
    string? SourceReference,
    int Version,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);

/// <summary>
/// The Server's Case Operation thumbnail files: PNG, JPEG, BMP or GIF up to 10 MB, checked by content and stored once per
/// hash below the release root as <c>operation-thumbnails/&lt;operation&gt;/&lt;sha256&gt;.&lt;ext&gt;</c>, so the
/// recovery set backs them up. The TV never receives these files; it gets a small re-encoded copy
/// (<see cref="TvThumbnailRenderer"/>).
/// </summary>
internal sealed class OperationThumbnailStore(GCodeOptions options)
{
    internal const long MaximumBytes = 10L * 1024 * 1024;
    internal const string Folder = "operation-thumbnails";

    /// <summary>Checks and writes <paramref name="bytes"/>; returns the thumbnail record (not yet saved, version 0).</summary>
    internal async Task<OperationThumbnail> WriteAsync(
        string caseOperationId, string source, string? fileName, byte[] bytes, string? sourceReference, string? actor,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (bytes.Length == 0) throw new CasePictureException("thumbnail_empty", "The thumbnail image is empty.");
        if (bytes.Length > MaximumBytes)
            throw new CasePictureException("thumbnail_too_large", $"An Operation thumbnail may be at most {MaximumBytes / (1024 * 1024)} MB.");
        var (mediaType, extension) = CasePictureStore.Detect(bytes)
            ?? throw new CasePictureException("thumbnail_format_unsupported", "Use a PNG, JPEG, BMP or GIF image.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var folder = Path.Combine(Root(), FolderName(caseOperationId));
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, hash + extension);
        if (!File.Exists(target))
        {
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, target, overwrite: true);
        }
        var name = Path.GetFileName((fileName ?? string.Empty).Trim());
        if (name.Length is 0 or > 255) name = "thumbnail" + extension;
        var reference = string.IsNullOrWhiteSpace(sourceReference) ? null : sourceReference.Trim();
        if (reference is { Length: > 1000 }) reference = reference[..1000];
        return new OperationThumbnail(caseOperationId, source, name, mediaType, bytes.LongLength, hash,
            Path.GetRelativePath(options.ResolvedReleaseRoot, target).Replace('\\', '/'), reference, 0, now, actor);
    }

    /// <summary>The stored file below the release root, or null when it is missing or outside the root.</summary>
    internal string? PathOf(string storedRelativePath)
    {
        var root = Path.GetFullPath(options.ResolvedReleaseRoot);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, storedRelativePath));
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }

    private string Root() => Path.GetFullPath(Path.Combine(options.ResolvedReleaseRoot, Folder));

    private static string FolderName(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? id
            : "o-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..32];
}
