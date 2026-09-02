using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.GCode;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Domain.Readiness;

namespace Meimad.Planner.Server.Application.ProductionPackages;

internal sealed class ProductionPackageService(
    IProductionPackageRepository repository,
    ProductionPackageOptions options,
    GCodeArtifactStore releaseStore,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    internal async Task<ProductionPackageRecord> CreateAsync(
        string batchOperationId,
        string createdBy,
        string toolOffsetMode = "MEASURED",
        CancellationToken cancellationToken = default)
    {
        var operationId = Required(batchOperationId, "batchOperationId");
        var actor = Required(createdBy, "createdBy");
        var context = await repository.ReadBuildContextAsync(operationId, cancellationToken)
            ?? throw new ProductionPackageBuildException(
                "production_package_operation_not_found",
                "The assigned Operation was not found.");
        var offsetMode = NormalizeOffsetMode(toolOffsetMode);
        ValidatePrerequisites(context, offsetMode);

        var packageId = Guid.NewGuid().ToString("N");
        var offsetLoaderId = context.Verification is null ? null : Guid.NewGuid().ToString("N");
        var releaseToken = context.Verification is null
            ? (int?)null
            : RandomNumberGenerator.GetInt32(100000, 1_000_000);
        var createdAt = timeProvider.GetUtcNow();
        var root = Path.GetFullPath(options.ResolvedPackageRoot);
        Directory.CreateDirectory(root);
        var staging = ResolveChild(root, $".staging-{packageId}");
        var final = ResolveChild(root, packageId);
        Directory.CreateDirectory(staging);
        var moved = false;
        try
        {
            var artifacts = new List<ProductionPackageArtifact>();
            int? placeholderProtocolVersion = null;
            if (context.ExecutionMode == "CNC_GCODE")
            {
                var sourcePath = releaseStore.ResolveStoredPath(context.GCodeStoredRelativePath!);
                if (!File.Exists(sourcePath))
                    throw new ProductionPackageBuildException(
                        "production_package_source_missing",
                        "The immutable NC source artifact is missing; no package was activated.");
                var sourceBytes = await ReadVerifiedSourceAsync(
                    sourcePath, context.GCodeHash!, "NC", cancellationToken);
                var sourceLines = Encoding.UTF8.GetString(sourceBytes).Split(
                    ["\r\n", "\n", "\r"], StringSplitOptions.None);
                var transformOptions = new NcPackageTransformOptions(
                    context.Verification is not null,
                    context.Verification?.VerifyProgramNumber ?? 9002,
                    context.Verification?.ExpectedMacroVersion ?? 1,
                    context.Verification?.EventSequenceVariable ?? 10000);
                int ncId;
                byte[] transformed;
                if (NcPackagePlaceholderSchema.IsCanonical(sourceLines))
                {
                    if (context.ProductionRunId is null)
                        throw new ProductionPackageBuildException(
                            "production_package_run_missing",
                            "Canonical CNC package creation requires a concrete Production Run.");
                    ncId = context.NcIdentityToken
                        ?? throw new ProductionPackageBuildException(
                            "production_package_nc_identity_missing",
                            "The current immutable NC release has no bound NC identity token.");
                    transformed = NcPackageTemplateTransformer.TransformCanonical(
                        sourceLines, transformOptions,
                        new(context.PartName, context.OperationName, context.ProductionRunId,
                            packageId, context.MachineId, context.GCodeReleaseId!, offsetLoaderId),
                        ncId, out var protocol);
                    placeholderProtocolVersion = protocol;
                }
                else
                {
                    transformed = NcPackageTemplateTransformer.Transform(
                        sourceLines, transformOptions, out ncId);
                    placeholderProtocolVersion = 1;
                }
                artifacts.Add(await WriteAsync(
                    staging, packageId, ProductionPackageArtifactTypes.RunnableNc,
                    $"nc/{SafeFileName(context.GCodeOriginalFileName!)}", transformed,
                    context.GCodeReleaseId, cancellationToken));

                if (context.Verification is not null)
                {
                    var loader = Encoding.ASCII.GetBytes(string.Join("\r\n", new[]
                    {
                        "%",
                        "O01990 (MEIMAD PACKAGE OFFSET LOADER)",
                        $"(PRODUCTION PACKAGE {packageId})",
                        $"(PRODUCTION RUN {context.ProductionRunId})",
                        $"(BATCH OPERATION {context.BatchOperationId})",
                        $"(MACHINE {context.MachineId})",
                        $"(NC RELEASE {context.GCodeReleaseId})",
                        $"(OFFSET LOADER RELEASE {offsetLoaderId})",
                        $"G65 P{context.Verification.ChallengeProgramNumber} A{releaseToken}. B{ncId}.",
                        "M30",
                        "%",
                        string.Empty
                    }));
                    artifacts.Add(await WriteAsync(
                        staging, packageId, ProductionPackageArtifactTypes.OffsetLoader,
                        "offset-loader/O01990.nc", loader, offsetLoaderId, cancellationToken));
                }
            }

            if (offsetMode == "MEASURED")
            {
                var toolPath = releaseStore.ResolveStoredPath(context.ToolTableStoredRelativePath);
                if (!File.Exists(toolPath))
                    throw new ProductionPackageBuildException(
                        "production_package_source_missing",
                        "The immutable Tool Table source artifact is missing; no package was activated.");
                var toolBytes = await ReadVerifiedSourceAsync(
                    toolPath, context.ToolTableHash, "Tool Table", cancellationToken);
                artifacts.Add(await WriteAsync(
                    staging, packageId, ProductionPackageArtifactTypes.ToolTable,
                    $"tool-table/{SafeFileName(context.ToolTableOriginalFileName)}", toolBytes,
                    context.ToolTableReleaseId, cancellationToken));
            }

            var manifestRelative = $"{packageId}/manifest.json";
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 2,
                placeholderProtocolVersion,
                productionPackageId = packageId,
                batchOperationId = context.BatchOperationId,
                productionRunId = context.ProductionRunId,
                partName = context.PartName,
                operationName = context.OperationName,
                machineAssignmentId = context.MachineAssignmentId,
                machine = new { id = context.MachineId, number = context.MachineNumber, name = context.MachineName },
                executionMode = context.ExecutionMode,
                toolOffsetMode = offsetMode,
                setupistMustEnterToolOffsetsManually = offsetMode == "MANUAL_DUMMY",
                serverVerificationEnabled = context.Verification is not null,
                verificationConfigurationVersion = context.Verification?.Version,
                verificationMacroVersion = context.Verification?.ExpectedMacroVersion,
                gCodeReleaseId = context.GCodeReleaseId,
                gCodeSourceHash = context.GCodeHash,
                toolTableReleaseId = context.ToolTableReleaseId,
                toolTableSourceHash = offsetMode == "MEASURED" ? context.ToolTableHash : null,
                offsetLoaderReleaseId = offsetLoaderId,
                offsetLoaderReleaseToken = releaseToken,
                createdAt,
                createdBy = actor,
                supersedesProductionPackageId = context.CurrentPackageId,
                machineCapabilitySnapshot = new
                {
                    context.ExecutionMode,
                    context.ManualDummyToolOffsetsAllowed,
                    context.DirectTransferConfigured,
                    context.DirectTransferOnline,
                    serverVerificationEnabled = context.Verification is not null,
                    verificationConfigurationVersion = context.Verification?.Version,
                    challengeProgramNumber = context.Verification?.ChallengeProgramNumber,
                    verifyProgramNumber = context.Verification?.VerifyProgramNumber,
                    expectedMacroVersion = context.Verification?.ExpectedMacroVersion,
                    eventSequenceVariable = context.Verification?.EventSequenceVariable
                },
                artifacts = artifacts.Select(value => new
                {
                    value.ArtifactId,
                    value.ArtifactType,
                    value.LogicalPath,
                    value.FileSize,
                    sha256 = value.FileHash,
                    value.SourceReleaseId
                })
            }, JsonOptions);
            var manifest = await WriteAsync(
                staging, packageId, ProductionPackageArtifactTypes.Manifest,
                "manifest.json", manifestBytes, null, cancellationToken);
            artifacts.Add(manifest);

            Directory.Move(staging, final);
            moved = true;
            var record = new ProductionPackageRecord(
                packageId, context.BatchOperationId, context.ProductionRunId,
                context.MachineAssignmentId, context.MachineId, context.GCodeReleaseId,
                context.ToolTableReleaseId, offsetLoaderId, context.ExecutionMode,
                offsetMode,
                context.Verification is not null, context.Verification?.Version,
                context.Verification?.ExpectedMacroVersion, manifestRelative, manifest.FileHash,
                createdAt, actor, context.CurrentPackageId,
                context.DirectTransferConfigured, context.DirectTransferOnline, artifacts);
            var loaderPublication = offsetLoaderId is null ? null : new OffsetLoaderPublication(
                offsetLoaderId, releaseToken!.Value,
                artifacts.Single(value => value.ArtifactType == ProductionPackageArtifactTypes.OffsetLoader).FileHash);
            await repository.ActivateAsync(record, loaderPublication, cancellationToken);
            return record;
        }
        catch
        {
            DeleteDirectory(moved ? final : staging);
            throw;
        }
    }

    internal Task<ProductionPackageRecord?> ReadCurrentAsync(
        string batchOperationId,
        CancellationToken cancellationToken = default) =>
        repository.ReadCurrentAsync(Required(batchOperationId, "batchOperationId"), cancellationToken);

    internal async Task<(string Path, string FileName, string Hash)?> OpenCurrentArtifactAsync(
        string batchOperationId,
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        var package = await ReadCurrentAsync(batchOperationId, cancellationToken);
        var artifact = package?.Artifacts.SingleOrDefault(value => value.ArtifactId == artifactId);
        if (artifact is null) return null;
        var path = ResolveChild(Path.GetFullPath(options.ResolvedPackageRoot), artifact.StoredRelativePath);
        if (!File.Exists(path))
            throw new ProductionPackageBuildException(
                "production_package_artifact_missing",
                "The immutable Production Package artifact is missing from Server storage.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actual, artifact.FileHash, StringComparison.Ordinal))
            throw new ProductionPackageBuildException(
                "production_package_artifact_corrupt",
                "The immutable Production Package artifact failed its checksum verification.");
        return (path, Path.GetFileName(artifact.LogicalPath), artifact.FileHash);
    }

    private static void ValidatePrerequisites(ProductionPackageBuildContext context, string offsetMode)
    {
        var readiness = ProductionReadinessEvaluator.Evaluate(context.ReadinessContext);
        var requiredKeys = offsetMode == "MANUAL_DUMMY"
            ? (context.ExecutionMode == "MANUAL" ? Array.Empty<string>() :
                new[] { ReadinessComponentKeys.GCode, ReadinessComponentKeys.MachinePostprocessorCompatibility,
                    ReadinessComponentKeys.ToolCapacity })
            : context.ExecutionMode == "MANUAL"
            ? new[] { ReadinessComponentKeys.ToolTable, ReadinessComponentKeys.ToolCapacity, ReadinessComponentKeys.ToolOffsets }
            : new[] { ReadinessComponentKeys.GCode, ReadinessComponentKeys.MachinePostprocessorCompatibility,
                ReadinessComponentKeys.ToolTable, ReadinessComponentKeys.ToolCapacity, ReadinessComponentKeys.ToolOffsets };
        var missing = readiness.Components
            .Where(value => requiredKeys.Contains(value.Key, StringComparer.Ordinal)
                && (value.IsBlocking || value.State is not (ReadinessStates.Ready or ReadinessStates.NotRequired)))
            .Select(value => $"{value.Label}: {value.Message}")
            .ToArray();
        if (missing.Length > 0)
            throw new ProductionPackageBuildException(
                "production_package_prerequisites_not_ready",
                "Production Package cannot be created. " + string.Join(" ", missing));
        if (context.ExecutionMode == "CNC_GCODE" && context.GCodeReleaseId is null)
            throw new ProductionPackageBuildException(
                "production_package_gcode_missing", "A current compatible NC release is required.");
        if (context.Verification is not null && context.ProductionRunId is null)
            throw new ProductionPackageBuildException(
                "production_package_run_missing",
                "Server Verification requires a concrete Production Run for exact Run/Machine/NC/Offset Loader binding.");
        if (offsetMode == "MANUAL_DUMMY" && !context.ManualDummyToolOffsetsAllowed)
            throw new ProductionPackageBuildException(
                "manual_dummy_tool_offsets_not_enabled",
                "Manual / Dummy Tool Offsets is not enabled for the assigned Machine.");
    }

    private static string NormalizeOffsetMode(string? value)
    {
        var result = string.IsNullOrWhiteSpace(value) ? "MEASURED" : value.Trim().ToUpperInvariant();
        return result is "MEASURED" or "MANUAL_DUMMY" ? result :
            throw new ProductionPackageBuildException("production_package_offset_mode_invalid",
                "toolOffsetMode must be MEASURED or MANUAL_DUMMY.");
    }

    private async Task<ProductionPackageArtifact> WriteAsync(
        string staging, string packageId, string type, string logicalPath, byte[] bytes,
        string? sourceReleaseId, CancellationToken token)
    {
        if (bytes.LongLength is 0 || bytes.LongLength > options.MaximumArtifactBytes)
            throw new ProductionPackageBuildException(
                "production_package_artifact_size_invalid",
                $"Artifact '{logicalPath}' is empty or exceeds the configured limit.");
        var path = ResolveChild(staging, logicalPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, token);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var actual = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, token)));
        if (actual != hash)
            throw new ProductionPackageBuildException(
                "production_package_artifact_write_failed", $"Artifact '{logicalPath}' failed write verification.");
        return new(Guid.NewGuid().ToString("N"), type, logicalPath,
            $"{packageId}/{logicalPath}", bytes.LongLength, hash, sourceReleaseId);
    }

    private static async Task<byte[]> ReadVerifiedSourceAsync(
        string path,
        string expectedHash,
        string label,
        CancellationToken token)
    {
        var bytes = await File.ReadAllBytesAsync(path, token);
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new ProductionPackageBuildException(
                "production_package_source_corrupt",
                $"The immutable {label} source failed checksum verification; no package was activated.");
        return bytes;
    }

    private static string Required(string? value, string field)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > 200)
            throw new ProductionPackageBuildException("production_package_input_invalid", $"{field} is required.");
        return trimmed;
    }

    private static string SafeFileName(string value)
    {
        var name = Path.GetFileName(value);
        return new string(name.Select(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_').ToArray());
    }

    private static string ResolveChild(string parent, string child)
    {
        var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        var result = Path.GetFullPath(Path.Combine(root, child));
        if (!result.StartsWith(root + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ProductionPackageBuildException(
                "production_package_path_invalid", "Production Package path escaped Server storage.");
        return result;
    }

    private static void DeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
