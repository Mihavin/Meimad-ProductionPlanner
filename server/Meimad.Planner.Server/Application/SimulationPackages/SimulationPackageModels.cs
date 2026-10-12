using Meimad.Planner.SimulationPackage.Validation;

namespace Meimad.Planner.Server.Application.SimulationPackages;

internal static class SimulationPackageStatuses
{
    internal const string Imported = "imported";
    internal const string Superseded = "superseded";
    internal const string Blocked = "blocked";
}

internal static class MachineMatchKinds
{
    internal const string ExternalId = "external_id";
    internal const string Postprocessor = "postprocessor";
    internal const string MappingTable = "mapping_table";
    internal const string CompatiblePair = "compatible_pair";
    internal const string PlannerSelected = "planner_selected";
}

internal static class MachineMappingFields
{
    internal const string ExternalId = "external_id";
    internal const string Postprocessor = "postprocessor";
    internal const string MachineName = "machine_name";

    internal static readonly IReadOnlyList<string> All = [ExternalId, Postprocessor, MachineName];
}

/// <summary>A stored file of an imported package.</summary>
internal sealed record SimulationPackageImportFile(
    string Role,
    string RelativePath,
    string Sha256,
    long SizeBytes,
    string? StockKind,
    string? SourceOperation,
    string? Name,
    bool? Collision,
    bool Selected,
    string? CaseModelFileId);

/// <summary>An import record (schema v117) with its files and stored report.</summary>
internal sealed record SimulationPackageImport(
    string ImportId,
    string? CaseId,
    string? CaseOperationId,
    string Status,
    string? PackageId,
    string ManifestSha256,
    int? SchemaVersion,
    string PackagePath,
    string? ExporterName,
    string? ExporterVersion,
    string? CimatronVersion,
    string? SourceApplication,
    string? SourceProject,
    string? NcFileName,
    string? ExportedAt,
    string? OperationNumber,
    string? OperationName,
    int? ProcedureId,
    string? ProcedureName,
    string? MachineExternalId,
    string? MachineName,
    string? Postprocessor,
    string? AxisConfiguration,
    string? MachineId,
    string? MachineMatchedBy,
    string? Units,
    string? CoordinateSystemJson,
    string? SelectedStockPath,
    string? NcSha256,
    string? ToolTableSha256,
    IReadOnlyList<int> ToolNumbers,
    string ReportJson,
    DateTimeOffset ImportedAt,
    string ImportedBy,
    DateTimeOffset? SupersededAt,
    string? SupersededByImportId,
    IReadOnlyList<SimulationPackageImportFile> Files,
    string? MatchingGcodeReleaseId);

internal sealed record SimulationPackageMachineMapping(
    string MappingId,
    string MatchField,
    string MatchValue,
    string MachineId,
    string? MachineNumber,
    string? MachineName,
    DateTimeOffset CreatedAt,
    string CreatedBy);

/// <summary>The Case an import resolves to, with its working folder (absolute).</summary>
internal sealed record SimulationPackageCase(string CaseId, string PartNumber, string Name, string? WorkingFolder);

internal sealed record SimulationPackageOperation(string CaseOperationId, int OperationNumber, string Name);

/// <summary>The outcome of the Machine mapping of §10.</summary>
internal sealed record MachineMappingResolution(
    string? MachineId,
    string? MatchedBy,
    IReadOnlyList<MachineMappingCandidate> Candidates,
    bool Ambiguous);

/// <summary>Who and when changed the state an import was based on (409 explanation).</summary>
internal sealed record SimulationPackageConflict(string Code, string Message, string? ChangedBy, DateTimeOffset? ChangedAt, int? CurrentCamSetupVersion, string? CurrentImportId);

/// <summary>An import attempt's server-side outcome, including a stale-state refusal.</summary>
internal sealed record SimulationPackageImportOutcome(PackageImportResult Result, SimulationPackageConflict? Conflict, SimulationPackageImport? Import);
