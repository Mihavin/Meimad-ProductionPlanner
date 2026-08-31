namespace Meimad.Planner.Server.Application.Timeline;

internal sealed record TimelineProjection(
    DateTimeOffset ReadAt,
    DateTimeOffset HorizonStart,
    DateTimeOffset HorizonEnd,
    IReadOnlyList<TimelineProjectionBatch> Batches,
    IReadOnlyList<TimelineProjectionMachine> Machines,
    IReadOnlyList<TimelineProjectionDependency> Dependencies,
    IReadOnlyList<TimelineProjectionConflict> Conflicts,
    string DisplayTimeZoneId,
    string DayStartsAtLocal,
    string DayEndsAtLocal,
    IReadOnlyList<TimelineProductionRunProjection>? ProductionRuns = null);

internal sealed record TimelineProductionRunProjection(
    string ProductionRunId, string MachineId, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    IReadOnlyList<TimelineProductionRunProgramCompletion> ProgramCompletions);
internal sealed record TimelineProductionRunProgramCompletion(
    string ProductionRunProgramId, DateTimeOffset CompletesAt,
    IReadOnlyList<string> ProductionRunOutputIds);

internal sealed record TimelineProjectionBatch(
    string BatchId,
    string BatchNumber,
    string PartNumber,
    DateOnly? WorkFinishDate = null);

internal sealed record TimelineProjectionMachine(
    string MachineId,
    string Number,
    string Name,
    IReadOnlyList<TimelineProjectionInterval> Intervals,
    IReadOnlyList<TimelineProjectionNonWorkingWindow>? NonWorkingWindows = null);

internal sealed record TimelineProjectionNonWorkingWindow(
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Detail);

internal sealed record TimelineProjectionInterval(
    string Type,
    string MachineId,
    string? OperationId,
    string? BatchId,
    string? BatchNumber,
    string? PartNumber,
    int? OperationNumber,
    string? OperationName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Detail,
    string? TimingKind = null,
    string? OperationStatus = null,
    DateTimeOffset? ForecastStart = null,
    DateTimeOffset? ForecastEnd = null,
    DateTimeOffset? ActualStart = null,
    DateTimeOffset? ActualEnd = null,
    string? MachineAssignmentId = null,
    string? PlanningMode = null,
    DateOnly? WorkFinishDate = null,
    IReadOnlyList<TimelineProjectionPhase>? Phases = null,
    string OverallReadinessState = "NOT_MANAGED",
    bool IsReadyForProduction = true,
    string? ReadinessSummary = null,
    int CompletedQuantity = 0,
    int? TargetQuantity = null,
    double? MeasuredAverageCycleSeconds = null,
    int MeasuredCycleSampleCount = 0,
    string PlanningCycleTimeSource = "manual");

internal sealed record TimelineProjectionPhase(
    string Type,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Detail);

internal sealed record TimelineProjectionDependency(
    string DependencyId,
    string BatchId,
    string BatchNumber,
    string PartNumber,
    string Type,
    string FromOperationId,
    int FromOperationNumber,
    string FromOperationName,
    string ToOperationId,
    int ToOperationNumber,
    string ToOperationName,
    string? SimultaneousGroupKey);

internal sealed record TimelineProjectionConflict(
    string ConflictId,
    string Code,
    string Severity,
    string Message,
    IReadOnlyList<string> OperationIds,
    IReadOnlyList<string> MachineIds);
