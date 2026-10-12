namespace Meimad.Planner.Server.Application.MachineEvidence;

/// <summary>Status of a measured work offset record (schema v116 <c>machine_work_offsets.status</c>).</summary>
internal static class WorkOffsetStatuses
{
    /// <summary>Matched and checked; the latest VALID record of its Machine + Case Operation + NC release + work offset is current.</summary>
    internal const string Valid = "VALID";

    /// <summary>Matched, but not made current automatically (large change, old NC release, older than current); a user accepts or rejects it.</summary>
    internal const string ReviewRequired = "REVIEW_REQUIRED";

    /// <summary>Kept as evidence and never applied: invalid content, a reused session, or a candidate a user rejected.</summary>
    internal const string Rejected = "REJECTED";

    /// <summary>Was VALID and was replaced by a newer VALID record of the same key.</summary>
    internal const string Superseded = "SUPERSEDED";

    /// <summary>Kept as "Unmatched machine evidence": the Machine, NC release or Operation could not be matched unambiguously.</summary>
    internal const string Unmatched = "UNMATCHED";

    internal static readonly IReadOnlyList<string> All = [Valid, ReviewRequired, Rejected, Superseded, Unmatched];

    internal static bool IsKnown(string? value) => value is not null && All.Contains(value);
}

/// <summary>Where a measured work offset came from.</summary>
internal static class WorkOffsetSources
{
    internal const string DprntTcp = "DPRNT_TCP";
    internal const string DprntFile = "DPRNT_FILE";
    internal const string DprntFtp = "DPRNT_FTP";
    internal const string DprntSerial = "DPRNT_SERIAL";
    internal const string Dprnt = "DPRNT";
    internal const string Api = "API";
}

/// <summary>
/// One measured work offset as received, before matching: from a Machine's DPRNT channel
/// (<see cref="SourceMachineId"/> is the Machine whose channel delivered it) or from the ingest
/// endpoint (null). <see cref="Values"/> are in <see cref="Units"/>; <see cref="RejectCode"/> is set
/// when the parser already found the content invalid.
/// </summary>
internal sealed record WorkOffsetEvidence(
    string? SourceMachineId,
    string Source,
    string ReceivedBy,
    string? Session,
    long? Sequence,
    string? Machine,
    string? Program,
    string? Operation,
    string? Release,
    string? WorkOffset,
    string? Units,
    IReadOnlyDictionary<string, double> Values,
    string RawMessage,
    string RawMessageHash,
    DateTimeOffset? MeasuredAt,
    string? RejectCode)
{
    internal static WorkOffsetEvidence FromMessage(
        WorkOffsetMessage message, string sourceMachineId, string source, string receivedBy) => new(
        sourceMachineId, source, receivedBy, message.Session, message.Sequence, message.Machine,
        message.Program, message.Operation, message.Release, message.WorkOffset, message.Units,
        message.Values, message.RawText, message.ContentHash(), null, message.RejectCode);
}

/// <summary>A stored measured work offset with the names a reader needs.</summary>
internal sealed record WorkOffsetRecord(
    string WorkOffsetId,
    string? MachineId,
    string? MachineNumber,
    string? MachineName,
    string? CaseOperationId,
    int? OperationNumber,
    string? OperationName,
    string? NcReleaseId,
    int? NcIdentityToken,
    string? NcReleaseLabel,
    bool? NcReleaseIsCurrent,
    string WorkOffset,
    WorkOffsetPosition PositionMm,
    WorkOffsetRotation RotationDeg,
    string? Units,
    string Source,
    string SourceSession,
    long? SourceSequence,
    WorkOffsetClaim Claimed,
    string RawMessage,
    string RawMessageHash,
    DateTimeOffset MeasuredAt,
    DateTimeOffset ReceivedAt,
    string ReceivedBy,
    string Status,
    string? StatusReason,
    string InitialStatus,
    string? SupersedesId,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    string? ReviewComment,
    int Version);

internal sealed record WorkOffsetPosition(double? X, double? Y, double? Z);

internal sealed record WorkOffsetRotation(double? A, double? B, double? C);

/// <summary>The identity text the Machine sent, kept as it was (upper-case).</summary>
internal sealed record WorkOffsetClaim(string? Machine, string? Program, string? Operation, string? Release);

/// <summary>Result of an ingest: the record and whether it was already stored (an idempotent repeat).</summary>
internal sealed record WorkOffsetIngestResult(bool Duplicate, WorkOffsetRecord Record);

/// <summary>Current and historical measurements of one Machine and Case Operation.</summary>
internal sealed record WorkOffsetOperationView(
    string MachineId,
    string CaseOperationId,
    string? WorkOffset,
    IReadOnlyList<WorkOffsetRecord> Current,
    IReadOnlyList<WorkOffsetRecord> History);

/// <summary>A reviewer's decision on a REVIEW_REQUIRED candidate.</summary>
internal sealed record WorkOffsetReviewCommand(
    string WorkOffsetId,
    bool Accept,
    int ExpectedVersion,
    bool CheckCurrent,
    string? ExpectedCurrentId,
    string? Comment,
    string UserName);

internal sealed class WorkOffsetValidationException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}

internal sealed class WorkOffsetNotFoundException(string message) : Exception(message);
