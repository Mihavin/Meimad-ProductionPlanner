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
    bool IsForActiveProcess);

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
