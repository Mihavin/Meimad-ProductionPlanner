using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using Meimad.Planner.Server.Domain.Timeline;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Application.EventLogging;
using Meimad.Planner.Server.Application.Readiness;
using Meimad.Planner.Server.Domain.Readiness;
using Meimad.Planner.Server.Application.PlanningBoard;

namespace Meimad.Planner.Server.Application.Timeline;

internal sealed class TimelineProjectionService
{
    internal const string DuplicateTimelineBlockLogTemplate =
        "DUPLICATE_TIMELINE_BLOCK assignmentId={AssignmentId} operationId={OperationId} machineId={MachineId}";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ITimelineSourceRepository repository;
    private readonly TimelineCalculationEngine engine;
    private readonly TimelineOptions options;
    private readonly IStructuredEventLogRepository eventLog;
    private readonly IProductionReadinessRepository? readinessRepository;
    private readonly ILogger<TimelineProjectionService> logger;
    private readonly IProductionRunPlanningProjectionRepository? productionRuns;

    public TimelineProjectionService(
        ITimelineSourceRepository repository,
        TimelineCalculationEngine engine,
        TimelineOptions options,
        IStructuredEventLogRepository eventLog,
        ILogger<TimelineProjectionService> logger,
        IProductionReadinessRepository? readinessRepository = null,
        IProductionRunPlanningProjectionRepository? productionRuns = null)
    {
        this.repository = repository;
        this.engine = engine;
        this.options = options;
        this.eventLog = eventLog;
        this.readinessRepository = readinessRepository;
        this.logger = logger;
        this.productionRuns = productionRuns;
    }

    internal async Task<TimelineProjection> CalculateAsync(
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        CancellationToken cancellationToken = default) =>
        await CalculateAsync(horizonStart, horizonEnd, null, cancellationToken);

