using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Application.GCode;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Domain.Cases;
using Meimad.Planner.Server.Domain.Machines;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.SimulationPackage.Coordinates;
using Meimad.Planner.SimulationPackage.Diagnostics;
using Meimad.Planner.SimulationPackage.Geometry;
using Meimad.Planner.SimulationPackage.Manifest;
using Meimad.Planner.SimulationPackage.Tools;
using Meimad.Planner.SimulationPackage.Validation;
using static Meimad.Planner.SimulationPackage.Diagnostics.PackageDiagnosticCodes;

namespace Meimad.Planner.Server.Application.SimulationPackages;

/// <summary>
/// Server-side package importer (Cimatron exporter definition §13; execution plan §5
/// <c>IPackageImporter.Validate/Import</c>). The Server is the only reader of the package that writes:
/// it validates the folder (approved roots, COMPLETE, manifest, hashes, geometry, NC/tool table, UCS),
/// resolves the Case and Operation, maps the Machine (§10, never picking among several), lets the
/// planner choose among several stock candidates, and then stores — in one transaction — the import
/// record, the Operation model-file links (selected stock, every fixture, the finished part) and the
/// Work CS (mode A: identity at the manifest work offset; mode B: the UCS origin/rotation). The package
/// files are only read. Importing the same manifest again for the same Operation changes nothing.
/// The Machine mapping is CAM context only: it never assigns the Operation to a Machine.
/// </summary>
internal sealed class SimulationPackageImportService(
    SqliteSimulationPackageRepository repository,
    SimulationPackageOptions options,
    ServerFileAccessOptions fileAccess,
    TimeProvider timeProvider) : IPackageImporter
{
    internal const string WatcherActor = "package-watcher";

    public async Task<PackageValidationReport> ValidateAsync(string packagePath, CancellationToken cancellationToken = default) =>
        (await ValidateCoreAsync(packagePath, null, cancellationToken)).Report;

    public async Task<PackageImportResult> ImportAsync(PackageImportRequest request, CancellationToken cancellationToken = default) =>
        (await ImportCoreAsync(request, checkActiveImport: false, expectedActiveImportId: null, recordBlocked: false, cancellationToken)).Result;

    /// <summary>Validates with the approved roots of the Server (and the Case working folder when known).</summary>
    internal async Task<(PackageValidationReport Report, string ResolvedPath)> ValidateCoreAsync(
        string packagePath, string? caseWorkingFolder, CancellationToken cancellationToken)
    {
        var network = await repository.ReadNetworkFolderAsync(cancellationToken);
        var resolved = ResolvePath(packagePath, network);
        var roots = new List<string>();
        if (network.IsConfigured) roots.Add(network.RootPath!);
        roots.AddRange(options.ApprovedRoots);
        if (!string.IsNullOrWhiteSpace(caseWorkingFolder) && Path.IsPathFullyQualified(caseWorkingFolder)) roots.Add(caseWorkingFolder);
        var validator = new SimulationPackageValidator(new PackageValidationOptions
        {
            ApprovedRoots = roots,
            MaxFileBytes = options.MaximumFileBytes,
            MaxFixtureCount = options.MaximumFixtureCount,
            TestedCimatronVersions = options.TestedCimatronVersions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ToolTableReader = new ReleasedToolTableNumberReader()
        });
        return (await validator.ValidateAsync(resolved, cancellationToken), resolved);
    }

    /// <summary>
    /// A PC drive path (Z:\…) is converted to the network root like every Case link, then through the
    /// configured drive mappings, so the Server reads the same folder the PC sees.
    /// </summary>
    internal string ResolvePath(string packagePath, NetworkFolderSettings network)
    {
        var trimmed = (packagePath ?? string.Empty).Trim();
        if (trimmed.Length == 0) return trimmed;
        var viaRoot = network.ToAbsolute(network.ToStored(trimmed)) ?? trimmed;
        if (!Path.IsPathFullyQualified(viaRoot)) return viaRoot;
        var drive = Path.GetPathRoot(viaRoot)?.TrimEnd('\\', '/');
        if (drive is { Length: 2 } && fileAccess.DriveMappings.TryGetValue(drive, out var mapped))
            return Path.Combine(mapped, viaRoot[drive.Length..].TrimStart('\\', '/'));
        return viaRoot;
    }

    internal async Task<SimulationPackageImportOutcome> ImportCoreAsync(
        PackageImportRequest request,
        bool checkActiveImport,
        string? expectedActiveImportId,
        bool recordBlocked,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<PackageDiagnostic>();
        SimulationPackageCase? packageCase = null;
        if (!string.IsNullOrWhiteSpace(request.CaseId))
        {
            packageCase = await repository.GetCaseAsync(request.CaseId.Trim(), cancellationToken);
            if (packageCase is null)
                diagnostics.Add(PackageDiagnostic.Error(CaseUnresolved, "The Case does not exist.", ("caseId", request.CaseId)));
        }

        var (validation, resolvedPath) = await ValidateCoreAsync(request.PackagePath, packageCase?.WorkingFolder, cancellationToken);
        if (validation.HasBlockingErrors || diagnostics.Count > 0 || validation.Manifest is null)
            return await BlockAsync(request, validation, diagnostics, [], null, null, packageCase?.CaseId, request.CaseOperationId, recordBlocked, cancellationToken);

        var manifest = validation.Manifest;
        if (packageCase is null)
        {
            var cases = await repository.FindCasesAsync(validation.PackagePath, manifest.Part?.Number, cancellationToken);
            if (cases.Count == 1) packageCase = cases[0];
            else
            {
                diagnostics.Add(PackageDiagnostic.Error(CaseUnresolved, cases.Count == 0
                        ? "No Case matches the package folder or part number; import it from the Case."
                        : "Several Cases match the package folder or part number; import it from the right Case.",
                    ("partNumber", manifest.Part?.Number), ("candidates", string.Join(",", cases.Select(item => item.CaseId)))));
                return await BlockAsync(request, validation, diagnostics, [], null, null, null, null, recordBlocked, cancellationToken);
            }
        }

        var operations = await repository.ListOperationsAsync(packageCase.CaseId, cancellationToken);
        var manifestNumber = int.TryParse(manifest.Operation?.Number?.Trim().TrimStart('O', 'P', 'o', 'p'), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : (int?)null;
        SimulationPackageOperation? operation;
        if (!string.IsNullOrWhiteSpace(request.CaseOperationId))
        {
            operation = operations.FirstOrDefault(item => item.CaseOperationId == request.CaseOperationId.Trim());
            if (operation is null)
            {
                diagnostics.Add(PackageDiagnostic.Error(OperationUnresolved, "The Operation does not belong to the Case.", ("caseOperationId", request.CaseOperationId)));
            }
            else if (manifestNumber != operation.OperationNumber)
            {
                diagnostics.Add(PackageDiagnostic.Warning(OperationMismatch,
                    "The package was exported for another Operation number than the Operation it is imported into.",
                    ("packageOperation", manifest.Operation?.Number), ("operationNumber", operation.OperationNumber.ToString(CultureInfo.InvariantCulture))));
            }
        }
        else
        {
            var matching = operations.Where(item => item.OperationNumber == manifestNumber).ToArray();
            operation = matching.Length == 1 ? matching[0] : null;
            if (operation is null)
            {
                diagnostics.Add(PackageDiagnostic.Error(OperationUnresolved, "No single Operation of the Case has the package Operation number.",
                    ("packageOperation", manifest.Operation?.Number), ("caseId", packageCase.CaseId)));
            }
        }

        if (operation is null)
            return await BlockAsync(request, validation, diagnostics, [], null, null, packageCase.CaseId, null, recordBlocked, cancellationToken);

        var existing = await repository.FindActiveAsync(operation.CaseOperationId, validation.ManifestSha256!, cancellationToken);
        if (existing is not null)
        {
            diagnostics.Add(PackageDiagnostic.Info(AlreadyImported, "This package was already imported for the Operation; nothing changed.", ("importId", existing.ImportId)));
            return new(new PackageImportResult(PackageImportOutcome.AlreadyImported, existing.ImportId, validation, diagnostics, [],
                existing.MachineId, existing.MachineMatchedBy), null, existing);
        }

        // Stock: one candidate is taken; several need the planner's choice (§9: never silently).
        var candidates = validation.StockCandidates;
        string? selectedStock = null;
        if (candidates.Count == 1)
        {
            selectedStock = candidates[0].RelativePath;
        }
        else if (candidates.Count > 1)
        {
            var chosen = request.SelectedStockPath?.Replace('\\', '/').Trim();
            if (string.IsNullOrEmpty(chosen))
            {
                diagnostics.Add(PackageDiagnostic.Error(StockSelectionRequired, "The package has several stock candidates; choose the one to start from.",
                    ("candidates", string.Join(",", candidates.Select(item => item.RelativePath)))));
            }
            else if (candidates.FirstOrDefault(item => string.Equals(item.RelativePath, chosen, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                selectedStock = match.RelativePath;
            }
            else
            {
                diagnostics.Add(PackageDiagnostic.Error(StockSelectionInvalid, "The chosen stock is not one of the package stock candidates.", ("selectedStockPath", chosen)));
            }
        }

        // Machine mapping (§10).
        var mapping = await repository.ResolveMachineAsync(manifest.Machine, cancellationToken);
        string? machineId = mapping.MachineId;
        string? matchedBy = mapping.MatchedBy;
        IReadOnlyList<MachineMappingCandidate> machineCandidates = mapping.Candidates;
        (string, string)? remember = null;
        if (!string.IsNullOrWhiteSpace(request.MachineId))
        {
            var chosen = request.MachineId.Trim();
            var allowed = mapping.Ambiguous
                ? mapping.Candidates
                : mapping.MachineId is null ? await repository.ListMachinesAsync(cancellationToken) : mapping.Candidates;
            if (allowed.All(item => item.MachineId != chosen))
            {
                diagnostics.Add(PackageDiagnostic.Error(MachineSelectionInvalid, "The chosen Machine is not one of the Machine candidates.", ("machineId", chosen)));
            }
            else if (mapping.MachineId != chosen)
            {
                machineId = chosen;
                matchedBy = MachineMatchKinds.PlannerSelected;
                if (request.RememberMachineMapping)
                {
                    var value = manifest.Machine?.ExternalId ?? manifest.Machine?.Postprocessor ?? manifest.Machine?.Name;
                    var field = manifest.Machine?.ExternalId is not null ? MachineMappingFields.ExternalId
                        : manifest.Machine?.Postprocessor is not null ? MachineMappingFields.Postprocessor : MachineMappingFields.MachineName;
                    if (!string.IsNullOrWhiteSpace(value)) remember = (field, value);
                }
            }
        }
        else if (mapping.Ambiguous)
        {
            diagnostics.Add(PackageDiagnostic.Error(MachineMappingAmbiguous, "Several Machines match the package machine; choose one.",
                ("matchedBy", mapping.MatchedBy), ("candidates", string.Join(",", mapping.Candidates.Select(item => item.Number)))));
        }
        else if (mapping.MachineId is null)
        {
            diagnostics.Add(PackageDiagnostic.Error(MachineMappingUnresolved,
                "No Machine matches the package machine (external id, postprocessor, mapping table or name); choose one or add a mapping in Setup.",
                ("externalId", manifest.Machine?.ExternalId), ("postprocessor", manifest.Machine?.Postprocessor), ("machineName", manifest.Machine?.Name)));
            machineCandidates = await repository.ListMachinesAsync(cancellationToken);
        }

        // Work CS (§5): mode A identity at the work offset, mode B the UCS origin/rotation in RzRyRx.
        CaseOperationCamSetupValues? workCs = null;
        var converted = CoordinateService.ToOperationWorkCs(manifest.CoordinateSystem);
        if (converted is { } cs)
        {
            try
            {
                var notes = $"Imported from Cimatron package, UCS {manifest.CoordinateSystem!.Name}";
                workCs = CaseOperationCamSetupService.Validate(new CaseOperationCamSetupInput(
                    CaseOperationCamSetupSources.Cimatron,
                    manifest.CoordinateSystem.WorkOffset,
                    new MachineVector3(cs.OriginMm.X, cs.OriginMm.Y, cs.OriginMm.Z),
                    new MachineVector3(cs.RotationDeg.X, cs.RotationDeg.Y, cs.RotationDeg.Z),
                    notes.Length > CaseOperationCamSetupService.NotesMaximumLength ? notes[..CaseOperationCamSetupService.NotesMaximumLength] : notes));
            }
            catch (CaseOperationCamSetupValidationException exception)
            {
                foreach (var issue in exception.Issues)
                {
                    diagnostics.Add(PackageDiagnostic.Error(WorkOffsetInvalid, "The package Work CS cannot be stored: " + issue.Message,
                        ("field", issue.Field), ("workOffset", manifest.CoordinateSystem?.WorkOffset)));
                }
            }
        }
        else
        {
            diagnostics.Add(PackageDiagnostic.Error(UcsMissing, "The package coordinate system does not resolve; geometry cannot be placed safely."));
        }

        var currentSetup = await repository.ReadCamSetupAsync(operation.CaseOperationId, cancellationToken);
        if (currentSetup is { Source: CaseOperationCamSetupSources.Manual } && !request.OverwriteManualWorkCs)
        {
            diagnostics.Add(PackageDiagnostic.Error(WorkCsManualConflict,
                "The Operation has a manually entered Work CS; confirm that the package Work CS replaces it.",
                ("camSetupVersion", currentSetup.Value.Version.ToString(CultureInfo.InvariantCulture))));
        }

        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) || workCs is null)
        {
            return await BlockAsync(request, validation, diagnostics, machineCandidates, machineId, matchedBy,
                packageCase.CaseId, operation.CaseOperationId, recordBlocked, cancellationToken);
        }

        var importId = Guid.NewGuid().ToString("N");
        var files = validation.Files.Where(file => file.ActualSha256 is not null && file.SizeBytes is not null).Select(file => new SimulationPackageImportFile(
            PackageFileRoles.Name(file.Role), file.RelativePath, file.ActualSha256!, file.SizeBytes!.Value, file.StockKind, file.SourceOperation,
            file.Name, file.Collision, PackageFileRoles.IsStock(file.Role) && file.RelativePath == selectedStock, null)).ToArray();
        var links = BuildLinks(validation, selectedStock);
        var reportJson = SqliteSimulationPackageRepository.Serialize(SimulationPackageReport.From(validation, diagnostics, machineCandidates, machineId, matchedBy));
        var apply = await repository.ApplyAsync(new SimulationPackageApplyCommand(
            importId,
            packageCase.CaseId,
            operation.CaseOperationId,
            manifest,
            validation.ManifestSha256!,
            validation.PackagePath,
            selectedStock,
            machineId,
            matchedBy,
            JsonSerializer.Serialize(manifest.CoordinateSystem, ManifestJson.Options),
            validation.Files.First(file => file.Role == PackageFileRole.Nc).ActualSha256!,
            validation.Files.FirstOrDefault(file => file.Role is PackageFileRole.ToolTable or PackageFileRole.ToolReport)?.ActualSha256,
            validation.ToolTableNumbers.Count > 0 ? validation.ToolTableNumbers : validation.Nc?.ToolNumbers ?? [],
            files,
            links,
            workCs,
            request.OverwriteManualWorkCs,
            request.BasedOnCamSetupVersion,
            checkActiveImport,
            expectedActiveImportId,
            remember,
            reportJson,
            request.Actor,
            timeProvider.GetUtcNow()), cancellationToken);

        switch (apply.Status)
        {
            case SimulationPackageApplyStatus.Imported:
            {
                var stored = await repository.GetAsync(importId, cancellationToken);
                return new(new PackageImportResult(PackageImportOutcome.Imported, importId, validation, diagnostics, machineCandidates, machineId, matchedBy), null, stored);
            }
            case SimulationPackageApplyStatus.AlreadyImported:
            {
                var stored = await repository.GetAsync(apply.ImportId!, cancellationToken);
                diagnostics.Add(PackageDiagnostic.Info(AlreadyImported, "This package was already imported for the Operation; nothing changed.", ("importId", apply.ImportId)));
                return new(new PackageImportResult(PackageImportOutcome.AlreadyImported, apply.ImportId, validation, diagnostics, [], stored?.MachineId, stored?.MachineMatchedBy), null, stored);
            }
            case SimulationPackageApplyStatus.ManualWorkCs:
                diagnostics.Add(PackageDiagnostic.Error(WorkCsManualConflict,
                    "The Operation has a manually entered Work CS; confirm that the package Work CS replaces it."));
                return await BlockAsync(request, validation, diagnostics, machineCandidates, machineId, matchedBy,
                    packageCase.CaseId, operation.CaseOperationId, recordBlocked, cancellationToken);
            case SimulationPackageApplyStatus.OperationNotFound:
                diagnostics.Add(PackageDiagnostic.Error(OperationUnresolved, "The Operation no longer belongs to the Case."));
                return await BlockAsync(request, validation, diagnostics, machineCandidates, machineId, matchedBy,
                    packageCase.CaseId, null, recordBlocked, cancellationToken);
            default:
                return new(new PackageImportResult(PackageImportOutcome.Blocked, null, validation, diagnostics, machineCandidates, machineId, matchedBy),
                    apply.Conflict, null);
        }
    }

    /// <summary>The Operation model-file links: the selected stock, every fixture, the finished part.</summary>
    private static List<SimulationPackageLink> BuildLinks(PackageValidationReport validation, string? selectedStock)
    {
        var links = new List<SimulationPackageLink>();
        foreach (var file in validation.Files)
        {
            if (file.FullPath is null || !file.Readable) continue;
            var format = CaseModelFileFormats.FromPath(file.FullPath);
            if (format is null) continue;
            var label = string.IsNullOrWhiteSpace(file.Name) ? Path.GetFileName(file.FullPath) : file.Name.Trim();
            if (PackageFileRoles.IsStock(file.Role))
            {
                if (file.RelativePath != selectedStock) continue;
                var kind = file.Role == PackageFileRole.RemainingStock
                    ? $"Remaining stock{(string.IsNullOrWhiteSpace(file.SourceOperation) ? "" : " from OP" + file.SourceOperation)}"
                    : "Initial stock";
                links.Add(new(file.RelativePath, CaseModelFileKinds.Stock, format, file.FullPath, Trim($"{kind}: {label}")));
            }
            else if (file.Role == PackageFileRole.Fixture)
            {
                links.Add(new(file.RelativePath, CaseModelFileKinds.Fixture, format, file.FullPath, Trim(label)));
            }
            else if (file.Role == PackageFileRole.Part)
            {
                links.Add(new(file.RelativePath, CaseModelFileKinds.Part, format, file.FullPath, Trim($"Finished part: {label}")));
            }
        }

        return links;

        static string Trim(string value) => value.Length > 200 ? value[..200] : value;
    }

    private async Task<SimulationPackageImportOutcome> BlockAsync(
        PackageImportRequest request,
        PackageValidationReport validation,
        List<PackageDiagnostic> diagnostics,
        IReadOnlyList<MachineMappingCandidate> machineCandidates,
        string? machineId,
        string? matchedBy,
        string? caseId,
        string? caseOperationId,
        bool recordBlocked,
        CancellationToken cancellationToken)
    {
        var blocking = validation.Errors.Concat(diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error)).ToArray();
        var now = timeProvider.GetUtcNow();
        string? blockedId = null;
        if (recordBlocked && validation.ManifestSha256 is not null)
        {
            var report = SqliteSimulationPackageRepository.Serialize(SimulationPackageReport.From(validation, diagnostics, machineCandidates, machineId, matchedBy));
            blockedId = await repository.RecordBlockedAsync(caseId, caseOperationId, validation.Manifest, validation.ManifestSha256,
                validation.PackagePath, report, blocking, request.Actor, now, cancellationToken);
        }
        else
        {
            await repository.LogBlockedAsync(caseId, caseOperationId, validation.PackagePath, validation.ManifestSha256, blocking, request.Actor, now, cancellationToken);
        }

        return new(new PackageImportResult(PackageImportOutcome.Blocked, blockedId, validation, diagnostics, machineCandidates, machineId, matchedBy), null, null);
    }
}

