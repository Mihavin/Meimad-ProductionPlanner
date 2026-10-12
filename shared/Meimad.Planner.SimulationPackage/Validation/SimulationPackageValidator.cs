using System.Globalization;
using System.Text.Json;
using Meimad.Planner.SimulationPackage.Coordinates;
using Meimad.Planner.SimulationPackage.Diagnostics;
using Meimad.Planner.SimulationPackage.Geometry;
using Meimad.Planner.SimulationPackage.Manifest;
using Meimad.Planner.SimulationPackage.Nc;
using Meimad.Planner.SimulationPackage.Schema;
using Meimad.Planner.SimulationPackage.Tools;
using static Meimad.Planner.SimulationPackage.Diagnostics.PackageDiagnosticCodes;

namespace Meimad.Planner.SimulationPackage.Validation;

/// <summary>
/// Validates a package folder without changing it (exporter definition §13 steps 1–5, §14, §16): the
/// folder must lie under an approved root and carry <c>COMPLETE</c>; the manifest must be JSON of a
/// supported <c>schemaVersion</c> that satisfies the schema; every path must be relative and stay inside
/// the package; every file must exist within the size limits and match its SHA-256; geometry must be
/// readable; the NC must have a header; the tool table must cover every T-number the NC calls; the
/// units and the UCS must be known. Blocking errors and warnings follow §14 exactly.
/// </summary>
public sealed class SimulationPackageValidator
{
    private static readonly HashSet<string> SemanticallyCheckedPointers = new(StringComparer.Ordinal)
    {
        "/coordinateSystem", "/units", "/operation", "/operation/number", "/files"
    };

    private readonly PackageValidationOptions options;
    private readonly JsonSchemaSubsetValidator schemas;

    public SimulationPackageValidator(PackageValidationOptions options, JsonSchemaSubsetValidator? schemas = null)
    {
        this.options = options;
        this.schemas = schemas ?? JsonSchemaSubsetValidator.Default;
    }

    public async Task<PackageValidationReport> ValidateAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<PackageDiagnostic>();
        var full = CheckPackagePath(packagePath, diagnostics);
        if (full is null) return Empty(packagePath, false, diagnostics);
        if (!Directory.Exists(full))
        {
            diagnostics.Add(PackageDiagnostic.Error(PackageNotFound, "The package folder does not exist or cannot be read.", ("packagePath", full)));
            return Empty(full, false, diagnostics);
        }

        var complete = File.Exists(Path.Combine(full, ManifestJson.CompleteMarkerFileName));
        if (!complete && options.RequireCompleteMarker)
        {
            diagnostics.Add(PackageDiagnostic.Error(PackageIncomplete,
                "The package has no COMPLETE marker: the export did not finish (or is still running), so it is not imported.",
                ("packagePath", full)));
            return Empty(full, false, diagnostics);
        }

        var manifestPath = Path.Combine(full, ManifestJson.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            diagnostics.Add(PackageDiagnostic.Error(ManifestMissing, "The package has no manifest.json.", ("packagePath", full)));
            return Empty(full, complete, diagnostics);
        }