    internal async Task<TimelineProjection> CalculateAsync(
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        DateTimeOffset? asOf,
        CancellationToken cancellationToken = default)
    {
        var total = Stopwatch.StartNew();
        horizonStart = horizonStart.ToUniversalTime();
        horizonEnd = horizonEnd.ToUniversalTime();
        var sourceRead = Stopwatch.StartNew();
        var source = await repository.ReadAsync(horizonStart, horizonEnd, cancellationToken);
        sourceRead.Stop();
        var requestedForecastCursor = (asOf ?? source.ReadAt).ToUniversalTime();
        var forecastCursor = requestedForecastCursor < horizonStart
            ? horizonStart
            : requestedForecastCursor >= horizonEnd
                ? horizonEnd
                : requestedForecastCursor;
        var mappingConflicts = new List<TimelineProjectionConflict>();
        var machinesById = source.Machines.ToDictionary(
            machine => machine.MachineId,
            StringComparer.Ordinal);
        var operationsById = source.Operations.ToDictionary(
            operation => operation.OperationId,
            StringComparer.Ordinal);
        var assigned = source.Operations
            .Where(operation => operation.Status != "completed"
                && operation.MachineId is not null
                && operation.BacklogPosition.HasValue)
            .ToArray();

        if (requestedForecastCursor >= horizonEnd)
        {
            foreach (var operation in assigned.Where(operation =>
                         operation.Status == "not_started"))
            {
                mappingConflicts.Add(Conflict(
                    "timeline_horizon_elapsed",
                    "blocking",
                    $"Batch {operation.BatchNumber} OP{operation.OperationNumber} is not started, but the requested Timeline horizon has already elapsed; no historical forecast was created.",
                    [operation.OperationId],
                    [operation.MachineId!]));
            }
        }
        var actualHistory = source.Operations
            .Where(operation => operation.ActualStart.HasValue
                && operation.ActualMachineId is not null
                && operation.Status is "completed" or "in_progress" or "suspended")
            .ToArray();

        logger.LogInformation(
            "Timeline source loaded for {HorizonStart} to {HorizonEnd} at {SourceReadAt}; effective forecast cursor {ForecastCursor}: {MachineCount} Machines, {OperationCount} Batch Operations, {AssignedCount} assigned, {ResourceCount} active resources, {DowntimeCount} downtime windows.",
            horizonStart, horizonEnd, source.ReadAt, forecastCursor,
            source.Machines.Count, source.Operations.Count,
            assigned.Length, source.Resources.Count, source.Downtimes.Count);
        foreach (var operation in assigned)
        {
            logger.LogDebug(
                "Timeline assignment {MachineAssignmentId} input: operation {OperationId} Batch {BatchId} OP{OperationNumber} -> Machine {MachineId} backlog {BacklogPosition}; planning mode {PlanningMode}; due {WorkFinishDate}; status {Status}, actual {ActualStart} to {ActualEnd}; quantity {PlannedQuantity}, setup {SetupSeconds}, cycle {CycleSeconds}, QA {QaSeconds}, load/unload {LoadUnloadSeconds}.",
                operation.MachineAssignmentId,
                operation.OperationId, operation.BatchId, operation.OperationNumber,
                operation.MachineId, operation.BacklogPosition, operation.PlanningMode,
                operation.PriorityWorkFinishDate, operation.Status,
                operation.ActualStart, operation.ActualEnd, operation.PlannedQuantity,
                operation.SetupSeconds, operation.CycleSeconds, operation.QaSeconds,
                operation.LoadUnloadSeconds);
            if (PlanningMode(operation) == TimelinePlanningMode.Backward
                && operation.Status == "in_progress")
            {
                mappingConflicts.Add(Conflict(
                    "backward_in_progress_fallback",
                    "attention",
                    $"Batch {operation.BatchNumber} OP{operation.OperationNumber} keeps its authoritative actual start while in progress; backward placement resumes only after Reset.",
                    [operation.OperationId],
                    [operation.MachineId!]));
            }
        }

        foreach (var operation in source.Operations.Where(operation =>
                     operation.Status != "completed" && operation.MachineId is null))
        {
            mappingConflicts.Add(Conflict(
                "unassigned_operation",
                "attention",
                $"Batch {operation.BatchNumber} OP{operation.OperationNumber} is not assigned to a Machine.",
                [operation.OperationId],
                []));
        }

        foreach (var operation in source.Operations.Where(operation =>
                     operation.Status == "completed"
                     && (!operation.ActualStart.HasValue || !operation.ActualEnd.HasValue
                         || operation.ActualMachineId is null)))
        {
            mappingConflicts.Add(Conflict(
                "actual_history_missing",
                "attention",
                $"Batch {operation.BatchNumber} OP{operation.OperationNumber} was completed before authoritative actual timing was recorded.",
                [operation.OperationId],
                operation.ActualMachineId is null ? [] : [operation.ActualMachineId]));
        }

        var usableOperations = new List<TimelineSourceOperation>();
        foreach (var operation in assigned)
        {
            if (!operation.SetupSeconds.HasValue || !operation.CycleSeconds.HasValue)
            {
                mappingConflicts.Add(Conflict(
                    "missing_timing",
                    "blocking",
                    $"Batch {operation.BatchNumber} OP{operation.OperationNumber} is missing setup or cycle timing.",
                    [operation.OperationId],
                    [operation.MachineId!]));
                continue;
            }

            try
            {
                var productionCycleQuantity = operation.ProductionCycleQuantity
                    ?? operation.PlannedQuantity;
                var productionSeconds = operation.CycleSeconds.Value
                    * productionCycleQuantity;
                if (!double.IsFinite(operation.SetupSeconds.Value)
                    || !double.IsFinite(operation.CycleSeconds.Value)
                    || !double.IsFinite(productionSeconds)
                    || operation.SetupSeconds.Value > TimeSpan.MaxValue.TotalSeconds
                    || operation.CycleSeconds.Value > TimeSpan.MaxValue.TotalSeconds
                    || productionSeconds > TimeSpan.MaxValue.TotalSeconds)
                {
                    throw new OverflowException();
                }
                var loadUnloadSeconds = LoadUnloadTotalSeconds(operation);
                if (operation.SetupSeconds.Value == 0
                    && operation.QaSeconds == 0
                    && loadUnloadSeconds == 0
                    && productionSeconds == 0)
                {
                    mappingConflicts.Add(Conflict(
                        "zero_duration",
                        "blocking",
                        $"Batch {operation.BatchNumber} OP{operation.OperationNumber} has no calculable setup, QA, load/unload, or production duration.",
                        [operation.OperationId],
                        [operation.MachineId!]));
                    continue;
                }
                usableOperations.Add(operation);
            }
            catch (OverflowException)
            {
                mappingConflicts.Add(Conflict(
                    "duration_overflow",
                    "blocking",
                    $"Batch {operation.BatchNumber} OP{operation.OperationNumber} duration exceeds the supported range.",
                    [operation.OperationId],
                    [operation.MachineId!]));
            }
        }

        var calendars = new List<TimelineMachineCalendar>();
        var masterAvailability = source.MasterCalendarJson is null ? null : ReadAvailability(
            source.MasterCalendarJson, source.MasterCalendarTimeZoneId, horizonStart, horizonEnd,
            "Israel Master Calendar", mappingConflicts, [], source.Holidays);
        foreach (var machine in source.Machines)
        {
            var windows = ReadAvailability(
                machine.CalendarJson,
                machine.TimeZoneId,
                horizonStart,
                horizonEnd,
                $"Machine {machine.Number} calendar",
                mappingConflicts,
                [machine.MachineId],
                source.Holidays);
            calendars.Add(new TimelineMachineCalendar(machine.MachineId,
                machine.RespectMasterCalendar && masterAvailability is not null
                    ? IntersectAvailability(windows, masterAvailability)
                    : windows,
                machine.SkillTokens));
        }
        var machineCalendarsById = calendars.ToDictionary(
            calendar => calendar.MachineId,
            StringComparer.Ordinal);

        var setupAvailability = source.SetupCalendarJson is null
            ? DefaultSetupAvailability(horizonStart, horizonEnd, mappingConflicts)
            : ReadAvailability(
                source.SetupCalendarJson,
                source.SetupCalendarTimeZoneId,
                horizonStart,
                horizonEnd,
                "Setup calendar",
                mappingConflicts,
                [],
                source.Holidays);
        var resourceCalendars = source.Resources.Select(resource =>
        {
            var windows = ApplyResourceExceptions(
                ReadAvailability(resource.CalendarJson, resource.TimeZoneId, horizonStart, horizonEnd,
                    $"Employee resource {resource.ResourceId} calendar", mappingConflicts, [], source.Holidays),
                resource.TimeZoneId, resource.Exceptions, horizonStart, horizonEnd);
            if (resource.RespectMasterCalendar && masterAvailability is not null)
                windows = IntersectAvailability(windows, masterAvailability);
            return new TimelineResourceCalendar(resource.ResourceId, ResourceRole(resource.Role), windows, resource.Skills);
        }).ToArray();
        var dayShiftCalendars = source.Machines.Select(machine => new TimelineMachineCalendar(
            machine.MachineId,
            ExpandDailyWindow(machine.TimeZoneId, options.DayShiftStartsAtLocal,
                options.DayShiftEndsAtLocal, horizonStart, horizonEnd))).ToArray();
        var externalWorkingDayDelays = source.Operations
            .Where(operation => operation.ExternalDelayWorkingDays > 0
                && operation.ExternalDelayCalendarJson is not null
                && operation.ExternalDelayCalendarTimeZoneId is not null)
            .ToDictionary(operation => operation.OperationId, operation =>
            {
                var windows = ReadAvailability(
                    operation.ExternalDelayCalendarJson!,
                    operation.ExternalDelayCalendarTimeZoneId,
                    horizonStart,
                    horizonEnd,
                    $"External delay calendar for Batch {operation.BatchNumber} OP{operation.OperationNumber}",
                    mappingConflicts,
                    operation.MachineId is null ? [] : [operation.MachineId],
                    source.Holidays);
                if (operation.ExternalDelayRespectMasterCalendar && masterAvailability is not null)
                    windows = IntersectAvailability(windows, masterAvailability);
                return new TimelineWorkingDayDelay(
                    operation.ExternalDelayWorkingDays,
                    operation.ExternalDelayCalendarTimeZoneId!,
                    windows);
            }, StringComparer.Ordinal);
        var dependencyBlockedOperationIds = new HashSet<string>(StringComparer.Ordinal);
        var dependencies = BuildDependencies(
            source.Operations,
            mappingConflicts,
            dependencyBlockedOperationIds);
        var usableOperationIds = usableOperations
            .Select(operation => operation.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        var backlogBlockers = assigned
            .Where(operation => !usableOperationIds.Contains(operation.OperationId)
                || dependencyBlockedOperationIds.Contains(operation.OperationId)
                || operation.ActivePauseReason is not null)
            .ToArray();
        var backlogBlockedOperationIds = assigned
            .Where(operation => backlogBlockers.Any(blocker =>
                blocker.MachineId == operation.MachineId
                && blocker.BacklogPosition <= operation.BacklogPosition))
            .Select(operation => operation.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        var requestedBacklogs = usableOperations
            .Where(operation => !dependencyBlockedOperationIds.Contains(operation.OperationId))
            .Where(operation => !backlogBlockedOperationIds.Contains(operation.OperationId))
            .GroupBy(operation => operation.MachineId!, StringComparer.Ordinal)
            .Select(group => new TimelineMachineBacklog(
                group.Key,
                group.OrderBy(operation => operation.BacklogPosition)
                    .Select(operation => new TimelineOperationInput(
                        operation.OperationId,
                        TimeSpan.FromSeconds(operation.SetupSeconds!.Value),
                        TimeSpan.FromSeconds(operation.CycleSeconds!.Value),
                        TimeSpan.FromSeconds(operation.QaSeconds),
                        TimeSpan.FromSeconds(operation.LoadUnloadSeconds),
                        operation.LoadUnloadRequiresWorker,
                        operation.DayShiftOnly,
                        operation.PriorityWorkFinishDate,
                        operation.PriorityOrderNumber,
                        EarliestForecastStart(operation, source.Operations, forecastCursor),
                        CalculationPlanningMode(operation) == TimelinePlanningMode.Backward
                            ? BackwardFinishCutoff(
                                operation.PriorityWorkFinishDate,
                                horizonEnd,
                                mappingConflicts,
                                operation)
                            : null,
                        CalculationPlanningMode(operation),
                        operation.PlannedQuantity,
                        operation.AutomaticLoading,
                        operation.LoadUnloadEveryNParts,
                        operation.ExternalDelayAfter,
                        externalWorkingDayDelays.GetValueOrDefault(operation.OperationId),
                        operation.ProductionCycleQuantity))
                    .ToArray()))
            .OrderBy(backlog => machinesById.TryGetValue(backlog.MachineId, out var machine)
                ? machine.Number
                : backlog.MachineId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var calculationOperationIds = requestedBacklogs
            .SelectMany(backlog => backlog.Operations)
            .Select(operation => operation.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        var calculationDependencies = dependencies
            .Where(dependency => calculationOperationIds.Contains(dependency.Domain.FromOperationId)
                && calculationOperationIds.Contains(dependency.Domain.ToOperationId))
            .ToArray();
        foreach (var dependency in calculationDependencies)
        {
            logger.LogDebug(
                "Timeline loaded {DependencyType} dependency {DependencyId}: {FromOperationId} -> {ToOperationId}.",
                dependency.Domain.Type, dependency.Domain.DependencyId,
                dependency.Domain.FromOperationId, dependency.Domain.ToOperationId);
        }
        logger.LogInformation(
            "Timeline calculation input contains {CalculationOperationCount} operations in {BacklogCount} Machine backlogs and {DependencyCount} dependencies; {ExcludedCount} assigned operations are represented as blocked waiting rather than calculation nodes. Resource roles: setup={SetupWorkers}, QA={QaWorkers}, regular={RegularWorkers}.",
            calculationOperationIds.Count, requestedBacklogs.Length, calculationDependencies.Length,
            assigned.Length - calculationOperationIds.Count,
            resourceCalendars.Count(value => value.Role == TimelineResourceRole.SetupWorker),
            resourceCalendars.Count(value => value.Role == TimelineResourceRole.QaWorker),
            resourceCalendars.Count(value => value.Role == TimelineResourceRole.RegularWorker));
        var requestedCalculationInput = new TimelineCalculationInput(
            horizonStart,
            horizonEnd,
            requestedBacklogs,
            calendars,
            new TimelineSetupCalendar(setupAvailability),
            source.Downtimes.Select(downtime => new TimelineDowntime(
                downtime.DowntimeId,
                downtime.MachineId,
                downtime.StartsAt,
                downtime.EndsAt,
                downtime.Reason)).ToArray(),
            calculationDependencies.Select(dependency => dependency.Domain).ToArray(),
            resourceCalendars,
            dayShiftCalendars);
        var requiresMissedStartBaseline = forecastCursor > horizonStart
            && requestedBacklogs.SelectMany(backlog => backlog.Operations).Any(operation =>
                operationsById[operation.OperationId].Status == "not_started");
        var backwardOperationIds = requestedBacklogs
            .SelectMany(backlog => backlog.Operations)
            .Where(operation => operation.PlanningMode == TimelinePlanningMode.Backward
                && operationsById[operation.OperationId].Status == "not_started")
            .Select(operation => operation.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        var baselineStopwatch = Stopwatch.StartNew();
        var baselineStarts = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        TimelineCalculationResult? baselineCalculation = null;
        if (requiresMissedStartBaseline || backwardOperationIds.Count > 0)
        {
            var baselineBacklogs = requestedBacklogs.Select(backlog => backlog with
            {
                Operations = backlog.Operations.Select(operation => operation with
                {
                    EarliestStart = operationsById[operation.OperationId].Status == "not_started"
                        ? EarliestForecastStart(operationsById[operation.OperationId], source.Operations, horizonStart)
                        : operation.EarliestStart
                }).ToArray()
            }).ToArray();
            baselineCalculation = engine.Calculate(
                requestedCalculationInput with { MachineBacklogs = baselineBacklogs });
            baselineStarts = baselineCalculation.Operations.ToDictionary(
                    operation => operation.OperationId,
                    operation => operation.StartsAt,
                    StringComparer.Ordinal);
        }
        baselineStopwatch.Stop();

        var backwardMissedStartOperationIds = backwardOperationIds
            .Where(operationId => baselineStarts.TryGetValue(operationId, out var baselineStart)
                && baselineStart < forecastCursor)
            .ToHashSet(StringComparer.Ordinal);
        var backwardUnavailableOperationIds = backwardOperationIds
            .Where(operationId => !baselineStarts.ContainsKey(operationId)
                && baselineCalculation?.Conflicts.Any(conflict =>
                    conflict.Code == "backward_schedule_cannot_fit"
                    && conflict.OperationIds.Contains(operationId, StringComparer.Ordinal)) == true)
            .ToHashSet(StringComparer.Ordinal);
        var backwardFallbackOperationIds = backwardMissedStartOperationIds
            .Concat(backwardUnavailableOperationIds)
            .ToHashSet(StringComparer.Ordinal);
        ExpandLockedBackwardFallback(
            backwardFallbackOperationIds,
            backwardOperationIds,
            calculationDependencies.Select(dependency => dependency.Domain));
        var propagatedBackwardFallbackOperationIds = backwardFallbackOperationIds
            .Where(operationId => !backwardMissedStartOperationIds.Contains(operationId)
                && !backwardUnavailableOperationIds.Contains(operationId))
            .ToHashSet(StringComparer.Ordinal);
        var backlogs = ApplyBackwardFallback(
            requestedBacklogs, backwardFallbackOperationIds);
        var calculationInput = requestedCalculationInput with { MachineBacklogs = backlogs };
        var calculationStopwatch = Stopwatch.StartNew();
        var calculation = forecastCursor == horizonStart
            && backwardFallbackOperationIds.Count == 0
            && baselineCalculation is not null
                ? baselineCalculation
                : engine.Calculate(calculationInput);

        // A missed backward predecessor may make a later backward node infeasible only
        // after the predecessor is reclassified. Re-run deterministically until every
        // newly exposed missed node has joined the same forward fallback. Stored modes
        // and backlog order remain untouched.
        while (true)
        {
            var newlyMissed = calculation.Conflicts
                .Where(conflict => conflict.Code == "backward_schedule_cannot_fit")
                .SelectMany(conflict => conflict.OperationIds)
                .Where(operationId => backwardOperationIds.Contains(operationId)
                    && !backwardFallbackOperationIds.Contains(operationId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (newlyMissed.Length == 0)
            {
                break;
            }

            var priorFallbackOperationIds = backwardFallbackOperationIds
                .ToHashSet(StringComparer.Ordinal);
            backwardFallbackOperationIds.UnionWith(newlyMissed);
            ExpandLockedBackwardFallback(
                backwardFallbackOperationIds,
                backwardOperationIds,
                calculationDependencies.Select(dependency => dependency.Domain));
            propagatedBackwardFallbackOperationIds.UnionWith(
                backwardFallbackOperationIds.Where(operationId =>
                    !priorFallbackOperationIds.Contains(operationId)));
            backlogs = ApplyBackwardFallback(
                requestedBacklogs, backwardFallbackOperationIds);
            calculationInput = requestedCalculationInput with { MachineBacklogs = backlogs };
            calculation = engine.Calculate(calculationInput);
        }
        calculationStopwatch.Stop();
        if (backwardFallbackOperationIds.Count > 0)
        {
            logger.LogWarning(
                "Timeline moved {BackwardFallbackCount} not-started backward assignments to transient forward calculation at cursor {ForecastCursor}; stored planning modes and backlog order were unchanged. Operations: {OperationIds}.",
                backwardFallbackOperationIds.Count,
                forecastCursor,
                string.Join(',', backwardFallbackOperationIds.OrderBy(value => value, StringComparer.Ordinal)));
        }

        var intervalsByMachine = calculation.Machines.ToDictionary(
            machine => machine.MachineId,
            StringComparer.Ordinal);
        var resultsByOperation = calculation.Operations.ToDictionary(
            operation => operation.OperationId, StringComparer.Ordinal);
        var scheduledOperationIds = calculation.Operations
            .Where(operation => operation.SetupIntervals.Count > 0
                || (operation.QaIntervals?.Count ?? 0) > 0
                || (operation.LoadUnloadIntervals?.Count ?? 0) > 0
                || operation.ProductionIntervals.Count > 0
                || operation.ReservedIntervals.Count > 0
                || operation.WaitingIntervals.Count > 0)
            .Select(operation => operation.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        var domainConflicts = calculation.Conflicts.Select(conflict => new TimelineProjectionConflict(
            conflict.ConflictId,
            conflict.Code,
            conflict.Severity.ToString().ToLowerInvariant(),
            conflict.Message,
            conflict.OperationIds,
            conflict.MachineIds)).ToArray();
        var requestedInputsById = requestedBacklogs
            .SelectMany(backlog => backlog.Operations)
            .ToDictionary(operation => operation.OperationId, StringComparer.Ordinal);
        var backwardStartWarnings = backwardMissedStartOperationIds
            .OrderBy(operationId => operationId, StringComparer.Ordinal)
            .Select(operationId =>
            {
                var operation = operationsById[operationId];
                var priorStart = baselineStarts[operationId];
                return Conflict(
                    "backward_start_missed",
                    "attention",
                    $"Batch {operation.BatchNumber} OP{operation.OperationNumber} was moved to the nearest feasible forward slot at or after {forecastCursor:O} because its latest-fit start {priorStart:O} was not reported as started. The stored backward mode and Machine backlog order were not changed.",
                    [operationId],
                    [operation.MachineId!]);
            })
            .ToArray();
        var backwardFallbackWarnings = backwardFallbackOperationIds
            .Where(operationId => !backwardMissedStartOperationIds.Contains(operationId))
            .OrderBy(operationId => operationId, StringComparer.Ordinal)
            .Select(operationId =>
            {
                var operation = operationsById[operationId];
                var reason = backwardUnavailableOperationIds.Contains(operationId)
                    ? "no latest-fit slot was available before its backward cutoff"
                    : propagatedBackwardFallbackOperationIds.Contains(operationId)
                        ? "an upstream Machine-backlog or dependency fallback made its backward slot infeasible"
                        : "its locked-simultaneous group required one shared calculation direction";
                return Conflict(
                    "backward_fallback_required",
                    "attention",
                    $"Batch {operation.BatchNumber} OP{operation.OperationNumber} was recalculated from the nearest feasible slot at or after {forecastCursor:O} because {reason}. The stored backward mode and Machine backlog order were not changed.",
                    [operationId],
                    [operation.MachineId!]);
            })
            .ToArray();
        var backwardDeadlineWarnings = backwardFallbackOperationIds
            .Where(operationId => requestedInputsById[operationId].LatestFinish is { } deadline
                && (!resultsByOperation.TryGetValue(operationId, out var result)
                    || result.FinishesAt > deadline))
            .OrderBy(operationId => operationId, StringComparer.Ordinal)
            .Select(operationId =>
            {
                var operation = operationsById[operationId];
                var deadline = requestedInputsById[operationId].LatestFinish!.Value;
                var finishDetail = resultsByOperation.TryGetValue(operationId, out var result)
                    ? $"the recalculated finish is {result.FinishesAt:O}"
                    : "no future feasible slot exists inside the selected horizon";
                return Conflict(
                    "backward_deadline_missed",
                    "attention",
                    $"Batch {operation.BatchNumber} OP{operation.OperationNumber} can no longer meet its backward cutoff {deadline:O}; {finishDetail}.",
                    [operationId],
                    [operation.MachineId!]);
            })
            .ToArray();
        var backwardBlockedConflicts = backwardFallbackOperationIds
            .Where(operationId => !resultsByOperation.ContainsKey(operationId))
            .OrderBy(operationId => operationId, StringComparer.Ordinal)
            .Select(operationId =>
            {
                var operation = operationsById[operationId];
                return Conflict(
                    "backward_schedule_cannot_fit",
                    "blocking",
                    $"Batch {operation.BatchNumber} OP{operation.OperationNumber} missed its backward start and cannot fit at or after {forecastCursor:O} inside the selected Timeline horizon.",
                    [operationId],
                    [operation.MachineId!]);
            })
            .ToArray();
        var missedStartWarnings = requiresMissedStartBaseline
            ? calculation.Operations
                .Where(result => operationsById.TryGetValue(result.OperationId, out var operation)
                    && operation.Status == "not_started"
                    && !backwardFallbackOperationIds.Contains(result.OperationId)
                    && baselineStarts.TryGetValue(result.OperationId, out var priorStart)
                    && priorStart < forecastCursor)
                .Select(result => Conflict(
                    "missed_forecast_start",
                    "attention",
                    $"Planned start was missed. Batch {operationsById[result.OperationId].BatchNumber} OP{operationsById[result.OperationId].OperationNumber} was recalculated to the next available slot at or after {forecastCursor:O}.",
                    [result.OperationId],
                    [result.MachineId]))
                .ToArray()
            : [];
        var overdueWarnings = calculation.Operations
            .Where(result => operationsById.TryGetValue(result.OperationId, out var operation)
                && operation.Status == "in_progress"
                && result.FinishesAt < forecastCursor)
            .Select(result => Conflict(
                "in_progress_forecast_overdue",
                "attention",
                $"Batch {operationsById[result.OperationId].BatchNumber} OP{operationsById[result.OperationId].OperationNumber} is still in progress after its calculated finish; it was not completed automatically.",
                [result.OperationId],
                [result.MachineId]))
            .ToArray();
        var allConflicts = mappingConflicts.Concat(domainConflicts)
            .Concat(backwardStartWarnings)
            .Concat(backwardFallbackWarnings)
            .Concat(backwardDeadlineWarnings)
            .Concat(backwardBlockedConflicts)
            .Concat(missedStartWarnings).Concat(overdueWarnings)
            .GroupBy(conflict => conflict.ConflictId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        var projectedMachines = source.Machines.Select(machine =>
        {
            var rawIntervals = (intervalsByMachine.TryGetValue(machine.MachineId, out var timeline)
                    ? timeline.Intervals.Select(interval => ProjectInterval(
                        interval, operationsById, machinesById, resultsByOperation, forecastCursor))
                    : [])
                .Concat(ActualHistoryIntervals(machine.MachineId, actualHistory, horizonStart, horizonEnd, forecastCursor))
                .Concat(PauseIntervals(machine.MachineId, source.Operations, horizonStart, horizonEnd))
                .Concat(UnscheduledIntervals(
                    machine.MachineId, assigned, scheduledOperationIds,
                    backlogBlockedOperationIds, resultsByOperation, allConflicts,
                    horizonStart, horizonEnd, forecastCursor));
            return new TimelineProjectionMachine(
                machine.MachineId,
                machine.Number,
                machine.Name,
                NormalizeMachineIntervals(rawIntervals, operationsById),
                MachineCalendarBackgroundWindows(
                    machineCalendarsById[machine.MachineId].Availability,
                    horizonStart,
                    horizonEnd));
        }).ToArray();
        var resourceWaitIntervals = projectedMachines
            .SelectMany(machine => machine.Intervals)
            .Where(IsResourceWait)
            .ToArray();
        projectedMachines = NormalizeGlobalAssignmentIdentity(
            projectedMachines,
            source.Operations,
            allConflicts);
        var readinessByOperation = new Dictionary<string, ProductionReadinessResult>(
            StringComparer.Ordinal);
        foreach (var operationId in projectedMachines
                     .SelectMany(machine => machine.Intervals)
                     .Where(interval => interval.OperationStatus == "not_started"
                         && interval.OperationId is not null)
                     .Select(interval => interval.OperationId!)
                     .Distinct(StringComparer.Ordinal))
        {
            if (readinessRepository is null) break;
            var readiness = await readinessRepository.ReadAsync(operationId, cancellationToken);
            if (readiness.IsManaged) readinessByOperation[operationId] = readiness;
        }
        projectedMachines = projectedMachines.Select(machine => machine with
        {
            Intervals = machine.Intervals.Select(interval =>
                interval.OperationId is not null
                && readinessByOperation.TryGetValue(interval.OperationId, out var readiness)
                    ? interval with
                    {
                        OverallReadinessState = readiness.OverallState,
                        IsReadyForProduction = readiness.IsReadyForProduction,
                        ReadinessSummary = readiness.Summary
                    }
                    : interval).ToArray()
        }).ToArray();
        var batches = source.Operations
            .GroupBy(operation => operation.BatchId, StringComparer.Ordinal)
            .Select(group => new TimelineProjectionBatch(
                group.Key,
                group.First().BatchNumber,
                group.First().PartNumber,
                group.Where(operation => operation.PriorityWorkFinishDate.HasValue)
                    .Select(operation => operation.PriorityWorkFinishDate)
                    .Min()))
            .OrderBy(batch => batch.PartNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(batch => batch.BatchNumber, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var runCards = productionRuns is null ? [] : await productionRuns.ReadAsync(cancellationToken);
        var runIntervals = new List<TimelineProductionRunProjection>();
        foreach (var machineGroup in runCards.Where(value => value.MachineId is not null)
                     .GroupBy(value => value.MachineId!, StringComparer.Ordinal))
        {
            var cursor = forecastCursor;
            foreach (var card in machineGroup.OrderBy(value => value.BacklogPosition))
            {
                var starts = cursor;
                var ends = starts.AddSeconds(card.RemainingDurationSeconds);
                runIntervals.Add(new(card.ProductionRunId, machineGroup.Key, starts, ends,
                    card.Programs.Select(program => new TimelineProductionRunProgramCompletion(
                        program.ProductionRunProgramId, starts.AddSeconds(program.ForecastCompletionOffsetSeconds),
                        program.Outputs.Select(output => output.ProductionRunOutputId).ToArray())).ToArray()));
                cursor = ends;
            }
        }
        var projection = new TimelineProjection(
            source.ReadAt,
            horizonStart,
            horizonEnd,
            batches,
            projectedMachines,
            dependencies.Select(dependency => dependency.Projection).ToArray(),
            allConflicts,
            options.TimeZoneId,
            options.DayShiftStartsAtLocal,
            options.DayShiftEndsAtLocal,
            runIntervals);
        total.Stop();
        logger.LogInformation(
            "Timeline performance: total {TotalMilliseconds} ms; source read {SourceReadMilliseconds} ms; engine {EngineMilliseconds} ms; baseline engine {BaselineMilliseconds} ms; backward fallbacks {BackwardFallbackCount}; scheduled {ScheduledOperationCount}; projected intervals {IntervalCount}; conflicts {ConflictCount}.",
            total.ElapsedMilliseconds, sourceRead.ElapsedMilliseconds, calculationStopwatch.ElapsedMilliseconds,
            baselineStopwatch.ElapsedMilliseconds, backwardFallbackOperationIds.Count,
            calculation.Operations.Count,
            projectedMachines.Sum(machine => machine.Intervals.Count), projection.Conflicts.Count);
        foreach (var result in calculation.Operations)
        {
            var sourceOperation = operationsById.GetValueOrDefault(result.OperationId);
            logger.LogDebug(
                "Timeline assignment {MachineAssignmentId} calculated: operation {OperationId}, Machine {MachineId}, planning mode {PlanningMode}, backlog order {BacklogPosition}, calculated start {CalculatedStart}, calculated end {CalculatedEnd}, status {Status}; predecessor constraints were applied before placement.",
                sourceOperation?.MachineAssignmentId, result.OperationId, result.MachineId,
                sourceOperation?.PlanningMode, sourceOperation?.BacklogPosition,
                result.StartsAt, result.FinishesAt, sourceOperation?.Status);
        }
        foreach (var operation in assigned.Where(operation => !scheduledOperationIds.Contains(operation.OperationId)))
        {
            logger.LogDebug(
                "Timeline skipped calculation for assigned operation {OperationId} on Machine {MachineId}; status {Status}; represented as waiting. Conflicts: {ConflictCodes}.",
                operation.OperationId, operation.MachineId, operation.Status,
                string.Join(',', allConflicts.Where(conflict => conflict.OperationIds.Contains(operation.OperationId))
                    .Select(conflict => conflict.Code)));
        }
        await LogProjectionEventsAsync(projection, resourceWaitIntervals, cancellationToken);
        return projection;
    }

    private async Task LogProjectionEventsAsync(
        TimelineProjection projection,
        IReadOnlyList<TimelineProjectionInterval> resourceWaitIntervals,
        CancellationToken token)
    {
        var day = projection.ReadAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var conflict in projection.Conflicts)
            await eventLog.AppendAsync(new(
                "timeline_conflict_detected", projection.ReadAt, "system",
                new Dictionary<string,string> {
                    ["conflictId"]=conflict.ConflictId,
                    ["operationIds"]=string.Join(',',conflict.OperationIds),
                    ["machineIds"]=string.Join(',',conflict.MachineIds) },
                conflict.Code, conflict.Message, null, new { conflict.Severity },
                $"timeline-conflict:{day}:{conflict.ConflictId}"), token);

        foreach (var interval in resourceWaitIntervals)
            await eventLog.AppendAsync(new(
                "resource_wait_detected", projection.ReadAt, "system",
                new Dictionary<string,string> {
                    ["batchOperationId"]=interval.OperationId!,["machineId"]=interval.MachineId },
                "resource_unavailable_or_contended", interval.Detail, null,
                new { interval.StartsAt,interval.EndsAt },
                $"resource-wait:{day}:{interval.OperationId}:{interval.MachineId}:{interval.Detail}"), token);
    }

    private static bool IsResourceWait(TimelineProjectionInterval interval) =>
        interval.Type == "waiting"
        && interval.OperationId is not null
        && interval.Detail is not null
        && (interval.Detail.Contains("worker", StringComparison.OrdinalIgnoreCase)
            || interval.Detail.StartsWith("resource", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<TimelineWindow> DefaultSetupAvailability(
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        ICollection<TimelineProjectionConflict> conflicts)
    {
        conflicts.Add(Conflict(
            "setup_calendar_defaulted",
            "attention",
            "No separate setup calendar is configured; setup uses each assigned Machine's availability.",
            [],
            []));
        return [new TimelineWindow(horizonStart, horizonEnd)];
    }

    private static IReadOnlyList<TimelineProjectionNonWorkingWindow> MachineCalendarBackgroundWindows(
        IReadOnlyList<TimelineWindow> availability,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd)
    {
        var usableWindows = availability
            .Where(window => window.EndsAt > window.StartsAt
                && window.EndsAt > horizonStart
                && window.StartsAt < horizonEnd)
            .Select(window => new TimelineWindow(
                window.StartsAt < horizonStart ? horizonStart : window.StartsAt,
                window.EndsAt > horizonEnd ? horizonEnd : window.EndsAt))
            .OrderBy(window => window.StartsAt)
            .ThenBy(window => window.EndsAt)
            .ToArray();
        var windows = new List<TimelineProjectionNonWorkingWindow>();
        var cursor = horizonStart;
        foreach (var window in usableWindows)
        {
            if (window.StartsAt > cursor)
            {
                windows.Add(MachineCalendarBackgroundWindow(cursor, window.StartsAt));
            }

            if (window.EndsAt > cursor)
            {
                cursor = window.EndsAt;
            }
        }

        if (cursor < horizonEnd)
        {
            windows.Add(MachineCalendarBackgroundWindow(cursor, horizonEnd));
        }

        return windows;
    }

    private static TimelineProjectionNonWorkingWindow MachineCalendarBackgroundWindow(
        DateTimeOffset startsAt,
        DateTimeOffset endsAt) => new(
            startsAt,
            endsAt,
            "Machine calendar: non-working time.");

    private static IReadOnlyList<TimelineWindow> ReadAvailability(
        string json,
        string? timeZoneId,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        string label,
        ICollection<TimelineProjectionConflict> conflicts,
        IReadOnlyList<string> machineIds,
        IReadOnlyList<TimelineSourceHoliday> holidays)
    {
        try
        {
            var document = JsonSerializer.Deserialize<AvailabilityDocument>(json, JsonOptions);
            if (document?.Availability is { Count: > 0 })
            {
                return document.Availability.Select(window => new TimelineWindow(
                    window.StartsAt,
                    window.EndsAt)).ToArray();
            }

            if (document?.WeeklySchedule is not null && timeZoneId is not null)
            {
                return ExpandWeeklySchedule(
                    document.WeeklySchedule,
                    timeZoneId,
                    horizonStart,
                    horizonEnd,
                    document.UseIsraeliHolidays ? holidays : []);
            }

            if (document is null)
            {
                conflicts.Add(Conflict(
                    "calendar_configuration_missing",
                    "blocking",
                    $"{label} has no availability definition.",
                    [],
                    machineIds));
                return [];
            }

            conflicts.Add(Conflict(
                "calendar_configuration_missing",
                "blocking",
                $"{label} has no supported availability definition.",
                [],
                machineIds));
            return [];
        }
        catch (Exception exception) when (exception is
            JsonException or InvalidOperationException or FormatException
            or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            conflicts.Add(Conflict(
                "calendar_configuration_invalid",
                "blocking",
                $"{label} contains invalid availability JSON.",
                [],
                machineIds));
            return [];
        }
    }

    private static IReadOnlyList<TimelineWindow> ExpandWeeklySchedule(
        WeeklySchedule schedule,
        string timeZoneId,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        IReadOnlyList<TimelineSourceHoliday> holidays)
    {
        var scheduleWindows = schedule.Windows is { Count: > 0 }
            ? schedule.Windows
            : string.IsNullOrWhiteSpace(schedule.ShiftStartsAtLocal) || string.IsNullOrWhiteSpace(schedule.ShiftEndsAtLocal)
                ? []
                : [new WeeklyWindow(schedule.ShiftStartsAtLocal, schedule.ShiftEndsAtLocal)];
        if (schedule.Workdays is null || schedule.Workdays.Count == 0 || scheduleWindows.Count == 0)
        {
            throw new InvalidOperationException("Weekly schedule is incomplete.");
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var shifts = ReadLocalWindows(scheduleWindows, "Weekly working windows", allowEmpty: false);
        var breaks = AlignBreaksToOvernightShift(
            shifts,
            ReadLocalWindows(schedule.BreakWindows ?? [], "Weekly break windows", allowEmpty: true));
        ValidateBreaks(shifts, breaks);
        var exceptions = (schedule.Exceptions ?? []).ToDictionary(
            value => DateOnly.ParseExact(value.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        var holidaysByDate = holidays.ToDictionary(value => value.Date);

        var days = schedule.Workdays.Select(ParseDayOfWeek).ToHashSet();
        var localStartDate = TimeZoneInfo.ConvertTime(horizonStart, timeZone).Date.AddDays(-1);
        var localEndDate = TimeZoneInfo.ConvertTime(horizonEnd, timeZone).Date.AddDays(1);
        var windows = new List<TimelineWindow>();
        for (var date = localStartDate; date <= localEndDate; date = date.AddDays(1))
        {
            holidaysByDate.TryGetValue(DateOnly.FromDateTime(date), out var holiday);
            (int Start, int End)[] dateShifts;
            (int Start, int End)[] dateBreaks;
            if (exceptions.TryGetValue(DateOnly.FromDateTime(date), out var exception))
            {
                dateShifts = ReadLocalWindows(exception.Windows ?? [], "Exception working windows", allowEmpty: true);
                dateBreaks = AlignBreaksToOvernightShift(dateShifts,
                    ReadLocalWindows(exception.BreakWindows ?? [], "Exception break windows", allowEmpty: true));
                ValidateBreaks(dateShifts, dateBreaks);
            }
            else if (holiday is not null
                     && holiday.Status == "non_working")
            {
                continue;
            }
            else if (holiday is not null && holiday.Status == "partial_working")
            {
                if (holiday.StartsAtLocal is null || holiday.EndsAtLocal is null)
                    throw new InvalidOperationException("Partial-working holiday has no working-time range.");
                dateShifts = ReadLocalWindows([new WeeklyWindow(holiday.StartsAtLocal, holiday.EndsAtLocal)], "Holiday working window", allowEmpty: false);
                dateBreaks = [];
            }
            else if (days.Contains(date.DayOfWeek))
            {
                dateShifts = shifts;
                dateBreaks = breaks;
            }
            else
            {
                continue;
            }

            foreach (var shift in SubtractBreaks(dateShifts, dateBreaks))
            {
                var localStart = DateTime.SpecifyKind(date.AddMinutes(shift.Start), DateTimeKind.Unspecified);
                var localEnd = DateTime.SpecifyKind(date.AddMinutes(shift.End), DateTimeKind.Unspecified);
                if (timeZone.IsInvalidTime(localStart) || timeZone.IsInvalidTime(localEnd)) continue;
                var startsAt = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone));
                var endsAt = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localEnd, timeZone));
                if (endsAt > horizonStart && startsAt < horizonEnd) windows.Add(new TimelineWindow(startsAt, endsAt));
            }
        }

        return windows;
    }

    private static IReadOnlyList<TimelineWindow> IntersectAvailability(
        IReadOnlyList<TimelineWindow> resource,
        IReadOnlyList<TimelineWindow> master)
    {
        var result = new List<TimelineWindow>();
        foreach (var left in resource)
        foreach (var right in master)
        {
            var start = left.StartsAt > right.StartsAt ? left.StartsAt : right.StartsAt;
            var end = left.EndsAt < right.EndsAt ? left.EndsAt : right.EndsAt;
            if (end > start) result.Add(new TimelineWindow(start, end));
        }
        return result.OrderBy(window => window.StartsAt).ThenBy(window => window.EndsAt).ToArray();
    }

    private static (int Start, int End)[] ReadLocalWindows(
        IReadOnlyList<WeeklyWindow> windows,
        string label,
        bool allowEmpty)
    {
        if (!allowEmpty && windows.Count == 0)
            throw new InvalidOperationException($"{label} are required.");
        var result = windows.Select(window =>
        {
            var start = ParseLocalMinutes(window.StartsAtLocal, false);
            var end = ParseLocalMinutes(window.EndsAtLocal, true);
            if (end == start) throw new InvalidOperationException($"{label} contain an empty window.");
            if (end < start) end += 24 * 60;
            return (Start: start, End: end);
        }).OrderBy(window => window.Start).ToArray();
        if (result.Length > 1 && result.Any(window => window.End > 24 * 60))
            throw new InvalidOperationException($"{label} combine an overnight window with other windows.");
        if (result.Skip(1).Zip(result, (next, prior) => next.Start < prior.End).Any(overlap => overlap))
            throw new InvalidOperationException($"{label} overlap.");
        return result;
    }

    private static (int Start, int End)[] AlignBreaksToOvernightShift(
        IReadOnlyList<(int Start, int End)> shifts,
        IReadOnlyList<(int Start, int End)> breaks)
    {
        var overnight = shifts.SingleOrDefault(shift => shift.End > 24 * 60);
        if (overnight == default) return breaks.ToArray();
        return breaks.Select(pause => pause.Start < overnight.Start
            ? (pause.Start + 24 * 60, pause.End + 24 * 60)
            : pause).ToArray();
    }

    private static void ValidateBreaks(
        IReadOnlyList<(int Start, int End)> shifts,
        IReadOnlyList<(int Start, int End)> breaks)
    {
        if (breaks.Any(pause => !shifts.Any(shift => shift.Start <= pause.Start && shift.End >= pause.End)))
            throw new InvalidOperationException("Every break must be contained in a working window.");
    }

    private static IEnumerable<(int Start, int End)> SubtractBreaks(
        IReadOnlyList<(int Start, int End)> shifts,
        IReadOnlyList<(int Start, int End)> breaks)
    {
        foreach (var shift in shifts)
        {
            var cursor = shift.Start;
            foreach (var pause in breaks.Where(value => value.End > shift.Start && value.Start < shift.End))
            {
                if (pause.Start > cursor) yield return (cursor, pause.Start);
                cursor = Math.Max(cursor, pause.End);
            }
            if (cursor < shift.End) yield return (cursor, shift.End);
        }

    }

    private static int ParseLocalMinutes(string value, bool allowEndOfDay)
    {
        if (allowEndOfDay && value == "24:00")
        {
            return 24 * 60;
        }

        var parsed = TimeOnly.ParseExact(value, "HH:mm", CultureInfo.InvariantCulture);
        return parsed.Hour * 60 + parsed.Minute;
    }

    private static DayOfWeek ParseDayOfWeek(string value) =>
        Enum.TryParse<DayOfWeek>(value, true, out var result)
            ? result
            : throw new FormatException($"Unknown workday '{value}'.");

    private static IReadOnlyList<MappedDependency> BuildDependencies(
        IReadOnlyList<TimelineSourceOperation> operations,
        ICollection<TimelineProjectionConflict> conflicts,
        ISet<string> blockedOperationIds)
    {
        var result = new List<MappedDependency>();
        foreach (var batch in operations.GroupBy(operation => operation.BatchId, StringComparer.Ordinal))
        {
            var bySource = batch.ToDictionary(
                operation => operation.SourceCaseOperationId,
                StringComparer.Ordinal);
            foreach (var operation in batch.OrderBy(value => value.OperationNumber))
            {
                if (operation.Status == "completed")
                {
                    continue;
                }

                if (operation.DependencyType is "sequential" or "parallel_capable")
                {
                    if (operation.PredecessorSourceCaseOperationId is null
                        || !bySource.TryGetValue(
                            operation.PredecessorSourceCaseOperationId,
                            out var predecessor))
                    {
                        conflicts.Add(Conflict(
                            "dependency_snapshot_missing",
                            "blocking",
                            $"Batch {operation.BatchNumber} OP{operation.OperationNumber} has no matching dependency snapshot.",
                            [operation.OperationId],
                            operation.MachineId is null ? [] : [operation.MachineId]));
                        blockedOperationIds.Add(operation.OperationId);
                        continue;
                    }

                    if (predecessor.Status == "completed")
                    {
                        result.Add(MapDependency(
                            $"{batch.Key}:{operation.SourceCaseOperationId}",
                            operation.DependencyType == "sequential"
                                ? TimelineDependencyType.Sequential
                                : TimelineDependencyType.ParallelCapable,
                            predecessor,
                            operation,
                            null));
                        if (operation.DependencyType == "sequential" && !predecessor.ActualEnd.HasValue)
                        {
                            conflicts.Add(Conflict(
                                "actual_finish_missing",
                                "blocking",
                                $"Batch {operation.BatchNumber} OP{operation.OperationNumber} cannot use its completed predecessor because the predecessor has no authoritative actual finish.",
                                [predecessor.OperationId, operation.OperationId],
                                operation.MachineId is null ? [] : [operation.MachineId]));
                            blockedOperationIds.Add(operation.OperationId);
                        }
                        continue;
                    }

                    if (predecessor.MachineId is null || !predecessor.BacklogPosition.HasValue)
                    {
                        conflicts.Add(Conflict(
                            "dependency_predecessor_unassigned",
                            operation.DependencyType == "sequential" ? "blocking" : "attention",
                            $"Batch {operation.BatchNumber} OP{operation.OperationNumber} waits for OP{predecessor.OperationNumber}, which is not assigned to a Machine.",
                            [predecessor.OperationId, operation.OperationId],
                            operation.MachineId is null ? [] : [operation.MachineId]));
                        if (operation.DependencyType == "sequential")
                        {
                            blockedOperationIds.Add(operation.OperationId);
                        }

                        continue;
                    }

                    var type = operation.DependencyType == "sequential"
                        ? TimelineDependencyType.Sequential
                        : TimelineDependencyType.ParallelCapable;
                    result.Add(MapDependency(
                        $"{batch.Key}:{operation.SourceCaseOperationId}",
                        type,
                        predecessor,
                        operation,
                        null));
                }
            }

            foreach (var group in batch
                         .Where(operation => operation.DependencyType == "locked_simultaneous"
                             && !string.IsNullOrWhiteSpace(operation.SimultaneousGroupKey))
                         .GroupBy(operation => operation.SimultaneousGroupKey!, StringComparer.Ordinal))
            {
                var members = group.OrderBy(operation => operation.OperationNumber).ToArray();
                var activeMembers = members
                    .Where(operation => operation.Status != "completed")
                    .ToArray();
                if (activeMembers.Length == 0)
                {
                    continue;
                }

                if (activeMembers.Length != members.Length)
                {
                    conflicts.Add(Conflict(
                        "simultaneous_group_partially_completed",
                        "blocking",
                        $"Batch {members[0].BatchNumber} simultaneous group '{group.Key}' has a mix of completed and unfinished operations.",
                            activeMembers.Select(value => value.OperationId).ToArray(),
                            activeMembers.Where(value => value.MachineId is not null)
                             .Select(value => value.MachineId!).ToArray()));
                    foreach (var member in activeMembers)
                    {
                        blockedOperationIds.Add(member.OperationId);
                    }
                    continue;
                }

                if (members.Length < 2)
                {
                    conflicts.Add(Conflict(
                        "simultaneous_snapshot_incomplete",
                        "blocking",
                            $"Batch {members[0].BatchNumber} simultaneous group '{group.Key}' has fewer than two members.",
                            [members[0].OperationId],
                            members[0].MachineId is null ? [] : [members[0].MachineId!]));
                    blockedOperationIds.Add(members[0].OperationId);
                    continue;
                }

                for (var index = 1; index < members.Length; index++)
                {
                    result.Add(MapDependency(
                        $"{batch.Key}:{group.Key}:{index}",
                        TimelineDependencyType.LockedSimultaneous,
                        members[0],
                        members[index],
                        group.Key));
                }
            }
        }

        return result;
    }

    private static MappedDependency MapDependency(
        string dependencyId,
        TimelineDependencyType type,
        TimelineSourceOperation from,
        TimelineSourceOperation to,
        string? groupKey) => new(
        new TimelineDependency(
            dependencyId,
            type,
            from.OperationId,
            to.OperationId,
            groupKey),
        new TimelineProjectionDependency(
            dependencyId,
            to.BatchId,
            to.BatchNumber,
            to.PartNumber,
            DependencyToken(type),
            from.OperationId,
            from.OperationNumber,
            from.OperationName,
            to.OperationId,
            to.OperationNumber,
            to.OperationName,
            groupKey));

    private static TimelineProjectionInterval ProjectInterval(
        TimelineInterval interval,
        IReadOnlyDictionary<string, TimelineSourceOperation> operations,
        IReadOnlyDictionary<string, TimelineSourceMachine> machines,
        IReadOnlyDictionary<string, TimelineOperationResult> results,
        DateTimeOffset forecastCursor)
    {
        var operation = interval.OperationId is not null
            && operations.TryGetValue(interval.OperationId, out var found)
                ? found
                : null;
        results.TryGetValue(interval.OperationId ?? string.Empty, out var result);
        return new TimelineProjectionInterval(
            interval.Type.ToString().ToLowerInvariant(),
            interval.MachineId,
            interval.OperationId,
            operation?.BatchId,
            operation?.BatchNumber,
            operation?.PartNumber,
            operation?.OperationNumber,
            operation?.OperationName,
            interval.StartsAt,
            interval.EndsAt,
            interval.Type == TimelineIntervalType.Waiting
                ? interval.Detail?.StartsWith("resource:", StringComparison.Ordinal) == true
                    ? interval.Detail["resource:".Length..]
                    : DependencyWaitingDetail(interval.Detail, operations, machines)
                : interval.Detail,
            interval.OperationId is null ? null : "forecast",
            operation?.Status,
            result?.StartsAt,
            result?.FinishesAt,
            operation?.ActualStart,
            operation?.ActualEnd,
            operation?.MachineAssignmentId,
            operation?.PlanningMode,
            operation?.PriorityWorkFinishDate);
    }

    private DateTimeOffset BackwardFinishCutoff(
        DateOnly? workFinishDate,
        DateTimeOffset horizonEnd,
        ICollection<TimelineProjectionConflict> conflicts,
        TimelineSourceOperation operation)
    {
        if (!workFinishDate.HasValue)
        {
            conflicts.Add(Conflict(
                "backward_deadline_missing",
                "attention",
                $"Batch {operation.BatchNumber} OP{operation.OperationNumber} has no allocated Order Work Finish Date; the selected Timeline horizon end is used as its visual cutoff.",
                [operation.OperationId],
                [operation.MachineId!]));
            return horizonEnd;
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
        var nextMidnight = DateTime.SpecifyKind(
            workFinishDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue),
            DateTimeKind.Unspecified);
        var target = new DateTimeOffset(nextMidnight, timeZone.GetUtcOffset(nextMidnight))
            .ToUniversalTime();
        if (target > horizonEnd)
        {
            conflicts.Add(Conflict(
                "backward_deadline_outside_horizon",
                "attention",
                $"Batch {operation.BatchNumber} OP{operation.OperationNumber} is due {workFinishDate:yyyy-MM-dd}, after the selected Timeline horizon; the horizon end is used as its temporary visual cutoff.",
                [operation.OperationId],
                [operation.MachineId!]));
            return horizonEnd;
        }
        return target;
    }

    private static TimelinePlanningMode PlanningMode(TimelineSourceOperation operation) =>
        operation.PlanningMode?.ToLowerInvariant() switch
        {
            "forward" => TimelinePlanningMode.Forward,
            "backward" => TimelinePlanningMode.Backward,
            _ => TimelinePlanningMode.Manual
        };

    private static TimelinePlanningMode CalculationPlanningMode(TimelineSourceOperation operation) =>
        operation.Status == "in_progress"
            && PlanningMode(operation) == TimelinePlanningMode.Backward
                ? TimelinePlanningMode.Manual
                : PlanningMode(operation);

    private static TimelineMachineBacklog[] ApplyBackwardFallback(
        IReadOnlyList<TimelineMachineBacklog> requestedBacklogs,
        IReadOnlySet<string> fallbackOperationIds) => requestedBacklogs
        .Select(backlog => backlog with
        {
            Operations = backlog.Operations.Select(operation =>
                    fallbackOperationIds.Contains(operation.OperationId)
                        ? operation with { PlanningMode = TimelinePlanningMode.Forward }
                        : operation)
                .ToArray()
        })
        .ToArray();

    private static void ExpandLockedBackwardFallback(
        ISet<string> fallbackOperationIds,
        IReadOnlySet<string> backwardOperationIds,
        IEnumerable<TimelineDependency> dependencies)
    {
        var locked = dependencies
            .Where(dependency => dependency.Type == TimelineDependencyType.LockedSimultaneous)
            .ToArray();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var dependency in locked)
            {
                if (!backwardOperationIds.Contains(dependency.FromOperationId)
                    || !backwardOperationIds.Contains(dependency.ToOperationId)
                    || (!fallbackOperationIds.Contains(dependency.FromOperationId)
                        && !fallbackOperationIds.Contains(dependency.ToOperationId)))
                {
                    continue;
                }

                changed |= fallbackOperationIds.Add(dependency.FromOperationId);
                changed |= fallbackOperationIds.Add(dependency.ToOperationId);
            }
        }
    }

    private static DateTimeOffset EarliestForecastStart(
        TimelineSourceOperation operation,
        IReadOnlyList<TimelineSourceOperation> operations,
        DateTimeOffset forecastCursor)
    {
        var movedAfterStarting = operation.Status == "in_progress"
            && operation.ActualStart.HasValue
            && operation.ActualMachineId is not null
            && operation.MachineId is not null
            && !string.Equals(
                operation.ActualMachineId, operation.MachineId, StringComparison.Ordinal);
        var earliest = operation.Status == "in_progress" && operation.ActualStart.HasValue
            ? movedAfterStarting
                ? operation.MovePauseEndedAt ?? forecastCursor
                : operation.ActualStart.Value
            : forecastCursor;
        if (operation.DependencyType != "sequential"
            || operation.PredecessorSourceCaseOperationId is null)
        {
            return earliest;
        }

        var completedFinish = operations.FirstOrDefault(candidate =>
            candidate.BatchId == operation.BatchId
            && candidate.SourceCaseOperationId == operation.PredecessorSourceCaseOperationId
            && candidate.Status == "completed")?.ActualEnd;
        return completedFinish.HasValue && completedFinish.Value > earliest
            ? completedFinish.Value
            : earliest;
    }

    private static TimelineProjectionInterval[] ActualHistoryIntervals(
        string machineId,
        IReadOnlyList<TimelineSourceOperation> operations,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        DateTimeOffset asOf) => operations
        .Where(operation => operation.ActualMachineId == machineId
            && operation.ActualStart.HasValue
            && operation.ActualStart < horizonEnd
            && (operation.ActualEnd ?? asOf) > horizonStart)
        .Select(operation =>
        {
            var startsAt = operation.ActualStart!.Value < horizonStart
                ? horizonStart : operation.ActualStart.Value;
            var rawEnd = operation.ActualEnd
                ?? (operation.Status == "suspended" && operation.PauseStartedAt.HasValue
                    ? operation.PauseStartedAt.Value
                    : operation.Status == "in_progress"
                      && operation.MachineId is not null
                      && !string.Equals(
                          operation.ActualMachineId, operation.MachineId, StringComparison.Ordinal)
                      && operation.MovePauseStartedAt.HasValue
                        ? operation.MovePauseStartedAt.Value
                    : asOf > operation.ActualStart.Value ? asOf : operation.ActualStart.Value);
            var endsAt = rawEnd > horizonEnd ? horizonEnd : rawEnd;
            return new TimelineProjectionInterval(
                "production", machineId, operation.OperationId, operation.BatchId,
                operation.BatchNumber, operation.PartNumber, operation.OperationNumber,
                operation.OperationName, startsAt, endsAt,
                operation.Status switch
                {
                    "completed" => "Recorded actual work",
                    "suspended" => "Actual work elapsed before the active pause",
                    _ => "Actual work elapsed; finish remains forecast"
                },
                "actual", operation.Status, null, null,
                operation.ActualStart, operation.ActualEnd,
                operation.MachineAssignmentId, operation.PlanningMode,
                operation.PriorityWorkFinishDate);
        })
        .Where(interval => interval.EndsAt > interval.StartsAt)
        .ToArray();

    private static TimelineProjectionInterval[] PauseIntervals(
        string machineId, IReadOnlyList<TimelineSourceOperation> operations,
        DateTimeOffset horizonStart, DateTimeOffset horizonEnd) => operations
        .Where(operation => operation.MachineId == machineId
            && operation.ActivePauseReason is not null
            && operation.PauseStartedAt < horizonEnd)
        .Select(operation =>
        {
            var startsAt = operation.PauseStartedAt!.Value;
            // A suspended assignment may be explicitly moved. The pause began on
            // the recorded actual Machine, so it must not be backdated onto the
            // current target Machine before that assignment mutation occurred.
            if (operation.ActualMachineId is not null
                && !string.Equals(operation.ActualMachineId, operation.MachineId, StringComparison.Ordinal)
                && operation.MachineMovedAt is { } machineMovedAt
                && machineMovedAt > startsAt)
            {
                startsAt = machineMovedAt;
            }
            if (startsAt < horizonStart)
            {
                startsAt = horizonStart;
            }
            return new TimelineProjectionInterval(
                "waiting", machineId, operation.OperationId, operation.BatchId,
                operation.BatchNumber, operation.PartNumber, operation.OperationNumber,
                operation.OperationName, startsAt, horizonEnd,
                $"Operation paused by {operation.PausedBy}: {operation.ActivePauseReason}",
                MachineAssignmentId: operation.MachineAssignmentId,
                PlanningMode: operation.PlanningMode,
                WorkFinishDate: operation.PriorityWorkFinishDate);
        })
        .Where(interval => interval.EndsAt > interval.StartsAt)
        .ToArray();

    private static TimelineProjectionInterval[] UnscheduledIntervals(
        string machineId,
        IReadOnlyList<TimelineSourceOperation> assigned,
        IReadOnlySet<string> scheduledOperationIds,
        IReadOnlySet<string> backlogBlockedOperationIds,
        IReadOnlyDictionary<string, TimelineOperationResult> scheduledResults,
        IReadOnlyList<TimelineProjectionConflict> conflicts,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        DateTimeOffset forecastCursor) => assigned
        .Where(operation => operation.MachineId == machineId
            && !scheduledOperationIds.Contains(operation.OperationId)
            && operation.ActivePauseReason is null)
        .Select(operation =>
        {
            var precedingScheduledFinish = assigned
                .Where(candidate => string.Equals(
                        candidate.MachineId, operation.MachineId, StringComparison.Ordinal)
                    && candidate.BacklogPosition < operation.BacklogPosition
                    && scheduledResults.ContainsKey(candidate.OperationId))
                .Select(candidate => scheduledResults[candidate.OperationId].FinishesAt)
                .DefaultIfEmpty(horizonStart)
                .Max();
            var blockedCursor = operation.Status == "not_started"
                ? forecastCursor
                : horizonStart;
            var startsAt = precedingScheduledFinish > blockedCursor
                ? precedingScheduledFinish
                : blockedCursor;
            if (startsAt > horizonEnd)
            {
                startsAt = horizonEnd;
            }
            var ownConflict = conflicts.FirstOrDefault(conflict =>
                conflict.OperationIds.Contains(operation.OperationId, StringComparer.Ordinal));
            var detail = ownConflict?.Message
                ?? (backlogBlockedOperationIds.Contains(operation.OperationId)
                    ? "Waiting because an earlier operation blocks the stored Machine backlog order."
                    : "Waiting because the operation cannot be placed inside the current Timeline horizon.");
            return new TimelineProjectionInterval(
                "waiting", machineId, operation.OperationId, operation.BatchId,
                operation.BatchNumber, operation.PartNumber, operation.OperationNumber,
                operation.OperationName, startsAt, horizonEnd, detail,
                TimingKind: "blocked",
                OperationStatus: operation.Status,
                MachineAssignmentId: operation.MachineAssignmentId,
                PlanningMode: operation.PlanningMode,
                WorkFinishDate: operation.PriorityWorkFinishDate);
        })
        .ToArray();

    private IReadOnlyList<TimelineProjectionInterval> NormalizeMachineIntervals(
        IEnumerable<TimelineProjectionInterval> rawIntervals,
        IReadOnlyDictionary<string, TimelineSourceOperation> operations)
    {
        var materialized = rawIntervals.ToArray();
        // Waiting and downtime are real Machine-capacity annotations, not a second
        // copy of the assignment. Keep them visually distinct but deliberately
        // remove the assignment identity. Every assignment is represented by one
        // canonical operation block below.
        var calculatedAssignmentIds = materialized
            .Where(interval => interval.OperationId is not null
                && interval.Type is not ("waiting" or "downtime" or "idle")
                && !string.IsNullOrWhiteSpace(interval.MachineAssignmentId))
            .Select(interval => interval.MachineAssignmentId!)
            .ToHashSet(StringComparer.Ordinal);
        var activePauseAssignmentIdsWithCanonicalWork = materialized
            .Where(interval => interval.OperationId is not null
                && interval.Type is not ("waiting" or "downtime" or "idle")
                && !string.IsNullOrWhiteSpace(interval.MachineAssignmentId))
            .Where(interval => operations.TryGetValue(interval.OperationId!, out var operation)
                && operation.ActivePauseReason is not null
                && string.Equals(operation.MachineId, interval.MachineId, StringComparison.Ordinal))
            .Select(interval => interval.MachineAssignmentId!)
            .ToHashSet(StringComparer.Ordinal);
        var machineOnly = materialized
            .Where(interval => interval.OperationId is null
                || interval.Type is "waiting" or "downtime" or "idle"
                && (string.IsNullOrWhiteSpace(interval.MachineAssignmentId)
                    || calculatedAssignmentIds.Contains(interval.MachineAssignmentId)))
            .Where(interval => string.IsNullOrWhiteSpace(interval.MachineAssignmentId)
                || !activePauseAssignmentIdsWithCanonicalWork.Contains(
                    interval.MachineAssignmentId))
            .Select(interval => interval with { MachineAssignmentId = null })
            .OrderBy(interval => interval.StartsAt)
            .ThenBy(interval => interval.Type, StringComparer.Ordinal)
            .ToArray();
        var operationBlocks = materialized
            .Where(interval => interval.OperationId is not null
                && interval.Type is not ("waiting" or "downtime" or "idle"))
            .GroupBy(interval =>
                string.IsNullOrWhiteSpace(interval.MachineAssignmentId)
                    ? $"operation:{interval.OperationId}"
                    : $"assignment:{interval.MachineAssignmentId}",
                StringComparer.Ordinal)
            .Select(group =>
            {
                var workValues = group.OrderBy(interval => interval.StartsAt)
                    .ThenBy(interval => interval.EndsAt).ToArray();
                var operationId = workValues.Select(interval => interval.OperationId)
                    .First(value => value is not null)!;
                var source = operations.GetValueOrDefault(operationId);
                var pauseValues = source?.ActivePauseReason is not null
                    && string.Equals(source.MachineId, workValues[0].MachineId, StringComparison.Ordinal)
                    ? materialized.Where(interval => interval.Type == "waiting"
                            && interval.OperationId == operationId
                            && interval.MachineAssignmentId == source.MachineAssignmentId)
                        .OrderBy(interval => interval.StartsAt)
                        .ThenBy(interval => interval.EndsAt)
                        .ToArray()
                    : [];
                var values = workValues.Concat(pauseValues)
                    .OrderBy(interval => interval.StartsAt)
                    .ThenBy(interval => interval.EndsAt)
                    .ToArray();
                var actual = workValues.FirstOrDefault(interval => interval.TimingKind == "actual");
                var representative = actual ?? workValues[0];
                var startsAt = values.Min(interval => interval.StartsAt);
                var endsAt = values.Max(interval => interval.EndsAt);
                // Once an operation has recorded actual production, a forecast
                // setup that overlaps that actual segment is stale and must not
                // be painted after it. Keep QA/load-unload phases: they are real
                // machine occupancy and removing them creates apparent pauses
                // between the blue production runs.
                var activeActual = actual is not null
                    && string.Equals(source?.Status, "in_progress", StringComparison.Ordinal);
                var phases = values
                    .Where(interval => interval.EndsAt > interval.StartsAt)
                    .Where(interval => !activeActual
                        || interval.Type != "setup"
                        || interval.EndsAt <= actual!.StartsAt)
                    .Select(interval => new TimelineProjectionPhase(
                        interval.Type,
                        interval.StartsAt,
                        interval.EndsAt,
                        interval.Detail))
                    .Distinct()
                    .ToArray();
                var phaseDescription = phases.Select(phase =>
                    $"{PhaseLabel(phase.Type)} {phase.StartsAt:O} to {phase.EndsAt:O}"
                    + (string.IsNullOrWhiteSpace(phase.Detail) ? string.Empty : $" ({phase.Detail})"))
                    .ToArray();
                return representative with
                {
                    StartsAt = startsAt,
                    EndsAt = endsAt,
                    Type = "operation",
                    TimingKind = pauseValues.Length > 0
                        ? "hold"
                        : actual is not null ? "actual" : representative.TimingKind,
                    OperationStatus = source?.Status ?? representative.OperationStatus,
                    Detail = phaseDescription.Length == 0
                        ? representative.Detail
                        : $"Phases: {string.Join("; ", phaseDescription)}",
                    ForecastStart = workValues.Select(interval => interval.ForecastStart).FirstOrDefault(value => value.HasValue),
                    ForecastEnd = workValues.Select(interval => interval.ForecastEnd).LastOrDefault(value => value.HasValue),
                    ActualStart = source?.ActualStart ?? values.Select(interval => interval.ActualStart).FirstOrDefault(value => value.HasValue),
                    ActualEnd = source?.ActualEnd ?? values.Select(interval => interval.ActualEnd).LastOrDefault(value => value.HasValue),
                    MachineAssignmentId = string.Equals(
                            source?.MachineId, representative.MachineId, StringComparison.Ordinal)
                        ? source?.MachineAssignmentId ?? representative.MachineAssignmentId
                        : representative.MachineAssignmentId,
                    PlanningMode = string.Equals(
                            source?.MachineId, representative.MachineId, StringComparison.Ordinal)
                        ? source?.PlanningMode ?? representative.PlanningMode
                        : representative.PlanningMode,
                    WorkFinishDate = source?.PriorityWorkFinishDate ?? representative.WorkFinishDate,
                    Phases = phases
                };
            })
            .OrderBy(interval => interval.StartsAt)
            .ThenBy(interval => interval.OperationId, StringComparer.Ordinal)
            .ToArray();
        var blockedAssignmentBlocks = materialized
            .Where(interval => interval.OperationId is not null
                && interval.Type == "waiting"
                && !string.IsNullOrWhiteSpace(interval.MachineAssignmentId))
            .Where(interval => operationBlocks.All(block =>
                !string.Equals(block.MachineAssignmentId, interval.MachineAssignmentId,
                    StringComparison.Ordinal)))
            .GroupBy(interval => interval.MachineAssignmentId!, StringComparer.Ordinal)
            .Select(group =>
            {
                var values = group.OrderBy(interval => interval.StartsAt).ToArray();
                var first = values[0];
                var source = first.OperationId is null
                    ? null
                    : operations.GetValueOrDefault(first.OperationId);
                var isActivePause = source?.ActivePauseReason is not null;
                return first with
                {
                    StartsAt = values.Min(interval => interval.StartsAt),
                    EndsAt = values.Max(interval => interval.EndsAt),
                    Type = isActivePause ? "operation" : first.Type,
                    TimingKind = isActivePause ? "hold" : first.TimingKind,
                    OperationStatus = source?.Status ?? first.OperationStatus,
                    ActualStart = source?.ActualStart ?? first.ActualStart,
                    ActualEnd = source?.ActualEnd ?? first.ActualEnd,
                    Phases = isActivePause
                        ? values.Select(interval => new TimelineProjectionPhase(
                                "waiting", interval.StartsAt, interval.EndsAt, interval.Detail))
                            .Distinct()
                            .ToArray()
                        : first.Phases,
                    Detail = string.Join("; ", values.Select(interval => interval.Detail)
                        .Where(detail => !string.IsNullOrWhiteSpace(detail))
                        .Distinct(StringComparer.Ordinal))
                };
            })
            .ToArray();
        var normalized = machineOnly.Concat(operationBlocks).Concat(blockedAssignmentBlocks)
            .OrderBy(interval => interval.StartsAt)
            .ThenBy(interval => interval.OperationId is null ? 0 : 1)
            .ThenBy(interval => interval.Type, StringComparer.Ordinal)
            .ToArray();
        var duplicate = normalized
            .Where(interval => !string.IsNullOrWhiteSpace(interval.MachineAssignmentId))
            .GroupBy(interval => interval.MachineAssignmentId!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToArray();
        if (duplicate.Length > 0)
        {
            foreach (var group in duplicate)
            {
                var duplicateBlock = group.First();
                logger.LogError(
                    DuplicateTimelineBlockLogTemplate,
                    group.Key,
                    duplicateBlock.OperationId,
                    duplicateBlock.MachineId);
            }
        }
        logger.LogDebug(
            "Timeline duplicate block detection result: {DuplicateCount} duplicate assignment blocks.",
            duplicate.Length);

        return normalized;
    }

    private TimelineProjectionMachine[] NormalizeGlobalAssignmentIdentity(
        IReadOnlyList<TimelineProjectionMachine> machines,
        IReadOnlyList<TimelineSourceOperation> operations,
        ICollection<TimelineProjectionConflict> conflicts)
    {
        var operationById = operations.ToDictionary(
            operation => operation.OperationId,
            StringComparer.Ordinal);
        var slots = machines.SelectMany((machine, machineIndex) =>
                machine.Intervals.Select((interval, intervalIndex) => (
                    MachineIndex: machineIndex,
                    IntervalIndex: intervalIndex,
                    Machine: machine,
                    Interval: interval)))
            .ToArray();
        var replacements = slots.ToDictionary(
            slot => (slot.MachineIndex, slot.IntervalIndex),
            slot => (TimelineProjectionInterval?)slot.Interval);

        foreach (var operationGroup in slots
                     .Where(slot => slot.Interval.OperationId is not null)
                     .GroupBy(slot => slot.Interval.OperationId!, StringComparer.Ordinal))
        {
            if (!operationById.TryGetValue(operationGroup.Key, out var operation))
            {
                continue;
            }

            var identified = operationGroup.ToArray();
            var isActiveAssigned = operation.Status != "completed"
                && operation.MachineId is not null
                && !string.IsNullOrWhiteSpace(operation.MachineAssignmentId);
            var canonical = isActiveAssigned
                ? identified
                    .OrderByDescending(slot => string.Equals(
                        slot.Machine.MachineId, operation.MachineId, StringComparison.Ordinal))
                    .ThenByDescending(slot => string.Equals(
                        slot.Interval.MachineAssignmentId,
                        operation.MachineAssignmentId,
                        StringComparison.Ordinal))
                    .ThenByDescending(slot => slot.Interval.Type == "operation")
                    .ThenByDescending(slot => slot.Interval.TimingKind != "actual")
                    .FirstOrDefault()
                : identified
                    .Where(slot => slot.Interval.TimingKind == "actual"
                        || slot.Interval.Type == "actual_history")
                    .OrderByDescending(slot => string.Equals(
                        slot.Machine.MachineId,
                        operation.ActualMachineId,
                        StringComparison.Ordinal))
                    .ThenBy(slot => slot.Interval.StartsAt)
                    .FirstOrDefault();

            if (canonical.Interval is null)
            {
                foreach (var capacity in identified.Where(slot => IsCapacityAnnotation(slot.Interval)))
                {
                    replacements[(capacity.MachineIndex, capacity.IntervalIndex)] =
                        AnonymizeCapacityInterval(capacity.Interval);
                }
                continue;
            }

            var secondary = identified.Where(slot =>
                    slot.MachineIndex != canonical.MachineIndex
                    || slot.IntervalIndex != canonical.IntervalIndex)
                .ToArray();
            var canonicalInterval = FoldOperationFacts(
                canonical.Interval,
                canonical.Machine,
                secondary,
                operation,
                isActiveAssigned);
            if (!isActiveAssigned)
            {
                canonicalInterval = canonicalInterval with
                {
                    Type = "actual_history",
                    TimingKind = "actual",
                    MachineAssignmentId = null,
                    PlanningMode = null,
                    ForecastStart = null,
                    ForecastEnd = null
                };
            }
            replacements[(canonical.MachineIndex, canonical.IntervalIndex)] = canonicalInterval;

            foreach (var extra in secondary)
            {
                if (IsCapacityAnnotation(extra.Interval))
                {
                    replacements[(extra.MachineIndex, extra.IntervalIndex)] =
                        AnonymizeCapacityInterval(extra.Interval);
                    continue;
                }

                var isExpectedPriorActual = isActiveAssigned
                    && extra.Interval.TimingKind == "actual"
                    && !string.Equals(
                        extra.Machine.MachineId, operation.MachineId, StringComparison.Ordinal);
                if (isExpectedPriorActual)
                {
                    replacements[(extra.MachineIndex, extra.IntervalIndex)] =
                        AnonymizeCapacityInterval(extra.Interval with
                        {
                            Type = "actual_history",
                            Detail = "Recorded actual Machine occupancy; operation details are attached to its current block."
                        });
                    continue;
                }
                else
                {
                    logger.LogError(
                        DuplicateTimelineBlockLogTemplate,
                        extra.Interval.MachineAssignmentId,
                        operation.OperationId,
                        extra.Machine.MachineId);
                }
                replacements[(extra.MachineIndex, extra.IntervalIndex)] = null;
            }
        }

        var normalized = machines.Select((machine, machineIndex) => machine with
        {
            Intervals = machine.Intervals.Select((_, intervalIndex) =>
                    replacements[(machineIndex, intervalIndex)])
                .Where(interval => interval is not null)
                .Select(interval => interval!)
                .OrderBy(interval => interval.StartsAt)
                .ThenBy(interval => interval.OperationId is null ? 0 : 1)
                .ThenBy(interval => interval.Type, StringComparer.Ordinal)
                .ToArray()
        }).ToArray();

        normalized = ReconcileMachineOperationOverlaps(normalized, operations, conflicts);

        foreach (var operation in operations.Where(operation =>
                     operation.Status != "completed"
                     && operation.MachineId is not null
                     && !string.IsNullOrWhiteSpace(operation.MachineAssignmentId)))
        {
            var publicAssignmentBlocks = normalized.SelectMany(machine => machine.Intervals)
                .Where(interval => string.Equals(
                    interval.MachineAssignmentId,
                    operation.MachineAssignmentId,
                    StringComparison.Ordinal))
                .ToArray();
            if (publicAssignmentBlocks.Length > 1)
            {
                logger.LogError(
                    DuplicateTimelineBlockLogTemplate,
                    operation.MachineAssignmentId,
                    operation.OperationId,
                    operation.MachineId);
            }
            else if (publicAssignmentBlocks.Length == 0)
            {
                logger.LogError(
                    "TIMELINE_OPERATION_IDENTITY_INVARIANT operationId={OperationId}, assignmentId={MachineAssignmentId}; expected one public assignment block but produced none.",
                    operation.OperationId,
                    operation.MachineAssignmentId);
            }
        }
        logger.LogDebug(
            "Timeline global operation identity normalization retained {OperationBlockCount} identified operation/history blocks.",
            normalized.Sum(machine => machine.Intervals.Count(interval => interval.OperationId is not null)));
        return normalized;
    }

    internal TimelineProjectionMachine[] ReconcileMachineOperationOverlaps(
        IReadOnlyList<TimelineProjectionMachine> machines,
        IReadOnlyList<TimelineSourceOperation> operations,
        ICollection<TimelineProjectionConflict> conflicts)
    {
        var operationsById = operations.ToDictionary(
            operation => operation.OperationId,
            StringComparer.Ordinal);
        var unresolvedOperationIds = new HashSet<string>(StringComparer.Ordinal);
        var reconciled = machines.Select(machine =>
        {
            var intervals = machine.Intervals.ToArray();
            var primary = intervals
                .Select((interval, index) => (Interval: interval, Index: index))
                .Where(slot => slot.Interval.Type == "operation"
                    && slot.Interval.OperationId is not null)
                .ToArray();
            var authoritative = primary
                .Where(slot => IsAuthoritativeMachineOccupancy(slot.Interval))
                .OrderBy(slot => slot.Interval.StartsAt)
                .ThenBy(slot => slot.Interval.OperationId, StringComparer.Ordinal)
                .ToArray();

            for (var index = 0; index < authoritative.Length; index++)
            {
                for (var otherIndex = index + 1; otherIndex < authoritative.Length; otherIndex++)
                {
                    var left = authoritative[index].Interval;
                    var right = authoritative[otherIndex].Interval;
                    if (!Overlaps(left, right))
                    {
                        continue;
                    }

                    AddConflictOnce(conflicts, Conflict(
                        "machine_operation_overlap",
                        "blocking",
                        $"Machine {machine.Number} has overlapping authoritative actual/hold occupancy for operations '{left.OperationId}' and '{right.OperationId}'. The Timeline retained the recorded occupancy and did not alter the backlog.",
                        [left.OperationId!, right.OperationId!],
                        [machine.MachineId]));
                    logger.LogError(
                        "TIMELINE_MACHINE_OPERATION_OVERLAP Machine={MachineId}, authoritativeOperation={LeftOperationId} {LeftStart}..{LeftEnd}, authoritativeOperation={RightOperationId} {RightStart}..{RightEnd}; recorded occupancy was retained.",
                        machine.MachineId, left.OperationId, left.StartsAt, left.EndsAt,
                        right.OperationId, right.StartsAt, right.EndsAt);
                }
            }

            var acceptedForecasts = new List<(TimelineProjectionInterval Interval, int Index)>();
            TimelineProjectionInterval? blockedBacklogBarrier = intervals
                .Where(interval => interval.OperationId is not null
                    && interval.Type == "waiting"
                    && interval.TimingKind == "blocked")
                .OrderBy(interval => BacklogPosition(interval, operationsById))
                .ThenBy(interval => interval.OperationId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (blockedBacklogBarrier?.OperationId is not null)
            {
                unresolvedOperationIds.Add(blockedBacklogBarrier.OperationId);
            }
            foreach (var forecast in primary
                         .Where(slot => !IsAuthoritativeMachineOccupancy(slot.Interval))
                         .OrderBy(slot => BacklogPosition(slot.Interval, operationsById))
                         .ThenBy(slot => slot.Interval.StartsAt)
                         .ThenBy(slot => slot.Interval.OperationId, StringComparer.Ordinal))
            {
                if (blockedBacklogBarrier is not null
                    && BacklogPosition(forecast.Interval, operationsById)
                        > BacklogPosition(blockedBacklogBarrier, operationsById))
                {
                    var detail = $"Blocked because earlier backlog operation {blockedBacklogBarrier.OperationId} could not be placed; stored Machine backlog order was preserved.";
                    intervals[forecast.Index] = DemoteOverlappingForecast(forecast.Interval, detail);
                    AddConflictOnce(conflicts, Conflict(
                        "dependency_unresolved",
                        "blocking",
                        $"Machine {machine.Number} operation '{forecast.Interval.OperationId}' could not remain calculated because earlier backlog operation '{blockedBacklogBarrier.OperationId}' is blocked. The Timeline did not leapfrog the stored backlog.",
                        [blockedBacklogBarrier.OperationId!, forecast.Interval.OperationId!],
                        [machine.MachineId]));
                    logger.LogWarning(
                        "TIMELINE_BACKLOG_BARRIER Machine={MachineId}, blockedOperation={BlockedOperationId}, laterOperation={LaterOperationId}; later forecast was demoted and stored order was preserved.",
                        machine.MachineId, blockedBacklogBarrier.OperationId,
                        forecast.Interval.OperationId);
                    unresolvedOperationIds.Add(forecast.Interval.OperationId!);
                    continue;
                }

                var actualOverlap = authoritative.FirstOrDefault(slot =>
                    Overlaps(slot.Interval, forecast.Interval));
                if (actualOverlap.Interval is not null)
                {
                    intervals[forecast.Index] = DemoteOverlappingForecast(
                        forecast.Interval,
                        $"Blocked because recorded actual/hold occupancy for operation {actualOverlap.Interval.OperationId} already uses this Machine time.");
                    AddConflictOnce(conflicts, Conflict(
                        "actual_backlog_overlap",
                        "blocking",
                        $"Machine {machine.Number} forecast operation '{forecast.Interval.OperationId}' overlaps authoritative actual/hold operation '{actualOverlap.Interval.OperationId}'. The forecast was shown as blocked waiting; stored backlog order was not changed.",
                        [forecast.Interval.OperationId!, actualOverlap.Interval.OperationId!],
                        [machine.MachineId]));
                    logger.LogWarning(
                        "TIMELINE_ACTUAL_BACKLOG_OVERLAP Machine={MachineId}, forecastOperation={ForecastOperationId} {ForecastStart}..{ForecastEnd}, authoritativeOperation={ActualOperationId} {ActualStart}..{ActualEnd}; forecast was demoted to blocked waiting.",
                        machine.MachineId, forecast.Interval.OperationId,
                        forecast.Interval.StartsAt, forecast.Interval.EndsAt,
                        actualOverlap.Interval.OperationId,
                        actualOverlap.Interval.StartsAt, actualOverlap.Interval.EndsAt);
                    blockedBacklogBarrier = EarlierBacklogBarrier(
                        blockedBacklogBarrier, forecast.Interval, operationsById);
                    unresolvedOperationIds.Add(forecast.Interval.OperationId!);
                    continue;
                }

                var forecastOverlap = acceptedForecasts.FirstOrDefault(slot =>
                    Overlaps(slot.Interval, forecast.Interval));
                if (forecastOverlap.Interval is not null)
                {
                    intervals[forecast.Index] = DemoteOverlappingForecast(
                        forecast.Interval,
                        $"Blocked because earlier backlog operation {forecastOverlap.Interval.OperationId} already uses this Machine time.");
                    AddConflictOnce(conflicts, Conflict(
                        "machine_operation_overlap",
                        "blocking",
                        $"Machine {machine.Number} calculated overlapping forecasts for operations '{forecastOverlap.Interval.OperationId}' and '{forecast.Interval.OperationId}'. The later stored-backlog operation was shown as blocked waiting; backlog order was not changed.",
                        [forecastOverlap.Interval.OperationId!, forecast.Interval.OperationId!],
                        [machine.MachineId]));
                    logger.LogWarning(
                        "TIMELINE_MACHINE_OPERATION_OVERLAP Machine={MachineId}, retainedForecast={RetainedOperationId} {RetainedStart}..{RetainedEnd}, blockedForecast={BlockedOperationId} {BlockedStart}..{BlockedEnd}; later backlog forecast was demoted.",
                        machine.MachineId, forecastOverlap.Interval.OperationId,
                        forecastOverlap.Interval.StartsAt, forecastOverlap.Interval.EndsAt,
                        forecast.Interval.OperationId,
                        forecast.Interval.StartsAt, forecast.Interval.EndsAt);
                    blockedBacklogBarrier = EarlierBacklogBarrier(
                        blockedBacklogBarrier, forecast.Interval, operationsById);
                    unresolvedOperationIds.Add(forecast.Interval.OperationId!);
                    continue;
                }

                acceptedForecasts.Add(forecast);
            }

            if (blockedBacklogBarrier is not null)
            {
                foreach (var actual in authoritative.Where(slot =>
                             BacklogPosition(slot.Interval, operationsById)
                             > BacklogPosition(blockedBacklogBarrier, operationsById)))
                {
                    AddConflictOnce(conflicts, Conflict(
                        "dependency_unresolved",
                        "blocking",
                        $"Machine {machine.Number} authoritative operation '{actual.Interval.OperationId}' occurs after blocked backlog operation '{blockedBacklogBarrier.OperationId}'. Recorded occupancy was retained, but the stored backlog is unresolved.",
                        [blockedBacklogBarrier.OperationId!, actual.Interval.OperationId!],
                        [machine.MachineId]));
                    unresolvedOperationIds.Add(actual.Interval.OperationId!);
                    logger.LogError(
                        "TIMELINE_BACKLOG_BARRIER Machine={MachineId}, blockedOperation={BlockedOperationId}, authoritativeLaterOperation={LaterOperationId}; recorded occupancy was retained and the backlog conflict was reported.",
                        machine.MachineId, blockedBacklogBarrier.OperationId,
                        actual.Interval.OperationId);
                }
            }

            return machine with
            {
                Intervals = intervals
                    .OrderBy(interval => interval.StartsAt)
                    .ThenBy(interval => interval.OperationId is null ? 0 : 1)
                    .ThenBy(interval => interval.Type, StringComparer.Ordinal)
                    .ToArray()
            };
        }).ToArray();

        return PropagateBlockedDependencies(
            reconciled, operations, conflicts, unresolvedOperationIds);
    }

    private TimelineProjectionMachine[] PropagateBlockedDependencies(
        IReadOnlyList<TimelineProjectionMachine> machines,
        IReadOnlyList<TimelineSourceOperation> operations,
        ICollection<TimelineProjectionConflict> conflicts,
        ISet<string> unresolvedOperationIds)
    {
        var operationsBySource = operations
            .GroupBy(operation => operation.BatchId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(
                    operation => operation.SourceCaseOperationId,
                    StringComparer.Ordinal),
                StringComparer.Ordinal);
        var slots = machines.SelectMany((machine, machineIndex) =>
                machine.Intervals.Select((interval, intervalIndex) => (
                    MachineIndex: machineIndex,
                    IntervalIndex: intervalIndex,
                    Machine: machine,
                    Interval: interval)))
            .Where(slot => slot.Interval.OperationId is not null)
            .ToDictionary(
                slot => slot.Interval.OperationId!,
                slot => slot,
                StringComparer.Ordinal);
        var replacements = slots.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Interval,
            StringComparer.Ordinal);
        var blocked = replacements.Values
            .Where(interval => interval.Type == "waiting"
                && interval.TimingKind == "blocked")
            .Select(interval => interval.OperationId!)
            .ToHashSet(StringComparer.Ordinal);
        blocked.UnionWith(unresolvedOperationIds);

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var machineBacklog in operations
                         .Where(operation => operation.MachineId is not null
                             && operation.BacklogPosition.HasValue)
                         .GroupBy(operation => operation.MachineId!, StringComparer.Ordinal))
            {
                var ordered = machineBacklog
                    .OrderBy(operation => operation.BacklogPosition)
                    .ThenBy(operation => operation.OperationId, StringComparer.Ordinal)
                    .ToArray();
                var barrierIndex = Array.FindIndex(ordered,
                    operation => blocked.Contains(operation.OperationId));
                if (barrierIndex < 0)
                {
                    continue;
                }

                var barrier = ordered[barrierIndex];
                foreach (var later in ordered.Skip(barrierIndex + 1)
                             .Where(operation => !blocked.Contains(operation.OperationId)))
                {
                    if (!replacements.TryGetValue(later.OperationId, out var laterInterval))
                    {
                        continue;
                    }

                    if (IsAuthoritativeMachineOccupancy(laterInterval))
                    {
                        AddConflictOnce(conflicts, Conflict(
                            "dependency_unresolved",
                            "blocking",
                            $"Machine backlog operation '{later.OperationId}' has authoritative actual/hold occupancy after blocked operation '{barrier.OperationId}'. Recorded occupancy was retained.",
                            [barrier.OperationId, later.OperationId],
                            [machineBacklog.Key]));
                        logger.LogError(
                            "TIMELINE_BACKLOG_BARRIER Machine={MachineId}, blockedOperation={BlockedOperationId}, authoritativeLaterOperation={LaterOperationId}; recorded occupancy was retained.",
                            machineBacklog.Key, barrier.OperationId, later.OperationId);
                    }
                    else
                    {
                        var detail = $"Blocked because earlier backlog operation {barrier.OperationId} could not be calculated.";
                        replacements[later.OperationId] =
                            DemoteOverlappingForecast(laterInterval, detail);
                        AddConflictOnce(conflicts, Conflict(
                            "dependency_unresolved",
                            "blocking",
                            $"Machine backlog operation '{later.OperationId}' could not remain calculated because earlier operation '{barrier.OperationId}' is blocked. Stored backlog order was preserved.",
                            [barrier.OperationId, later.OperationId],
                            [machineBacklog.Key]));
                        logger.LogWarning(
                            "TIMELINE_BACKLOG_BARRIER Machine={MachineId}, blockedOperation={BlockedOperationId}, blockedLaterOperation={LaterOperationId}; later forecast was demoted.",
                            machineBacklog.Key, barrier.OperationId, later.OperationId);
                    }
                    blocked.Add(later.OperationId);
                    changed = true;
                }
            }

            foreach (var child in operations.Where(operation =>
                         operation.DependencyType == "sequential"
                         && operation.PredecessorSourceCaseOperationId is not null))
            {
                if (!operationsBySource.TryGetValue(child.BatchId, out var batch)
                    || !batch.TryGetValue(child.PredecessorSourceCaseOperationId!, out var parent)
                    || !blocked.Contains(parent.OperationId)
                    || blocked.Contains(child.OperationId)
                    || !replacements.TryGetValue(child.OperationId, out var childInterval))
                {
                    continue;
                }

                var machineId = childInterval.MachineId;
                if (IsAuthoritativeMachineOccupancy(childInterval))
                {
                    AddConflictOnce(conflicts, Conflict(
                        "dependency_unresolved",
                        "blocking",
                        $"Operation '{child.OperationId}' has authoritative actual/hold occupancy even though sequential predecessor '{parent.OperationId}' is blocked. Recorded occupancy was retained.",
                        [parent.OperationId, child.OperationId],
                        [machineId]));
                    logger.LogError(
                        "TIMELINE_DEPENDENCY_BARRIER parentOperation={ParentOperationId}, authoritativeChildOperation={ChildOperationId}, Machine={MachineId}; recorded child occupancy was retained.",
                        parent.OperationId, child.OperationId, machineId);
                    blocked.Add(child.OperationId);
                    changed = true;
                    continue;
                }

                var detail = $"Blocked because sequential predecessor {parent.OperationId} could not be calculated.";
                replacements[child.OperationId] = DemoteOverlappingForecast(childInterval, detail);
                blocked.Add(child.OperationId);
                changed = true;
                AddConflictOnce(conflicts, Conflict(
                    "dependency_unresolved",
                    "blocking",
                    $"Operation '{child.OperationId}' could not remain calculated because sequential predecessor '{parent.OperationId}' is blocked. Dependency order was preserved.",
                    [parent.OperationId, child.OperationId],
                    [machineId]));
                logger.LogWarning(
                    "TIMELINE_DEPENDENCY_BARRIER parentOperation={ParentOperationId}, blockedChildOperation={ChildOperationId}, Machine={MachineId}; child forecast was demoted.",
                    parent.OperationId, child.OperationId, machineId);
            }

            foreach (var group in operations
                         .Where(operation => operation.DependencyType == "locked_simultaneous"
                             && !string.IsNullOrWhiteSpace(operation.SimultaneousGroupKey))
                         .GroupBy(operation => (operation.BatchId, operation.SimultaneousGroupKey!)))
            {
                var blockedMember = group.FirstOrDefault(member => blocked.Contains(member.OperationId));
                if (blockedMember is null)
                {
                    continue;
                }

                foreach (var member in group.Where(member => !blocked.Contains(member.OperationId)))
                {
                    if (!replacements.TryGetValue(member.OperationId, out var memberInterval))
                    {
                        continue;
                    }

                    var machineId = memberInterval.MachineId;
                    if (IsAuthoritativeMachineOccupancy(memberInterval))
                    {
                        AddConflictOnce(conflicts, Conflict(
                            "dependency_unresolved",
                            "blocking",
                            $"Locked-simultaneous operation '{member.OperationId}' has authoritative actual/hold occupancy while group member '{blockedMember.OperationId}' is blocked. Recorded occupancy was retained.",
                            [blockedMember.OperationId, member.OperationId],
                            [machineId]));
                        logger.LogError(
                            "TIMELINE_LOCKED_GROUP_BARRIER blockedOperation={BlockedOperationId}, authoritativeMemberOperation={MemberOperationId}, Machine={MachineId}; recorded occupancy was retained.",
                            blockedMember.OperationId, member.OperationId, machineId);
                    }
                    else
                    {
                        var detail = $"Blocked because locked-simultaneous group member {blockedMember.OperationId} could not be calculated.";
                        replacements[member.OperationId] =
                            DemoteOverlappingForecast(memberInterval, detail);
                        AddConflictOnce(conflicts, Conflict(
                            "dependency_unresolved",
                            "blocking",
                            $"Locked-simultaneous operation '{member.OperationId}' could not remain calculated because group member '{blockedMember.OperationId}' is blocked.",
                            [blockedMember.OperationId, member.OperationId],
                            [machineId]));
                        logger.LogWarning(
                            "TIMELINE_LOCKED_GROUP_BARRIER blockedOperation={BlockedOperationId}, blockedMemberOperation={MemberOperationId}, Machine={MachineId}; group member forecast was demoted.",
                            blockedMember.OperationId, member.OperationId, machineId);
                    }
                    blocked.Add(member.OperationId);
                    changed = true;
                }
            }
        }

        return machines.Select((machine, machineIndex) => machine with
        {
            Intervals = machine.Intervals.Select((interval, intervalIndex) =>
                    interval.OperationId is not null
                    && replacements.TryGetValue(interval.OperationId, out var replacement)
                        ? replacement
                        : interval)
                .OrderBy(interval => interval.StartsAt)
                .ThenBy(interval => interval.OperationId is null ? 0 : 1)
                .ThenBy(interval => interval.Type, StringComparer.Ordinal)
                .ToArray()
        }).ToArray();
    }

    private static TimelineProjectionInterval EarlierBacklogBarrier(
        TimelineProjectionInterval? current,
        TimelineProjectionInterval candidate,
        IReadOnlyDictionary<string, TimelineSourceOperation> operations)
    {
        if (current is null)
        {
            return candidate;
        }
        return BacklogPosition(candidate, operations) < BacklogPosition(current, operations)
            ? candidate
            : current;
    }

    private static bool IsAuthoritativeMachineOccupancy(TimelineProjectionInterval interval) =>
        interval.TimingKind is "actual" or "hold";

    private static bool Overlaps(
        TimelineProjectionInterval left,
        TimelineProjectionInterval right) =>
        left.StartsAt < right.EndsAt && right.StartsAt < left.EndsAt;

    private static int BacklogPosition(
        TimelineProjectionInterval interval,
        IReadOnlyDictionary<string, TimelineSourceOperation> operations) =>
        interval.OperationId is not null
        && operations.TryGetValue(interval.OperationId, out var operation)
            ? operation.BacklogPosition ?? int.MaxValue
            : int.MaxValue;

    private static TimelineProjectionInterval DemoteOverlappingForecast(
        TimelineProjectionInterval interval,
        string detail) => interval with
    {
        Type = "waiting",
        TimingKind = "blocked",
        Detail = detail,
        Phases =
        [
            new TimelineProjectionPhase("waiting", interval.StartsAt, interval.EndsAt, detail)
        ]
    };

    private static void AddConflictOnce(
        ICollection<TimelineProjectionConflict> conflicts,
        TimelineProjectionConflict conflict)
    {
        if (conflicts.Any(existing => string.Equals(
                existing.ConflictId, conflict.ConflictId, StringComparison.Ordinal)))
        {
            return;
        }
        conflicts.Add(conflict);
    }

    private static TimelineProjectionInterval FoldOperationFacts(
        TimelineProjectionInterval canonical,
        TimelineProjectionMachine canonicalMachine,
        IReadOnlyList<(int MachineIndex, int IntervalIndex, TimelineProjectionMachine Machine, TimelineProjectionInterval Interval)> secondary,
        TimelineSourceOperation operation,
        bool isActiveAssigned)
    {
        var phases = (canonical.Phases ?? [])
            .Concat(secondary.SelectMany(slot => FoldedPhases(slot, operation, isActiveAssigned)))
            .Distinct()
            .OrderBy(phase => phase.StartsAt)
            .ThenBy(phase => phase.EndsAt)
            .ToArray();
        var foldedDetails = secondary
            .Select(slot => FoldedFactDescription(slot, operation, isActiveAssigned))
            .Where(detail => !string.IsNullOrWhiteSpace(detail))
            .Prepend(canonical.Detail)
            .Where(detail => !string.IsNullOrWhiteSpace(detail))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return canonical with
        {
            // The current calculated/hold envelope belongs to the current Machine.
            // Prior-Machine facts are tooltip phases and must never backdate it.
            StartsAt = canonical.StartsAt,
            EndsAt = canonical.EndsAt,
            MachineId = canonicalMachine.MachineId,
            Detail = foldedDetails.Length == 0
                ? canonical.Detail
                : string.Join("; ", foldedDetails),
            Phases = phases
        };
    }

    private static IEnumerable<TimelineProjectionPhase> FoldedPhases(
        (int MachineIndex, int IntervalIndex, TimelineProjectionMachine Machine, TimelineProjectionInterval Interval) slot,
        TimelineSourceOperation operation,
        bool isActiveAssigned)
    {
        var isPriorActual = isActiveAssigned
            && slot.Interval.TimingKind == "actual"
            && !string.Equals(slot.Machine.MachineId, operation.MachineId, StringComparison.Ordinal);
        if (isPriorActual)
        {
            return
            [
                new TimelineProjectionPhase(
                    "actual_history",
                    slot.Interval.StartsAt,
                    slot.Interval.EndsAt,
                    $"Recorded actual work on {slot.Machine.Number} — {slot.Machine.Name}")
            ];
        }

        if (slot.Interval.Phases is { Count: > 0 })
        {
            return slot.Interval.Phases;
        }
        return
        [
            new TimelineProjectionPhase(
                slot.Interval.Type,
                slot.Interval.StartsAt,
                slot.Interval.EndsAt,
                slot.Interval.Detail)
        ];
    }

    private static string? FoldedFactDescription(
        (int MachineIndex, int IntervalIndex, TimelineProjectionMachine Machine, TimelineProjectionInterval Interval) slot,
        TimelineSourceOperation operation,
        bool isActiveAssigned)
    {
        var isPriorActual = isActiveAssigned
            && slot.Interval.TimingKind == "actual"
            && !string.Equals(slot.Machine.MachineId, operation.MachineId, StringComparison.Ordinal);
        if (isPriorActual)
        {
            return $"Actual history on {slot.Machine.Number} — {slot.Machine.Name}: "
                + $"{slot.Interval.StartsAt:O} to {slot.Interval.EndsAt:O}"
                + (string.IsNullOrWhiteSpace(slot.Interval.Detail)
                    ? string.Empty
                    : $" ({slot.Interval.Detail})");
        }
        return slot.Interval.Detail;
    }

    private static bool IsCapacityAnnotation(TimelineProjectionInterval interval) =>
        interval.Type is "waiting" or "downtime" or "idle";

    private static TimelineProjectionInterval AnonymizeCapacityInterval(
        TimelineProjectionInterval interval) => interval with
    {
        OperationId = null,
        BatchId = null,
        BatchNumber = null,
        PartNumber = null,
        OperationNumber = null,
        OperationName = null,
        TimingKind = null,
        OperationStatus = null,
        ForecastStart = null,
        ForecastEnd = null,
        ActualStart = null,
        ActualEnd = null,
        MachineAssignmentId = null,
        PlanningMode = null,
        WorkFinishDate = null,
        Phases = null
    };

    private static string PhaseLabel(string type) => type.ToLowerInvariant() switch
    {
        "setup" => "Setup",
        "qa" => "QA",
        "loadunload" => "Load/unload",
        "production" => "Production",
        "reserved" => "Reserved",
        _ => type
    };

    private static string DependencyWaitingDetail(
        string? predecessorIds,
        IReadOnlyDictionary<string, TimelineSourceOperation> operations,
        IReadOnlyDictionary<string, TimelineSourceMachine> machines)
    {
        var labels = (predecessorIds ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => operations.TryGetValue(id, out var operation)
                ? $"OP{operation.OperationNumber} on {MachineLabel(operation.MachineId, machines)}"
                : $"operation {id}")
            .ToArray();
        return labels.Length switch
        {
            0 => "Waiting for a sequential predecessor to finish.",
            1 => $"Waiting for {labels[0]} to finish.",
            _ => $"Waiting for sequential predecessors {string.Join(", ", labels)} to finish."
        };
    }

    private static string MachineLabel(
        string? machineId,
        IReadOnlyDictionary<string, TimelineSourceMachine> machines) =>
        machineId is not null && machines.TryGetValue(machineId, out var machine)
            ? $"Machine {machine.Number}"
            : "the predecessor Machine";

    private static TimelineProjectionConflict Conflict(
        string code,
        string severity,
        string message,
        IReadOnlyList<string> operationIds,
        IReadOnlyList<string> machineIds)
    {
        var operations = operationIds.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var machines = machineIds.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return new TimelineProjectionConflict(
            $"{code}:{string.Join(',', operations)}:{string.Join(',', machines)}",
            code,
            severity,
            message,
            operations,
            machines);
    }

    private static long LoadUnloadTotalSeconds(TimelineSourceOperation operation)
    {
        if (operation.LoadUnloadSeconds == 0) return 0;
        var occurrences = operation.AutomaticLoading
            ? operation.LoadUnloadEveryNParts.HasValue
                ? checked((operation.PlannedQuantity + (long)operation.LoadUnloadEveryNParts.Value - 1)
                    / operation.LoadUnloadEveryNParts.Value)
                : 0
            : operation.PlannedQuantity;
        return checked((long)operation.LoadUnloadSeconds * occurrences);
    }

    private static TimelineResourceRole ResourceRole(string role) => role switch
    {
        "setup_worker" => TimelineResourceRole.SetupWorker,
        "qa_worker" => TimelineResourceRole.QaWorker,
        "regular_worker" => TimelineResourceRole.RegularWorker,
        _ => throw new InvalidOperationException($"Unsupported employee resource role '{role}'.")
    };

    private static IReadOnlyList<TimelineWindow> ExpandDailyWindow(
        string timeZoneId, string startsAtLocal, string endsAtLocal,
        DateTimeOffset horizonStart, DateTimeOffset horizonEnd)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var startTime = TimeOnly.ParseExact(startsAtLocal, "HH:mm", CultureInfo.InvariantCulture);
        var endTime = TimeOnly.ParseExact(endsAtLocal, "HH:mm", CultureInfo.InvariantCulture);
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(horizonStart, zone).Date).AddDays(-1);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(horizonEnd, zone).Date).AddDays(1);
        var windows = new List<TimelineWindow>();
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            var startLocal = date.ToDateTime(startTime, DateTimeKind.Unspecified);
            var endLocal = date.ToDateTime(endTime, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(startLocal) || zone.IsInvalidTime(endLocal)) continue;
            var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(startLocal, zone));
            var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(endLocal, zone));
            start = start < horizonStart ? horizonStart : start;
            end = end > horizonEnd ? horizonEnd : end;
            if (end > start) windows.Add(new TimelineWindow(start, end));
        }
        return windows;
    }

    private static IReadOnlyList<TimelineWindow> ApplyResourceExceptions(
        IReadOnlyList<TimelineWindow> availability,
        string timeZoneId,
        IReadOnlyList<TimelineSourceResourceException> exceptions,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var exclusions = new List<TimelineWindow>();
        foreach (var exception in exceptions)
        {
            var startTime = exception.IsFullDay
                ? TimeOnly.MinValue
                : TimeOnly.ParseExact(exception.StartsAtLocal!, "HH:mm", CultureInfo.InvariantCulture);
            var endDate = exception.IsFullDay ? exception.Date.AddDays(1) : exception.Date;
            var endTime = exception.IsFullDay
                ? TimeOnly.MinValue
                : TimeOnly.ParseExact(exception.EndsAtLocal!, "HH:mm", CultureInfo.InvariantCulture);
            var startLocal = exception.Date.ToDateTime(startTime, DateTimeKind.Unspecified);
            var endLocal = endDate.ToDateTime(endTime, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(startLocal) || zone.IsInvalidTime(endLocal)) continue;
            var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(startLocal, zone));
            var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(endLocal, zone));
            start = start < horizonStart ? horizonStart : start;
            end = end > horizonEnd ? horizonEnd : end;
            if (end > start) exclusions.Add(new TimelineWindow(start, end));
        }

        var result = new List<TimelineWindow>();
        foreach (var source in availability)
        {
            var fragments = new List<TimelineWindow> { source };
            foreach (var exclusion in exclusions)
            {
                fragments = fragments.SelectMany(fragment =>
                {
                    if (exclusion.EndsAt <= fragment.StartsAt || exclusion.StartsAt >= fragment.EndsAt)
                        return [fragment];
                    var split = new List<TimelineWindow>();
                    if (exclusion.StartsAt > fragment.StartsAt)
                        split.Add(new TimelineWindow(fragment.StartsAt, exclusion.StartsAt));
                    if (exclusion.EndsAt < fragment.EndsAt)
                        split.Add(new TimelineWindow(exclusion.EndsAt, fragment.EndsAt));
                    return split;
                }).ToList();
            }
            result.AddRange(fragments);
        }
        return result;
    }

