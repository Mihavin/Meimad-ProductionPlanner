using System.Security.Cryptography;
using Meimad.Planner.NcEngine;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Domain.GCode;

namespace Meimad.Planner.Server.Application.GCode;

internal sealed class GCodeArtifactStore
{
    private static readonly HashSet<string> GCodeExtensions = new(
        [".nc", ".tap", ".gcode", ".cnc", ".iso", ".mpf", ".spf"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ToolTableExtensions = new(
        [".json", ".csv", ".txt", ".mht", ".mhtml"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Most subprogram files one release may carry.</summary>
    internal const int MaximumSubprogramFiles = 50;

    private readonly GCodeOptions options;

    public GCodeArtifactStore(GCodeOptions options) => this.options = options;

    internal Task<StoredArtifactPublication> PublishGCodeAsync(
        string operationId,
        string releaseId,
        UploadedReleaseFile file,
        CancellationToken cancellationToken) =>
        PublishGCodeAsync(operationId, releaseId, file, [], cancellationToken);

    /// <summary>
    /// Stores the main program and its subprogram files in one immutable release folder. A
    /// subprogram with a program number is stored as <c>O&lt;number&gt;</c> with its extension, the
    /// name the NC engine looks for in the program's folder, so the Server's analysis follows the
    /// calls; its original name is kept for downloads and packages.
    /// </summary>
    internal Task<StoredArtifactPublication> PublishGCodeAsync(
        string operationId,
        string releaseId,
        UploadedReleaseFile file,
        IReadOnlyList<UploadedReleaseFile> subprograms,
        CancellationToken cancellationToken) =>
        PublishAsync(
            operationId,
            "gcode",
            releaseId,
            file,
            options.MaximumGCodeFileBytes,
            GCodeExtensions,
            cancellationToken,
            subprograms);

    internal Task<StoredArtifactPublication> PublishToolTableAsync(
        string operationId,
        string releaseId,
        UploadedReleaseFile file,
        CancellationToken cancellationToken) =>
        PublishAsync(
            operationId,
            "tool-tables",
            releaseId,
            file,
            options.MaximumToolTableFileBytes,
            ToolTableExtensions,
            cancellationToken);

    internal string ResolveStoredPath(string relativePath)
    {
        var root = Path.GetFullPath(options.ResolvedReleaseRoot);
        var path = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(path, root))
        {
            throw new InvalidDataException("Stored G-code path leaves the configured release root.");
        }

        return path;
    }

    internal void DeletePublication(StoredArtifactPublication? publication)
    {
        if (publication is null)
        {
            return;
        }

        DeleteDirectory(publication.DirectoryPath);
    }

    internal string RootPath => Path.GetFullPath(options.ResolvedReleaseRoot);

    private async Task<StoredArtifactPublication> PublishAsync(
        string operationId,
        string kind,
        string artifactId,
        UploadedReleaseFile file,
        long maximumBytes,
        IReadOnlySet<string> allowedExtensions,
        CancellationToken cancellationToken,
        IReadOnlyList<UploadedReleaseFile>? subprograms = null)
    {
        if (subprograms is { Count: > MaximumSubprogramFiles })
        {
            throw new GCodeValidationException(
                "subprogramFiles",
                "too_many_subprograms",
                $"A release may carry at most {MaximumSubprogramFiles} subprogram files.");
        }

        if (file.Content is null || !file.Content.CanRead)
        {
            throw new GCodeValidationException("file", "required", "A readable release file is required.");
        }

        var originalName = RequiredFileName(file.OriginalFileName, "file");
        var extension = Path.GetExtension(originalName);
        if (!allowedExtensions.Contains(extension))
        {
            throw new GCodeValidationException(
                "file",
                "unsupported_extension",
                $"File extension '{extension}' is not allowed for this release artifact.");
        }

        if (file.DeclaredLength is <= 0 || file.DeclaredLength > maximumBytes)
        {
            throw new GCodeValidationException(
                "file",
                "file_size_invalid",
                $"Release file size must be between 1 and {maximumBytes} bytes.");
        }

        var safeName = SanitizeStoredFileName(originalName, extension);
        var root = RootPath;
        Directory.CreateDirectory(root);
        var operationSegment = SafeIdentifier(operationId, "caseOperationId");
        var artifactSegment = SafeIdentifier(artifactId, "artifactId");
        var parent = ResolveChild(root, Path.Combine("operations", operationSegment, kind));
        Directory.CreateDirectory(parent);
        var stagingDirectory = ResolveChild(parent, $".staging-{artifactSegment}");
        var finalDirectory = ResolveChild(parent, artifactSegment);
        if (Directory.Exists(stagingDirectory) || Directory.Exists(finalDirectory))
        {
            throw new GCodeStorageException("The immutable release storage target already exists.");
        }

        Directory.CreateDirectory(stagingDirectory);
        try
        {
            var outputPath = ResolveChild(stagingDirectory, safeName);
            var (length, hash) = await WriteUploadAsync(file, outputPath, maximumBytes, "file", cancellationToken);
            var storedSubprograms = await WriteSubprogramsAsync(
                subprograms ?? [], stagingDirectory, originalName, safeName, maximumBytes, cancellationToken);

            await File.WriteAllTextAsync(
                ResolveChild(stagingDirectory, ".meimad-release-id"),
                artifactId,
                cancellationToken);
            Directory.Move(stagingDirectory, finalDirectory);
            string Relative(string name) => Path.GetRelativePath(root, ResolveChild(finalDirectory, name))
                .Replace(Path.DirectorySeparatorChar, '/');
            return new StoredArtifactPublication(
                new StoredReleaseFile(
                    artifactId,
                    originalName,
                    Relative(safeName),
                    length,
                    hash),
                finalDirectory,
                storedSubprograms.Select(value => new StoredSubprogramFile(
                    new StoredReleaseFile(
                        value.SubprogramId, value.OriginalName, Relative(value.StoredName), value.Length, value.Hash),
                    value.ProgramNumber)).ToArray());
        }
        catch
        {
            DeleteDirectory(stagingDirectory);
            throw;
        }
    }

    private static async Task<IReadOnlyList<WrittenSubprogram>> WriteSubprogramsAsync(
        IReadOnlyList<UploadedReleaseFile> subprograms,
        string stagingDirectory,
        string mainOriginalName,
        string mainStoredName,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var written = new List<WrittenSubprogram>();
        var originalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mainOriginalName };
        var storedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mainStoredName };
        var numbers = new Dictionary<int, string>();
        for (var index = 0; index < subprograms.Count; index++)
        {
            var subprogram = subprograms[index];
            if (subprogram.Content is null || !subprogram.Content.CanRead)
            {
                throw new GCodeValidationException("subprogramFiles", "required", "A readable subprogram file is required.");
            }

            var originalName = RequiredFileName(subprogram.OriginalFileName, "subprogramFiles");
            var extension = Path.GetExtension(originalName);
            if (!NcSubprogramCalls.FileExtensions.Contains(extension))
            {
                throw new GCodeValidationException(
                    "subprogramFiles",
                    "unsupported_extension",
                    $"Subprogram file '{originalName}' has an extension that is not allowed for NC programs.");
            }

            if (!originalNames.Add(originalName))
            {
                throw new GCodeValidationException(
                    "subprogramFiles",
                    "duplicate_file_name",
                    $"'{originalName}' is included twice, or has the main program's name.");
            }

            if (subprogram.DeclaredLength is <= 0 || subprogram.DeclaredLength > maximumBytes)
            {
                throw new GCodeValidationException(
                    "subprogramFiles",
                    "file_size_invalid",
                    $"Subprogram file '{originalName}' must be between 1 and {maximumBytes} bytes.");
            }

            var temporary = ResolveChild(stagingDirectory, $".subprogram-{index}");
            var (length, hash) = await WriteUploadAsync(
                subprogram, temporary, maximumBytes, "subprogramFiles", cancellationToken);
            var number = NcSubprogramCalls.ProgramNumber(originalName, File.ReadLines(temporary).Take(20).ToArray());
            if (number is int programNumber && !numbers.TryAdd(programNumber, originalName))
            {
                throw new GCodeValidationException(
                    "subprogramFiles",
                    "duplicate_program_number",
                    $"'{numbers[programNumber]}' and '{originalName}' are both program O{programNumber}.");
            }

            var storedName = number is int value
                ? StoredSubprogramName(value, extension)
                : SanitizeStoredFileName(originalName, extension);
            if (!storedNames.Add(storedName))
            {
                storedName = SanitizeStoredFileName(originalName, extension);
                if (!storedNames.Add(storedName))
                {
                    throw new GCodeValidationException(
                        "subprogramFiles",
                        "file_name_conflict",
                        $"'{originalName}' cannot be stored beside a file with a similar name; rename one of them.");
                }
            }

            File.Move(temporary, ResolveChild(stagingDirectory, storedName));
            written.Add(new(Guid.NewGuid().ToString("N"), originalName, storedName, number, length, hash));
        }

        return written;
    }

    private static string StoredSubprogramName(int number, string extension)
    {
        var lower = extension.ToLowerInvariant();
        return FormattableString.Invariant(
            $"O{number}{(NcSubprogramCalls.FolderExtensions.Contains(lower) ? lower : ".nc")}");
    }

    private static async Task<(long Length, string Hash)> WriteUploadAsync(
        UploadedReleaseFile file,
        string outputPath,
        long maximumBytes,
        string field,
        CancellationToken cancellationToken)
    {
        long length = 0;
        await using (var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await file.Content.ReadAsync(buffer, cancellationToken)) > 0)
            {
                length = checked(length + read);
                if (length > maximumBytes)
                {
                    throw new GCodeValidationException(
                        field, "file_too_large", $"Release file exceeds {maximumBytes} bytes.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            await output.FlushAsync(cancellationToken);
        }

        if (length == 0)
        {
            throw new GCodeValidationException(field, "file_empty", "Release file cannot be empty.");
        }

        await using var input = File.OpenRead(outputPath);
        return (length, Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken)));
    }

