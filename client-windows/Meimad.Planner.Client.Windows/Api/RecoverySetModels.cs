namespace Meimad.Planner.Client.Windows.Api;

internal sealed record RecoverySetReceipt(string FileName, string SetId, DateTimeOffset SnapshotAt,
    DateTimeOffset VerifiedAt, int SchemaVersion, long ByteLength, string Sha256, int FileCount, int ExternalFolderCount);
internal sealed record RecoverySetStatus(bool Configured, string? Destination, string ScheduleOwner,
    RecoverySetReceipt? LastComplete, double? AgeHours, string? LastAttemptResult,
    int RecoveryPointTargetHours, int RecoveryTimeTargetHours, bool RecoveryPointOverdue);