    private static string DependencyToken(TimelineDependencyType type) => type switch
    {
        TimelineDependencyType.Sequential => "SEQUENTIAL",
        TimelineDependencyType.ParallelCapable => "PARALLEL_CAPABLE",
        TimelineDependencyType.Independent => "INDEPENDENT",
        TimelineDependencyType.LockedSimultaneous => "LOCKED_SIMULTANEOUS",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    private sealed record AvailabilityDocument(
        IReadOnlyList<AvailabilityWindow>? Availability,
        WeeklySchedule? WeeklySchedule,
        bool UseIsraeliHolidays = false);

    private sealed record AvailabilityWindow(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

    private sealed record WeeklySchedule(
        IReadOnlyList<string>? Workdays,
        string? ShiftStartsAtLocal,
        string? ShiftEndsAtLocal,
        IReadOnlyList<WeeklyWindow>? Windows,
        IReadOnlyList<WeeklyWindow>? BreakWindows,
        IReadOnlyList<WeeklyException>? Exceptions);

    private sealed record WeeklyWindow(string StartsAtLocal, string EndsAtLocal);

    private sealed record WeeklyException(
        string Date,
        IReadOnlyList<WeeklyWindow>? Windows,
        IReadOnlyList<WeeklyWindow>? BreakWindows,
        string? Name);

    private sealed record MappedDependency(
        TimelineDependency Domain,
        TimelineProjectionDependency Projection);
}
