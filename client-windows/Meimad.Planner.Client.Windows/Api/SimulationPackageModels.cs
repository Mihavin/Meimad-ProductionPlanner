namespace Meimad.Planner.Client.Windows.Api;

// Cimatron simulation packages (Server schema v117, docs/api-contract.md "Cimatron simulation packages").

internal sealed record SimulationPackageDiagnostic(
    string Code,
    string Severity,
    string Message,
    IReadOnlyDictionary<string, string?>? Context);

internal sealed record SimulationPackageMachineCandidate(string MachineId, string Number, string Name, string MatchedBy);

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
    IReadOnlyList<int>? ToolNumbers,
    string? Units,
    bool UsesRotaryAxes,
    bool UsesTiltedPlane,
    double[]? CuttingBoundsMm);

internal sealed record SimulationPackageCoordinateSystem(
    string? Type,
    string? Name,
    string? WorkOffset,
    string? FilesAreIn,
    PlannerXyz? OriginMm,
    PlannerXyz? RotationDeg,
    string? RotationOrder);

internal sealed record SimulationPackageMachine(string? ExternalId, string? Name, string? Postprocessor, string? AxisConfiguration);

/// <summary>The Server's package report: every diagnostic has a stable code and machine-readable context.</summary>
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
    SimulationPackageCoordinateSystem? CoordinateSystem,
    SimulationPackageMachine? Machine,
    IReadOnlyList<SimulationPackageReportFile>? Files,
    SimulationPackageReportNc? Nc,
    IReadOnlyList<int>? ToolTableNumbers,
    IReadOnlyList<string>? StockCandidates,
    IReadOnlyList<SimulationPackageMachineCandidate>? MachineCandidates,
    string? MachineId,
    string? MachineMatchedBy,
    bool HasBlockingErrors,
    IReadOnlyList<SimulationPackageDiagnostic>? Diagnostics);

internal sealed record SimulationPackageImportFile(
    string Role,
    string Path,
    string Sha256,
    long SizeBytes,
    string? StockKind,
    string? SourceOperation,
    string? Name,
    bool? Collision,
    bool Selected,
    string? CaseModelFileId);

/// <summary>One imported package of an Operation (status imported, superseded or blocked).</summary>
internal sealed record SimulationPackageImport(
    string ImportId,
    string? CaseId,
    string? CaseOperationId,
    string Status,
    string? PackageId,
    string ManifestSha256,
    string PackagePath,
    string? ExporterVersion,
    string? CimatronVersion,
    string? NcFileName,
    string? OperationNumber,
    string? MachineId,
    string? MachineName,
    string? MachineMatchedBy,
    string? Postprocessor,
    string? SelectedStockPath,
    string? NcSha256,
    string? ToolTableSha256,
    string? MatchingGcodeReleaseId,
    IReadOnlyList<int>? ToolNumbers,
    DateTimeOffset ImportedAt,
    string ImportedBy,
    IReadOnlyList<SimulationPackageImportFile>? Files,
    SimulationPackageReport? Report)
{
    /// <summary>The full path of the first file with <paramref name="role"/>, or null.</summary>
    internal string? FilePath(params string[] roles) =>
        Files?.Where(file => roles.Contains(file.Role, StringComparer.Ordinal))
            .Select(file => System.IO.Path.Combine(PackagePath, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar)))
            .FirstOrDefault();
}

internal sealed record SimulationPackageImportRequest(
    string PackagePath,
    int ExpectedCamSetupVersion,
    string? ExpectedCurrentImportId,
    string? SelectedStockPath = null,
    string? MachineId = null,
    bool RememberMachineMapping = false,
    bool OverwriteManualWorkCs = false);

/// <summary>
/// The answer to an import: <c>imported</c>, <c>already_imported</c> or <c>blocked</c> (the Server's
/// 422 with the report; nothing changed). A stale state (409) is thrown as <see cref="PlannerApiException"/>.
/// </summary>
internal sealed record SimulationPackageImportResult(string Outcome, SimulationPackageImport? Import, SimulationPackageReport Report)
{
    internal const string Imported = "imported";
    internal const string AlreadyImported = "already_imported";
    internal const string Blocked = "blocked";

    internal bool IsBlocked => Outcome == Blocked;
}
