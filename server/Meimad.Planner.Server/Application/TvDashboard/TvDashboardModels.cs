namespace Meimad.Planner.Server.Application.TvDashboard;

internal sealed record TvDashboardProjection(
    int SchemaVersion,
    string DashboardBuild,
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
    TvConnectionStatus Connection,
    string? MachineStatus,
    TvStatus Status,
    TvJob? Current,
    TvJob? Next,
    TvJob? Third,
    TvDowntime? Downtime,
    IReadOnlyList<TvConflict> Conflicts,
    // RUNNING, SETUP, PAUSED, WAITING, COMPLETED, IDLE or ERROR (a CNC alarm); the TV writes it as a word.
    string DisplayStatus = "IDLE",
    // One small warning (CNC alarm or a blocking planning conflict of the current Operation), or null.
    TvWarning? Warning = null,
    // The last part worked on the Machine, only when the Machine has no current Operation and
    // TvDashboard:ShowLastPartImageWhenIdle is on.
    TvIdlePart? IdlePart = null);

/// <summary>A warning shown as a small icon with a word next to it; <c>Code</c> is cnc_alarm or planning_blocked.</summary>
internal sealed record TvWarning(string Code, string Severity, string Label);

/// <summary>The part last worked on an idle Machine, with its picture.</summary>
internal sealed record TvIdlePart(
    string PartNumber,
    string? PartName,
    int OperationNumber,
    string OperationName,
    string? ThumbnailUrl,
    string? ThumbnailVersion,
    string ThumbnailAlt,
    string ThumbnailSource);

internal sealed record TvConnectionStatus(
    string Code,
    string Label,
    bool Online,
    string SourceCode);

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
    string? WorkFinishDate,
    TvOperationProgress Progress,
    // The Case name, when the Case has one.
    string? PartName = null,
    // The part picture of this Operation (Case picture, else package thumbnail, else snapshot): an opaque
    // URL whose last segment is also the version, so a TV loads it once; null means the placeholder.
    string? ThumbnailUrl = null,
    string? ThumbnailVersion = null,
    string ThumbnailAlt = "",
    // case_picture, package, snapshot or placeholder.
    string ThumbnailSource = "placeholder");

internal sealed record TvOperationProgress(
    string StatusCode,
    string StatusLabel,
    string Phase,
    string CompletionLabel,
    int? CompletionPercent,
    int? SetupPercent,
    int? CurrentPart,
    int PlannedParts,
    double? AverageCycleSeconds = null,
    int AverageCycleSampleCount = 0);

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
