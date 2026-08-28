using Meimad.Planner.Server.Application.EditMode;

namespace Meimad.Planner.Server.Application.Maintenance;

internal static class CollectedDataTypes
{
    internal const string CncRawTelemetry = "cnc_raw_telemetry";
    internal const string CncStateHistory = "cnc_state_history";
    internal const string CncConnectionEvents = "cnc_connection_events";

    internal static readonly IReadOnlyList<string> All =
    [
        CncRawTelemetry,
        CncStateHistory,
        CncConnectionEvents
    ];
}

internal sealed record CollectedDataFilter(
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive,
    IReadOnlyList<string> Types,
    string? MachineId);

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

internal sealed record CollectedDataPreview(
    CollectedDataFilter Filter,
    IReadOnlyList<CollectedDataTypeSummary> Items,
    long TotalRows,
    DateTimeOffset ReadAt);

internal sealed record MaintenanceBackupInfo(
    string FileName,
    DateTimeOffset CreatedAt,
    long ByteLength,
    string Sha256,
    bool IntegrityVerified,
    bool RestoreVerified);

internal sealed record MaintenanceBackupArtifact(
    Stream Content,
    MaintenanceBackupInfo Backup);

internal sealed record CollectedDataPurgeResult(
    CollectedDataFilter Filter,
    IReadOnlyList<CollectedDataTypeSummary> Deleted,
    long TotalDeletedRows,
    string Reason,
    string PerformedBy,
    DateTimeOffset PerformedAt,
    MaintenanceBackupInfo Backup,
    DatabaseStorageStatus Database);

internal interface IServerMaintenanceRepository
{
    Task<DatabaseStorageStatus> ReadStatusAsync(CancellationToken token);
    Task<CollectedDataPreview> PreviewAsync(CollectedDataFilter filter, CancellationToken token);
    Task ValidateEditAuthorityAsync(EditAuthority authority, CancellationToken token);
    Task RecordBackupAsync(
        MaintenanceBackupInfo backup,
        string userId,
        EditAuthority authority,
        DateTimeOffset occurredAt,
        CancellationToken token);
    Task<IReadOnlyList<CollectedDataTypeSummary>> PurgeAsync(
        CollectedDataFilter filter,
        long expectedTotalRows,
        string reason,
        string userId,
        EditAuthority authority,
        MaintenanceBackupInfo backup,
        DateTimeOffset occurredAt,
        CancellationToken token);
}

internal sealed class ServerMaintenanceValidationException : Exception
{
    internal ServerMaintenanceValidationException(string code, string message)
        : base(message) => Code = code;

    internal string Code { get; }
}

internal sealed class CollectedDataPreviewChangedException : Exception
{
    internal CollectedDataPreviewChangedException(long expectedRows, long actualRows)
        : base($"Collected data changed after preview: expected {expectedRows} rows but found {actualRows}. Preview again before deleting.")
    {
        ExpectedRows = expectedRows;
        ActualRows = actualRows;
    }

    internal long ExpectedRows { get; }
    internal long ActualRows { get; }
}
