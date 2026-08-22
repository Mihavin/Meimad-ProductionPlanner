namespace Meimad.Planner.Server.Application.Timeline;

internal interface ITimelineSourceRepository
{
    Task<TimelineSourceSnapshot> ReadAsync(
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        CancellationToken cancellationToken);
}

internal sealed record TimelineSourceSnapshot(
    DateTimeOffset ReadAt,
    IReadOnlyList<TimelineSourceMachine> Machines,
    IReadOnlyList<TimelineSourceOperation> Operations,
    IReadOnlyList<TimelineSourceDowntime> Downtimes,
    string? SetupCalendarJson,
    string? SetupCalendarTimeZoneId,
    IReadOnlyList<TimelineSourceHoliday> Holidays,
    IReadOnlyList<TimelineSourceResource> Resources,
    string? MasterCalendarJson = null,
    string? MasterCalendarTimeZoneId = null);

internal sealed record TimelineSourceHoliday(
    DateOnly Date, string Name, string Status, string? StartsAtLocal, string? EndsAtLocal);

internal sealed record TimelineSourceMachine(
    string MachineId,
    string Number,
    string Name,
    string TimeZoneId,
    string CalendarJson,
    IReadOnlyList<string> SkillTokens,
    bool RespectMasterCalendar = true);

internal sealed record TimelineSourceOperation(
    string OperationId,
    string BatchId,
    string BatchNumber,
    string CaseId,
    string PartNumber,
    int OperationNumber,
    string OperationName,
    string Status,
    int PlannedQuantity,
    double? SetupSeconds,
    double? CycleSeconds,
    string SourceCaseOperationId,
    string DependencyType,
    string? PredecessorSourceCaseOperationId,
    string? SimultaneousGroupKey,
    string? MachineAssignmentId,
    string? MachineId,
    int? BacklogPosition,
    string? PlanningMode,
    DateTimeOffset? MachineMovedAt,
    int QaSeconds,
    int LoadUnloadSeconds,
    bool LoadUnloadRequiresWorker,
    bool AutomaticLoading,
    int? LoadUnloadEveryNParts,
    bool DayShiftOnly,
    DateOnly? PriorityWorkFinishDate,
    string? PriorityOrderNumber,
    string? ActivePauseReason,
    string? PausedBy,
    DateTimeOffset? PauseStartedAt,
    DateTimeOffset? MovePauseStartedAt,
    DateTimeOffset? MovePauseEndedAt,
    DateTimeOffset? ActualStart,
    DateTimeOffset? ActualEnd,
    string? ActualMachineId,
    TimeSpan ExternalDelayAfter = default,
    int ExternalDelayWorkingDays = 0,
    string? ExternalDelayCalendarJson = null,
    string? ExternalDelayCalendarTimeZoneId = null,
    bool ExternalDelayRespectMasterCalendar = true,
    int? ManualCycleSeconds = null,
    double? NcEstimatedCycleSeconds = null,
    string PlanningCycleTimeSource = "manual",
    string? NcEstimateConfidence = null,
    IReadOnlyList<string>? NcEstimateWarnings = null,
    string? NcEstimateGCodeReleaseId = null,
    int? FixtureSetupSeconds = null,
    int? RequiredToolCount = null,
    double ToolLoadingSeconds = 0,
    double? FirstPieceProveOutSeconds = null,
    double? TotalSetupSeconds = null,
    int? ProductionCycleQuantity = null,
    double? RemainingProductionSeconds = null,
    double? TotalPlannedMachineSeconds = null,
    IReadOnlyList<string>? SetupEstimateWarnings = null,
    bool UsesSetupOccupancyEstimate = false);

internal sealed record TimelineSourceResource(
    string ResourceId,
    string Role,
    string TimeZoneId,
    string CalendarJson,
    IReadOnlyList<TimelineSourceResourceException> Exceptions,
    IReadOnlyList<string> Skills,
    bool RespectMasterCalendar = true,
    double ToolLoadSecondsPerTool = 60,
    double? FixtureAssemblySeconds = null,
    double FirstPartRunningSpeedPercent = 66.6666666667);

internal sealed record TimelineSourceResourceException(
    DateOnly Date,
    bool IsFullDay,
    string? StartsAtLocal,
    string? EndsAtLocal);

internal sealed record TimelineSourceDowntime(
    string DowntimeId,
    string MachineId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Reason);
