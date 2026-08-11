namespace Meimad.Planner.Server.Backup;

internal sealed record BackupResult(
    string BackupPath,
    DateTimeOffset CreatedAt,
    long ByteLength,
    string Sha256,
    int RetentionDeletedCount,
    bool IntegrityVerified,
    bool RestoreVerified);
