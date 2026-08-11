namespace Meimad.Planner.Server.Domain.Timeline;

internal sealed class TimelineCalculationEngine
{
    internal TimelineCalculationResult Calculate(TimelineCalculationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var horizonStart = input.HorizonStart.ToUniversalTime();
        var horizonEnd = input.HorizonEnd.ToUniversalTime();
        var conflicts = new ConflictCollector();
        if (horizonEnd <= horizonStart)
        {
            conflicts.Add(
                "invalid_horizon",
                TimelineConflictSeverity.Blocking,
                "The calculation horizon must end after it starts.",
                [],
                []);
            return new TimelineCalculationResult(
                horizonStart,
                horizonEnd,
                [],
                [],
                conflicts.Items);
        }

        var backlogEntries = ReadBacklogs(input.MachineBacklogs, conflicts);
        var entriesByOperation = backlogEntries
            .GroupBy(entry => entry.Operation.OperationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var invalidOperations = entriesByOperation
            .Where(pair => pair.Value.Length > 1)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var operationId in invalidOperations.OrderBy(value => value, StringComparer.Ordinal))
        {
            conflicts.Add(
                "duplicate_operation",
                TimelineConflictSeverity.Blocking,
                $"Operation '{operationId}' appears more than once in the Machine backlogs.",
                [operationId],
                entriesByOperation[operationId]
                    .Select(entry => entry.MachineId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray());
        }

        foreach (var entry in backlogEntries)
        {
            if (entry.Operation.SetupDuration < TimeSpan.Zero
                || entry.Operation.ProductionDuration < TimeSpan.Zero)
            {
                invalidOperations.Add(entry.Operation.OperationId);
                conflicts.Add(
                    "invalid_duration",
                    TimelineConflictSeverity.Blocking,
                    $"Operation '{entry.Operation.OperationId}' has a negative setup or production duration.",
                    [entry.Operation.OperationId],
                    [entry.MachineId]);
            }
        }

        var operationEntries = entriesByOperation
            .Where(pair => pair.Value.Length == 1 && !invalidOperations.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value[0], StringComparer.Ordinal);
        var machineWindows = ReadMachineAvailability(
            input,
            horizonStart,
            horizonEnd,
            conflicts);
        var setupWindows = NormalizeWindows(
            input.SetupCalendar.Availability,
            horizonStart,
            horizonEnd,
            "setup_calendar",
            null,
            conflicts);
        var dependencyModel = BuildDependencies(
            input.Dependencies,
            operationEntries,
            invalidOperations,
            conflicts);
        var nodes = BuildNodes(
            operationEntries,
            dependencyModel.LockedGroupByOperation,
            invalidOperations);
        var predecessors = nodes.Keys.ToDictionary(
            key => key,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        AddBacklogEdges(input.MachineBacklogs, nodes, invalidOperations, predecessors);
        AddSequentialEdges(
            input.Dependencies,
            nodes,
            operationEntries,
            predecessors,
            conflicts);
        var invalidNodes = ValidateLockedMachineMembership(nodes, operationEntries, conflicts);

        var scheduledByOperation = ScheduleNodes(
            nodes,
            predecessors,
            operationEntries,
            machineWindows,
            setupWindows,
            horizonStart,
            invalidNodes,
            conflicts);
        var operationResults = backlogEntries
            .Where(entry => scheduledByOperation.ContainsKey(entry.Operation.OperationId))
            .Select(entry => scheduledByOperation[entry.Operation.OperationId])
            .ToArray();
        var machineResults = BuildMachineResults(
            input,
            machineWindows,
            operationResults,
            horizonStart,
            horizonEnd);

        return new TimelineCalculationResult(
            horizonStart,
            horizonEnd,
            operationResults,
            machineResults,
            conflicts.Items);
    }

    private static IReadOnlyList<BacklogEntry> ReadBacklogs(
        IReadOnlyList<TimelineMachineBacklog> backlogs,
        ConflictCollector conflicts)
    {
        var entries = new List<BacklogEntry>();
        var duplicateMachines = backlogs
            .GroupBy(backlog => backlog.MachineId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var machineId in duplicateMachines.OrderBy(value => value, StringComparer.Ordinal))
        {
            conflicts.Add(
                "duplicate_machine_backlog",
                TimelineConflictSeverity.Blocking,
                $"Machine '{machineId}' has more than one backlog input.",
                [],
                [machineId]);
        }

        foreach (var backlog in backlogs)
        {
            if (string.IsNullOrWhiteSpace(backlog.MachineId) || duplicateMachines.Contains(backlog.MachineId))
            {
                continue;
            }

            for (var position = 0; position < backlog.Operations.Count; position++)
            {
                var operation = backlog.Operations[position];
                if (string.IsNullOrWhiteSpace(operation.OperationId))
                {
                    conflicts.Add(
                        "missing_operation_id",
                        TimelineConflictSeverity.Blocking,
                        $"Machine '{backlog.MachineId}' contains an operation without an ID.",
                        [],
                        [backlog.MachineId]);
                    continue;
                }

                entries.Add(new BacklogEntry(backlog.MachineId, position, operation));
            }
        }

        return entries;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> ReadMachineAvailability(
        TimelineCalculationInput input,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        ConflictCollector conflicts)
    {
        var calendarGroups = input.MachineCalendars
            .GroupBy(calendar => calendar.MachineId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<InstantWindow>>(StringComparer.Ordinal);
        foreach (var backlog in input.MachineBacklogs
                     .GroupBy(value => value.MachineId, StringComparer.Ordinal)
                     .Select(group => group.First()))
        {
            if (!calendarGroups.TryGetValue(backlog.MachineId, out var calendars))
            {
                conflicts.Add(
                    "missing_machine_calendar",
                    TimelineConflictSeverity.Blocking,
                    $"Machine '{backlog.MachineId}' has no availability calendar.",
                    backlog.Operations.Select(operation => operation.OperationId).ToArray(),
                    [backlog.MachineId]);
                result[backlog.MachineId] = [];
                continue;
            }

            if (calendars.Length > 1)
            {
                conflicts.Add(
                    "duplicate_machine_calendar",
                    TimelineConflictSeverity.Blocking,
                    $"Machine '{backlog.MachineId}' has more than one availability calendar.",
                    backlog.Operations.Select(operation => operation.OperationId).ToArray(),
                    [backlog.MachineId]);
                result[backlog.MachineId] = [];
                continue;
            }

            var available = NormalizeWindows(
                calendars[0].Availability,
                horizonStart,
                horizonEnd,
                "machine_calendar",
                backlog.MachineId,
                conflicts);
            var downtime = input.Downtimes
                .Where(value => string.Equals(
                    value.MachineId,
                    backlog.MachineId,
                    StringComparison.Ordinal))
                .Select(value => new TimelineWindow(value.StartsAt, value.EndsAt))
                .ToArray();
            var blocked = NormalizeWindows(
                downtime,
                horizonStart,
                horizonEnd,
                "downtime",
                backlog.MachineId,
                conflicts);
            result[backlog.MachineId] = Subtract(available, blocked);
        }

        foreach (var downtime in input.Downtimes.Where(value =>
                     !result.ContainsKey(value.MachineId)))
        {
            conflicts.Add(
                "unknown_downtime_machine",
                TimelineConflictSeverity.Warning,
                $"Downtime '{downtime.DowntimeId}' references unknown Machine '{downtime.MachineId}'.",
                [],
                [downtime.MachineId]);
        }

        return result;
    }

    private static DependencyModel BuildDependencies(
        IReadOnlyList<TimelineDependency> dependencies,
        IReadOnlyDictionary<string, BacklogEntry> operations,
        ISet<string> invalidOperations,
        ConflictCollector conflicts)
    {
        var lockedGroupByOperation = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dependency in dependencies.OrderBy(value => value.DependencyId, StringComparer.Ordinal))
        {
            if (!operations.ContainsKey(dependency.FromOperationId)
                || !operations.ContainsKey(dependency.ToOperationId))
            {
                conflicts.Add(
                    "invalid_dependency_reference",
                    TimelineConflictSeverity.Blocking,
                    $"Dependency '{dependency.DependencyId}' references an operation that is not present exactly once in the backlogs.",
                    [dependency.FromOperationId, dependency.ToOperationId],
                    []);
                continue;
            }

            if (string.Equals(
                    dependency.FromOperationId,
                    dependency.ToOperationId,
                    StringComparison.Ordinal))
            {
                conflicts.Add(
                    "self_dependency",
                    TimelineConflictSeverity.Blocking,
                    $"Operation '{dependency.FromOperationId}' cannot depend on itself.",
                    [dependency.FromOperationId],
                    [operations[dependency.FromOperationId].MachineId]);
                continue;
            }

            if (dependency.Type != TimelineDependencyType.LockedSimultaneous)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(dependency.SimultaneousGroupKey))
            {
                conflicts.Add(
                    "missing_simultaneous_group",
                    TimelineConflictSeverity.Blocking,
                    $"Locked-simultaneous dependency '{dependency.DependencyId}' requires a group key.",
                    [dependency.FromOperationId, dependency.ToOperationId],
                    []);
                continue;
            }

            var key = dependency.SimultaneousGroupKey.Trim();
            AddLockedMembership(dependency.FromOperationId, key);
            AddLockedMembership(dependency.ToOperationId, key);

            void AddLockedMembership(string operationId, string groupKey)
            {
                if (lockedGroupByOperation.TryGetValue(operationId, out var existing)
                    && !string.Equals(existing, groupKey, StringComparison.Ordinal))
                {
                    invalidOperations.Add(operationId);
                    conflicts.Add(
                        "multiple_simultaneous_groups",
                        TimelineConflictSeverity.Blocking,
                        $"Operation '{operationId}' belongs to multiple locked-simultaneous groups.",
                        [operationId],
                        [operations[operationId].MachineId]);
                    return;
                }

                lockedGroupByOperation[operationId] = groupKey;
            }
        }

        foreach (var operationId in invalidOperations)
        {
            lockedGroupByOperation.Remove(operationId);
        }

        return new DependencyModel(lockedGroupByOperation);
    }

    private static IReadOnlyDictionary<string, ScheduleNode> BuildNodes(
        IReadOnlyDictionary<string, BacklogEntry> operations,
        IReadOnlyDictionary<string, string> lockedGroupByOperation,
        ISet<string> invalidOperations)
    {
        var nodes = new Dictionary<string, ScheduleNode>(StringComparer.Ordinal);
        foreach (var entry in operations.Values)
        {
            if (invalidOperations.Contains(entry.Operation.OperationId))
            {
                continue;
            }

            var key = lockedGroupByOperation.TryGetValue(entry.Operation.OperationId, out var group)
                ? $"group:{group}"
                : $"operation:{entry.Operation.OperationId}";
            if (!nodes.TryGetValue(key, out var node))
            {
                node = new ScheduleNode(key, []);
                nodes.Add(key, node);
            }

            node.Members.Add(entry.Operation.OperationId);
        }

        foreach (var node in nodes.Values)
        {
            node.Members.Sort(StringComparer.Ordinal);
        }

        return nodes;
    }

    private static void AddBacklogEdges(
        IReadOnlyList<TimelineMachineBacklog> backlogs,
        IReadOnlyDictionary<string, ScheduleNode> nodes,
        ISet<string> invalidOperations,
        IReadOnlyDictionary<string, HashSet<string>> predecessors)
    {
        foreach (var backlog in backlogs)
        {
            string? previousNode = null;
            foreach (var operation in backlog.Operations)
            {
                if (invalidOperations.Contains(operation.OperationId))
                {
                    continue;
                }

                var node = nodes.Values.FirstOrDefault(value =>
                    value.Members.Contains(operation.OperationId, StringComparer.Ordinal));
                if (node is null)
                {
                    continue;
                }

                if (previousNode is not null
                    && !string.Equals(previousNode, node.Key, StringComparison.Ordinal))
                {
                    predecessors[node.Key].Add(previousNode);
                }

                previousNode = node.Key;
            }
        }
    }

    private static void AddSequentialEdges(
        IReadOnlyList<TimelineDependency> dependencies,
        IReadOnlyDictionary<string, ScheduleNode> nodes,
        IReadOnlyDictionary<string, BacklogEntry> operations,
        IReadOnlyDictionary<string, HashSet<string>> predecessors,
        ConflictCollector conflicts)
    {
        var nodeByOperation = nodes.Values
            .SelectMany(node => node.Members.Select(member => (member, node.Key)))
            .ToDictionary(value => value.member, value => value.Key, StringComparer.Ordinal);
        foreach (var dependency in dependencies
                     .Where(value => value.Type == TimelineDependencyType.Sequential)
                     .OrderBy(value => value.DependencyId, StringComparer.Ordinal))
        {
            if (!nodeByOperation.TryGetValue(dependency.FromOperationId, out var from)
                || !nodeByOperation.TryGetValue(dependency.ToOperationId, out var to))
            {
                continue;
            }

            if (string.Equals(from, to, StringComparison.Ordinal))
            {
                conflicts.Add(
                    "sequential_inside_simultaneous_group",
                    TimelineConflictSeverity.Blocking,
                    "A sequential dependency cannot exist inside one locked-simultaneous group.",
                    [dependency.FromOperationId, dependency.ToOperationId],
                    operations.TryGetValue(dependency.FromOperationId, out var entry)
                        ? [entry.MachineId]
                        : []);
                continue;
            }

            predecessors[to].Add(from);
        }
    }

    private static IReadOnlySet<string> ValidateLockedMachineMembership(
        IReadOnlyDictionary<string, ScheduleNode> nodes,
        IReadOnlyDictionary<string, BacklogEntry> operations,
        ConflictCollector conflicts)
    {
        var invalidNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes.Values.Where(value => value.Members.Count > 1))
        {
            var duplicateMachines = node.Members
                .GroupBy(operationId => operations[operationId].MachineId, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicateMachines.Length > 0)
            {
                invalidNodes.Add(node.Key);
                conflicts.Add(
                    "simultaneous_same_machine",
                    TimelineConflictSeverity.Blocking,
                    "Locked-simultaneous operations cannot share one Machine.",
                    node.Members,
                    duplicateMachines);
            }
        }

        return invalidNodes;
    }

    private static IReadOnlyDictionary<string, TimelineOperationResult> ScheduleNodes(
        IReadOnlyDictionary<string, ScheduleNode> nodes,
        IReadOnlyDictionary<string, HashSet<string>> predecessors,
        IReadOnlyDictionary<string, BacklogEntry> operations,
        IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> machineWindows,
        IReadOnlyList<InstantWindow> setupWindows,
        DateTimeOffset horizonStart,
        IReadOnlySet<string> invalidNodes,
        ConflictCollector conflicts)
    {
        var pending = nodes.Keys
            .Where(node => !invalidNodes.Contains(node))
            .ToHashSet(StringComparer.Ordinal);
        var completedFinish = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var failed = invalidNodes.ToHashSet(StringComparer.Ordinal);
        var results = new Dictionary<string, TimelineOperationResult>(StringComparer.Ordinal);

        while (pending.Count > 0)
        {
            var blocked = pending
                .Where(node => predecessors[node].Any(failed.Contains))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            foreach (var nodeKey in blocked)
            {
                var node = nodes[nodeKey];
                conflicts.Add(
                    "dependency_unresolved",
                    TimelineConflictSeverity.Blocking,
                    "An operation could not be calculated because a required predecessor failed.",
                    node.Members,
                    node.Members.Select(id => operations[id].MachineId).ToArray());
                failed.Add(nodeKey);
                pending.Remove(nodeKey);
            }

            var ready = pending
                .Where(node => predecessors[node].All(completedFinish.ContainsKey))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (ready.Length == 0)
            {
                if (pending.Count == 0)
                {
                    break;
                }

                var cycleOperations = pending
                    .SelectMany(node => nodes[node].Members)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                conflicts.Add(
                    "dependency_cycle",
                    TimelineConflictSeverity.Blocking,
                    "Backlog order and sequential dependencies contain a cycle; the engine did not reorder them.",
                    cycleOperations,
                    cycleOperations.Select(id => operations[id].MachineId).Distinct().ToArray());
                break;
            }

            foreach (var nodeKey in ready)
            {
                var node = nodes[nodeKey];
                var earliest = predecessors[nodeKey].Count == 0
                    ? horizonStart
                    : predecessors[nodeKey].Max(predecessor => completedFinish[predecessor]);
                var scheduled = node.Members.Count == 1
                    ? ScheduleSingle(
                        operations[node.Members[0]],
                        earliest,
                        machineWindows,
                        setupWindows)
                    : ScheduleLockedGroup(
                        node,
                        operations,
                        earliest,
                        machineWindows,
                        setupWindows);
                pending.Remove(nodeKey);
                if (scheduled is null)
                {
                    failed.Add(nodeKey);
                    conflicts.Add(
                        "insufficient_availability",
                        TimelineConflictSeverity.Blocking,
                        "The operation cannot fit inside the supplied Machine/setup calendars and horizon after downtime.",
                        node.Members,
                        node.Members.Select(id => operations[id].MachineId).ToArray());
                    continue;
                }

                foreach (var result in scheduled.Results)
                {
                    results[result.OperationId] = result;
                }

                completedFinish[nodeKey] = scheduled.FinishesAt;
            }
        }

        return results;
    }

    private static ScheduledNode? ScheduleSingle(
        BacklogEntry entry,
        DateTimeOffset earliest,
        IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> machineWindows,
        IReadOnlyList<InstantWindow> setupWindows)
    {
        if (!machineWindows.TryGetValue(entry.MachineId, out var availability))
        {
            return null;
        }

        var result = ScheduleMember(entry, earliest, availability, setupWindows);
        return result is null
            ? null
            : new ScheduledNode([result], result.FinishesAt);
    }

    private static ScheduledNode? ScheduleLockedGroup(
        ScheduleNode node,
        IReadOnlyDictionary<string, BacklogEntry> operations,
        DateTimeOffset earliest,
        IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> machineWindows,
        IReadOnlyList<InstantWindow> setupWindows)
    {
        var members = node.Members.Select(id => operations[id]).ToArray();
        if (members.Select(member => member.MachineId).Distinct(StringComparer.Ordinal).Count()
            != members.Length)
        {
            return null;
        }

        var phaseWindows = new List<IReadOnlyList<InstantWindow>>();
        foreach (var member in members)
        {
            if (!machineWindows.TryGetValue(member.MachineId, out var availability))
            {
                return null;
            }

            if (member.Operation.SetupDuration > TimeSpan.Zero)
            {
                phaseWindows.Add(Intersect(availability, setupWindows));
            }
            else if (member.Operation.ProductionDuration > TimeSpan.Zero)
            {
                phaseWindows.Add(availability);
            }
        }

        var commonStart = FindCommonStart(earliest, phaseWindows);
        if (commonStart is null)
        {
            return null;
        }

        var results = new List<TimelineOperationResult>();
        foreach (var member in members)
        {
            var result = ScheduleMember(
                member,
                commonStart.Value,
                machineWindows[member.MachineId],
                setupWindows);
            if (result is null || result.StartsAt != commonStart.Value)
            {
                return null;
            }

            results.Add(result);
        }

        var groupFinish = results.Max(result => result.FinishesAt);
        var final = results.Select(result =>
        {
            var reserved = result.FinishesAt < groupFinish
                ? new[]
                {
                    new TimelineInterval(
                        TimelineIntervalType.Reserved,
                        result.MachineId,
                        result.OperationId,
                        result.FinishesAt,
                        groupFinish,
                        "Locked-simultaneous reservation")
                }
                : [];
            return result with
            {
                FinishesAt = groupFinish,
                ReservedIntervals = reserved
            };
        }).ToArray();
        return new ScheduledNode(final, groupFinish);
    }

    private static TimelineOperationResult? ScheduleMember(
        BacklogEntry entry,
        DateTimeOffset earliest,
        IReadOnlyList<InstantWindow> machineAvailability,
        IReadOnlyList<InstantWindow> setupWindows)
    {
        var setupAvailability = Intersect(machineAvailability, setupWindows);
        var setup = Allocate(entry.Operation.SetupDuration, earliest, setupAvailability);
        if (setup is null)
        {
            return null;
        }

        var production = Allocate(
            entry.Operation.ProductionDuration,
            setup.FinishesAt,
            machineAvailability);
        if (production is null)
        {
            return null;
        }

        var startsAt = setup.Intervals.FirstOrDefault()?.StartsAt
            ?? production.Intervals.FirstOrDefault()?.StartsAt
            ?? earliest;
        var setupIntervals = setup.Intervals.Select(window => new TimelineInterval(
            TimelineIntervalType.Setup,
            entry.MachineId,
            entry.Operation.OperationId,
            window.StartsAt,
            window.EndsAt)).ToArray();
        var productionIntervals = production.Intervals.Select(window => new TimelineInterval(
            TimelineIntervalType.Production,
            entry.MachineId,
            entry.Operation.OperationId,
            window.StartsAt,
            window.EndsAt)).ToArray();
        return new TimelineOperationResult(
            entry.Operation.OperationId,
            entry.MachineId,
            entry.BacklogPosition,
            startsAt,
            production.FinishesAt,
            setupIntervals,
            productionIntervals,
            []);
    }

    private static IReadOnlyList<TimelineMachineResult> BuildMachineResults(
        TimelineCalculationInput input,
        IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> machineAvailability,
        IReadOnlyList<TimelineOperationResult> operationResults,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd)
    {
        var results = new List<TimelineMachineResult>();
        foreach (var backlog in input.MachineBacklogs
                     .GroupBy(value => value.MachineId, StringComparer.Ordinal)
                     .Select(group => group.First()))
        {
            var operationIntervals = operationResults
                .Where(operation => string.Equals(
                    operation.MachineId,
                    backlog.MachineId,
                    StringComparison.Ordinal))
                .SelectMany(operation => operation.SetupIntervals
                    .Concat(operation.ProductionIntervals)
                    .Concat(operation.ReservedIntervals))
                .ToArray();
            var occupied = Merge(operationIntervals.Select(interval => new InstantWindow(
                interval.StartsAt,
                interval.EndsAt)));
            var idle = machineAvailability.TryGetValue(backlog.MachineId, out var available)
                ? Subtract(available, occupied).Select(window => new TimelineInterval(
                    TimelineIntervalType.Idle,
                    backlog.MachineId,
                    null,
                    window.StartsAt,
                    window.EndsAt,
                    "Available Machine time")).ToArray()
                : [];
            var downtime = input.Downtimes
                .Where(value => string.Equals(
                    value.MachineId,
                    backlog.MachineId,
                    StringComparison.Ordinal))
                .Select(value => Clip(value.StartsAt, value.EndsAt, horizonStart, horizonEnd) is { } window
                    ? new TimelineInterval(
                        TimelineIntervalType.Downtime,
                        backlog.MachineId,
                        null,
                        window.StartsAt,
                        window.EndsAt,
                        value.Reason)
                    : null)
                .Where(value => value is not null)
                .Cast<TimelineInterval>();
            var intervals = operationIntervals
                .Concat(idle)
                .Concat(downtime)
                .OrderBy(interval => interval.StartsAt)
                .ThenBy(interval => interval.Type)
                .ThenBy(interval => interval.OperationId, StringComparer.Ordinal)
                .ToArray();
            results.Add(new TimelineMachineResult(backlog.MachineId, intervals));
        }

        return results;
    }

    private static Allocation? Allocate(
        TimeSpan duration,
        DateTimeOffset earliest,
        IReadOnlyList<InstantWindow> windows)
    {
        if (duration == TimeSpan.Zero)
        {
            return new Allocation([], earliest);
        }

        var remainingTicks = duration.Ticks;
        var intervals = new List<InstantWindow>();
        foreach (var window in windows)
        {
            if (window.EndsAt <= earliest)
            {
                continue;
            }

            var start = window.StartsAt > earliest ? window.StartsAt : earliest;
            var availableTicks = (window.EndsAt - start).Ticks;
            if (availableTicks <= 0)
            {
                continue;
            }

            var usedTicks = Math.Min(availableTicks, remainingTicks);
            var end = start.AddTicks(usedTicks);
            intervals.Add(new InstantWindow(start, end));
            remainingTicks -= usedTicks;
            earliest = end;
            if (remainingTicks == 0)
            {
                return new Allocation(intervals, end);
            }
        }

        return null;
    }

    private static DateTimeOffset? FindCommonStart(
        DateTimeOffset earliest,
        IReadOnlyList<IReadOnlyList<InstantWindow>> windowsByMember)
    {
        if (windowsByMember.Count == 0)
        {
            return earliest;
        }

        var candidate = earliest;
        var maximumIterations = windowsByMember.Sum(windows => windows.Count) + 1;
        for (var iteration = 0; iteration < maximumIterations; iteration++)
        {
            var nextValues = windowsByMember
                .Select(windows => NextAvailableAtOrAfter(candidate, windows))
                .ToArray();
            if (nextValues.Any(value => value is null))
            {
                return null;
            }

            var nextCandidate = nextValues.Max(value => value!.Value);
            if (windowsByMember.All(windows => Contains(windows, nextCandidate)))
            {
                return nextCandidate;
            }

            candidate = nextCandidate;
        }

        return null;
    }

    private static DateTimeOffset? NextAvailableAtOrAfter(
        DateTimeOffset value,
        IReadOnlyList<InstantWindow> windows)
    {
        foreach (var window in windows)
        {
            if (value >= window.StartsAt && value < window.EndsAt)
            {
                return value;
            }

            if (window.StartsAt > value)
            {
                return window.StartsAt;
            }
        }

        return null;
    }

    private static bool Contains(IReadOnlyList<InstantWindow> windows, DateTimeOffset value) =>
        windows.Any(window => value >= window.StartsAt && value < window.EndsAt);

    private static IReadOnlyList<InstantWindow> NormalizeWindows(
        IReadOnlyList<TimelineWindow> windows,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        string source,
        string? machineId,
        ConflictCollector conflicts)
    {
        var valid = new List<InstantWindow>();
        foreach (var window in windows)
        {
            var start = window.StartsAt.ToUniversalTime();
            var end = window.EndsAt.ToUniversalTime();
            if (end <= start)
            {
                conflicts.Add(
                    "invalid_calendar_window",
                    TimelineConflictSeverity.Blocking,
                    $"A {source} window must end after it starts.",
                    [],
                    machineId is null ? [] : [machineId]);
                continue;
            }

            if (Clip(start, end, horizonStart, horizonEnd) is { } clipped)
            {
                valid.Add(clipped);
            }
        }

        return Merge(valid);
    }

    private static InstantWindow? Clip(
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd)
    {
        start = start.ToUniversalTime();
        end = end.ToUniversalTime();
        var clippedStart = start > horizonStart ? start : horizonStart;
        var clippedEnd = end < horizonEnd ? end : horizonEnd;
        return clippedEnd > clippedStart ? new InstantWindow(clippedStart, clippedEnd) : null;
    }

    private static IReadOnlyList<InstantWindow> Merge(IEnumerable<InstantWindow> windows)
    {
        var ordered = windows
            .Where(window => window.EndsAt > window.StartsAt)
            .OrderBy(window => window.StartsAt)
            .ThenBy(window => window.EndsAt)
            .ToArray();
        if (ordered.Length == 0)
        {
            return [];
        }

        var merged = new List<InstantWindow> { ordered[0] };
        foreach (var window in ordered.Skip(1))
        {
            var previous = merged[^1];
            if (window.StartsAt <= previous.EndsAt)
            {
                merged[^1] = previous with
                {
                    EndsAt = window.EndsAt > previous.EndsAt
                        ? window.EndsAt
                        : previous.EndsAt
                };
            }
            else
            {
                merged.Add(window);
            }
        }

        return merged;
    }

    private static IReadOnlyList<InstantWindow> Intersect(
        IReadOnlyList<InstantWindow> left,
        IReadOnlyList<InstantWindow> right)
    {
        var result = new List<InstantWindow>();
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Count && rightIndex < right.Count)
        {
            var start = left[leftIndex].StartsAt > right[rightIndex].StartsAt
                ? left[leftIndex].StartsAt
                : right[rightIndex].StartsAt;
            var end = left[leftIndex].EndsAt < right[rightIndex].EndsAt
                ? left[leftIndex].EndsAt
                : right[rightIndex].EndsAt;
            if (end > start)
            {
                result.Add(new InstantWindow(start, end));
            }

            if (left[leftIndex].EndsAt <= right[rightIndex].EndsAt)
            {
                leftIndex++;
            }
            else
            {
                rightIndex++;
            }
        }

        return result;
    }

