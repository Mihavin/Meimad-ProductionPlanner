namespace Meimad.Planner.Server.Application.PlanningBoard;

using Meimad.Planner.Server.Domain.Readiness;

internal interface IPlanningBoardRepository
{
    Task<PlanningBoardSnapshot> ReadAsync(CancellationToken cancellationToken);
}

internal sealed record PlanningBoardSnapshot(
    DateTimeOffset ReadAt,
    IReadOnlyList<PlanningBoardOperation> Pool,
    IReadOnlyList<PlanningBoardMachine> Machines,
    IReadOnlyList<ProductionRunPlanningCard>? ProductionRuns = null);

internal sealed record ProductionRunPlanningCard(
    string ProductionRunId, string Status, string? MachineId, int? BacklogPosition,
    int SharedSetupSeconds, int ProgramCount, long RemainingDurationSeconds,
    string ReadinessState, bool IsReady, IReadOnlyList<ProductionRunPlanningProgram> Programs);
internal sealed record ProductionRunPlanningProgram(
    string ProductionRunProgramId, string ManufacturingProgramId, string? GCodeReleaseId,
    int SequencePosition, int TargetCycles, int CompletedCycles, long ForecastCompletionOffsetSeconds,
    IReadOnlyList<ProductionRunPlanningOutput> Outputs);
internal sealed record ProductionRunPlanningOutput(
    string ProductionRunOutputId, string BatchOperationId, string BatchNumber,
    string CaseId, string PartNumber, int OperationNumber, int QuantityPerCycle,
    int TargetQuantity, int ProducedQuantity, int RemainingQuantity);

internal interface IProductionRunPlanningProjectionRepository
{
    Task<IReadOnlyList<ProductionRunPlanningCard>> ReadAsync(CancellationToken token);
}

internal sealed record PlanningBoardOperation(
    string BatchOperationId,
    string BatchId,
    string BatchNumber,
    string CaseId,
    string PartNumber,
    int OperationNumber,
    string OperationName,
    string? RequiredMachineType,
    int? SetupTimeSeconds,
    int? CycleTimePerPartSeconds,
    string Status,
    string? MachineId,
    int? BacklogPosition,
    int PlannedQuantity,
    IReadOnlyList<string> OrderReferences,
    long? EstimatedTimeSeconds,
    int QaTimeAfterSetupSeconds,
    int LoadUnloadTimeSeconds,
    bool LoadUnloadRequiresWorker,
    bool AutomaticLoading,
    int? LoadUnloadEveryNParts,
    bool DayShiftOnly,
    string? ActivePauseReason,
    string? PausedBy,
    DateTimeOffset? PauseStartedAt,
    string? CaseName = null,
    DateTimeOffset? ActualStart = null,
    DateTimeOffset? ActualEnd = null,
    string? ActualMachineId = null,
    string? MachineAssignmentId = null,
    int? AssignmentVersion = null,
    string PlanningMode = "manual",
    string? WorkFinishDate = null,
    DateTimeOffset? LatestStart = null,
    string? LatestStartWarning = null,
    bool IsLatestStartOverdue = false,
    int RoutePosition = 0,
    long ExternalDelaySeconds = 0,
    string ToolCapacityStatus = "not_managed",
    string ToolCapacityMessage = "Tool capacity is not managed for this Operation.",
    int? RequiredToolCount = null,
    int? AvailableToolPositions = null,
    bool IsToolCapacitySatisfied = true,
    string OverallReadinessState = OverallReadinessStates.NotReady,
    bool IsReadyForProduction = false,
    string ReadinessSummary = "Readiness has not been evaluated.",
    IReadOnlyList<ReadinessComponent>? ReadinessComponents = null,
    string? EffectiveGCodeReleaseId = null,
    bool RequiresExplicitGCodeSelection = false,
    IReadOnlyList<ReadinessRelease>? CompatibleGCodeReleases = null,
    double? NcEstimatedCycleTimePerPartSeconds = null,
    double? PlanningCycleTimePerPartSeconds = null,
    string PlanningCycleTimeSource = "manual",
    string? NcEstimateConfidence = null,
    IReadOnlyList<string>? NcEstimateWarnings = null,
    string? NcEstimateGCodeReleaseId = null,
    double ToolLoadingTimeSeconds = 0,
    double? FixtureSetupTimeSeconds = null,
    double? FirstPieceProveOutTimeSeconds = null,
    double? TotalSetupTimeSeconds = null,
    int? RemainingProductionQuantity = null,
    double? RemainingProductionRuntimeSeconds = null,
    double? TotalPlannedMachineTimeSeconds = null,
    IReadOnlyList<string>? SetupEstimateWarnings = null,
    bool UsesSetupOccupancyEstimate = false);

internal sealed record PlanningBoardMachine(
    string MachineId,
    string Number,
    string Name,
    string ProcessType,
    string? AxisType,
    IReadOnlyList<string> Capabilities,
    bool IsActive,
    IReadOnlyList<PlanningBoardOperation> Backlog);
