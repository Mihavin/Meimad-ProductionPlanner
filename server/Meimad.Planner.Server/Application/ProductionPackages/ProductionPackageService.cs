using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.GCode;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Domain.Readiness;
using Meimad.Planner.Server.Domain.ToolPreparations;

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
        // MEASURED: the Tool Room's saved measurements of every required tool become the package's
        // offset payload; the control writes them from the Offset Loader (or a separate offset
        // program without verification). MANUAL_DUMMY carries no measured payload at all.
        var toolOffsets = offsetMode == "MEASURED" ? RequireMeasuredOffsets(context) : null;
        var turning = IsTurning(context.ProcessType);
        var dialectForOffsets = context.ExecutionMode == "CNC_GCODE" ? NcDialects.Profile(context.NcDialect) : null;
        var offsetLines = toolOffsets is { Count: > 0 } && dialectForOffsets is not null
            ? dialectForOffsets.ToolOffsetLines(
                toolOffsets, context.ToolDiameterOffsetKind == ToolDiameterOffsetKinds.Radius, turning)
            : null;

        var packageId = Guid.NewGuid().ToString("N");
        var packageNumber = await repository.AllocatePackageNumberAsync(cancellationToken);
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
                var dialect = NcDialects.Profile(context.NcDialect);
                var transformOptions = new NcPackageTransformOptions(
                    context.Verification is not null,
                    context.Verification?.VerifyProgramNumber ?? 9002,
                    context.Verification?.ExpectedMacroVersion ?? 1,
                    context.Verification?.EventSequenceVariable ?? dialect.PersistentVariables.Minimum,
                    dialect.Id);
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
                    var runNumber = context.RunNumber
                        ?? throw new ProductionPackageBuildException(
                            "production_package_run_number_missing",
                            "The current Production Run has no bound Run number.");
                    transformed = NcPackageTemplateTransformer.TransformCanonical(
                        sourceLines, transformOptions,
                        new(context.PartName, context.OperationName,
                            runNumber.ToString(CultureInfo.InvariantCulture),
                            packageNumber.ToString(CultureInfo.InvariantCulture),
                            context.MachineNumber, ncId.ToString(CultureInfo.InvariantCulture), offsetLoaderId),
                        ncId, out var protocol);
                    placeholderProtocolVersion = protocol;
                }
                else
                {
                    // Legacy V1 markers predate the dialect model and were only ever Haas releases.
                    if (dialect.Id != NcDialects.HaasNgc)
                        throw new ProductionPackageBuildException(
                            "production_package_dialect_legacy_unsupported",
                            $"A legacy V1 release cannot be built for the {dialect.DisplayName} dialect; re-release the NC as a canonical [[MEIMAD:...]] template.");
                    transformed = NcPackageTemplateTransformer.Transform(
                        sourceLines, transformOptions, out ncId);
                    placeholderProtocolVersion = 1;
                }
                artifacts.Add(await WriteAsync(
                    staging, packageId, ProductionPackageArtifactTypes.RunnableNc,
                    $"nc/{SafeFileName(context.GCodeOriginalFileName!)}", transformed,
                    context.GCodeReleaseId, cancellationToken));

                var identityComments = new List<string>
                {
                    FormattableString.Invariant($"(PRODUCTION PACKAGE {packageNumber})"),
                    FormattableString.Invariant($"(PRODUCTION RUN {context.RunNumber ?? 0})"),
                    $"(BATCH OPERATION {context.BatchOperationId})",
                    $"(MACHINE {context.MachineNumber})",
                    FormattableString.Invariant($"(NC RELEASE {ncId})")
                };
                var offsetComments = OffsetComments(offsetMode, toolOffsets, offsetLines, context.ToolDiameterOffsetKind);
                if (context.Verification is not null)
                {
                    // The Offset Loader writes the measured offsets first and then arms verification,
                    // so one program run on the control does both.
                    var body = new List<string>(identityComments) { $"(OFFSET LOADER RELEASE {offsetLoaderId})" };
                    body.AddRange(offsetComments);
                    if (offsetLines is not null) body.AddRange(offsetLines);
                    var loader = Encoding.ASCII.GetBytes(string.Join("\r\n", dialect.OffsetLoader(
                        body, context.Verification.ChallengeProgramNumber, releaseToken!.Value, ncId)));
                    artifacts.Add(await WriteAsync(
                        staging, packageId, ProductionPackageArtifactTypes.OffsetLoader,
                        dialect.OffsetLoaderLogicalPath, loader, offsetLoaderId, cancellationToken));
                }
                else if (offsetLines is not null)
                {
                    var body = new List<string>(identityComments);
                    body.AddRange(offsetComments);
                    var program = Encoding.ASCII.GetBytes(string.Join("\r\n", dialect.ToolOffsetProgram(body, offsetLines)));
                    artifacts.Add(await WriteAsync(
                        staging, packageId, ProductionPackageArtifactTypes.ToolOffsetProgram,
                        dialect.ToolOffsetProgramLogicalPath, program,
                        context.ToolPreparation?.ToolPreparationId, cancellationToken));
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
                if (context.ToolPreparation is not null)
                {
                    artifacts.Add(await WriteAsync(
                        staging, packageId, ProductionPackageArtifactTypes.ToolOffsets,
                        "tool-offsets/tool-offsets.json",
                        ToolOffsetsArtifact(context, toolOffsets!, offsetLines is not null),
                        context.ToolPreparation.ToolPreparationId, cancellationToken));
                }
            }

            var manifestRelative = $"{packageId}/manifest.json";
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 2,
                placeholderProtocolVersion,
                productionPackageId = packageId,
                productionPackageNumber = packageNumber,
                batchOperationId = context.BatchOperationId,
                productionRunId = context.ProductionRunId,
                productionRunNumber = context.RunNumber,
                partName = context.PartName,
                operationName = context.OperationName,
                machineAssignmentId = context.MachineAssignmentId,
                machine = new { id = context.MachineId, number = context.MachineNumber, name = context.MachineName, ncDialect = context.NcDialect },
                executionMode = context.ExecutionMode,
                ncDialect = context.NcDialect,
                toolOffsetMode = offsetMode,
                setupistMustEnterToolOffsetsManually = offsetMode == "MANUAL_DUMMY",
                serverVerificationEnabled = context.Verification is not null,
                verificationConfigurationVersion = context.Verification?.Version,
                verificationMacroVersion = context.Verification?.ExpectedMacroVersion,
                gCodeReleaseId = context.GCodeReleaseId,
                ncIdentityToken = context.NcIdentityToken,
                gCodeSourceHash = context.GCodeHash,
                toolTableReleaseId = context.ToolTableReleaseId,
                toolTableSourceHash = offsetMode == "MEASURED" ? context.ToolTableHash : null,
                toolPreparationId = offsetMode == "MEASURED" ? context.ToolPreparation?.ToolPreparationId : null,
                toolPreparationVersion = offsetMode == "MEASURED" ? context.ToolPreparation?.VersionNumber : null,
                toolPreparationHash = offsetMode == "MEASURED" ? context.ToolPreparation?.ContentHash : null,
                toolDiameterOffsetKind = context.ToolDiameterOffsetKind,
                measuredToolCount = toolOffsets?.Count,
                toolOffsetsLoadedByProgram = offsetLines is not null,
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
                packageId, packageNumber, context.BatchOperationId, context.ProductionRunId,
                context.MachineAssignmentId, context.MachineId, context.GCodeReleaseId,
                context.ToolTableReleaseId, offsetLoaderId, context.ExecutionMode,
                offsetMode,
                context.Verification is not null, context.Verification?.Version,
                context.Verification?.ExpectedMacroVersion, manifestRelative, manifest.FileHash,
                createdAt, actor, context.CurrentPackageId,
                context.DirectTransferConfigured, context.DirectTransferOnline, artifacts,
                offsetMode == "MEASURED" ? context.ToolPreparation?.ToolPreparationId : null);
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
        if (offsetMode == "MANUAL_DUMMY" && context.ExecutionMode == "CNC_GCODE"
            && context.Verification is null)
            throw new ProductionPackageBuildException(
                "manual_dummy_verification_required",
                "Manual / Dummy Tool Offsets requires Server Verification so the package contains its verification-only Offset Loader.");
        if (offsetMode == "MANUAL_DUMMY" && !context.ManualDummyToolOffsetsAllowed)
            throw new ProductionPackageBuildException(
                "manual_dummy_tool_offsets_not_enabled",
                "Manual / Dummy Tool Offsets is not enabled for the assigned Machine.");
    }

    /// <summary>
    /// The measured offsets a MEASURED package writes. Every active required released tool needs a
    /// saved measurement (length and diameter) for this Machine and Tool Table; optional tools are
    /// written when measured. A released table without rows (pre-v36 history) needs nothing.
    /// </summary>
    internal static IReadOnlyList<NcToolOffset> RequireMeasuredOffsets(ProductionPackageBuildContext context)
    {
        var released = context.ReleasedTools ?? [];
        if (released.Count == 0) return [];
        var preparation = context.ToolPreparation;
        if (preparation is null || preparation.ToolTableReleaseId != context.ToolTableReleaseId)
            throw new ProductionPackageBuildException(
                "production_package_tool_measurements_missing",
                "Tool Room measurements are missing for this Machine and Tool Table release. Open the tool table in the Tool Room, enter the measured length and diameter of every required tool, and save it.");
        var prepared = preparation.Tools.ToDictionary(tool => tool.ToolIdentifier.Trim(), tool => tool, StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var offsets = new List<NcToolOffset>();
        foreach (var tool in released)
        {
            prepared.TryGetValue(tool.ToolIdentifier.Trim(), out var measured);
            var complete = measured is { MeasuredLength: not null, MeasuredDiameter: not null }
                && measured.EffectiveOffsetNumber is not null;
            if (complete)
            {
                offsets.Add(new NcToolOffset(
                    measured!.EffectiveOffsetNumber!.Value, tool.ToolIdentifier, tool.Description,
                    measured.MeasuredLength!.Value, measured.MeasuredDiameter!.Value));
            }
            else if (tool.IsRequired)
            {
                missing.Add(tool.ToolIdentifier);
            }
        }
        if (missing.Count > 0)
            throw new ProductionPackageBuildException(
                "production_package_tool_measurements_missing",
                $"Tool Room measurements (length, diameter and offset number) are missing for {string.Join(", ", missing)}. Enter them in the Tool Room's tool table and save it.");
        var duplicate = offsets.GroupBy(offset => offset.OffsetNumber).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ProductionPackageBuildException(
                "production_package_tool_offset_number_duplicate",
                $"Offset number {duplicate.Key} is used by {string.Join(" and ", duplicate.Select(offset => offset.ToolIdentifier))}; give every tool its own offset number.");
        return offsets.OrderBy(offset => offset.OffsetNumber).ToArray();
    }

    internal static bool IsTurning(string processType) =>
        processType.Contains("turn", StringComparison.OrdinalIgnoreCase)
        || processType.Contains("lathe", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> OffsetComments(
        string offsetMode, IReadOnlyList<NcToolOffset>? toolOffsets, IReadOnlyList<string>? offsetLines, string kind)
    {
        if (offsetMode == "MANUAL_DUMMY") return ["(MANUAL DUMMY TOOL OFFSETS - VERIFICATION ONLY)"];
        var comments = new List<string> { "(MEASURED TOOL OFFSETS - VERIFICATION AND RELEASE BINDING)" };
        if (toolOffsets is { Count: > 0 })
        {
            comments.Add(offsetLines is null
                ? FormattableString.Invariant($"(TOOL OFFSETS FOR {toolOffsets.Count} TOOLS ARE IN tool-offsets.json - ENTER THEM ON THIS CONTROL)")
                : FormattableString.Invariant($"(WRITES {toolOffsets.Count} MEASURED TOOL OFFSETS IN MM - {kind} CUTTER VALUES)"));
        }
        return comments;
    }

    private static byte[] ToolOffsetsArtifact(
        ProductionPackageBuildContext context, IReadOnlyList<NcToolOffset> offsets, bool loadedByProgram)
    {
        var preparation = context.ToolPreparation!;
        var prepared = preparation.Tools.ToDictionary(tool => tool.ToolIdentifier.Trim(), tool => tool, StringComparer.OrdinalIgnoreCase);
        var written = offsets.ToDictionary(offset => offset.ToolIdentifier.Trim(), offset => offset, StringComparer.OrdinalIgnoreCase);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            machine = new
            {
                id = context.MachineId,
                number = context.MachineNumber,
                name = context.MachineName,
                ncDialect = context.NcDialect,
                processType = context.ProcessType,
                toolDiameterOffsetKind = context.ToolDiameterOffsetKind
            },
            toolTableReleaseId = context.ToolTableReleaseId,
            toolPreparationId = preparation.ToolPreparationId,
            toolPreparationVersion = preparation.VersionNumber,
            savedAt = preparation.SavedAt,
            savedBy = preparation.SavedBy,
            contentHash = preparation.ContentHash,
            lengthUnit = "mm",
            offsetsLoadedByProgram = loadedByProgram,
            tools = (context.ReleasedTools ?? []).Select(released =>
            {
                prepared.TryGetValue(released.ToolIdentifier.Trim(), out var measured);
                written.TryGetValue(released.ToolIdentifier.Trim(), out var offset);
                return new
                {
                    tool = released.ToolIdentifier,
                    description = released.Description,
                    isRequired = released.IsRequired,
                    magazinePosition = released.MagazinePosition,
                    offsetNumber = offset?.OffsetNumber ?? measured?.EffectiveOffsetNumber,
                    measuredLength = measured?.MeasuredLength,
                    measuredDiameter = measured?.MeasuredDiameter,
                    writtenByProgram = offset is not null && loadedByProgram,
                    shapeType = measured?.ShapeType,
                    shape = measured?.Shape,
                    notes = measured?.Notes,
                    components = (measured?.Components ?? []).Select(component => new
                    {
                        component.Sequence,
                        component.ComponentType,
                        component.Name,
                        component.CatalogNumber,
                        component.Length,
                        component.Diameter,
                        component.Notes
                    })
                };
            })
        }, JsonOptions);
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
