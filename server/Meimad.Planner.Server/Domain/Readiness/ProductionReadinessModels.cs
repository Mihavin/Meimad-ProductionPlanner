namespace Meimad.Planner.Server.Domain.Readiness;

internal static class ReadinessStates
{
    internal const string Ready = "READY";
    internal const string Missing = "MISSING";
    internal const string Outdated = "OUTDATED";
    internal const string Incompatible = "INCOMPATIBLE";
    internal const string Blocked = "BLOCKED";
    internal const string NotRequired = "NOT_REQUIRED";
    internal const string Unverified = "UNVERIFIED";
}

internal static class OverallReadinessStates
{
    internal const string ReadyForProduction = "READY_FOR_PRODUCTION";
    internal const string NotReady = "NOT_READY";
}

internal static class ReadinessComponentKeys
{
    internal const string GCode = "gcode";
    internal const string ToolTable = "toolTable";
    internal const string ToolOffsets = "toolOffsets";
    internal const string Material = "material";
    internal const string MachinePostprocessorCompatibility = "machinePostprocessorCompatibility";
    internal const string ToolCapacity = "toolCapacity";
    internal const string ProductionPackage = "productionPackage";
}

internal sealed record ReadinessRelease(
    string GCodeReleaseId,
    string ProcessRevisionId,
    string PostprocessorId,
    string PostprocessorName,
    string OriginalFileName,
    int PostSpecificRevision);

internal sealed record ToolOffsetReadinessFact(
    string MachineId,
    string ProcessRevisionId,
    string? GCodeReleaseId,
    string Status,
    string? Comment,
    DateTimeOffset RecordedAt);

/// <summary>
/// The latest Tool Room tool preparation of the Operation on its assigned Machine (schema v79):
/// which Tool Table release it was saved for and how many required released tools still lack a
/// measured length, diameter or offset number.
/// </summary>
internal sealed record ToolPreparationReadinessFact(
    string ToolTableReleaseId,
    int Version,
    int RequiredToolCount,
    int UnmeasuredRequiredCount,
    DateTimeOffset SavedAt);

/// <summary>
/// The facts one Operation's readiness is evaluated from. <see cref="ReplacedGCodeReleaseId"/> is
/// set while a started operation is back in setup for a newer G-code release (schema v90): it is
/// evaluated like a new one until a new Production Package pins that release.
/// <see cref="ProductionPinned"/> marks a started operation evaluated against the release pinned
/// when it started.
/// </summary>
internal sealed record ProductionReadinessContext(
    string BatchOperationId,
    string? MachineAssignmentId,
    string? MachineId,
    string? ExecutionMode,
    IReadOnlySet<string> SupportedPostprocessorIds,
    int? UsableToolPositions,
    string? ActiveProcessRevisionId,
    string? ActiveToolTableReleaseId,
    int? RequiredToolCount,
    IReadOnlyList<ReadinessRelease> Releases,
    string? SelectedGCodeReleaseId,
    IReadOnlyList<ToolOffsetReadinessFact> ToolOffsetFacts,
    string MaterialStatus,
    string? MaterialComment,
    ToolPreparationReadinessFact? ToolPreparation = null,
    string? ReplacedGCodeReleaseId = null,
    bool ProductionPinned = false,
    string ToolOffsetMode = "MEASURED",
    bool VerificationRequired = false,
    bool VerificationSucceeded = false,
    string? ExecutionEvidenceStamp = null,
    bool AmbiguousExecutionContext = false,
    bool ManualSetupReportingSupported = false,
    bool LoaderExecutionObserved = false,
    int ReleasedToolCount = 0,
    string? ExecutionContextVersion = null);

internal sealed record ReadinessComponent(
    string Key,
    string Label,
    string State,
    string Message,
    bool IsBlocking);

internal sealed record ProductionReadinessResult(
    string OverallState,
    bool IsReadyForProduction,
    bool IsManaged,
    IReadOnlyList<ReadinessComponent> Components,
    string? EffectiveGCodeReleaseId,
    bool RequiresExplicitGCodeSelection,
    IReadOnlyList<ReadinessRelease> CompatibleGCodeReleases,
    IReadOnlyList<ProductionActionDecision>? Actions = null)
{
    internal string Summary => IsReadyForProduction && Actions?.Any(x => x.Action == "RecordProduction"
        && x.Reasons.Any(reason => reason.Code == "controller_verification" && reason.Classification == "BLOCKING")) == true
        ? "Preparation ready; physical production still requires controller verification."
        : IsReadyForProduction && Actions?.Any(x => x.Action == "RecordProduction" && !x.IsAllowed) == true
        ? "Preparation ready; review the blocking execution-context requirements."
        : !IsManaged
        ? IsReadyForProduction
            ? "Ready for production; this legacy Operation has no managed G-code process revision."
            : "Not ready: material is not reconciled for this legacy Operation's Production Batch."
        : IsReadyForProduction
        ? "Ready for production"
        : $"Not ready: {Components.Count(component => component.IsBlocking)} blocking component(s)";
}
