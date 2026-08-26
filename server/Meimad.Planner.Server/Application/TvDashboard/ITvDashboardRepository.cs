namespace Meimad.Planner.Server.Application.TvDashboard;

internal interface ITvDashboardRepository
{
    Task<TvDashboardSource> ReadAsync(CancellationToken cancellationToken);
}

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
    IReadOnlyList<TvSourceOperation> Backlog);

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
    DateTimeOffset? ActivePauseStartedAt);

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
