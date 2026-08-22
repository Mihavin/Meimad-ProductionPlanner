namespace Meimad.Planner.Server.Domain.GCode;

internal static class GCodeChangeScopes
{
    internal const string LocalPostRevision = "LOCAL_POST_REVISION";
    internal const string NewProcessRevision = "NEW_PROCESS_REVISION";

    internal static bool IsSupported(string? value) =>
        value is LocalPostRevision or NewProcessRevision;
}

internal sealed record ToolTableRelease(
    string ToolTableReleaseId,
    string CaseOperationId,
    int RevisionNumber,
    string OriginalFileName,
    string StoredRelativePath,
    long FileSize,
    string FileHash,
    DateTimeOffset ReleasedAt,
    string ReleasedBy,
    string ReleaseComment,
    int? RequiredToolCount,
    IReadOnlyList<ReleasedTool> Tools);

internal sealed record ReleasedTool(
    string ReleasedToolId,
    int RowNumber,
    string ToolIdentifier,
    string Description,
    bool IsRequired,
    bool RequiresMagazinePosition,
    bool IsActive,
    string? MagazinePosition);

internal sealed record ReleasedToolTableDefinition(
    IReadOnlyList<ReleasedTool> Tools,
    int RequiredToolCount);

internal sealed record ProcessRevision(
    string ProcessRevisionId,
    string CaseOperationId,
    int ProcessRevisionNumber,
    bool IsActive,
    string ToolTableReleaseId,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string ChangeDescription,
    int Version,
    ToolTableRelease ToolTable);

internal sealed record GCodeRelease(
    string GCodeReleaseId,
    string CaseOperationId,
    string ProcessRevisionId,
    int ProcessRevisionNumber,
    string PostprocessorId,
    string PostprocessorName,
    int PostSpecificRevision,
    string OriginalFileName,
    string StoredRelativePath,
    long FileSize,
    string FileHash,
    DateTimeOffset ReleasedAt,
    string ReleasedBy,
    string ChangeScope,
    string ReleaseComment,
    string ToolTableReleaseId,
    bool IsCurrentForProcessAndPost,
    bool IsForActiveProcess,
    NcProgramAnalysis? NcAnalysis = null,
    IReadOnlyList<NcMachineCycleEstimate>? MachineCycleEstimates = null,
    Meimad.Planner.Server.Domain.Haas.NcHeaderMetadata? HeaderMetadata = null);

internal static class NcAnalysisStatus
{
    internal const string Complete = "COMPLETE";
    internal const string Partial = "PARTIAL";
    internal const string Unavailable = "UNAVAILABLE";
}

internal static class NcEstimateConfidence
{
    internal const string High = "HIGH";
    internal const string Medium = "MEDIUM";
    internal const string Low = "LOW";
    internal const string Unavailable = "UNAVAILABLE";
}

internal sealed record NcProgramAnalysis(
    string ParserVersion,
    string Status,
    double FeedMotionSeconds,
    double RapidDistanceMillimeters,
    int ToolChangeCount,
    double DwellSeconds,
    string? DetectedUnits,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> UnsupportedConstructs,
    string Confidence,
    DateTimeOffset AnalyzedAt);

internal sealed record NcMachineTiming(
    string MachineId,
    double? RapidRateMillimetersPerMinute,
    double? ToolChangeTimeSeconds,
    double MachineTimeFactor);

internal sealed record NcMachineCycleEstimate(
    string GCodeReleaseId,
    string MachineId,
    string ParserVersion,
    double RawFeedSeconds,
    double RapidDistanceMillimeters,
    double? RapidSeconds,
    int ToolChangeCount,
    double? ToolChangeSeconds,
    double DwellSeconds,
    double? MachineRapidRateMillimetersPerMinute,
    double? MachineToolChangeTimeSeconds,
    double MachineTimeFactor,
    double? RawCycleSeconds,
    double? EstimatedCycleSeconds,
    IReadOnlyList<string> Warnings,
    string Confidence,
    DateTimeOffset CalculatedAt);

internal sealed record PostprocessorReleaseStatus(
    string PostprocessorId,
    string PostprocessorName,
    bool IsActive,
    string Status,
    GCodeRelease? CurrentRelease,
    GCodeRelease? LatestHistoricalRelease);

internal sealed record OperationGCodeCatalog(
    string CaseOperationId,
    ProcessRevision? ActiveProcessRevision,
    IReadOnlyList<ProcessRevision> ProcessRevisions,
    IReadOnlyList<PostprocessorReleaseStatus> Postprocessors,
    IReadOnlyList<GCodeRelease> Releases);

internal sealed record StoredReleaseFile(
    string ArtifactId,
    string OriginalFileName,
    string StoredRelativePath,
    long FileSize,
    string FileHash);
