namespace Meimad.Planner.Server.Application.TvDashboard;

internal sealed record TvDashboardProjection(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    string Freshness,
    int RefreshAfterSeconds,
    TvDashboardSummary Summary,
    IReadOnlyList<TvUrgentBatch> UrgentBatches,
    IReadOnlyList<TvMachine> Machines);

internal sealed record TvDashboardSummary(
    int MachineCount,
    int CriticalConflictCount,
    int UrgentBatchCount,
    int DowntimeMachineCount);

internal sealed record TvUrgentBatch(
    string BatchId,
    string BatchNumber,
    string PartNumber,
    string WorkFinishDate,
    bool IsOverdue,
    string? MachineNumber);

internal sealed record TvMachine(
    string MachineId,
    string Number,
    string Name,
    string ProcessType,
    TvStatus Status,
    TvJob? Current,
    TvJob? Next,
    TvDowntime? Downtime,
    IReadOnlyList<TvConflict> Conflicts);

internal sealed record TvStatus(string Code, string Label, string Icon, string Color);

internal sealed record TvJob(
    string OperationId,
    string BatchId,
    string PartNumber,
    string BatchNumber,
    int OperationNumber,
    string OperationName,
    string Status,
    DateTimeOffset? ProjectedFinish,
    bool Urgent,
    string? WorkFinishDate);

internal sealed record TvDowntime(
    string DowntimeId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Reason,
    bool IsCurrent);

internal sealed record TvConflict(
    string ConflictId,
    string Code,
    string Severity,
    string Message);
