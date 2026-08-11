using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Domain.Timeline;

namespace Meimad.Planner.Server.Application.Timeline;

internal sealed class TimelineProjectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ITimelineSourceRepository repository;
    private readonly TimelineCalculationEngine engine;

    public TimelineProjectionService(
        ITimelineSourceRepository repository,
        TimelineCalculationEngine engine)
    {
        this.repository = repository;
        this.engine = engine;
    }

    internal async Task<TimelineProjection> CalculateAsync(
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        CancellationToken cancellationToken = default)
    {
        horizonStart = horizonStart.ToUniversalTime();
        horizonEnd = horizonEnd.ToUniversalTime();
        var source = await repository.ReadAsync(horizonStart, horizonEnd, cancellationToken);
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
                _ = checked((long)operation.CycleSeconds.Value * operation.PlannedQuantity);
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
                [machine.MachineId]);
            calendars.Add(new TimelineMachineCalendar(machine.MachineId, windows));
        }

        var setupAvailability = source.SetupCalendarJson is null
            ? MissingSetupCalendar(mappingConflicts)
            : ReadAvailability(
                source.SetupCalendarJson,
                null,
                horizonStart,
                horizonEnd,
                "Setup calendar",
                mappingConflicts,
                []);
        var dependencies = BuildDependencies(source.Operations, mappingConflicts);
        var backlogs = usableOperations
            .GroupBy(operation => operation.MachineId!, StringComparer.Ordinal)
            .Select(group => new TimelineMachineBacklog(
                group.Key,
                group.OrderBy(operation => operation.BacklogPosition)
                    .Select(operation => new TimelineOperationInput(
                        operation.OperationId,
                        TimeSpan.FromSeconds(operation.SetupSeconds!.Value),
                        TimeSpan.FromSeconds(checked(
                            (long)operation.CycleSeconds!.Value * operation.PlannedQuantity))))
                    .ToArray()))
            .OrderBy(backlog => machinesById.TryGetValue(backlog.MachineId, out var machine)
                ? machine.Number
                : backlog.MachineId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var calculation = engine.Calculate(new TimelineCalculationInput(
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
            dependencies.Select(dependency => dependency.Domain).ToArray()));

        var intervalsByMachine = calculation.Machines.ToDictionary(
            machine => machine.MachineId,
            StringComparer.Ordinal);
        var projectedMachines = source.Machines.Select(machine => new TimelineProjectionMachine(
            machine.MachineId,
            machine.Number,
            machine.Name,
            intervalsByMachine.TryGetValue(machine.MachineId, out var timeline)
                ? timeline.Intervals.Select(interval => ProjectInterval(
                    interval,
                    operationsById)).ToArray()
                : [])).ToArray();
        var batches = source.Operations
            .GroupBy(operation => operation.BatchId, StringComparer.Ordinal)
            .Select(group => new TimelineProjectionBatch(
                group.Key,
                group.First().BatchNumber,
                group.First().PartNumber))
            .OrderBy(batch => batch.PartNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(batch => batch.BatchNumber, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var domainConflicts = calculation.Conflicts.Select(conflict => new TimelineProjectionConflict(
            conflict.ConflictId,
            conflict.Code,
            conflict.Severity.ToString().ToLowerInvariant(),
            conflict.Message,
            conflict.OperationIds,
            conflict.MachineIds));
        return new TimelineProjection(
            source.ReadAt,
            horizonStart,
            horizonEnd,
            batches,
            projectedMachines,
            dependencies.Select(dependency => dependency.Projection).ToArray(),
            mappingConflicts.Concat(domainConflicts).ToArray());
    }

    private static IReadOnlyList<TimelineWindow> MissingSetupCalendar(
        ICollection<TimelineProjectionConflict> conflicts)
    {
        conflicts.Add(Conflict(
            "setup_calendar_configuration_missing",
            "blocking",
            "Server setting 'timeline.setup_calendar_json' is missing.",
            [],
            []));
        return [];
    }

    private static IReadOnlyList<TimelineWindow> ReadAvailability(
        string json,
        string? timeZoneId,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        string label,
        ICollection<TimelineProjectionConflict> conflicts,
        IReadOnlyList<string> machineIds)
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
                    horizonEnd);
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
            or TimeZoneNotFoundException or InvalidTimeZoneException)
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
        DateTimeOffset horizonEnd)
    {
        if (schedule.Workdays is null || schedule.Workdays.Count == 0
            || string.IsNullOrWhiteSpace(schedule.ShiftStartsAtLocal)
            || string.IsNullOrWhiteSpace(schedule.ShiftEndsAtLocal))
        {
            throw new InvalidOperationException("Weekly schedule is incomplete.");
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var startMinutes = ParseLocalMinutes(schedule.ShiftStartsAtLocal, false);
        var endMinutes = ParseLocalMinutes(schedule.ShiftEndsAtLocal, true);
        if (endMinutes <= startMinutes)
        {
            throw new InvalidOperationException("Overnight or empty shifts are unsupported.");
        }

        var days = schedule.Workdays.Select(ParseDayOfWeek).ToHashSet();
        var localStartDate = TimeZoneInfo.ConvertTime(horizonStart, timeZone).Date.AddDays(-1);
        var localEndDate = TimeZoneInfo.ConvertTime(horizonEnd, timeZone).Date.AddDays(1);
        var windows = new List<TimelineWindow>();
        for (var date = localStartDate; date <= localEndDate; date = date.AddDays(1))
        {
            if (!days.Contains(date.DayOfWeek))
            {
                continue;
            }

            var localStart = DateTime.SpecifyKind(date.AddMinutes(startMinutes), DateTimeKind.Unspecified);
            var localEnd = DateTime.SpecifyKind(date.AddMinutes(endMinutes), DateTimeKind.Unspecified);
            if (timeZone.IsInvalidTime(localStart) || timeZone.IsInvalidTime(localEnd))
            {
                continue;
            }

            var startsAt = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone));
            var endsAt = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localEnd, timeZone));
            if (endsAt > horizonStart && startsAt < horizonEnd)
            {
                windows.Add(new TimelineWindow(startsAt, endsAt));
            }
        }

        return windows;
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
        ICollection<TimelineProjectionConflict> conflicts)
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
                        continue;
                    }

                    if (predecessor.Status == "completed")
                    {
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
        IReadOnlyDictionary<string, TimelineSourceOperation> operations)
    {
        var operation = interval.OperationId is not null
            && operations.TryGetValue(interval.OperationId, out var found)
                ? found
                : null;
        return new TimelineProjectionInterval(
            interval.Type.ToString().ToLowerInvariant(),
            interval.MachineId,
            interval.OperationId,
            operation?.BatchId,
            operation?.BatchNumber,
            operation?.PartNumber,
            operation?.OperationNumber,
            interval.StartsAt,
            interval.EndsAt,
            interval.Detail);
    }

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
        WeeklySchedule? WeeklySchedule);

    private sealed record AvailabilityWindow(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

    private sealed record WeeklySchedule(
        IReadOnlyList<string>? Workdays,
        string? ShiftStartsAtLocal,
        string? ShiftEndsAtLocal);

    private sealed record MappedDependency(
        TimelineDependency Domain,
        TimelineProjectionDependency Projection);
}
