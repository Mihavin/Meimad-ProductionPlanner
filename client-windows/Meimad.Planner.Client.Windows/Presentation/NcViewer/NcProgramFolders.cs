using System.Globalization;
using System.IO;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation.NcViewer;

/// <summary>
/// The numbers of one G-code release of a Case Operation: its process revision, its
/// postprocessor, and the postprocessor's own ("local") revision within that process revision.
/// </summary>
internal sealed record NcProgramRevision(
    int ProcessRevision,
    string PostprocessorId,
    string PostprocessorName,
    int LocalRevision);

/// <summary>A revision folder that exists on disk, ready to receive the program file.</summary>
internal sealed record NcProgramFolder(string Path, NcProgramRevision Revision);

/// <summary>Where a released program is, and whether it is in the Operation's G-code folder.</summary>
internal sealed record NcProgramPlacement(string Path, bool InOperationFolder);

/// <summary>What a revision folder is built from, read fresh from the Server for every save.</summary>
internal sealed record NcProgramLocation(
    string WorkingFolderPath,
    string CaseName,
    int OperationNumber,
    PlannerGCodeCatalog Catalog);

/// <summary>
/// Where the NC viewer saves a modified program of a Case Operation:
/// <c>{Case Working Folder}\Gcode\{Case name}\{Operation number}\{process revision}\{postprocessor name}\{local revision}\</c>.
/// Missing folders are created. A program being prepared goes to the folder of the release it
/// becomes, computed from the Operation's G-code catalog with the Server's numbering rules; a
/// copy of a release goes to that release's folder. A released program saved under the
/// Operation's folder is moved to the folder of the numbers the Server actually assigned.
/// </summary>
internal sealed class NcProgramFolders
{
    internal const string RootFolderName = "Gcode";
    internal const string NewProcessRevision = "NEW_PROCESS_REVISION";
    internal const string LocalPostRevision = "LOCAL_POST_REVISION";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly Func<CancellationToken, Task<NcProgramLocation>> load;

    internal NcProgramFolders(
        Func<CancellationToken, Task<NcProgramLocation>> load,
        string? defaultPostprocessorId,
        string? defaultChangeScope = null,
        NcProgramRevision? source = null)
    {
        this.load = load;
        DefaultPostprocessorId = defaultPostprocessorId;
        DefaultChangeScope = defaultChangeScope;
        Source = source;
    }

    /// <summary>The postprocessor a plain Save prepares the program for.</summary>
    internal string? DefaultPostprocessorId { get; }

    /// <summary>The change scope a plain Save prepares the program for; null follows the Operation.</summary>
    internal string? DefaultChangeScope { get; }

    /// <summary>The release the viewer's document comes from, if any.</summary>
    internal NcProgramRevision? Source { get; }

    /// <summary>Reads the Case, the Operation and its G-code catalog from the Server.</summary>
    internal static NcProgramFolders ForOperation(
        IPlannerApiClient api,
        string caseId,
        string caseOperationId,
        string? defaultPostprocessorId,
        string? defaultChangeScope = null,
        NcProgramRevision? source = null) =>
        new(async token =>
            {
                var resource = await api.GetCaseAsync(caseId, token);
                var operations = await api.ListCaseOperationsAsync(caseId, token);
                var operation = operations.FirstOrDefault(value => value.CaseOperationId == caseOperationId)
                    ?? throw new InvalidOperationException("The Operation no longer exists on the Server.");
                var catalog = await api.GetOperationGCodeAsync(caseId, caseOperationId, token);
                return new NcProgramLocation(
                    resource.Value.WorkingFolderPath,
                    CaseFolderName(resource.Value),
                    operation.OperationNumber,
                    catalog);
            },
            defaultPostprocessorId,
            defaultChangeScope,
            source);

    /// <summary>The Case name, or the Part Number when the Case has no name.</summary>
    internal static string CaseFolderName(PlannerCase value) =>
        string.IsNullOrWhiteSpace(value.Name) ? value.PartNumber : value.Name;

    /// <summary>
    /// The folder of the release the program becomes: the next release of the postprocessor
    /// (default: <see cref="DefaultPostprocessorId"/>) with the change scope (default:
    /// <see cref="DefaultChangeScope"/>, else a local post revision when the Operation has an
    /// active process revision and a new process revision when it has none).
    /// </summary>
    internal async Task<NcProgramFolder> NextReleaseAsync(
        string? postprocessorId,
        string? changeScope,
        CancellationToken cancellationToken = default)
    {
        var location = await load(cancellationToken);
        var postprocessor = postprocessorId ?? DefaultPostprocessorId;
        if (string.IsNullOrWhiteSpace(postprocessor))
        {
            throw new InvalidOperationException("Choose the postprocessor of this Operation first; its name is part of the G-code folder.");
        }
        var scope = changeScope ?? DefaultChangeScope
            ?? (location.Catalog.ActiveProcessRevision is null ? NewProcessRevision : LocalPostRevision);
        return Create(location, NextRevision(location.Catalog, postprocessor, scope));
    }