        var manifestLength = new FileInfo(manifestPath).Length;
        if (manifestLength > options.MaxManifestBytes)
        {
            diagnostics.Add(PackageDiagnostic.Error(FileTooLarge, "manifest.json is larger than allowed.",
                ("path", ManifestJson.ManifestFileName), ("sizeBytes", Invariant(manifestLength)), ("limitBytes", Invariant(options.MaxManifestBytes))));
            return Empty(full, complete, diagnostics);
        }

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var manifestSha = PackageHashing.Sha256OfBytes(manifestBytes);
        SimulationPackageManifest? manifest;
        List<JsonSchemaViolation> violations;
        try
        {
            using var document = JsonDocument.Parse(manifestBytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("The manifest must be a JSON object.");
            if (!root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var schemaVersion))
            {
                diagnostics.Add(PackageDiagnostic.Error(SchemaVersionMissing, "manifest.json has no integer schemaVersion."));
                return new(full, complete, manifestSha, null, [], null, [], diagnostics);
            }

            if (schemaVersion != ManifestJson.CurrentSchemaVersion)
            {
                diagnostics.Add(PackageDiagnostic.Error(SchemaVersionUnsupported,
                    schemaVersion < ManifestJson.CurrentSchemaVersion
                        ? "The package was written for an older manifest schema this Meimad no longer reads; export it again."
                        : "The package was written for a newer manifest schema; update Meimad before importing it.",
                    ("schemaVersion", Invariant(schemaVersion)), ("supported", Invariant(ManifestJson.CurrentSchemaVersion))));
                return new(full, complete, manifestSha, null, [], null, [], diagnostics);
            }

            foreach (var pointer in MeasuredOffsetProperties(root, ""))
            {
                diagnostics.Add(PackageDiagnostic.Error(ManifestContainsMeasuredOffset,
                    "The manifest carries a measured machine offset; measured G54/G55 values come from the machine, never from the CAM export.",
                    ("pointer", pointer)));
            }

            violations = schemas.Validate(JsonSchemaSubsetValidator.SimulationPackageSchema, root).ToList();
            manifest = root.Deserialize<SimulationPackageManifest>(ManifestJson.Options);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(PackageDiagnostic.Error(ManifestInvalidJson, "manifest.json is not valid JSON of the package contract.",
                ("detail", exception.Message), ("line", exception.LineNumber?.ToString(CultureInfo.InvariantCulture))));
            return new(full, complete, manifestSha, null, [], null, [], diagnostics);
        }

        if (manifest is null)
        {
            diagnostics.Add(PackageDiagnostic.Error(ManifestInvalidJson, "manifest.json is empty."));
            return new(full, complete, manifestSha, null, [], null, [], diagnostics);
        }

        var coordinateDiagnostics = new List<PackageDiagnostic>();
        var ucsInModel = CoordinateService.ResolveUcsInModel(manifest.CoordinateSystem, coordinateDiagnostics);
        foreach (var violation in violations)
        {
            if (SemanticallyCheckedPointers.Contains(violation.InstancePointer)) continue;
            if (violation.InstancePointer.StartsWith("/coordinateSystem", StringComparison.Ordinal) && coordinateDiagnostics.Count > 0) continue;
            if (violation.InstancePointer.StartsWith("/files/stock/", StringComparison.Ordinal) && violation.InstancePointer.EndsWith("/kind", StringComparison.Ordinal)) continue;
            diagnostics.Add(PackageDiagnostic.Error(ManifestSchemaViolation, violation.Message,
                ("pointer", violation.InstancePointer), ("keyword", violation.Keyword)));
        }

