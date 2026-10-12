using System.Globalization;

namespace Meimad.Planner.Client.Windows.Api;

/// <summary>Measured position of a work offset in millimetres; null for an axis the Machine did not report.</summary>
internal sealed record WorkOffsetPositionInfo(double? X, double? Y, double? Z);

/// <summary>Measured rotation of a work offset in degrees; null for an axis the Machine did not report.</summary>
internal sealed record WorkOffsetRotationInfo(double? A, double? B, double? C);

/// <summary>The identity text the Machine sent with the measurement, as it was received.</summary>
internal sealed record WorkOffsetClaimInfo(string? Machine, string? Program, string? Operation, string? Release);

/// <summary>
/// One measured work offset (schema v116): G54 etc. as the Machine reported it after the setupist
/// measured the part, with its Machine, Case Operation and NC release, source and status. Status is
/// VALID (current when it is the newest for its Machine + Operation + NC release + offset),
/// REVIEW_REQUIRED, REJECTED, SUPERSEDED or UNMATCHED.
/// </summary>
internal sealed record WorkOffsetRecordInfo(
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
    WorkOffsetPositionInfo PositionMm,
    WorkOffsetRotationInfo RotationDeg,
    string? Units,
    string Source,
    string SourceSession,
    long? SourceSequence,
    WorkOffsetClaimInfo Claimed,
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
    int Version)
{
    public string MeasuredText => MeasuredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string MachineText => MachineNumber is null
        ? Claimed.Machine is null ? "-" : $"? {Claimed.Machine}"
        : string.IsNullOrWhiteSpace(MachineName) || MachineName == MachineNumber ? MachineNumber : $"{MachineNumber} - {MachineName}";

    public string OperationText => OperationNumber is { } number
        ? $"{number.ToString(CultureInfo.InvariantCulture)} {OperationName}".Trim()
        : Claimed.Operation is null ? "-" : $"? {Claimed.Operation}";

    public string ReleaseText => NcReleaseLabel ?? (Claimed.Release is null ? "-" : $"? {Claimed.Release}");

    public string PositionText => string.Join("  ", new[]
        {
            Axis("X", PositionMm.X), Axis("Y", PositionMm.Y), Axis("Z", PositionMm.Z),
            Axis("A", RotationDeg.A), Axis("B", RotationDeg.B), Axis("C", RotationDeg.C)
        }.Where(text => text is not null));

    /// <summary>The status as words with an icon character, so it never depends on color alone.</summary>
    public string StatusText => WorkOffsetStatusTexts.Describe(Status);

    public string ReasonText => WorkOffsetStatusTexts.Reason(StatusReason);

    public bool IsReviewRequired => Status == "REVIEW_REQUIRED";

    private static string? Axis(string name, double? value) =>
        value is { } number ? $"{name} {number.ToString("0.0000", CultureInfo.InvariantCulture)}" : null;
}

/// <summary>Current records and history of one Machine and Case Operation.</summary>
internal sealed record WorkOffsetOperationInfo(
    string MachineId,
    string CaseOperationId,
    string? WorkOffset,
    IReadOnlyList<WorkOffsetRecordInfo> Current,
    IReadOnlyList<WorkOffsetRecordInfo> History);

internal sealed record WorkOffsetListInfo(IReadOnlyList<WorkOffsetRecordInfo> Items);

/// <summary>A reviewer's decision: the candidate's version and, when accepting, the current record compared with ("" for none).</summary>
internal sealed record WorkOffsetReviewRequestInfo(int Version, string? ExpectedCurrentId, string? Comment);

/// <summary>The Machine's work offset report program in its NC dialect.</summary>
internal sealed record WorkOffsetReportInfo(string NcDialect, string FileName, string Text, IReadOnlyList<string> Lines);

internal static class WorkOffsetStatusTexts
{
    internal static string Describe(string status) => status switch
    {
        "VALID" => "✔ Valid",
        "REVIEW_REQUIRED" => "⚠ Review required",
        "REJECTED" => "✖ Rejected",
        "SUPERSEDED" => "↺ Superseded",
        "UNMATCHED" => "? Unmatched",
        _ => status
    };

    internal static string Reason(string? code) => code switch
    {
        null => string.Empty,
        "large_change" => "Large change against the current measurement",
        "stale_release" => "Measured for an NC release that is no longer current",
        "older_than_current" => "Older than the current measurement",
        "wrong_machine" => "Sent on another Machine's channel",
        "unknown_machine" => "Unknown Machine",
        "ambiguous_machine" => "Machine name matches several Machines",
        "machine_inactive" => "Machine is inactive",
        "unknown_release" => "Unknown NC release",
        "ambiguous_release" => "NC release matches several releases",
        "program_release_mismatch" => "Program and NC release do not match",
        "operation_mismatch" => "Operation does not match the NC release",
        "session_reused" => "Session reused with other content",
        "unsupported_work_offset" => "Work offset not available on this control",
        "units_missing" => "Units missing",
        "value_out_of_range" => "Value out of range",
        "duplicate_axis" => "Axis sent twice",
        "invalid_number" => "Invalid number",
        "invalid_units" => "Invalid units",
        _ => code
    };
}
