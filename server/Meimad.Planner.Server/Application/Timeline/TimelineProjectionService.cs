using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Domain.Timeline;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Application.EventLogging;

namespace Meimad.Planner.Server.Application.Timeline;

internal sealed class TimelineProjectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ITimelineSourceRepository repository;
    private readonly TimelineCalculationEngine engine;
    private readonly TimelineOptions options;
    private readonly IStructuredEventLogRepository eventLog;
    private readonly ILogger<TimelineProjectionService> logger;

    public TimelineProjectionService(
        ITimelineSourceRepository repository,
        TimelineCalculationEngine engine,
        TimelineOptions options,
        IStructuredEventLogRepository eventLog,
        ILogger<TimelineProjectionService> logger)
    {
        this.repository = repository;
        this.engine = engine;
        this.options = options;
        this.eventLog = eventLog;
        this.logger = logger;
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
        horizonStart = horizonStart.ToUniversalTime();
        horizonEnd = horizonEnd.ToUniversalTime();
        var source = await repository.ReadAsync(horizonStart, horizonEnd, cancellationToken);
        var forecastCursor = (asOf ?? source.ReadAt).ToUniversalTime();
        if (forecastCursor < horizonStart || forecastCursor >= horizonEnd)
        {
            forecastCursor = horizonStart;
        }
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
        var actualHistory = source.Operations
            .Where(operation => operation.ActualStart.HasValue
                && operation.ActualMachineId is not null
                && (operation.Status == "completed" || operation.Status == "in_progress"))
            .ToArray();

        logger.LogInformation(
            "Timeline source loaded for {HorizonStart} to {HorizonEnd}: {MachineCount} Machines, {OperationCount} Batch Operations, {AssignedCount} assigned, {ResourceCount} active resources, {DowntimeCount} downtime windows.",
            horizonStart, horizonEnd, source.Machines.Count, source.Operations.Count,
            assigned.Length, source.Resources.Count, source.Downtimes.Count);
        foreach (var operation in assigned)
        {
            logger.LogDebug(
                "Timeline assigned operation {OperationId} Batch {BatchId} OP{OperationNumber} -> Machine {MachineId} backlog {BacklogPosition}; status {Status}, actual {ActualStart} to {ActualEnd}; quantity {PlannedQuantity}, setup {SetupSeconds}, cycle {CycleSeconds}, QA {QaSeconds}, load/unload {LoadUnloadSeconds}.",
                operation.OperationId, operation.BatchId, operation.OperationNumber,
                operation.MachineId, operation.BacklogPosition, operation.Status,
                operation.ActualStart, operation.ActualEnd, operation.PlannedQuantity,
                operation.SetupSeconds, operation.CycleSeconds, operation.QaSeconds,
                operation.LoadUnloadSeconds);
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
                var productionSeconds = checked(
                    (long)operation.CycleSeconds.Value * operation.PlannedQuantity);
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
            calendars.Add(new TimelineMachineCalendar(machine.MachineId, windows, machine.SkillTokens));
        }

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
        var resourceCalendars = source.Resources.Select(resource => new TimelineResourceCalendar(
            resource.ResourceId,
            ResourceRole(resource.Role),
            ApplyResourceExceptions(
                ReadAvailability(resource.CalendarJson, resource.TimeZoneId, horizonStart, horizonEnd,
                    $"Employee resource {resource.ResourceId} calendar", mappingConflicts, [], source.Holidays),
                resource.TimeZoneId, resource.Exceptions, horizonStart, horizonEnd),
            resource.Skills)).ToArray();
        var dayShiftCalendars = source.Machines.Select(machine => new TimelineMachineCalendar(
            machine.MachineId,
            ExpandDailyWindow(machine.TimeZoneId, options.DayShiftStartsAtLocal,
                options.DayShiftEndsAtLocal, horizonStart, horizonEnd))).ToArray();
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
        var backlogs = usableOperations
            .Where(operation => !dependencyBlockedOperationIds.Contains(operation.OperationId))
            .Where(operation => !backlogBlockedOperationIds.Contains(operation.OperationId))
            .GroupBy(operation => operation.MachineId!, StringComparer.Ordinal)
            .Select(group => new TimelineMachineBacklog(
                group.Key,
                group.OrderBy(operation => operation.BacklogPosition)
                    .Select(operation => new TimelineOperationInput(
                        operation.OperationId,
                        TimeSpan.FromSeconds(operation.SetupSeconds!.Value),
                        TimeSpan.FromSeconds(checked(
                            (long)operation.CycleSeconds!.Value * operation.PlannedQuantity)),
                        TimeSpan.FromSeconds(operation.QaSeconds),
                        TimeSpan.FromSeconds(LoadUnloadTotalSeconds(operation)),
                        operation.LoadUnloadRequiresWorker,
                        operation.DayShiftOnly,
                        operation.PriorityWorkFinishDate,
                        operation.PriorityOrderNumber,
                        EarliestForecastStart(operation, source.Operations, forecastCursor)))
                    .ToArray()))
            .OrderBy(backlog => machinesById.TryGetValue(backlog.MachineId, out var machine)
                ? machine.Number
                : backlog.MachineId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var calculationOperationIds = backlogs
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
            calculationOperationIds.Count, backlogs.Length, calculationDependencies.Length,
            assigned.Length - calculationOperationIds.Count,
            resourceCalendars.Count(value => value.Role == TimelineResourceRole.SetupWorker),
            resourceCalendars.Count(value => value.Role == TimelineResourceRole.QaWorker),
            resourceCalendars.Count(value => value.Role == TimelineResourceRole.RegularWorker));
        var calculationInput = new TimelineCalculationInput(
            horizonStart,
            horizonEnd,
            backlogs,
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
        var calculation = engine.Calculate(calculationInput);
        var baselineBacklogs = backlogs.Select(backlog => backlog with
        {
            Operations = backlog.Operations.Select(operation => operation with
            {
                EarliestStart = operationsById[operation.OperationId].Status == "not_started"
                    ? EarliestForecastStart(operationsById[operation.OperationId], source.Operations, horizonStart)
                    : operation.EarliestStart
            }).ToArray()
        }).ToArray();
        var baselineStarts = forecastCursor > horizonStart
            ? engine.Calculate(calculationInput with { MachineBacklogs = baselineBacklogs })
                .Operations.ToDictionary(
                    operation => operation.OperationId,
                    operation => operation.StartsAt,
                    StringComparer.Ordinal)
            : new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

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
        var missedStartWarnings = forecastCursor > horizonStart
            ? calculation.Operations
                .Where(result => operationsById.TryGetValue(result.OperationId, out var operation)
                    && operation.Status == "not_started"
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
            .Concat(missedStartWarnings).Concat(overdueWarnings).ToArray();
        var projectedMachines = source.Machines.Select(machine => new TimelineProjectionMachine(
            machine.MachineId,
            machine.Number,
            machine.Name,
            intervalsByMachine.TryGetValue(machine.MachineId, out var timeline)
                ? timeline.Intervals.Select(interval => ProjectInterval(
                    interval, operationsById, machinesById, resultsByOperation, forecastCursor))
                    .Concat(ActualHistoryIntervals(machine.MachineId, actualHistory, horizonStart, horizonEnd, forecastCursor))
                    .Concat(PauseIntervals(machine.MachineId, source.Operations, horizonStart, horizonEnd))
                    .Concat(UnscheduledIntervals(
                        machine.MachineId, assigned, scheduledOperationIds,
                        backlogBlockedOperationIds, allConflicts, horizonStart, horizonEnd))
                    .OrderBy(interval => interval.StartsAt).ThenBy(interval => interval.Type).ToArray()
                : ActualHistoryIntervals(machine.MachineId, actualHistory, horizonStart, horizonEnd, forecastCursor)
                    .Concat(PauseIntervals(machine.MachineId, source.Operations, horizonStart, horizonEnd))
                    .Concat(UnscheduledIntervals(
                        machine.MachineId, assigned, scheduledOperationIds,
                        backlogBlockedOperationIds, allConflicts, horizonStart, horizonEnd))
                    .OrderBy(interval => interval.StartsAt).ThenBy(interval => interval.Type).ToArray())).ToArray();
        var batches = source.Operations
            .GroupBy(operation => operation.BatchId, StringComparer.Ordinal)
            .Select(group => new TimelineProjectionBatch(
                group.Key,
                group.First().BatchNumber,
                group.First().PartNumber))
            .OrderBy(batch => batch.PartNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(batch => batch.BatchNumber, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var projection = new TimelineProjection(
            source.ReadAt,
            horizonStart,
            horizonEnd,
            batches,
            projectedMachines,
            dependencies.Select(dependency => dependency.Projection).ToArray(),
            allConflicts);
        logger.LogInformation(
            "Timeline calculation completed with {ScheduledOperationCount} scheduled operations and {ConflictCount} conflicts.",
            calculation.Operations.Count, projection.Conflicts.Count);
        foreach (var result in calculation.Operations)
        {
            logger.LogDebug(
                "Timeline computed operation {OperationId} on Machine {MachineId}: {ForecastStart} to {ForecastEnd}; status {Status}; predecessor constraints were applied before placement.",
                result.OperationId, result.MachineId, result.StartsAt, result.FinishesAt,
                operationsById.GetValueOrDefault(result.OperationId)?.Status);
        }
        foreach (var operation in assigned.Where(operation => !scheduledOperationIds.Contains(operation.OperationId)))
        {
            logger.LogDebug(
                "Timeline skipped calculation for assigned operation {OperationId} on Machine {MachineId}; status {Status}; represented as waiting. Conflicts: {ConflictCodes}.",
                operation.OperationId, operation.MachineId, operation.Status,
                string.Join(',', allConflicts.Where(conflict => conflict.OperationIds.Contains(operation.OperationId))
                    .Select(conflict => conflict.Code)));
        }
        await LogProjectionEventsAsync(projection, cancellationToken);
        return projection;
    }

    private async Task LogProjectionEventsAsync(TimelineProjection projection, CancellationToken token)
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

        foreach (var interval in projection.Machines.SelectMany(machine => machine.Intervals)
                     .Where(interval => interval.Type == "waiting"
                         && interval.OperationId is not null
                         && interval.Detail is not null
                         && (interval.Detail.Contains("worker", StringComparison.OrdinalIgnoreCase)
                             || interval.Detail.StartsWith("resource", StringComparison.OrdinalIgnoreCase))))
            await eventLog.AppendAsync(new(
                "resource_wait_detected", projection.ReadAt, "system",
                new Dictionary<string,string> {
                    ["batchOperationId"]=interval.OperationId!,["machineId"]=interval.MachineId },
                "resource_unavailable_or_contended", interval.Detail, null,
                new { interval.StartsAt,interval.EndsAt },
                $"resource-wait:{day}:{interval.OperationId}:{interval.MachineId}:{interval.Detail}"), token);
    }

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
        var breaks = ReadLocalWindows(schedule.BreakWindows ?? [], "Weekly break windows", allowEmpty: true);
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
                dateBreaks = ReadLocalWindows(exception.BreakWindows ?? [], "Exception break windows", allowEmpty: true);
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
            if (end <= start) throw new InvalidOperationException($"{label} contain an overnight or empty window.");
            return (Start: start, End: end);
        }).OrderBy(window => window.Start).ToArray();
        if (result.Skip(1).Zip(result, (next, prior) => next.Start < prior.End).Any(overlap => overlap))
            throw new InvalidOperationException($"{label} overlap.");
        return result;
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
            operation?.ActualEnd);
    }

    private static DateTimeOffset EarliestForecastStart(
        TimelineSourceOperation operation,
        IReadOnlyList<TimelineSourceOperation> operations,
        DateTimeOffset forecastCursor)
    {
        var earliest = operation.Status == "in_progress" && operation.ActualStart.HasValue
            ? operation.ActualStart.Value
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
                ?? (asOf > operation.ActualStart.Value ? asOf : operation.ActualStart.Value);
            var endsAt = rawEnd > horizonEnd ? horizonEnd : rawEnd;
            return new TimelineProjectionInterval(
                "production", machineId, operation.OperationId, operation.BatchId,
                operation.BatchNumber, operation.PartNumber, operation.OperationNumber,
                operation.OperationName, startsAt, endsAt,
                operation.Status == "completed" ? "Recorded actual work" : "Actual work elapsed; finish remains forecast",
                "actual", operation.Status, null, null,
                operation.ActualStart, operation.ActualEnd);
        })
        .Where(interval => interval.EndsAt > interval.StartsAt)
        .ToArray();

    private static TimelineProjectionInterval[] PauseIntervals(
        string machineId, IReadOnlyList<TimelineSourceOperation> operations,
        DateTimeOffset horizonStart, DateTimeOffset horizonEnd) => operations
        .Where(operation => operation.MachineId == machineId
            && operation.ActivePauseReason is not null
            && operation.PauseStartedAt < horizonEnd)
        .Select(operation => new TimelineProjectionInterval(
            "waiting", machineId, operation.OperationId, operation.BatchId,
            operation.BatchNumber, operation.PartNumber, operation.OperationNumber,
            operation.OperationName,
            operation.PauseStartedAt!.Value > horizonStart ? operation.PauseStartedAt.Value : horizonStart,
            horizonEnd,
            $"Operation paused by {operation.PausedBy}: {operation.ActivePauseReason}"))
        .ToArray();

    private static TimelineProjectionInterval[] UnscheduledIntervals(
        string machineId,
        IReadOnlyList<TimelineSourceOperation> assigned,
        IReadOnlySet<string> scheduledOperationIds,
        IReadOnlySet<string> backlogBlockedOperationIds,
        IReadOnlyList<TimelineProjectionConflict> conflicts,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd) => assigned
        .Where(operation => operation.MachineId == machineId
            && !scheduledOperationIds.Contains(operation.OperationId)
            && operation.ActivePauseReason is null)
        .Select(operation =>
        {
            var ownConflict = conflicts.FirstOrDefault(conflict =>
                conflict.OperationIds.Contains(operation.OperationId, StringComparer.Ordinal));
            var detail = ownConflict?.Message
                ?? (backlogBlockedOperationIds.Contains(operation.OperationId)
                    ? "Waiting because an earlier operation blocks the stored Machine backlog order."
                    : "Waiting because the operation cannot be placed inside the current Timeline horizon.");
            return new TimelineProjectionInterval(
                "waiting", machineId, operation.OperationId, operation.BatchId,
                operation.BatchNumber, operation.PartNumber, operation.OperationNumber,
                operation.OperationName, horizonStart, horizonEnd, detail);
        })
        .ToArray();

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
