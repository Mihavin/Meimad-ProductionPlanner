using Meimad.Planner.Server.Domain.GCode;

namespace Meimad.Planner.Server.Application.GCode;

internal sealed record UploadedReleaseFile(
    string OriginalFileName,
    Stream Content,
    long? DeclaredLength);

internal sealed record ReleaseGCodeCommand(
    string? CaseId,
    string? CaseOperationId,
    string? PostprocessorId,
    string? ChangeScope,
    string? ReleaseComment,
    string? ProcessChangeDescription,
    bool ConfirmNewProcessRevision,
    bool ReuseActiveToolTable,
    bool ConfirmToolTable,
    UploadedReleaseFile? GCodeFile,
    UploadedReleaseFile? ToolTableFile,
    string? ManufacturingProgramId = null,
    IReadOnlyList<ManufacturingProgramRevisionOutput>? Outputs = null);

internal sealed record PublishGCodeReleaseCommand(
    string CaseId,
    string CaseOperationId,
    string PostprocessorId,
    string ChangeScope,
    string ReleaseComment,
    string ProcessChangeDescription,
    bool ConfirmNewProcessRevision,
    bool ReuseActiveToolTable,
    bool ConfirmToolTable,
    string CandidateProcessRevisionId,
    StoredReleaseFile GCodeFile,
    StoredReleaseFile? ToolTableFile,
    ReleasedToolTableDefinition? ToolTableDefinition,
    NcProgramAnalysis NcAnalysis,
    Meimad.Planner.Server.Domain.Haas.NcHeaderMetadata HeaderMetadata,
    DateTimeOffset ReleasedAt,
    string? ManufacturingProgramId = null,
    IReadOnlyList<ManufacturingProgramRevisionOutput>? Outputs = null);

internal sealed record ProgramPublicationContext(string CaseId, string CaseOperationId);

internal sealed record ReleasedFileDownload(
    string AbsolutePath,
    string OriginalFileName,
    string FileHash,
    long FileSize);