        diagnostics.AddRange(coordinateDiagnostics);
        CheckManifestFacts(manifest, diagnostics);
        var files = CheckFiles(full, manifest, diagnostics);
        var nc = CheckNc(files, manifest, diagnostics);
        var toolNumbers = await CheckToolTableAsync(manifest, files, nc, diagnostics, cancellationToken);
        CheckGeometry(manifest, files, diagnostics);
        CheckAlignment(manifest, ucsInModel, files, nc, diagnostics);
        return new(full, complete, manifestSha, manifest, files, nc, toolNumbers, diagnostics);
    }

    private string? CheckPackagePath(string packagePath, List<PackageDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !Path.IsPathFullyQualified(packagePath.Trim()))
        {
            diagnostics.Add(PackageDiagnostic.Error(PackagePathInvalid, "The package path must be a full folder path.", ("packagePath", packagePath)));
            return null;
        }

        var raw = packagePath.Trim();
        if (raw.Split('\\', '/').Any(segment => segment == ".."))
        {
            diagnostics.Add(PackageDiagnostic.Error(FilePathTraversal, "The package path must not contain '..'.", ("packagePath", raw)));
            return null;
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
        if (!options.ApprovedRoots.Any(root => PackagePaths.IsUnder(full, root)))
        {
            diagnostics.Add(PackageDiagnostic.Error(PackageRootNotApproved,
                "The package folder is not under an approved root (the network folder, the Case working folder or a configured package root).",
                ("packagePath", full)));
            return null;
        }

        return full;
    }

    private void CheckManifestFacts(SimulationPackageManifest manifest, List<PackageDiagnostic> diagnostics)
    {
        var scale = PackageUnits.ToMillimetres(manifest.Units);
        if (scale is null)
        {
            diagnostics.Add(PackageDiagnostic.Error(UnitsUnknown, "The package units are unknown; only mm and inch are accepted.", ("units", manifest.Units)));
        }
        else if (scale.Value != 1.0)
        {
            diagnostics.Add(PackageDiagnostic.Warning(UnitsConvertedFromInch, "The package files are in inches; Meimad converts them to millimetres.", ("units", manifest.Units)));
        }

        if (string.IsNullOrWhiteSpace(manifest.Operation?.Number))
            diagnostics.Add(PackageDiagnostic.Error(OperationNumberMissing, "The manifest does not give the Operation number."));
        if (manifest.Operation?.ProcedureId is null && (manifest.Operation?.ProcedureIds is null || manifest.Operation.ProcedureIds.Count == 0))
            diagnostics.Add(PackageDiagnostic.Error(ProcedureIdMissing, "The manifest does not identify the Cimatron NC procedure.", ("operation", manifest.Operation?.Number)));

        var cimatron = manifest.Exporter?.CimatronVersion;
        if (options.TestedCimatronVersions.Count > 0
            && (string.IsNullOrWhiteSpace(cimatron)
                || !options.TestedCimatronVersions.Any(tested => cimatron.StartsWith(tested, StringComparison.OrdinalIgnoreCase))))
        {
            diagnostics.Add(PackageDiagnostic.Warning(CimatronVersionUntested, "The Cimatron version differs from the versions the exporter was tested with.",
                ("cimatronVersion", cimatron), ("tested", string.Join(",", options.TestedCimatronVersions))));
        }

        if (manifest.Exporter?.UsedDefaultMeshTolerance == true)
        {
            diagnostics.Add(PackageDiagnostic.Warning(DefaultMeshTolerance, "The exporter used the default mesh tolerance.",
                ("meshToleranceMm", manifest.Exporter.MeshToleranceMm?.ToString(CultureInfo.InvariantCulture))));
        }

        if (manifest.Machine is null || (string.IsNullOrWhiteSpace(manifest.Machine.ExternalId)
                && string.IsNullOrWhiteSpace(manifest.Machine.Postprocessor) && string.IsNullOrWhiteSpace(manifest.Machine.Name)))
        {
            diagnostics.Add(PackageDiagnostic.Warning(MachineMissing, "The manifest does not name the target machine or postprocessor."));
        }
    }

    private List<ValidatedPackageFile> CheckFiles(string packageFolder, SimulationPackageManifest manifest, List<PackageDiagnostic> diagnostics)
    {
        var result = new List<ValidatedPackageFile>();
        var entries = manifest.EnumerateFiles().ToList();
        if (entries.Count > options.MaxFileCount)
        {
            diagnostics.Add(PackageDiagnostic.Error(PackageTooLarge, "The manifest lists more files than allowed.",
                ("fileCount", Invariant(entries.Count)), ("limit", Invariant(options.MaxFileCount))));
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in entries)
        {
            var role = PackageFileRoles.Name(entry.Role);
            var fullPath = PackagePaths.ResolveRelative(packageFolder, entry.Path, out var problem);
            if (fullPath is null)
            {
                diagnostics.Add(PackageDiagnostic.Error(problem!, problem == FilePathTraversal
                        ? "A manifest path leaves the package folder."
                        : "A manifest path is not relative to the package folder.",
                    ("path", entry.Path), ("role", role)));
                result.Add(Unchecked(entry, null, exists: false));
                continue;
            }

            if (!seen.Add(fullPath))
            {
                diagnostics.Add(PackageDiagnostic.Error(FileDuplicatePath, "The manifest lists the same file twice.", ("path", entry.Path), ("role", role)));
            }

            if (!File.Exists(fullPath))
            {
                diagnostics.Add(PackageDiagnostic.Error(entry.Role == PackageFileRole.Nc ? NcMissing : FileMissing,
                    "A file the manifest lists is missing from the package.", ("path", entry.Path), ("role", role)));
                result.Add(Unchecked(entry, fullPath, exists: false));
                continue;
            }

            var length = new FileInfo(fullPath).Length;
            total += length;
            if (length == 0)
            {
                diagnostics.Add(PackageDiagnostic.Error(FileEmpty, "A package file is empty.", ("path", entry.Path), ("role", role)));
                result.Add(Unchecked(entry, fullPath, exists: true) with { SizeBytes = 0 });
                continue;
            }

            if (length > options.MaxFileBytes)
            {
                diagnostics.Add(PackageDiagnostic.Error(FileTooLarge, "A package file is larger than allowed.",
                    ("path", entry.Path), ("role", role), ("sizeBytes", Invariant(length)), ("limitBytes", Invariant(options.MaxFileBytes))));
                result.Add(Unchecked(entry, fullPath, exists: true) with { SizeBytes = length });
                continue;
            }

            if (entry.SizeBytes is { } declaredSize && declaredSize != length)
            {
                diagnostics.Add(PackageDiagnostic.Error(FileHashMismatch, "A package file does not have the size the manifest records.",
                    ("path", entry.Path), ("role", role), ("declaredSizeBytes", Invariant(declaredSize)), ("sizeBytes", Invariant(length))));
            }

            var actual = PackageHashing.Sha256OfFile(fullPath);
            if (!PackageHashing.IsSha256(entry.Sha256))
            {
                diagnostics.Add(PackageDiagnostic.Error(FileHashMissing, "The manifest has no valid SHA-256 for a file.", ("path", entry.Path), ("role", role)));
            }
            else if (!string.Equals(actual, entry.Sha256, StringComparison.Ordinal))
            {
                diagnostics.Add(PackageDiagnostic.Error(FileHashMismatch, "A package file does not match the SHA-256 in the manifest; it changed after the export.",
                    ("path", entry.Path), ("role", role), ("declaredSha256", entry.Sha256), ("actualSha256", actual)));
            }

            result.Add(new ValidatedPackageFile(entry.Role, entry.Path, fullPath, entry.Sha256, actual, length, true, true,
                entry.StockKind, entry.SourceOperation, entry.Name, entry.Collision, entry.FoundBy));
        }

        if (total > options.MaxPackageBytes)
        {
            diagnostics.Add(PackageDiagnostic.Error(PackageTooLarge, "The package files together are larger than allowed.",
                ("sizeBytes", Invariant(total)), ("limitBytes", Invariant(options.MaxPackageBytes))));
        }

        return result;
    }

    private static NcProgramFacts? CheckNc(List<ValidatedPackageFile> files, SimulationPackageManifest manifest, List<PackageDiagnostic> diagnostics)
    {
        var nc = files.FirstOrDefault(file => file.Role == PackageFileRole.Nc);
        if (nc is null)
        {
            if (manifest.Files?.Nc is null)
                diagnostics.Add(PackageDiagnostic.Error(NcMissing, "The package has no NC program."));
            return null;
        }

        if (!nc.Exists || nc.FullPath is null || nc.SizeBytes is null or 0 || nc.ActualSha256 is null) return null;
        var facts = NcProgramScanner.ScanFile(nc.FullPath);
        if (!facts.HasHeader)
        {
            diagnostics.Add(PackageDiagnostic.Error(NcHeaderMissing,
                "The NC file has no program header (%, an O-number or a leading comment) or no NC words.", ("path", nc.RelativePath)));
        }

        var expected = PackageUnits.ToMillimetres(manifest.Units) switch { 1.0 => "mm", 25.4 => "inch", _ => null };
        if (expected is not null && facts.Units is not null && facts.Units != expected)
        {
            diagnostics.Add(PackageDiagnostic.Warning(NcUnitsMismatch, "The NC program units (G20/G21) differ from the manifest units.",
                ("manifestUnits", manifest.Units), ("ncUnits", facts.Units)));
        }

        return facts;
    }

    private async Task<IReadOnlyList<int>> CheckToolTableAsync(
        SimulationPackageManifest manifest,
        List<ValidatedPackageFile> files,
        NcProgramFacts? nc,
        List<PackageDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var table = files.FirstOrDefault(file => file.Role == PackageFileRole.ToolTable)
            ?? files.FirstOrDefault(file => file.Role == PackageFileRole.ToolReport);
        if (table is null)
        {
            if (manifest.Files?.ToolTable is null && manifest.Files?.ToolReport is null)
                diagnostics.Add(PackageDiagnostic.Error(ToolTableMissing, "The package has no tool table, so its tools cannot be checked against the NC program."));
            return [];
        }

        if (!table.Exists || table.FullPath is null || table.ActualSha256 is null) return [];
        ToolTableFacts? facts;
        try
        {
            facts = await ToolTableReaders.ReadBuiltInAsync(table.FullPath, cancellationToken);
            if (facts is null && options.ToolTableReader is not null)
                facts = await options.ToolTableReader.ReadAsync(table.FullPath, cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException or FormatException
                                              or InvalidOperationException or ArgumentException)
        {
            diagnostics.Add(PackageDiagnostic.Error(ToolTableUnreadable, "The tool table cannot be read.", ("path", table.RelativePath), ("detail", exception.Message)));
            return [];
        }

        if (facts is null)
        {
            diagnostics.Add(PackageDiagnostic.Error(ToolTableUnreadable, "The tool table format is not supported.", ("path", table.RelativePath)));
            return [];
        }

        if (facts.ToolsWithIncompleteHolder.Count > 0)
        {
            diagnostics.Add(PackageDiagnostic.Warning(HolderDimensionsIncomplete, "Some tools have no complete holder dimensions; holder collision checks are limited.",
                ("tools", string.Join(",", facts.ToolsWithIncompleteHolder.Select(Invariant)))));
        }
        else if (files.FirstOrDefault(file => file.Role == PackageFileRole.Holders) is { Exists: true, FullPath: not null, ActualSha256: not null } holders
                 && await ToolTableReaders.CountIncompleteHolderRowsAsync(holders.FullPath, cancellationToken) is var incomplete and > 0)
        {
            diagnostics.Add(PackageDiagnostic.Warning(HolderDimensionsIncomplete, "Some holders in the holders export have no complete dimensions.",
                ("path", holders.RelativePath), ("rows", Invariant(incomplete))));
        }

        if (nc is not null)
        {
            var missing = nc.ToolNumbers.Except(facts.ToolNumbers).ToArray();
            if (missing.Length > 0)
            {
                diagnostics.Add(PackageDiagnostic.Error(ToolTableMismatch, "The NC program calls tools that the tool table does not list.",
                    ("missingTools", string.Join(",", missing.Select(Invariant))), ("path", table.RelativePath)));
            }

            var unused = facts.ToolNumbers.Except(nc.ToolNumbers).ToArray();
            if (unused.Length > 0)
            {
                diagnostics.Add(PackageDiagnostic.Info(ToolTableUnusedTools, "The tool table lists tools the NC program does not call.",
                    ("unusedTools", string.Join(",", unused.Select(Invariant)))));
            }
        }

        return facts.ToolNumbers;
    }

    private void CheckGeometry(SimulationPackageManifest manifest, List<ValidatedPackageFile> files, List<PackageDiagnostic> diagnostics)
    {
        var stock = manifest.Files?.Stock ?? [];
        if (stock.Count == 0)
            diagnostics.Add(PackageDiagnostic.Error(StockMissing, "The package has no stock; the simulation needs the initial or remaining stock."));
        foreach (var item in stock)
        {
            if (!StockKinds.All.Contains(item.Kind ?? ""))
            {
                diagnostics.Add(PackageDiagnostic.Error(StockKindUnknown, "A stock file has no known kind (initial, remaining or machined).",
                    ("path", item.Path), ("kind", item.Kind)));
            }
            else if (item.Kind == StockKinds.Remaining && string.IsNullOrWhiteSpace(item.SourceOperation))
            {
                diagnostics.Add(PackageDiagnostic.Warning(StockSourceOperationMissing, "A remaining-stock file does not name the Operation it comes from.", ("path", item.Path)));
            }

            if (item.FoundBy == "fallback")
                diagnostics.Add(PackageDiagnostic.Warning(StockFallbackRule, "The stock was found only by a fallback rule; check it is the right file.", ("path", item.Path)));
        }

        var candidates = stock.Count(item => item.Kind is StockKinds.Initial or StockKinds.Remaining);
        if (candidates > 1)
        {
            diagnostics.Add(PackageDiagnostic.Warning(StockMultipleCandidates, "The package has several stock candidates; the planner chooses one at import.",
                ("candidates", string.Join(",", stock.Where(item => item.Kind is StockKinds.Initial or StockKinds.Remaining).Select(item => item.Path)))));
        }

        var fixtures = manifest.Files?.Fixtures ?? [];
        if (fixtures.Count == 0)
            diagnostics.Add(PackageDiagnostic.Warning(FixtureMissing, "The package has no fixture; fixture collisions cannot be checked."));
        if (fixtures.Count > options.MaxFixtureCount)
        {
            diagnostics.Add(PackageDiagnostic.Error(FixtureCountExceeded, "The package has more fixture files than allowed.",
                ("fixtureCount", Invariant(fixtures.Count)), ("limit", Invariant(options.MaxFixtureCount))));
        }

        if (manifest.Files?.Part is null)
            diagnostics.Add(PackageDiagnostic.Warning(PartMissing, "The package has no finished-part model."));

        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            if (!PackageFileRoles.IsGeometry(file.Role) || !file.Exists || file.FullPath is null || file.SizeBytes is null or 0 || file.ActualSha256 is null)
                continue;
            var role = PackageFileRoles.Name(file.Role);
            if (MeshFiles.FormatOf(file.FullPath) is null)
            {
                diagnostics.Add(file.Role == PackageFileRole.Part
                    ? PackageDiagnostic.Warning(PartUnreadable, "The finished-part file is not STL or STEP and is ignored.", ("path", file.RelativePath))
                    : PackageDiagnostic.Error(GeometryFormatUnsupported, "Package geometry must be STL or STEP.", ("path", file.RelativePath), ("role", role)));
                files[index] = file with { Readable = false };
                continue;
            }

            var mesh = MeshFiles.Read(file.FullPath);
            if (!mesh.Readable)
            {
                diagnostics.Add(file.Role == PackageFileRole.Part
                    ? PackageDiagnostic.Warning(PartUnreadable, "The finished-part file cannot be read and is ignored.", ("path", file.RelativePath), ("detail", mesh.Problem))
                    : PackageDiagnostic.Error(GeometryUnreadable, "A stock or fixture file cannot be read.", ("path", file.RelativePath), ("role", role), ("detail", mesh.Problem)));
                files[index] = file with { Readable = false };
                continue;
            }

            files[index] = file with { TriangleCount = mesh.TriangleCount, Bounds = mesh.Bounds };
        }
    }

    /// <summary>
    /// §17 "comparison of NC toolpath bounds and exported STL bounds": for a 3-axis program the cutting
    /// moves must reach the stock box. Rotary or tilted-plane programs are not checked here (the
    /// simulation engine owns their kinematics), which is reported as information.
    /// </summary>
    private void CheckAlignment(SimulationPackageManifest manifest, Transform3D? ucsInModel, List<ValidatedPackageFile> files, NcProgramFacts? nc, List<PackageDiagnostic> diagnostics)
    {
        if (nc?.CuttingBounds is null || ucsInModel is null) return;
        var stock = files.FirstOrDefault(file => PackageFileRoles.IsStock(file.Role) && file.Bounds is not null);
        if (stock?.Bounds is null) return;
        if (nc.UsesRotaryAxes || nc.UsesTiltedPlane || nc.UsesIncrementalMoves)
        {
            diagnostics.Add(PackageDiagnostic.Info(ToolpathAlignmentNotChecked,
                "The toolpath uses rotary axes, a tilted plane or incremental moves; the stock/toolpath alignment is checked by the simulation, not at import."));
            return;
        }

        var scale = PackageUnits.ToMillimetres(manifest.Units) ?? 1.0;
        var fileToUcs = ucsInModel.Inverse();
        var stockBox = new BoundingBox(stock.Bounds.Value.Min * scale, stock.Bounds.Value.Max * scale).Transform(fileToUcs);
        var toolpath = new BoundingBox(nc.CuttingBounds.Value.Min * scale, nc.CuttingBounds.Value.Max * scale);
        if (!stockBox.Inflate(options.AlignmentMarginMm).Intersects(toolpath))
        {
            diagnostics.Add(PackageDiagnostic.Warning(ToolpathStockMisaligned,
                "The cutting moves of the NC program do not reach the stock: the coordinate system or the stock file may be wrong.",
                ("stockMin", stockBox.Min.ToString()), ("stockMax", stockBox.Max.ToString()),
                ("toolpathMin", toolpath.Min.ToString()), ("toolpathMax", toolpath.Max.ToString())));
        }
    }

    private static IEnumerable<string> MeasuredOffsetProperties(JsonElement element, string pointer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var child = pointer + "/" + property.Name;
                if (property.Name.StartsWith("measured", StringComparison.OrdinalIgnoreCase)) yield return child;
                foreach (var nested in MeasuredOffsetProperties(property.Value, child)) yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in MeasuredOffsetProperties(item, pointer + "/" + index.ToString(CultureInfo.InvariantCulture))) yield return nested;
                index++;
            }
        }
    }

    private static ValidatedPackageFile Unchecked(PackageFileEntry entry, string? fullPath, bool exists) =>
        new(entry.Role, entry.Path, fullPath, entry.Sha256, null, null, exists, false,
            entry.StockKind, entry.SourceOperation, entry.Name, entry.Collision, entry.FoundBy);

    private static PackageValidationReport Empty(string path, bool complete, List<PackageDiagnostic> diagnostics) =>
        new(path, complete, null, null, [], null, [], diagnostics);

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Path rules of the package format.</summary>
public static class PackagePaths
{
    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or lies below it (case-insensitive, separator-aware).</summary>
    public static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        string full, normalizedRoot;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Trim()));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (string.Equals(full, normalizedRoot, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar) ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a manifest path against the package folder. Returns null with
    /// <see cref="PackageDiagnosticCodes.FilePathNotRelative"/> for rooted/drive/UNC paths and
    /// <see cref="PackageDiagnosticCodes.FilePathTraversal"/> for '..' or a path that leaves the folder.
    /// </summary>
    public static string? ResolveRelative(string packageFolder, string relative, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || relative.StartsWith('/') || relative.StartsWith('\\')
            || Path.IsPathRooted(relative))
        {
            problem = PackageDiagnosticCodes.FilePathNotRelative;
            return null;
        }

        var segments = relative.Split('/', '\\');
        if (segments.Any(segment => segment is ".." or "." || segment.Length == 0))
        {
            problem = PackageDiagnosticCodes.FilePathTraversal;
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(packageFolder, Path.Combine(segments)));
        if (!IsUnder(full, packageFolder) || string.Equals(Path.TrimEndingDirectorySeparator(full), Path.TrimEndingDirectorySeparator(packageFolder), StringComparison.OrdinalIgnoreCase))
        {
            problem = PackageDiagnosticCodes.FilePathTraversal;
            return null;
        }

        return full;
    }
}