    /// <summary>The folder of an existing release's numbers.</summary>
    internal async Task<NcProgramFolder> ReleaseFolderAsync(
        NcProgramRevision revision,
        CancellationToken cancellationToken = default) =>
        Create(await load(cancellationToken), revision);

    /// <summary>
    /// After a release: a program saved under this Operation's G-code folder is moved to the
    /// folder of the numbers the Server assigned. A file from anywhere else (for example CAM
    /// output chosen in the Release G-code form) stays where it is, and an existing file is never
    /// replaced. Returns where the program is afterwards.
    /// </summary>
    internal async Task<NcProgramPlacement> PlaceReleasedProgramAsync(
        string filePath,
        NcProgramRevision released,
        CancellationToken cancellationToken = default)
    {
        var location = await load(cancellationToken);
        var fullPath = Path.GetFullPath(filePath);
        if (!IsInside(fullPath, OperationFolder(location))) return new(fullPath, false);
        var folder = Create(location, released);
        var destination = Path.Combine(folder.Path, Path.GetFileName(fullPath));
        if (SamePath(destination, fullPath) || File.Exists(destination) || !File.Exists(fullPath))
        {
            return new(fullPath, true);
        }
        File.Move(fullPath, destination);
        TryRemoveEmptyFolder(Path.GetDirectoryName(fullPath));
        return new(destination, true);
    }

    /// <summary>
    /// The Server's numbering: a new process revision is the highest process revision plus one
    /// with post revision 1; a local post revision stays on the active process revision and takes
    /// the postprocessor's highest post revision there plus one.
    /// </summary>
    internal static NcProgramRevision NextRevision(PlannerGCodeCatalog catalog, string postprocessorId, string changeScope)
    {
        var name = PostprocessorName(catalog, postprocessorId);
        var active = catalog.ActiveProcessRevision;
        if (changeScope == NewProcessRevision || active is null)
        {
            var highest = catalog.ProcessRevisions.Select(value => value.ProcessRevisionNumber)
                .Concat(catalog.Releases.Select(value => value.ProcessRevisionNumber))
                .DefaultIfEmpty(0)
                .Max();
            return new(highest + 1, postprocessorId, name, 1);
        }
        var local = catalog.Releases
            .Where(value => value.ProcessRevisionId == active.ProcessRevisionId
                && string.Equals(value.PostprocessorId, postprocessorId, StringComparison.Ordinal))
            .Select(value => value.PostSpecificRevision)
            .DefaultIfEmpty(0)
            .Max();
        return new(active.ProcessRevisionNumber, postprocessorId, name, local + 1);
    }

    /// <summary>The folder path for <paramref name="revision"/>; nothing is created.</summary>
    internal static string FolderPath(NcProgramLocation location, NcProgramRevision revision) => Path.Combine(
        OperationFolder(location),
        revision.ProcessRevision.ToString(CultureInfo.InvariantCulture),
        Segment(revision.PostprocessorName),
        revision.LocalRevision.ToString(CultureInfo.InvariantCulture));

    /// <summary>A Case or postprocessor name made safe as one Windows folder name.</summary>
    internal static string Segment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray())
            .Trim()
            .TrimEnd('.', ' ');
        if (cleaned.Length == 0) return "_";
        var stem = cleaned.Split('.')[0].TrimEnd();
        return ReservedNames.Contains(stem) ? "_" + cleaned : cleaned;
    }

    private static string OperationFolder(NcProgramLocation location)
    {
        var root = location.WorkingFolderPath?.Trim();
        if (string.IsNullOrEmpty(root))
        {
            throw new InvalidOperationException("This Case has no Working Folder. Set the Case Working Folder, then save again.");
        }
        if (!Path.IsPathFullyQualified(root))
        {
            throw new InvalidOperationException($"The Case Working Folder is not a full path: {root}");
        }
        return Path.Combine(
            Path.GetFullPath(root),
            RootFolderName,
            Segment(location.CaseName),
            location.OperationNumber.ToString(CultureInfo.InvariantCulture));
    }

    private static NcProgramFolder Create(NcProgramLocation location, NcProgramRevision revision)
    {
        var path = FolderPath(location, revision);
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException($"The G-code folder could not be created: {path}. {exception.Message}", exception);
        }
        return new(path, revision);
    }

    private static string PostprocessorName(PlannerGCodeCatalog catalog, string postprocessorId) =>
        catalog.Postprocessors.FirstOrDefault(value => value.PostprocessorId == postprocessorId)?.PostprocessorName
        ?? catalog.Releases.FirstOrDefault(value => value.PostprocessorId == postprocessorId)?.PostprocessorName
        ?? throw new InvalidOperationException("The postprocessor is not one of this Operation's postprocessors.");

    private static bool IsInside(string path, string folder)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void TryRemoveEmptyFolder(string? folder)
    {
        if (folder is null) return;
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An empty folder left behind is harmless.
        }
    }
}
