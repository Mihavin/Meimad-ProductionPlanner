using System.IO;
using System.Security.Cryptography;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// A finished-part picture of one Case Operation, rendered by the client from the part model of an
/// imported Cimatron simulation package and saved in the Case Working Folder's <c>_MeimadPlanner</c>
/// folder (AGENTS rule 11: generated files only there).
/// </summary>
internal sealed record OperationThumbnail(
    string CaseId,
    string CaseOperationId,
    string SourceModelPath,
    string SourceModelSha256,
    string SavedPath,
    byte[] Png,
    string Sha256,
    DateTimeOffset CreatedAt);

/// <summary>
/// Receives finished-part thumbnails for the TV dashboard (execution plan stage 8, thumbnail priority 2:
/// "thumbnail finished-part STL из package"). The upload to the Server belongs to the TV dashboard
/// workstream, which adds the endpoint; this workstream only produces the picture.
/// </summary>
internal interface IOperationThumbnailSink
{
    Task SubmitAsync(OperationThumbnail thumbnail, CancellationToken cancellationToken = default);
}

/// <summary>
/// The sink in use until the Server has a thumbnail endpoint: it keeps the last thumbnail per Operation
/// in memory and does nothing else.
/// TODO(TV dashboard thumbnails, schema v118 workstream): replace with a sink that uploads
/// <see cref="OperationThumbnail.Png"/> to the Server's Operation thumbnail endpoint (versioned by
/// <see cref="OperationThumbnail.Sha256"/>) once that endpoint exists; do not invent it here.
/// </summary>
internal sealed class PendingOperationThumbnailSink : IOperationThumbnailSink
{
    private readonly Dictionary<string, OperationThumbnail> latest = new(StringComparer.Ordinal);

    internal static PendingOperationThumbnailSink Instance { get; } = new();

    internal OperationThumbnail? Latest(string caseOperationId)
    {
        lock (latest) return latest.GetValueOrDefault(caseOperationId);
    }

    public Task SubmitAsync(OperationThumbnail thumbnail, CancellationToken cancellationToken = default)
    {
        lock (latest) latest[thumbnail.CaseOperationId] = thumbnail;
        return Task.CompletedTask;
    }
}

/// <summary>Saves operation thumbnails below the Case Working Folder's generated-files folder.</summary>
internal static class OperationThumbnailStore
{
    internal const string FolderName = "thumbnails";

    /// <summary>The file a thumbnail of the Operation is saved as (replaced by a newer one).</summary>
    internal static string PathFor(string workingFolder, string partNumber, int operationNumber) =>
        Path.Combine(workingFolder, CaseWorkspaceViewModel.GeneratedFolderName, FolderName,
            $"{Safe(partNumber)}-op{operationNumber:000}-finished-part.png");

    /// <summary>
    /// Writes the PNG and submits it; returns the saved thumbnail. Throws for a missing Working Folder
    /// (the caller reports it) — the engineering files around the folder are never touched.
    /// </summary>
    internal static async Task<OperationThumbnail> SaveAsync(
        string workingFolder, string partNumber, int operationNumber, string caseId, string caseOperationId,
        string sourceModelPath, byte[] png, IOperationThumbnailSink sink, TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingFolder) || !Directory.Exists(workingFolder))
            throw new DirectoryNotFoundException($"The Working Folder {workingFolder} was not found.");
        var target = PathFor(workingFolder, partNumber, operationNumber);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".tmp";
        await File.WriteAllBytesAsync(temporary, png, cancellationToken);
        File.Move(temporary, target, overwrite: true);
        string sourceHash;
        await using (var source = File.OpenRead(sourceModelPath))
            sourceHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, cancellationToken));
        var thumbnail = new OperationThumbnail(caseId, caseOperationId, sourceModelPath, sourceHash, target, png,
            Convert.ToHexStringLower(SHA256.HashData(png)), (clock ?? TimeProvider.System).GetUtcNow());
        await sink.SubmitAsync(thumbnail, cancellationToken);
        return thumbnail;
    }

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "part" : cleaned;
    }
}
