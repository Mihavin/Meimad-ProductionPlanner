namespace Meimad.Planner.Server.Application.TvDashboard;

internal interface ITvDashboardRepository
{
    /// <param name="includeLastWorked">Also read the part last worked on each Machine (for idle rows).</param>
    Task<TvDashboardSource> ReadAsync(bool includeLastWorked, CancellationToken cancellationToken);
}

/// <summary>
/// The stored pictures a TV row may show for an Operation, in priority order: the Case picture, the Operation's
/// package thumbnail and its snapshot (content hash and path below the release root of each, null when absent).
/// </summary>
internal sealed record TvSourceThumbnails(
    string? CasePictureHash,
    string? CasePicturePath,
    string? PackageHash,
    string? PackagePath,
    string? SnapshotHash,
    string? SnapshotPath);

internal sealed record TvDashboardSource(
    IReadOnlyList<TvSourceMachine> Machines,
    IReadOnlyList<TvSourceDowntime> Downtimes,
    IReadOnlyList<TvSourceBatchDueDate> BatchDueDates);

internal sealed record TvSourceMachine(
    string MachineId,
    string Number,
    string Name,
    string ProcessType,
    string ConnectionStatus,
    string? MachineStatus,
    IReadOnlyList<TvSourceOperation> Backlog,
    TvSourceOperation? LastWorked = null);

internal sealed record TvSourceOperation(
    string OperationId,
    string BatchId,
    string CaseId,
    string BatchNumber,
    string PartNumber,
    int OperationNumber,
    string OperationName,
    string Status,
    int BacklogPosition,
    int PlannedQuantity,
    int? SetupSeconds,
    int? CycleSeconds,
    string? PreviewPath,
    string WorkingFolderPath,
    DateTimeOffset? ActualStart,
    DateTimeOffset? ActualEnd,
    double ClosedPauseSeconds,
    DateTimeOffset? ActivePauseStartedAt,
    int? ProducedQuantity = null,
    double? MeasuredAverageCycleSeconds = null,
    int MeasuredCycleSampleCount = 0,
    bool CncProductionStarted = false,
    string? PartName = null,
    TvSourceThumbnails? Thumbnails = null);

internal sealed record TvSourceDowntime(
    string DowntimeId,
    string MachineId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Reason);

internal sealed record TvSourceBatchDueDate(
    string BatchId,
    string BatchNumber,
    string PartNumber,
    DateOnly WorkFinishDate);