/// <summary>
/// Reads the T-numbers of a Cimatron <c>*.TOOLS.mht</c> report with the released-tool-table parser
/// (the same parser an NC release uses), so the package tool check and the release agree.
/// </summary>
internal sealed class ReleasedToolTableNumberReader : IToolTableReader
{
    public async Task<ToolTableFacts?> ReadAsync(string absolutePath, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(absolutePath).ToLowerInvariant();
        if (extension is not (".mht" or ".mhtml" or ".txt")) return null;
        try
        {
            var definition = await ReleasedToolTableParser.ParseAsync(absolutePath, Path.GetFileName(absolutePath), cancellationToken);
            var numbers = definition.Tools
                .Select(tool => ToolTableReaders.ToolNumber(tool.ToolIdentifier))
                .Where(number => number is not null)
                .Select(number => number!.Value)
                .Distinct()
                .Order()
                .ToArray();
            return new ToolTableFacts(numbers, []);
        }
        catch (GCodeValidationException exception)
        {
            throw new InvalidDataException(exception.Message, exception);
        }
    }
}

/// <summary>The stored and returned import report (stable codes, machine-readable context).</summary>
internal sealed record SimulationPackageReport(
    string PackagePath,
    bool CompleteMarkerPresent,
    string? ManifestSha256,
    int? SchemaVersion,
    string? PackageId,
    string? ExporterVersion,
    string? CimatronVersion,
    string? OperationNumber,
    string? OperationName,
    int? ProcedureId,
    string? Units,
    CoordinateSystemDefinition? CoordinateSystem,
    MachineInfo? Machine,
    IReadOnlyList<SimulationPackageReportFile> Files,
    SimulationPackageReportNc? Nc,
    IReadOnlyList<int> ToolTableNumbers,
    IReadOnlyList<string> StockCandidates,
    IReadOnlyList<MachineMappingCandidate> MachineCandidates,
    string? MachineId,
    string? MachineMatchedBy,
    bool HasBlockingErrors,
    IReadOnlyList<PackageDiagnostic> Diagnostics)
{
    internal static SimulationPackageReport From(
        PackageValidationReport validation,
        IReadOnlyList<PackageDiagnostic> importDiagnostics,
        IReadOnlyList<MachineMappingCandidate> machineCandidates,
        string? machineId,
        string? matchedBy)
    {
        var all = validation.Diagnostics.Concat(importDiagnostics).ToArray();
        var manifest = validation.Manifest;
        return new(
            validation.PackagePath,
            validation.CompleteMarkerPresent,
            validation.ManifestSha256,
            manifest?.SchemaVersion,
            manifest?.PackageId,
            manifest?.Exporter?.Version,
            manifest?.Exporter?.CimatronVersion,
            manifest?.Operation?.Number,
            manifest?.Operation?.Name,
            manifest?.Operation?.ProcedureId,
            manifest?.Units,
            manifest?.CoordinateSystem,
            manifest?.Machine,
            validation.Files.Select(file => new SimulationPackageReportFile(
                PackageFileRoles.Name(file.Role), file.RelativePath, file.ActualSha256 ?? file.DeclaredSha256, file.SizeBytes,
                file.Exists, file.Readable, file.StockKind, file.SourceOperation, file.Name, file.Collision, file.TriangleCount,
                file.Bounds is { } box ? [box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z] : null)).ToArray(),
            validation.Nc is { } nc
                ? new SimulationPackageReportNc(nc.ProgramNumber, nc.ToolNumbers, nc.Units, nc.UsesRotaryAxes, nc.UsesTiltedPlane,
                    nc.CuttingBounds is { } cut ? [cut.Min.X, cut.Min.Y, cut.Min.Z, cut.Max.X, cut.Max.Y, cut.Max.Z] : null)
                : null,
            validation.ToolTableNumbers,
            validation.StockCandidates.Select(file => file.RelativePath).ToArray(),
            machineCandidates,
            machineId,
            matchedBy,
            all.Any(item => item.Severity == DiagnosticSeverity.Error),
            all);
    }
}

internal sealed record SimulationPackageReportFile(
    string Role,
    string Path,
    string Sha256,
    long? SizeBytes,
    bool Exists,
    bool Readable,
    string? StockKind,
    string? SourceOperation,
    string? Name,
    bool? Collision,
    int? TriangleCount,
    double[]? BoundsMm);

internal sealed record SimulationPackageReportNc(
    string? ProgramNumber,
    IReadOnlyList<int> ToolNumbers,
    string? Units,
    bool UsesRotaryAxes,
    bool UsesTiltedPlane,
    double[]? CuttingBoundsMm);
