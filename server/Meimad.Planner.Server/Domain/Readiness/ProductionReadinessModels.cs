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
    string? MaterialComment);

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
    IReadOnlyList<ReadinessRelease> CompatibleGCodeReleases)
{
    internal string Summary => !IsManaged
        ? IsReadyForProduction
            ? "Ready for production; this legacy Operation has no managed G-code process revision."
            : "Not ready: material is not reconciled for this legacy Operation's Production Batch."
        : IsReadyForProduction
        ? "Ready for production"
        : $"Not ready: {Components.Count(component => component.IsBlocking)} blocking component(s)";
}
