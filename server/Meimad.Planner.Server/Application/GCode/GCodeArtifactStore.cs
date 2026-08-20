using System.Security.Cryptography;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Domain.GCode;

namespace Meimad.Planner.Server.Application.GCode;

internal sealed class GCodeArtifactStore
{
    private static readonly HashSet<string> GCodeExtensions = new(
        [".nc", ".tap", ".gcode", ".cnc", ".iso", ".mpf", ".spf"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ToolTableExtensions = new(
        [".json", ".csv", ".txt"],
        StringComparer.OrdinalIgnoreCase);

    private readonly GCodeOptions options;

    public GCodeArtifactStore(GCodeOptions options) => this.options = options;

    internal Task<StoredArtifactPublication> PublishGCodeAsync(
        string operationId,
        string releaseId,
        UploadedReleaseFile file,
        CancellationToken cancellationToken) =>
        PublishAsync(
            operationId,
            "gcode",
            releaseId,
            file,
            options.MaximumGCodeFileBytes,
            GCodeExtensions,
            cancellationToken);

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
        CancellationToken cancellationToken)
    {
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
                            "file", "file_too_large", $"Release file exceeds {maximumBytes} bytes.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                await output.FlushAsync(cancellationToken);
            }

            if (length == 0)
            {
                throw new GCodeValidationException("file", "file_empty", "Release file cannot be empty.");
            }

            string hash;
            await using (var input = File.OpenRead(outputPath))
            {
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken));
            }

            await File.WriteAllTextAsync(
                ResolveChild(stagingDirectory, ".meimad-release-id"),
                artifactId,
                cancellationToken);
            Directory.Move(stagingDirectory, finalDirectory);
            var relativePath = Path.GetRelativePath(root, ResolveChild(finalDirectory, safeName))
                .Replace(Path.DirectorySeparatorChar, '/');
            return new StoredArtifactPublication(
                new StoredReleaseFile(
                    artifactId,
                    originalName,
                    relativePath,
                    length,
                    hash),
                finalDirectory);
        }
        catch
        {
            DeleteDirectory(stagingDirectory);
            throw;
        }
    }

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

internal sealed record StoredArtifactPublication(StoredReleaseFile File, string DirectoryPath);

internal sealed class GCodeStorageException(string message) : Exception(message);
