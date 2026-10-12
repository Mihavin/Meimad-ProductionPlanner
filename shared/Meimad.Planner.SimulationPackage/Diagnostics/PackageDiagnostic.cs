using System.Text.Json.Serialization;

namespace Meimad.Planner.SimulationPackage.Diagnostics;

[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticSeverity>))]
public enum DiagnosticSeverity
{
    /// <summary>Informational; never changes the outcome.</summary>
    [JsonStringEnumMemberName("info")] Info,

    /// <summary>The package can be imported; the operator should know.</summary>
    [JsonStringEnumMemberName("warning")] Warning,

    /// <summary>Blocking: the package is not imported.</summary>
    [JsonStringEnumMemberName("error")] Error
}

/// <summary>
/// One structured diagnostic. <see cref="Code"/> is stable (see <see cref="PackageDiagnosticCodes"/>) and
/// <see cref="Context"/> carries the machine-readable facts (path, role, values); <see cref="Message"/> is
/// an English explanation for logs and is translated by the client like any other text.
/// </summary>
public sealed record PackageDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    IReadOnlyDictionary<string, string?> Context)
{
    public static PackageDiagnostic Error(string code, string message, params (string Key, string? Value)[] context) =>
        new(code, DiagnosticSeverity.Error, message, ToContext(context));

    public static PackageDiagnostic Warning(string code, string message, params (string Key, string? Value)[] context) =>
        new(code, DiagnosticSeverity.Warning, message, ToContext(context));

    public static PackageDiagnostic Info(string code, string message, params (string Key, string? Value)[] context) =>
        new(code, DiagnosticSeverity.Info, message, ToContext(context));

    private static IReadOnlyDictionary<string, string?> ToContext((string Key, string? Value)[] context)
    {
        var values = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in context) values[key] = value;
        return values;
    }
}

/// <summary>
/// Stable diagnostic codes of package validation and import. Blocking codes follow the exporter
/// definition §14 "blocking errors"; warning codes follow §14 "warnings". Never rename a code: the
/// client, the import reports stored by the Server and the exporter log all key on it.
/// </summary>
public static class PackageDiagnosticCodes
{
    // Package folder and file policy (§16).
    public const string PackagePathInvalid = "package_path_invalid";
    public const string PackageRootNotApproved = "package_root_not_approved";
    public const string PackageNotFound = "package_not_found";
    public const string PackageIncomplete = "package_incomplete";
    public const string FilePathNotRelative = "file_path_not_relative";
    public const string FilePathTraversal = "file_path_traversal";
    public const string FileMissing = "file_missing";
    public const string FileEmpty = "file_empty";
    public const string FileTooLarge = "file_too_large";
    public const string PackageTooLarge = "package_too_large";
    public const string FileHashMissing = "file_hash_missing";
    public const string FileHashMismatch = "file_hash_mismatch";
    public const string FileDuplicatePath = "file_duplicate_path";

    // Manifest.
    public const string ManifestMissing = "manifest_missing";
    public const string ManifestInvalidJson = "manifest_invalid_json";
    public const string ManifestSchemaViolation = "manifest_schema_violation";
    public const string ManifestContainsMeasuredOffset = "manifest_contains_measured_offset";
    public const string SchemaVersionMissing = "schema_version_missing";
    public const string SchemaVersionUnsupported = "schema_version_unsupported";
    public const string UnitsUnknown = "units_unknown";
    public const string UnitsConvertedFromInch = "units_converted_from_inch";
    public const string ProcedureIdMissing = "procedure_id_missing";
    public const string OperationNumberMissing = "operation_number_missing";

    // Coordinates (§5).
    public const string UcsMissing = "ucs_missing";
    public const string UcsTypeUnknown = "ucs_type_unknown";
    public const string UcsTransformIncomplete = "ucs_transform_incomplete";
    public const string UcsMatrixInvalid = "ucs_matrix_invalid";
    public const string UcsMatrixInconsistent = "ucs_matrix_inconsistent";
    public const string CoordinateModeConflict = "coordinate_mode_conflict";
    public const string WorkOffsetMismatch = "work_offset_mismatch";
    public const string WorkOffsetInvalid = "work_offset_invalid";
    public const string ToolpathStockMisaligned = "toolpath_stock_misaligned";
    public const string ToolpathAlignmentNotChecked = "toolpath_alignment_not_checked";

    // NC and tools.
    public const string NcMissing = "nc_missing";
    public const string NcHeaderMissing = "nc_header_missing";
    public const string NcUnitsMismatch = "nc_units_mismatch";
    public const string ToolTableMissing = "tool_table_missing";
    public const string ToolTableUnreadable = "tool_table_unreadable";
    public const string ToolTableMismatch = "tool_table_mismatch";
    public const string ToolTableUnusedTools = "tool_table_unused_tools";
    public const string HolderDimensionsIncomplete = "holder_dimensions_incomplete";

    // Geometry (§9, §4).
    public const string StockMissing = "stock_missing";
    public const string StockKindUnknown = "stock_kind_unknown";
    public const string StockSourceOperationMissing = "stock_source_operation_missing";
    public const string StockFallbackRule = "stock_fallback_rule";
    public const string StockMultipleCandidates = "stock_multiple_candidates";
    public const string StockSelectionRequired = "stock_selection_required";
    public const string StockSelectionInvalid = "stock_selection_invalid";
    public const string GeometryUnreadable = "geometry_unreadable";
    public const string GeometryFormatUnsupported = "geometry_format_unsupported";
    public const string PartMissing = "part_missing";
    public const string PartUnreadable = "part_unreadable";
    public const string FixtureMissing = "fixture_missing";
    public const string FixtureCountExceeded = "fixture_count_exceeded";

    // Exporter facts (§14 warnings).
    public const string CimatronVersionUntested = "cimatron_version_untested";
    public const string DefaultMeshTolerance = "default_mesh_tolerance";
    public const string MachineMissing = "machine_missing";

    // Import (Server).
    public const string CaseUnresolved = "case_unresolved";
    public const string OperationUnresolved = "operation_unresolved";
    public const string OperationMismatch = "operation_mismatch";
    public const string MachineMappingUnresolved = "machine_mapping_unresolved";
    public const string MachineMappingAmbiguous = "machine_mapping_ambiguous";
    public const string MachineSelectionInvalid = "machine_selection_invalid";
    public const string WorkCsManualConflict = "work_cs_manual_conflict";
    public const string AlreadyImported = "already_imported";
}
