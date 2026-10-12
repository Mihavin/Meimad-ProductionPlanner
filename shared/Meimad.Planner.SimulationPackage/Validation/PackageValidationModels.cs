using Meimad.Planner.SimulationPackage.Coordinates;
using Meimad.Planner.SimulationPackage.Diagnostics;
using Meimad.Planner.SimulationPackage.Manifest;
using Meimad.Planner.SimulationPackage.Nc;

namespace Meimad.Planner.SimulationPackage.Validation;

/// <summary>Limits and policy of package validation (exporter definition §16).</summary>
public sealed record PackageValidationOptions
{
    /// <summary>
    /// The folders a package may be read from (the Server passes its network folder root, its aliases,
    /// the Case working folder and configured package roots). A package outside every root is refused.
    /// </summary>
    public IReadOnlyList<string> ApprovedRoots { get; init; } = [];

    /// <summary>False only for the exporter checking its own staging folder before it writes COMPLETE.</summary>
    public bool RequireCompleteMarker { get; init; } = true;

    public long MaxManifestBytes { get; init; } = 1L << 20;
    public long MaxFileBytes { get; init; } = 512L << 20;
    public long MaxPackageBytes { get; init; } = 2L << 30;
    public int MaxFixtureCount { get; init; } = 32;
    public int MaxFileCount { get; init; } = 128;

    /// <summary>Cimatron versions the exporter was verified against; others give a warning.</summary>
    public IReadOnlyList<string> TestedCimatronVersions { get; init; } = [];

    /// <summary>Reader for tool table formats beyond tool-table.json/CSV (the Server plugs its MHT parser in).</summary>
    public Tools.IToolTableReader? ToolTableReader { get; init; }

    /// <summary>Margin (mm) around the stock box the 3-axis cutting moves must touch.</summary>
    public double AlignmentMarginMm { get; init; } = 5.0;
}

/// <summary>One file of a validated package with the facts the validator established.</summary>
public sealed record ValidatedPackageFile(
    PackageFileRole Role,
    string RelativePath,
    string? FullPath,
    string DeclaredSha256,
    string? ActualSha256,
    long? SizeBytes,
    bool Exists,
    bool Readable,
    string? StockKind = null,
    string? SourceOperation = null,
    string? Name = null,
    bool? Collision = null,
    string? FoundBy = null,
    int? TriangleCount = null,
    BoundingBox? Bounds = null);

/// <summary>
/// The result of <see cref="IPackageImporter.ValidateAsync"/>: the parsed manifest, its SHA-256 (the
/// idempotency key of an import), every file with its hash, the NC facts and the diagnostics.
/// </summary>
public sealed record PackageValidationReport(
    string PackagePath,
    bool CompleteMarkerPresent,
    string? ManifestSha256,
    SimulationPackageManifest? Manifest,
    IReadOnlyList<ValidatedPackageFile> Files,
    NcProgramFacts? Nc,
    IReadOnlyList<int> ToolTableNumbers,
    IReadOnlyList<PackageDiagnostic> Diagnostics)
{
    public bool HasBlockingErrors => Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    public IEnumerable<PackageDiagnostic> Errors => Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error);

    public IEnumerable<PackageDiagnostic> Warnings => Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Warning);

    /// <summary>The stock files the planner can start from (initial or remaining).</summary>
    public IReadOnlyList<ValidatedPackageFile> StockCandidates =>
        Files.Where(file => file.Role is PackageFileRole.InitialStock or PackageFileRole.RemainingStock).ToArray();
}

/// <summary>
/// An import request. The Case/Operation may be omitted when the package folder or manifest identifies
/// them; <see cref="SelectedStockPath"/> answers a multiple-stock choice and <see cref="MachineId"/> an
/// ambiguous or unresolved Machine mapping (never chosen silently).
/// </summary>
public sealed record PackageImportRequest(
    string PackagePath,
    string Actor,
    string? CaseId = null,
    string? CaseOperationId = null,
    string? SelectedStockPath = null,
    string? MachineId = null,
    bool RememberMachineMapping = false,
    bool OverwriteManualWorkCs = false,
    int? BasedOnCamSetupVersion = null);

public enum PackageImportOutcome
{
    /// <summary>The package was imported and its links, Work CS and tool table were stored.</summary>
    Imported,

    /// <summary>The same manifest hash was already imported for this Operation; nothing changed.</summary>
    AlreadyImported,

    /// <summary>A blocking diagnostic stopped the import; nothing changed.</summary>
    Blocked
}

/// <summary>A Machine the mapping found (several of them are listed; none is picked).</summary>
public sealed record MachineMappingCandidate(string MachineId, string Number, string Name, string MatchedBy);

public sealed record PackageImportResult(
    PackageImportOutcome Outcome,
    string? ImportId,
    PackageValidationReport Validation,
    IReadOnlyList<PackageDiagnostic> ImportDiagnostics,
    IReadOnlyList<MachineMappingCandidate> MachineCandidates,
    string? MachineId,
    string? MachineMatchedBy);

/// <summary>Stage 0 importer contract (execution plan §5).</summary>
public interface IPackageImporter
{
    /// <summary>Checks a package folder without changing anything.</summary>
    Task<PackageValidationReport> ValidateAsync(string packagePath, CancellationToken cancellationToken = default);

    /// <summary>Validates and imports a package; idempotent per manifest hash and Operation.</summary>
    Task<PackageImportResult> ImportAsync(PackageImportRequest request, CancellationToken cancellationToken = default);
}
