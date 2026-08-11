using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Domain.JobPackages;

namespace Meimad.Planner.Server.Application.JobPackages;

internal sealed class JobPackageService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly HashSet<string> PreviewExtensions = new(
        [".png", ".jpg", ".jpeg", ".bmp", ".webp"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> NcExtensions = new(
        [".nc", ".tap", ".gcode", ".cnc", ".iso"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> TextExtensions = new(
        [".txt", ".md", ".csv", ".json", ".xml"],
        StringComparer.OrdinalIgnoreCase);

    private readonly IJobPackageRepository repository;
    private readonly EInkOptions options;
    private readonly TimeProvider timeProvider;

    public JobPackageService(
        IJobPackageRepository repository,
        EInkOptions options,
        TimeProvider timeProvider)
    {
        this.repository = repository;
        this.options = options;
        this.timeProvider = timeProvider;
    }

    internal async Task<JobPackage> GenerateAsync(
        GenerateJobPackageCommand command,
        EditAuthority editAuthority,
        CancellationToken cancellationToken = default)
    {
        var operationId = JobPackageValidator.RequiredIdentifier(
            command.BatchOperationId,
            "batchOperationId");
        var revision = JobPackageValidator.RequiredIdentifier(command.Revision, "revision", 80);
        var toolCartId = JobPackageValidator.OptionalText(command.ToolCartId, "toolCartId", 80);
        var instructions = JobPackageValidator.OptionalText(
            command.Instructions,
            "instructions",
            options.MaximumGeneratedTextCharacters);
        var sourceFiles = NormalizeSourceFiles(command.Files);
        var toolTable = NormalizeToolTable(command.ToolTable);
        var offsets = NormalizeOffsets(command.Offsets);

        var context = await repository.ReadGenerationContextAsync(
                operationId,
                editAuthority,
                cancellationToken)
            ?? throw new JobPackageOperationNotFoundException(operationId);
        if (context.Snapshot is null)
        {
            throw new JobPackageOperationNotAssignedException(operationId);
        }

        var packageId = Guid.NewGuid().ToString("N");
        var publishedAt = timeProvider.GetUtcNow();
        var packageRoot = Path.GetFullPath(options.ResolvedPackageRoot);
        Directory.CreateDirectory(packageRoot);
        var stagingPath = ResolveChild(packageRoot, $".staging-{packageId}");
        var finalPath = ResolveChild(packageRoot, packageId);
        Directory.CreateDirectory(stagingPath);
        var publishedToDisk = false;
        try
        {
            var assets = await GenerateAssetsAsync(
                packageId,
                stagingPath,
                context,
                command.IncludePreview,
                sourceFiles,
                toolTable,
                offsets,
                instructions,
                publishedAt,
                cancellationToken);
            if (assets.Count == 0)
            {
                throw new JobPackageValidationException(
                    "assets",
                    "A job package must contain at least one available asset.");
            }

            Directory.Move(stagingPath, finalPath);
            publishedToDisk = true;
            var package = new JobPackage(
                packageId,
                revision,
                toolCartId,
                publishedAt,
                context.Snapshot,
                assets);
            await repository.PublishAsync(
                package,
                context.Stamp,
                editAuthority,
                cancellationToken);
            return package;
        }
        catch
        {
            DeleteGeneratedDirectory(publishedToDisk ? finalPath : stagingPath, packageRoot);
            throw;
        }
    }

    private async Task<IReadOnlyList<JobPackageAsset>> GenerateAssetsAsync(
        string packageId,
        string stagingPath,
        JobPackageGenerationContext context,
        bool includePreview,
        IReadOnlyList<NormalizedSourceFile> sourceFiles,
        IReadOnlyList<ToolTableEntry> toolTable,
        IReadOnlyList<OffsetEntry> offsets,
        string? instructions,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken)
    {
        var assets = new List<JobPackageAsset>();
        var logicalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;

        if (includePreview && !string.IsNullOrWhiteSpace(context.PreviewPath))
        {
            var previewPath = ResolvePreviewPath(context.WorkingFolderPath, context.PreviewPath);
            EnsureExtension(previewPath, PreviewExtensions, "previewPath");
            var logicalPath = JobPackageValidator.SafeLogicalPath(
                $"preview/{Path.GetFileName(previewPath)}",
                "previewPath");
            assets.Add(await WriteSourceAssetAsync(
                packageId,
                stagingPath,
                previewPath,
                logicalPath,
                JobPackageAssetType.Preview,
                assets.Count,
                publishedAt,
                logicalPaths,
                cancellationToken));
            totalBytes = AddAndValidateTotal(totalBytes, assets[^1].ByteLength);
        }

        if (toolTable.Count > 0)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                tools = toolTable
            }, JsonOptions);
            assets.Add(await WriteGeneratedAssetAsync(
                packageId,
                stagingPath,
                "data/tool-table.json",
                JobPackageAssetType.ToolTable,
                "application/json; charset=utf-8",
                bytes,
                assets.Count,
                publishedAt,
                logicalPaths,
                cancellationToken));
            totalBytes = AddAndValidateTotal(totalBytes, bytes.LongLength);
        }

        foreach (var source in sourceFiles)
        {
            var sourcePath = ResolveCaseSourcePath(context.WorkingFolderPath, source.SourceRelativePath);
            EnsureExtension(
                sourcePath,
                source.AssetType == JobPackageAssetType.Nc ? NcExtensions : TextExtensions,
                "files.sourceRelativePath");
            assets.Add(await WriteSourceAssetAsync(
                packageId,
                stagingPath,
                sourcePath,
                source.LogicalPath,
                source.AssetType,
                assets.Count,
                publishedAt,
                logicalPaths,
                cancellationToken));
            totalBytes = AddAndValidateTotal(totalBytes, assets[^1].ByteLength);
        }

        if (offsets.Count > 0)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                offsets
            }, JsonOptions);
            assets.Add(await WriteGeneratedAssetAsync(
                packageId,
                stagingPath,
                "data/offsets.json",
                JobPackageAssetType.Offsets,
                "application/json; charset=utf-8",
                bytes,
                assets.Count,
                publishedAt,
                logicalPaths,
                cancellationToken));
            totalBytes = AddAndValidateTotal(totalBytes, bytes.LongLength);
        }

        if (instructions is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(instructions + Environment.NewLine);
            assets.Add(await WriteGeneratedAssetAsync(
                packageId,
                stagingPath,
                "instructions/instructions.txt",
                JobPackageAssetType.Instructions,
                "text/plain; charset=utf-8",
                bytes,
                assets.Count,
                publishedAt,
                logicalPaths,
                cancellationToken));
            _ = AddAndValidateTotal(totalBytes, bytes.LongLength);
        }

        if (assets.Count > options.MaximumPackageAssets)
        {
            throw new JobPackageValidationException(
                "assets",
                $"A package may contain at most {options.MaximumPackageAssets} assets.");
        }

        return assets;
    }

    private async Task<JobPackageAsset> WriteSourceAssetAsync(
        string packageId,
        string stagingPath,
        string sourcePath,
        string logicalPath,
        JobPackageAssetType assetType,
        int displayOrder,
        DateTimeOffset publishedAt,
        ISet<string> logicalPaths,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            throw new JobPackageSourceUnavailableException(sourcePath);
        }

        if (new FileInfo(sourcePath).Length > options.MaximumPackageFileBytes)
        {
            throw new JobPackageValidationException(
                "assets",
                $"Package file '{logicalPath}' exceeds the configured size limit.");
        }

        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        return await WriteGeneratedAssetAsync(
            packageId,
            stagingPath,
            logicalPath,
            assetType,
            MediaType(sourcePath),
            bytes,
            displayOrder,
            publishedAt,
            logicalPaths,
            cancellationToken);
    }

    private async Task<JobPackageAsset> WriteGeneratedAssetAsync(
        string packageId,
        string stagingPath,
        string logicalPath,
        JobPackageAssetType assetType,
        string mediaType,
        byte[] bytes,
        int displayOrder,
        DateTimeOffset publishedAt,
        ISet<string> logicalPaths,
        CancellationToken cancellationToken)
    {
        logicalPath = JobPackageValidator.SafeLogicalPath(logicalPath, "logicalPath");
        if (!logicalPaths.Add(logicalPath))
        {
            throw new JobPackageValidationException(
                "logicalPath",
                $"Package logical path '{logicalPath}' is duplicated.");
        }

        if (bytes.LongLength > options.MaximumPackageFileBytes)
        {
            throw new JobPackageValidationException(
                "assets",
                $"Package file '{logicalPath}' exceeds the configured size limit.");
        }

        var outputPath = ResolveChild(stagingPath, logicalPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken);
        return new JobPackageAsset(
            Guid.NewGuid().ToString("N"),
            assetType,
            logicalPath,
            $"{packageId}/{logicalPath}",
            mediaType,
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            publishedAt,
            displayOrder);
    }

    private IReadOnlyList<NormalizedSourceFile> NormalizeSourceFiles(
        IReadOnlyList<JobPackageSourceFileCommand>? values)
    {
        if (values is null)
        {
            return [];
        }

        if (values.Count > options.MaximumPackageAssets)
        {
            throw new JobPackageValidationException(
                "files",
                $"A package may contain at most {options.MaximumPackageAssets} source files.");
        }

        return values.Select((value, index) => new NormalizedSourceFile(
            JobPackageValidator.SourceAssetType(value.AssetType, $"files[{index}].assetType"),
            JobPackageValidator.SafeSourceRelativePath(
                value.SourceRelativePath,
                $"files[{index}].sourceRelativePath"),
            JobPackageValidator.SafeLogicalPath(
                value.LogicalPath,
                $"files[{index}].logicalPath"))).ToArray();
    }

    private static IReadOnlyList<ToolTableEntry> NormalizeToolTable(
        IReadOnlyList<ToolTableEntry>? values)
    {
        values ??= [];
        if (values.Count > 500)
        {
            throw new JobPackageValidationException(
                "toolTable",
                "A package tool table may contain at most 500 rows.");
        }

        return values.Select((value, index) => new ToolTableEntry(
            JobPackageValidator.RequiredIdentifier(value.ToolId, $"toolTable[{index}].toolId", 80),
            JobPackageValidator.RequiredIdentifier(value.Description, $"toolTable[{index}].description", 240),
            JobPackageValidator.OptionalText(value.Diameter, $"toolTable[{index}].diameter", 80),
            JobPackageValidator.OptionalText(value.Length, $"toolTable[{index}].length", 80),
            JobPackageValidator.OptionalText(value.Note, $"toolTable[{index}].note", 500)))
        .ToArray();
    }

    private static IReadOnlyList<OffsetEntry> NormalizeOffsets(
        IReadOnlyList<OffsetEntry>? values)
    {
        values ??= [];
        if (values.Count > 500)
        {
            throw new JobPackageValidationException(
                "offsets",
                "A package may contain at most 500 offset rows.");
        }

        return values.Select((value, index) => new OffsetEntry(
            JobPackageValidator.RequiredIdentifier(value.Name, $"offsets[{index}].name", 80),
            JobPackageValidator.RequiredIdentifier(value.Value, $"offsets[{index}].value", 120),
            JobPackageValidator.OptionalText(value.Unit, $"offsets[{index}].unit", 40),
            JobPackageValidator.OptionalText(value.Note, $"offsets[{index}].note", 500)))
        .ToArray();
    }

    private long AddAndValidateTotal(long total, long value)
    {
        var result = checked(total + value);
        if (result > options.MaximumPackageBytes)
        {
            throw new JobPackageValidationException(
                "assets",
                "The package exceeds the configured total size limit.");
        }

        return result;
    }

    private static string ResolveCaseSourcePath(string workingFolder, string relativePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingFolder));
        return ResolveChild(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string ResolvePreviewPath(string workingFolder, string previewPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingFolder));
        var fullPath = Path.GetFullPath(previewPath);
        if (!IsChildPath(root, fullPath))
        {
            throw new JobPackageValidationException(
                "previewPath",
                "The Case preview must be inside its Working Folder to enter an official package.");
        }

        return fullPath;
    }

    private static string ResolveChild(string root, string relativePath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!IsChildPath(normalizedRoot, fullPath))
        {
            throw new JobPackageValidationException(
                "path",
                "A package path escapes its authorized root.");
        }

        return fullPath;
    }

    private static void EnsureExtension(
        string path,
        IReadOnlySet<string> allowed,
        string field)
    {
        if (!allowed.Contains(Path.GetExtension(path)))
        {
            throw new JobPackageValidationException(
                field,
                $"File type '{Path.GetExtension(path)}' is not allowed for this package asset.");
        }
    }

    private static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".json" => "application/json; charset=utf-8",
        ".xml" => "application/xml; charset=utf-8",
        ".csv" => "text/csv; charset=utf-8",
        _ => "text/plain; charset=utf-8"
    };

    private static void DeleteGeneratedDirectory(string path, string packageRoot)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
        if (IsChildPath(root, fullPath)
            && Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private static bool IsChildPath(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative)
            && relative != "."
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private sealed record NormalizedSourceFile(
        JobPackageAssetType AssetType,
        string SourceRelativePath,
        string LogicalPath);
}

internal sealed class JobPackageOperationNotFoundException : Exception
{
    internal JobPackageOperationNotFoundException(string operationId)
        : base($"Batch Operation '{operationId}' was not found.") { }
}

internal sealed class JobPackageOperationNotAssignedException : Exception
{
    internal JobPackageOperationNotAssignedException(string operationId)
        : base($"Batch Operation '{operationId}' must be assigned to a Machine before publishing a package.") { }
}

internal sealed class JobPackageSourceUnavailableException : Exception
{
    internal JobPackageSourceUnavailableException(string path)
        : base($"An approved package source file is unavailable: '{Path.GetFileName(path)}'.") { }
}

internal sealed class JobPackageRevisionConflictException : Exception
{
    internal JobPackageRevisionConflictException(string revision)
        : base($"Package revision '{revision}' already exists for this Batch Operation.") { }
}

internal sealed class JobPackageContextChangedException : Exception
{
    internal JobPackageContextChangedException()
        : base("The Case, Batch, Operation, assignment, or Machine changed while the package was generated. Retry from current data.") { }
}