    private static IReadOnlyList<InstantWindow> Subtract(
        IReadOnlyList<InstantWindow> source,
        IReadOnlyList<InstantWindow> blocked)
    {
        var result = new List<InstantWindow>();
        foreach (var window in source)
        {
            var cursor = window.StartsAt;
            foreach (var block in blocked.Where(block =>
                         block.EndsAt > window.StartsAt && block.StartsAt < window.EndsAt))
            {
                if (block.StartsAt > cursor)
                {
                    result.Add(new InstantWindow(
                        cursor,
                        block.StartsAt < window.EndsAt ? block.StartsAt : window.EndsAt));
                }

                if (block.EndsAt > cursor)
                {
                    cursor = block.EndsAt;
                }

                if (cursor >= window.EndsAt)
                {
                    break;
                }
            }

            if (cursor < window.EndsAt)
            {
                result.Add(new InstantWindow(cursor, window.EndsAt));
            }
        }

        return result.Where(window => window.EndsAt > window.StartsAt).ToArray();
    }

    private sealed record BacklogEntry(
        string MachineId,
        int BacklogPosition,
        TimelineOperationInput Operation);

    private sealed record DependencyModel(
        IReadOnlyDictionary<string, string> LockedGroupByOperation);

    private sealed record ScheduleNode(string Key, List<string> Members);

    private sealed record ScheduledNode(
        IReadOnlyList<TimelineOperationResult> Results,
        DateTimeOffset FinishesAt);

    private sealed record Allocation(
        IReadOnlyList<InstantWindow> Intervals,
        DateTimeOffset FinishesAt);

    private sealed record InstantWindow(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

    private sealed class ConflictCollector
    {
        private readonly HashSet<string> identities = new(StringComparer.Ordinal);
        private readonly List<TimelineConflict> items = [];

        internal IReadOnlyList<TimelineConflict> Items => items;

        internal void Add(
            string code,
            TimelineConflictSeverity severity,
            string message,
            IReadOnlyList<string> operationIds,
            IReadOnlyList<string> machineIds)
        {
            var operations = operationIds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var machines = machineIds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var identity = $"{code}:{string.Join(',', operations)}:{string.Join(',', machines)}";
            if (!identities.Add(identity))
            {
                return;
            }

            items.Add(new TimelineConflict(
                identity,
                code,
                severity,
                message,
                operations,
                machines));
        }
    }
}
