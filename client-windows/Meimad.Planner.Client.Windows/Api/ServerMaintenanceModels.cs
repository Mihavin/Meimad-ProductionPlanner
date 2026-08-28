namespace Meimad.Planner.Client.Windows.Api;

internal sealed record CollectedDataTypeSummary(
    string Type,
    string DisplayName,
    long RowCount,
    DateTimeOffset? OldestAt,
    DateTimeOffset? NewestAt);

internal sealed record DatabaseStorageStatus(
    DateTimeOffset ReadAt,
    long DatabaseFileBytes,
    long WalFileBytes,
    long SharedMemoryFileBytes,
    long TotalOnDiskBytes,
    long PageSizeBytes,
    long PageCount,
    long FreePageCount,
    long UsedPageBytesEstimate,
    long ReusablePageBytes,
    int SchemaVersion,
    IReadOnlyList<CollectedDataTypeSummary> CollectedData);

internal sealed record CollectedDataTypeOption(
    string Type,
    string DisplayName,
    string Description);

internal sealed record ServerMaintenanceCatalog(
    DatabaseStorageStatus Database,
    IReadOnlyList<CollectedDataTypeOption> DeletableTypes,
    string BackupDownloadMethod,
    string BackupDownloadPath,
    string DeleteRangeSemantics);

internal sealed record CollectedDataFilter(
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive,
    IReadOnlyList<string> Types,
    string? MachineId);

internal sealed record CollectedDataPreviewRequest(
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive,
    IReadOnlyList<string> Types,
    string? MachineId);

internal sealed record CollectedDataPreview(
    CollectedDataFilter Filter,
    IReadOnlyList<CollectedDataTypeSummary> Items,
    long TotalRows,
    DateTimeOffset ReadAt);

internal sealed record CollectedDataPurgeRequest(
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive,
    IReadOnlyList<string> Types,
    string? MachineId,
    long ExpectedTotalRows,
    string Reason);

internal sealed record MaintenanceBackupInfo(
    string FileName,
    DateTimeOffset CreatedAt,
    long ByteLength,
    string Sha256,
    bool IntegrityVerified,
    bool RestoreVerified);

internal sealed record CollectedDataPurgeResult(
    CollectedDataFilter Filter,
    IReadOnlyList<CollectedDataTypeSummary> Deleted,
    long TotalDeletedRows,
    string Reason,
    string PerformedBy,
    DateTimeOffset PerformedAt,
    MaintenanceBackupInfo Backup,
    DatabaseStorageStatus Database);

internal sealed record DatabaseBackupDownload(
    string LocalPath,
    long ByteLength,
    string Sha256,
    DateTimeOffset? CreatedAt,
    bool IntegrityVerified,
    bool RestoreVerified);