    private sealed record WrittenSubprogram(
        string SubprogramId,
        string OriginalName,
        string StoredName,
        int? ProgramNumber,
        long Length,
        string Hash);

    private static string RequiredFileName(string value, string field)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 255)
        {
            throw new GCodeValidationException(field, "invalid_file_name", "A filename of at most 255 characters is required.");
        }

        return trimmed;
    }

    private static string SanitizeStoredFileName(string originalName, string extension)
    {
        var stem = Path.GetFileNameWithoutExtension(Path.GetFileName(originalName));
        var safe = new string(stem.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_').ToArray());
        safe = safe.Trim('.', '_');
        if (safe.Length == 0)
        {
            safe = "release";
        }

        if (safe.Length > 160)
        {
            safe = safe[..160];
        }

        return safe + extension.ToLowerInvariant();
    }

    private static string SafeIdentifier(string value, string field)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || trimmed.Length > 200
            || trimmed.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new GCodeValidationException(field, "invalid_identifier", $"{field} is not a safe identifier.");
        }

        return trimmed;
    }

    private static string ResolveChild(string parent, string child)
    {
        var resolved = Path.GetFullPath(Path.Combine(parent, child));
        if (!IsWithin(resolved, parent))
        {
            throw new GCodeStorageException("Release storage path escaped its configured parent.");
        }

        return resolved;
    }

    private static bool IsWithin(string child, string parent)
    {
        var normalizedParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedChild = Path.GetFullPath(child)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedChild.StartsWith(
            normalizedParent + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed record StoredArtifactPublication(
    StoredReleaseFile File,
    string DirectoryPath,
    IReadOnlyList<StoredSubprogramFile>? Subprograms = null);

/// <summary>A stored subprogram file; its ArtifactId is the subprogram's id.</summary>
internal sealed record StoredSubprogramFile(StoredReleaseFile File, int? ProgramNumber);

internal sealed class GCodeStorageException(string message) : Exception(message);
