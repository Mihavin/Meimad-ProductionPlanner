namespace Meimad.Planner.Server.Domain.Timeline;

internal sealed class TimelineCalculationEngine
{
    // Bounds response size and calculation work for periodic per-operation reload phases.
    private const int MaximumLoadUnloadOccurrencesPerOperation = 10_000;

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
                || entry.Operation.ProductionDuration < TimeSpan.Zero
                || entry.Operation.QaDuration < TimeSpan.Zero
                || entry.Operation.LoadUnloadDuration < TimeSpan.Zero
                || entry.Operation.PlannedQuantity < 0
                || entry.Operation.LoadUnloadEveryNParts <= 0)
            {
                invalidOperations.Add(entry.Operation.OperationId);
                conflicts.Add(
                    "invalid_duration",
                    TimelineConflictSeverity.Blocking,
                    $"Operation '{entry.Operation.OperationId}' has an invalid duration, quantity, or load/unload frequency.",
                    [entry.Operation.OperationId],
                    [entry.MachineId]);
                continue;
            }

            var loadUnloadOccurrences = LoadUnloadOccurrenceCount(entry.Operation);
            if (loadUnloadOccurrences > MaximumLoadUnloadOccurrencesPerOperation)
            {
                invalidOperations.Add(entry.Operation.OperationId);
                conflicts.Add(
                    "load_unload_occurrence_limit_exceeded",
                    TimelineConflictSeverity.Blocking,
                    $"Operation '{entry.Operation.OperationId}' requires {loadUnloadOccurrences} load/unload occurrences, exceeding the supported maximum of {MaximumLoadUnloadOccurrencesPerOperation}. Use automatic loading with a larger every-N-parts cadence or split the Batch.",
                    [entry.Operation.OperationId],
                    [entry.MachineId]);
                continue;
            }

            try
            {
                _ = TotalDuration(entry.Operation);
            }
            catch (OverflowException)
            {
                invalidOperations.Add(entry.Operation.OperationId);
                conflicts.Add(
                    "invalid_duration",
                    TimelineConflictSeverity.Blocking,
                    $"Operation '{entry.Operation.OperationId}' has a duration that exceeds the supported range.",
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
        var resources = ReadResourceAvailability(input, horizonStart, horizonEnd, conflicts);
        var machineSkills = input.MachineCalendars
            .GroupBy(value => value.MachineId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<string>)(group.First().SkillTokens ?? []),
                StringComparer.Ordinal);
        var dayShiftWindows = ReadDayShiftAvailability(input, horizonStart, horizonEnd, conflicts);
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
        var invalidNodes = ValidateLockedMachineMembership(nodes, operationEntries, conflicts)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var node in nodes.Values.Where(value => value.Members.Count > 1))
        {
            var modes = node.Members
                .Select(operationId => operationEntries[operationId].Operation.PlanningMode)
                .Distinct()
                .ToArray();
            if (modes.Length <= 1)
            {
                continue;
            }

            invalidNodes.Add(node.Key);
            conflicts.Add(
                "locked_group_planning_mode_conflict",
                TimelineConflictSeverity.Blocking,
                "Locked-simultaneous operations must use one shared planning mode; the engine did not silently choose a mode.",
                node.Members,
                node.Members.Select(id => operationEntries[id].MachineId).Distinct().ToArray());
        }

        var nodeModes = nodes.ToDictionary(
            pair => pair.Key,
            pair => NodePlanningMode(pair.Value, operationEntries),
            StringComparer.Ordinal);
        var scheduledByOperation = nodeModes.Values.All(mode => mode == TimelinePlanningMode.Backward)
            ? ScheduleNodesBackward(
                nodes,
                predecessors,
                operationEntries,
                machineWindows,
                setupWindows,
                resources,
                machineSkills,
                dayShiftWindows,
                horizonStart,
                horizonEnd,
                input.Downtimes,
                input.Dependencies,
                invalidNodes,
                conflicts)
            : nodeModes.Values.All(mode => mode != TimelinePlanningMode.Backward)
                ? ScheduleNodes(
                    nodes,
                    predecessors,
                    operationEntries,
                    machineWindows,
                    setupWindows,
                    resources,
                    machineSkills,
                    dayShiftWindows,
                    horizonStart,
                    input.Downtimes,
                    input.Dependencies,
                    invalidNodes,
                    conflicts)
                : ScheduleNodesMixed(
                nodes,
                predecessors,
                nodeModes,
                operationEntries,
                machineWindows,
                setupWindows,
                resources,
                machineSkills,
                dayShiftWindows,
                horizonStart,
                horizonEnd,
                input.Downtimes,
                input.Dependencies,
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

    private static IReadOnlyList<ResourceAvailability> ReadResourceAvailability(
        TimelineCalculationInput input,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        ConflictCollector conflicts)
    {
        if (input.ResourceCalendars is null)
        {
            IReadOnlyList<InstantWindow> unconstrained = [new InstantWindow(horizonStart, horizonEnd)];
            return Enumerable.Range(0, Math.Max(1, input.MachineBacklogs.Count))
                .SelectMany(index => new[]
                {
                    new ResourceAvailability($"legacy-setup-{index}", TimelineResourceRole.SetupWorker, unconstrained, ["*"]),
                    new ResourceAvailability($"legacy-qa-{index}", TimelineResourceRole.QaWorker, unconstrained, ["*"]),
                    new ResourceAvailability($"legacy-regular-{index}", TimelineResourceRole.RegularWorker, unconstrained, ["*"])
                })
                .ToArray();
        }
        return input.ResourceCalendars
            .Select(calendar => new ResourceAvailability(
                calendar.ResourceId,
                calendar.Role,
                NormalizeWindows(calendar.Availability, horizonStart, horizonEnd,
                    $"resource:{calendar.ResourceId}", null, conflicts),
                calendar.Skills ?? []))
            .OrderBy(value => value.ResourceId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> ReadDayShiftAvailability(
        TimelineCalculationInput input,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        ConflictCollector conflicts)
    {
        return (input.DayShiftCalendars ?? [])
            .GroupBy(value => value.MachineId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<InstantWindow>)Merge(group.SelectMany(calendar => NormalizeWindows(
                    calendar.Availability, horizonStart, horizonEnd,
                    $"day_shift:{calendar.MachineId}", calendar.MachineId, conflicts))),
                StringComparer.Ordinal);
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
                if (operations.ContainsKey(dependency.ToOperationId)
                    && (dependency.Type == TimelineDependencyType.Sequential
                        || dependency.Type == TimelineDependencyType.LockedSimultaneous))
                {
                    invalidOperations.Add(dependency.ToOperationId);
                }
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
                invalidOperations.Add(dependency.FromOperationId);
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
        IReadOnlyList<ResourceAvailability> resources,
        IReadOnlyDictionary<string, IReadOnlyList<string>> machineSkills,
        IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> dayShiftWindows,
        DateTimeOffset horizonStart,
        IReadOnlyList<TimelineDowntime> downtimes,
        IReadOnlyList<TimelineDependency> dependencies,
        IReadOnlySet<string> invalidNodes,
        ConflictCollector conflicts)
    {
        var pending = nodes.Keys
            .Where(node => !invalidNodes.Contains(node))
            .ToHashSet(StringComparer.Ordinal);
        var completedFinish = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var failed = invalidNodes.ToHashSet(StringComparer.Ordinal);
        var results = new Dictionary<string, TimelineOperationResult>(StringComparer.Ordinal);
        var occupiedResources = new Dictionary<string, List<ResourceReservation>>(StringComparer.Ordinal);

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

            // Propagate a failed predecessor through the complete dependency chain
            // before deciding that the remaining graph contains a cycle. Without
            // this pass, a grandchild can be mislabeled as a cycle merely because
            // its parent became unresolved in this iteration.
            if (blocked.Length > 0)
            {
                continue;
            }

            var ready = pending
                .Where(node => predecessors[node].All(completedFinish.ContainsKey))
                .OrderBy(value => value, Comparer<string>.Create((left, right) =>
                    CompareReadyNodes(left, right, nodes, operations)))
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
                var memberEarliest = node.Members
                    .Select(operationId => operations[operationId].Operation.EarliestStart)
                    .Where(value => value.HasValue)
                    .Select(value => value!.Value.ToUniversalTime())
                    .DefaultIfEmpty(earliest)
                    .Max();
                if (memberEarliest > earliest)
                {
                    earliest = memberEarliest;
                }
                var scheduled = node.Members.Count == 1
                    ? ScheduleSingle(
                        operations[node.Members[0]],
                        earliest,
                        machineWindows,
                        setupWindows,
                        resources,
                        machineSkills,
                        occupiedResources,
                        dayShiftWindows,
                        downtimes)
                    : ScheduleLockedGroup(
                        node,
                        operations,
                        earliest,
                        machineWindows,
                        setupWindows,
                        resources,
                        machineSkills,
                        occupiedResources,
                        dayShiftWindows,
                        downtimes);
                pending.Remove(nodeKey);
                if (scheduled is null)
                {
                    failed.Add(nodeKey);
                    conflicts.Add(
                        "insufficient_availability",
                        TimelineConflictSeverity.Blocking,
                        InsufficientAvailabilityMessage(
                            node, operations, resources, machineSkills),
                        node.Members,
                        node.Members.Select(id => operations[id].MachineId).ToArray());
                    continue;
                }

                foreach (var result in scheduled.Results)
                {
                    results[result.OperationId] = result;
                }

                foreach (var reservation in scheduled.ResourceReservations)
                {
                    if (!occupiedResources.TryGetValue(reservation.ResourceId, out var occupied))
                    {
                        occupied = [];
                        occupiedResources.Add(reservation.ResourceId, occupied);
                    }
                    occupied.Add(reservation);
                }

                completedFinish[nodeKey] = scheduled.FinishesAt;
            }
        }

        return AddDependencyWaitingIntervals(
            results,
            dependencies,
            machineWindows,
            horizonStart);
    }

    private static IReadOnlyDictionary<string, TimelineOperationResult> ScheduleNodesBackward(
        IReadOnlyDictionary<string, ScheduleNode> nodes,
        IReadOnlyDictionary<string, HashSet<string>> predecessors,
        IReadOnlyDictionary<string, BacklogEntry> operations,
        IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> machineWindows,
        IReadOnlyList<InstantWindow> setupWindows,
        IReadOnlyList<ResourceAvailability> resources,
        IReadOnlyDictionary<string, IReadOnlyList<string>> machineSkills,
        IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> dayShiftWindows,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        IReadOnlyList<TimelineDowntime> downtimes,
        IReadOnlyList<TimelineDependency> dependencies,
        IReadOnlySet<string> invalidNodes,
        ConflictCollector conflicts)
    {
        var successors = nodes.Keys.ToDictionary(
            key => key,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var (node, requiredPredecessors) in predecessors)
        {
            foreach (var predecessor in requiredPredecessors)
            {
                successors[predecessor].Add(node);
            }
        }

        var failed = invalidNodes.ToHashSet(StringComparer.Ordinal);
        PropagateBackwardFailure(
            invalidNodes, failed, successors, nodes, operations, conflicts);

        while (true)
        {
            var pending = nodes.Keys.Where(node => !failed.Contains(node))
                .ToHashSet(StringComparer.Ordinal);
            var scheduledStarts = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            var results = new Dictionary<string, TimelineOperationResult>(StringComparer.Ordinal);
            var occupiedResources = new Dictionary<string, List<ResourceReservation>>(StringComparer.Ordinal);
            var restart = false;

            while (pending.Count > 0)
            {
                var ready = pending
                    .Where(node => successors[node].All(successor =>
                        scheduledStarts.ContainsKey(successor) || failed.Contains(successor)))
                    .OrderBy(value => value, Comparer<string>.Create((left, right) =>
                        CompareBackwardReadyNodes(left, right, nodes, operations)))
                    .ToArray();
                if (ready.Length == 0)
                {
                    var cycleOperations = pending.SelectMany(node => nodes[node].Members)
                        .OrderBy(value => value, StringComparer.Ordinal).ToArray();
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
                    var scheduledSuccessors = successors[nodeKey]
                        .Where(scheduledStarts.ContainsKey)
                        .ToArray();
                    var latest = scheduledSuccessors.Length == 0
                        ? horizonEnd
                        : scheduledSuccessors.Min(successor => scheduledStarts[successor]);
                    var memberCutoffs = node.Members
                        .Select(operationId => operations[operationId].Operation.LatestFinish)
                        .Where(value => value.HasValue)
                        .Select(value => value!.Value.ToUniversalTime())
                        .ToArray();
                    if (memberCutoffs.Length > 0)
                    {
                        var due = memberCutoffs.Min();
                        if (due < latest)
                        {
                            latest = due;
                        }
                    }

                    var scheduled = node.Members.Count == 1
                        ? ScheduleSingleBackward(
                            operations[node.Members[0]], latest, machineWindows, setupWindows,
                            resources, machineSkills, occupiedResources, dayShiftWindows, downtimes)
                        : ScheduleLockedGroupBackward(
                            node, operations, latest, machineWindows, setupWindows,
                            resources, machineSkills, occupiedResources, dayShiftWindows, downtimes);
                    pending.Remove(nodeKey);
                    if (scheduled is null
                        || scheduled.Results.Any(result => result.StartsAt < horizonStart))
                    {
                        failed.Add(nodeKey);
                        conflicts.Add(
                            "backward_schedule_cannot_fit",
                            TimelineConflictSeverity.Blocking,
                            "The operation cannot fit before its Work Finish Date inside the selected horizon and configured Machine, worker, setup, downtime, and day-shift availability.",
                            node.Members,
                            node.Members.Select(id => operations[id].MachineId).ToArray());
                        PropagateBackwardFailure(
                            [nodeKey], failed, successors, nodes, operations, conflicts);
                        restart = true;
                        break;
                    }

                    foreach (var result in scheduled.Results)
                    {
                        results[result.OperationId] = result;
                    }
                    foreach (var reservation in scheduled.ResourceReservations)
                    {
                        if (!occupiedResources.TryGetValue(reservation.ResourceId, out var occupied))
                        {
                            occupied = [];
                            occupiedResources.Add(reservation.ResourceId, occupied);
                        }
                        occupied.Add(reservation);
                    }
                    scheduledStarts[nodeKey] = scheduled.Results.Min(result => result.StartsAt);
                }

                if (restart)
                {
                    break;
                }
            }

            if (restart)
            {
                continue;
            }

            return AddDependencyWaitingIntervals(results, dependencies, machineWindows, horizonStart);
        }
    }

    private static TimelinePlanningMode NodePlanningMode(
        ScheduleNode node,
        IReadOnlyDictionary<string, BacklogEntry> operations)
    {
        var modes = node.Members
            .Select(operationId => operations[operationId].Operation.PlanningMode)
            .ToArray();
        if (modes.Contains(TimelinePlanningMode.Backward))
        {
            return TimelinePlanningMode.Backward;
        }

        return modes.Contains(TimelinePlanningMode.Forward)
            ? TimelinePlanningMode.Forward
            : TimelinePlanningMode.Manual;
    }

    private static IReadOnlyDictionary<string, TimelineOperationResult> ScheduleNodesMixed(
        IReadOnlyDictionary<string, ScheduleNode> nodes,
        IReadOnlyDictionary<string, HashSet<string>> predecessors,
        IReadOnlyDictionary<string, TimelinePlanningMode> nodeModes,
        IReadOnlyDictionary<string, BacklogEntry> operations,
        IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> machineWindows,
        IReadOnlyList<InstantWindow> setupWindows,
        IReadOnlyList<ResourceAvailability> resources,
        IReadOnlyDictionary<string, IReadOnlyList<string>> machineSkills,
        IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> dayShiftWindows,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        IReadOnlyList<TimelineDowntime> downtimes,
        IReadOnlyList<TimelineDependency> dependencies,
        IReadOnlySet<string> invalidNodes,
        ConflictCollector conflicts)
    {
        var successors = nodes.Keys.ToDictionary(
            key => key,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var (node, requiredPredecessors) in predecessors)
        {
            foreach (var predecessor in requiredPredecessors)
            {
                successors[predecessor].Add(node);
            }
        }

        var failed = invalidNodes.ToHashSet(StringComparer.Ordinal);
        while (true)
        {
            var pending = nodes.Keys.Where(node => !failed.Contains(node))
                .ToHashSet(StringComparer.Ordinal);
            var scheduled = new Dictionary<string, ScheduledNode>(StringComparer.Ordinal);
            var results = new Dictionary<string, TimelineOperationResult>(StringComparer.Ordinal);
            var occupiedResources = new Dictionary<string, List<ResourceReservation>>(StringComparer.Ordinal);
            var restart = false;

            while (pending.Count > 0)
            {
                var blocked = pending.Where(node =>
                        predecessors[node].Any(failed.Contains))
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                if (blocked.Length > 0)
                {
                    foreach (var nodeKey in blocked)
                    {
                        failed.Add(nodeKey);
                        pending.Remove(nodeKey);
                        var node = nodes[nodeKey];
                        conflicts.Add(
                            "dependency_unresolved",
                            TimelineConflictSeverity.Blocking,
                            "An operation could not be calculated because a required predecessor or earlier Machine backlog operation failed.",
                            node.Members,
                            node.Members.Select(id => operations[id].MachineId).ToArray());
                    }

                    restart = true;
                    break;
                }

                var backwardReady = pending
                    .Where(node => nodeModes[node] == TimelinePlanningMode.Backward)
                    .Where(node => successors[node]
                        .Where(successor => nodeModes[successor] == TimelinePlanningMode.Backward)
                        .All(scheduled.ContainsKey))
                    .Where(node => predecessors[node]
                        .Where(predecessor => nodeModes[predecessor] != TimelinePlanningMode.Backward)
                        .All(scheduled.ContainsKey))
                    .OrderBy(value => value, Comparer<string>.Create((left, right) =>
                        CompareBackwardReadyNodes(left, right, nodes, operations)))
                    .ToArray();
                var forwardReady = pending
                    .Where(node => nodeModes[node] != TimelinePlanningMode.Backward)
                    .Where(node => predecessors[node].All(scheduled.ContainsKey))
                    .OrderBy(value => value, Comparer<string>.Create((left, right) =>
                        CompareReadyNodes(left, right, nodes, operations)))
                    .ToArray();

                var ready = backwardReady.Length > 0 ? backwardReady : forwardReady;
                if (ready.Length == 0)
                {
                    var cycleOperations = pending.SelectMany(node => nodes[node].Members)
                        .OrderBy(value => value, StringComparer.Ordinal).ToArray();
                    conflicts.Add(
                        "dependency_cycle",
                        TimelineConflictSeverity.Blocking,
                        "Backlog order and sequential dependencies contain a cycle; the engine did not reorder them.",
                        cycleOperations,
                        cycleOperations.Select(id => operations[id].MachineId).Distinct().ToArray());
                    return AddDependencyWaitingIntervals(results, dependencies, machineWindows, horizonStart);
                }

                foreach (var nodeKey in ready)
                {
                    if (!pending.Contains(nodeKey))
                    {
                        continue;
                    }

                    var node = nodes[nodeKey];
                    ScheduledNode? nodeResult;
                    if (nodeModes[nodeKey] == TimelinePlanningMode.Backward)
                    {
                        var backwardSuccessors = successors[nodeKey]
                            .Where(successor => nodeModes[successor] == TimelinePlanningMode.Backward)
                            .Select(successor => scheduled[successor].Results.Min(result => result.StartsAt))
                            .ToArray();
                        var latest = backwardSuccessors.Length == 0
                            ? horizonEnd
                            : backwardSuccessors.Min();
                        var cutoffs = node.Members
                            .Select(operationId => operations[operationId].Operation.LatestFinish)
                            .Where(value => value.HasValue)
                            .Select(value => value!.Value.ToUniversalTime())
                            .ToArray();
                        if (cutoffs.Length > 0)
                        {
                            latest = cutoffs.Min() < latest ? cutoffs.Min() : latest;
                        }

                        var forwardPredecessorFinish = predecessors[nodeKey]
                            .Where(predecessor => nodeModes[predecessor] != TimelinePlanningMode.Backward)
                            .Select(predecessor => scheduled[predecessor].FinishesAt)
                            .DefaultIfEmpty(horizonStart)
                            .Max();
                        var adjustedOperations = operations;
                        if (forwardPredecessorFinish > horizonStart)
                        {
                            adjustedOperations = operations.ToDictionary(
                                pair => pair.Key,
                                pair => node.Members.Contains(pair.Key, StringComparer.Ordinal)
                                    ? pair.Value with
                                    {
                                        Operation = pair.Value.Operation with
                                        {
                                            EarliestStart = pair.Value.Operation.EarliestStart is { } current
                                                && current > forwardPredecessorFinish
                                                    ? current
                                                    : forwardPredecessorFinish
                                        }
                                    }
                                    : pair.Value,
                                StringComparer.Ordinal);
                        }

                        nodeResult = node.Members.Count == 1
                            ? ScheduleSingleBackward(
                                adjustedOperations[node.Members[0]], latest, machineWindows, setupWindows,
                                resources, machineSkills, occupiedResources, dayShiftWindows, downtimes)
                            : ScheduleLockedGroupBackward(
                                node, adjustedOperations, latest, machineWindows, setupWindows,
                                resources, machineSkills, occupiedResources, dayShiftWindows, downtimes);
                    }
                    else
                    {
                        var earliest = predecessors[nodeKey].Count == 0
                            ? horizonStart
                            : predecessors[nodeKey].Max(predecessor => scheduled[predecessor].FinishesAt);
                        var memberEarliest = node.Members
                            .Select(operationId => operations[operationId].Operation.EarliestStart)
                            .Where(value => value.HasValue)
                            .Select(value => value!.Value.ToUniversalTime())
                            .DefaultIfEmpty(earliest)
                            .Max();
                        earliest = memberEarliest > earliest ? memberEarliest : earliest;
                        nodeResult = node.Members.Count == 1
                            ? ScheduleSingle(
                                operations[node.Members[0]], earliest, machineWindows, setupWindows,
                                resources, machineSkills, occupiedResources, dayShiftWindows, downtimes)
                            : ScheduleLockedGroup(
                                node, operations, earliest, machineWindows, setupWindows,
                                resources, machineSkills, occupiedResources, dayShiftWindows, downtimes);
                    }

                    pending.Remove(nodeKey);
                    if (nodeResult is null
                        || nodeResult.Results.Any(result => result.StartsAt < horizonStart
                            || result.FinishesAt > horizonEnd))
                    {
                        failed.Add(nodeKey);
                        conflicts.Add(
                            nodeModes[nodeKey] == TimelinePlanningMode.Backward
                                ? "backward_schedule_cannot_fit"
                                : "insufficient_availability",
                            TimelineConflictSeverity.Blocking,
                            nodeModes[nodeKey] == TimelinePlanningMode.Backward
                                ? "The operation cannot fit before its Work Finish Date inside the selected horizon and configured availability."
                                : InsufficientAvailabilityMessage(node, operations, resources, machineSkills),
                            node.Members,
                            node.Members.Select(id => operations[id].MachineId).ToArray());
                        restart = true;
                        break;
                    }

                    scheduled[nodeKey] = nodeResult;
                    foreach (var result in nodeResult.Results)
                    {
                        results[result.OperationId] = result;
                    }
                    foreach (var reservation in nodeResult.ResourceReservations)
                    {
                        if (!occupiedResources.TryGetValue(reservation.ResourceId, out var occupied))
                        {
                            occupied = [];
                            occupiedResources.Add(reservation.ResourceId, occupied);
                        }
                        occupied.Add(reservation);
                    }
                }

                if (restart)
                {
                    break;
                }
            }

            if (restart)
            {
                continue;
            }

            return AddDependencyWaitingIntervals(results, dependencies, machineWindows, horizonStart);
        }
    }

    private static void PropagateBackwardFailure(
        IEnumerable<string> roots,
        ISet<string> failed,
        IReadOnlyDictionary<string, HashSet<string>> successors,
        IReadOnlyDictionary<string, ScheduleNode> nodes,
        IReadOnlyDictionary<string, BacklogEntry> operations,
        ConflictCollector conflicts)
    {
        var queue = new Queue<string>(roots);
        while (queue.TryDequeue(out var failedNode))
        {
            foreach (var successor in successors[failedNode].OrderBy(value => value, StringComparer.Ordinal))
            {
                if (!failed.Add(successor))
                {
                    continue;
                }

                var node = nodes[successor];
                conflicts.Add(
                    "dependency_unresolved",
                    TimelineConflictSeverity.Blocking,
                    "An operation could not be placed backward because a required predecessor or earlier Machine backlog operation failed.",
                    node.Members,
                    node.Members.Select(id => operations[id].MachineId).ToArray());
                queue.Enqueue(successor);
            }
        }
    }

    private static int CompareBackwardReadyNodes(
        string left,
        string right,
        IReadOnlyDictionary<string, ScheduleNode> nodes,
        IReadOnlyDictionary<string, BacklogEntry> operations)
    {
        var leftOperation = nodes[left].Members.Select(id => operations[id].Operation)
            .OrderBy(value => value.PriorityWorkFinishDate.HasValue ? 0 : 1)
            .ThenBy(value => value.PriorityWorkFinishDate)
            .ThenBy(value => value.PriorityOrderNumber,
                Comparer<string?>.Create(TimelinePriorityComparer.CompareOrderNumbers))
            .First();
        var rightOperation = nodes[right].Members.Select(id => operations[id].Operation)
            .OrderBy(value => value.PriorityWorkFinishDate.HasValue ? 0 : 1)
            .ThenBy(value => value.PriorityWorkFinishDate)
            .ThenBy(value => value.PriorityOrderNumber,
                Comparer<string?>.Create(TimelinePriorityComparer.CompareOrderNumbers))
            .First();
        var due = Nullable.Compare(leftOperation.PriorityWorkFinishDate, rightOperation.PriorityWorkFinishDate);
        if (!leftOperation.PriorityWorkFinishDate.HasValue && rightOperation.PriorityWorkFinishDate.HasValue) due = 1;
        else if (leftOperation.PriorityWorkFinishDate.HasValue && !rightOperation.PriorityWorkFinishDate.HasValue) due = -1;
        if (due != 0) return due;

        var leftDuration = nodes[left].Members.Max(id => TotalDuration(operations[id].Operation).Ticks);
        var rightDuration = nodes[right].Members.Max(id => TotalDuration(operations[id].Operation).Ticks);
        var duration = leftDuration.CompareTo(rightDuration);
        if (duration != 0) return duration;
        var order = TimelinePriorityComparer.CompareOrderNumbers(
            leftOperation.PriorityOrderNumber, rightOperation.PriorityOrderNumber);
        return order != 0 ? order : StringComparer.Ordinal.Compare(left, right);
    }

    private static TimeSpan TotalDuration(TimelineOperationInput operation) =>
        TimeSpan.FromTicks(checked(
            operation.SetupDuration.Ticks
            + operation.QaDuration.Ticks
            + operation.ProductionDuration.Ticks * operation.PlannedQuantity
            + operation.LoadUnloadDuration.Ticks * LoadUnloadOccurrenceCount(operation)));

    private static int LoadUnloadOccurrenceCount(TimelineOperationInput operation)
    {
        if (operation.PlannedQuantity == 0 || operation.LoadUnloadDuration == TimeSpan.Zero)
        {
            return 0;
        }

        if (!operation.AutomaticLoading)
        {
            return operation.PlannedQuantity;
        }

        return operation.LoadUnloadEveryNParts is { } everyN
            ? (int)((operation.PlannedQuantity + (long)everyN - 1) / everyN)
            : 0;
    }

    private static IReadOnlyList<ProductionRun> ProductionRuns(TimelineOperationInput operation)
    {
        if (operation.PlannedQuantity == 0)
        {
            return [];
        }

        if (LoadUnloadOccurrenceCount(operation) == 0)
        {
            return [new ProductionRun(operation.PlannedQuantity, false)];
        }

        var partsPerRun = operation.AutomaticLoading
            ? operation.LoadUnloadEveryNParts!.Value
            : 1;
        var remaining = operation.PlannedQuantity;
        var runs = new List<ProductionRun>();
        while (remaining > 0)
        {
            var partCount = Math.Min(remaining, partsPerRun);
            runs.Add(new ProductionRun(partCount, true));
            remaining -= partCount;
        }
        return runs;
    }

    private static TimeSpan ProductionRunDuration(
        TimelineOperationInput operation,
        ProductionRun run) => TimeSpan.FromTicks(checked(
        operation.ProductionDuration.Ticks * run.PartCount));

    private static string LoadUnloadDetail(
        TimelineOperationInput operation,
        int occurrenceIndex,
        int occurrenceCount,
        string? resourceId)
    {
        var method = operation.LoadUnloadRequiresWorker
            ? $"Regular worker: {resourceId}"
            : operation.AutomaticLoading
                ? $"Automatic load/unload every {operation.LoadUnloadEveryNParts} parts"
                : "Manual load/unload";
        return $"Part reload {occurrenceIndex}/{occurrenceCount}; {method}";
    }

    private static string InsufficientAvailabilityMessage(
        ScheduleNode node,
        IReadOnlyDictionary<string, BacklogEntry> operations,
        IReadOnlyList<ResourceAvailability> resources,
        IReadOnlyDictionary<string, IReadOnlyList<string>> machineSkills)
    {
        var members = node.Members.Select(id => operations[id]).ToArray();
        if (members.Any(value => value.Operation.QaDuration > TimeSpan.Zero)
            && !resources.Any(value => value.Role == TimelineResourceRole.QaWorker))
        {
            return "The operation requires QA time, but no active QA worker calendar is available within the Timeline horizon.";
        }

        if (members.Any(value => value.Operation.LoadUnloadRequiresWorker
                && LoadUnloadOccurrenceCount(value.Operation) > 0)
            && !resources.Any(value => value.Role == TimelineResourceRole.RegularWorker))
        {
            return "The operation requires manual load/unload time, but no active regular worker calendar is available within the Timeline horizon.";
        }

        var setupMembers = members.Where(value => value.Operation.SetupDuration > TimeSpan.Zero).ToArray();
        if (setupMembers.Length > 0 && setupMembers.Any(member =>
                !resources.Any(resource => resource.Role == TimelineResourceRole.SetupWorker
                    && HasRequiredSkill(resource.Skills,
                        machineSkills.GetValueOrDefault(member.MachineId, [])))))
        {
            return "The operation requires setup, but no active setup worker is qualified for its assigned Machine.";
        }

        return "The operation cannot fit inside the supplied Machine, worker, setup, and day-shift availability within the horizon.";
    }

    private static IReadOnlyDictionary<string, TimelineOperationResult> AddDependencyWaitingIntervals(
        IReadOnlyDictionary<string, TimelineOperationResult> results,
        IReadOnlyList<TimelineDependency> dependencies,
        IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> machineWindows,
        DateTimeOffset horizonStart)
    {
        var occupiedByMachine = results.Values
            .GroupBy(result => result.MachineId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => Merge(group.SelectMany(result => result.SetupIntervals
                    .Concat(result.QaIntervals ?? [])
                    .Concat(result.LoadUnloadIntervals ?? [])
                    .Concat(result.ProductionIntervals)
                    .Concat(result.ReservedIntervals))
                    .Select(interval => new InstantWindow(interval.StartsAt, interval.EndsAt))),
                StringComparer.Ordinal);
        var sequentialByChild = dependencies
            .Where(dependency => dependency.Type == TimelineDependencyType.Sequential)
            .Where(dependency => results.ContainsKey(dependency.FromOperationId)
                && results.ContainsKey(dependency.ToOperationId))
            .GroupBy(dependency => dependency.ToOperationId, StringComparer.Ordinal);
        var withWaiting = new Dictionary<string, TimelineOperationResult>(results, StringComparer.Ordinal);

        foreach (var group in sequentialByChild)
        {
            var child = results[group.Key];
            var predecessors = group
                .Select(dependency => results[dependency.FromOperationId])
                .ToArray();
            var dependencyFinish = predecessors.Max(result => result.FinishesAt);
            if (dependencyFinish <= horizonStart
                || !machineWindows.TryGetValue(child.MachineId, out var availability))
            {
                continue;
            }

            var waitEnd = dependencyFinish < child.StartsAt ? dependencyFinish : child.StartsAt;
            if (waitEnd <= horizonStart)
            {
                continue;
            }

            var waitWindows = Subtract(
                Intersect(availability, [new InstantWindow(horizonStart, waitEnd)]),
                occupiedByMachine.GetValueOrDefault(child.MachineId, []));
            if (waitWindows.Count == 0)
            {
                continue;
            }

            var predecessorIds = predecessors
                .Where(result => result.FinishesAt == dependencyFinish)
                .Select(result => result.OperationId)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            var waiting = waitWindows.Select(window => new TimelineInterval(
                TimelineIntervalType.Waiting,
                child.MachineId,
                child.OperationId,
                window.StartsAt,
                window.EndsAt,
                string.Join("|", predecessorIds))).ToArray();
            withWaiting[child.OperationId] = child with
            {
                WaitingIntervals = child.WaitingIntervals.Concat(waiting)
                    .OrderBy(value => value.StartsAt)
                    .ThenBy(value => value.Detail, StringComparer.Ordinal)
                    .ToArray()
            };
        }

        return withWaiting;
    }

    private static ScheduledNode? ScheduleSingle(
        BacklogEntry entry,
        DateTimeOffset earliest,
        IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> machineWindows,
        IReadOnlyList<InstantWindow> setupWindows,
        IReadOnlyList<ResourceAvailability> resources,
        IReadOnlyDictionary<string, IReadOnlyList<string>> machineSkills,
        IReadOnlyDictionary<string, List<ResourceReservation>> occupiedResources,
        IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> dayShiftWindows,
        IReadOnlyList<TimelineDowntime> downtimes)
    {
        if (!machineWindows.TryGetValue(entry.MachineId, out var availability))
        {
            return null;
        }

        var scheduled = ScheduleMember(entry, earliest, availability, setupWindows,
            resources, machineSkills.GetValueOrDefault(entry.MachineId, []), occupiedResources,
            dayShiftWindows, downtimes);
        return scheduled is null
            ? null
            : new ScheduledNode([scheduled.Result], scheduled.Result.FinishesAt, scheduled.ResourceReservations);
    }

    private static ScheduledNode? ScheduleSingleBackward(
        BacklogEntry entry,
        DateTimeOffset latest,
        IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> machineWindows,
        IReadOnlyList<InstantWindow> setupWindows,
        IReadOnlyList<ResourceAvailability> resources,
        IReadOnlyDictionary<string, IReadOnlyList<string>> machineSkills,
        IReadOnlyDictionary<string, List<ResourceReservation>> occupiedResources,
        IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> dayShiftWindows,
        IReadOnlyList<TimelineDowntime> downtimes)
    {
        if (!machineWindows.TryGetValue(entry.MachineId, out var availability))
        {
            return null;
        }

        var scheduled = ScheduleMemberBackward(
            entry, latest, availability, setupWindows, resources,
            machineSkills.GetValueOrDefault(entry.MachineId, []), occupiedResources,
            dayShiftWindows, downtimes);
        return scheduled is null
            ? null
            : new ScheduledNode([scheduled.Result], scheduled.Result.FinishesAt,
                scheduled.ResourceReservations);
    }

    private static ScheduledNode? ScheduleLockedGroupBackward(
        ScheduleNode node,
        IReadOnlyDictionary<string, BacklogEntry> operations,
        DateTimeOffset latest,
        IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> machineWindows,
        IReadOnlyList<InstantWindow> setupWindows,
        IReadOnlyList<ResourceAvailability> resources,
        IReadOnlyDictionary<string, IReadOnlyList<string>> machineSkills,
        IReadOnlyDictionary<string, List<ResourceReservation>> occupiedResources,
        IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> dayShiftWindows,
        IReadOnlyList<TimelineDowntime> downtimes)
    {
        var members = node.Members.Select(id => operations[id]).ToArray();
        if (members.Select(member => member.MachineId).Distinct(StringComparer.Ordinal).Count()
            != members.Length)
        {
            return null;
        }

        var commonFinish = latest;
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var backwardResults = new List<TimelineOperationResult>();
            var localOccupied = occupiedResources.ToDictionary(
                pair => pair.Key, pair => new List<ResourceReservation>(pair.Value), StringComparer.Ordinal);
            foreach (var member in members)
            {
                if (!machineWindows.TryGetValue(member.MachineId, out var availability))
                {
                    return null;
                }
                var scheduled = ScheduleMemberBackward(
                    member, commonFinish, availability, setupWindows, resources,
                    machineSkills.GetValueOrDefault(member.MachineId, []), localOccupied,
                    dayShiftWindows, downtimes);
                if (scheduled is null)
                {
                    return null;
                }
                backwardResults.Add(scheduled.Result);
                foreach (var reservation in scheduled.ResourceReservations)
                {
                    if (!localOccupied.TryGetValue(reservation.ResourceId, out var occupied))
                    {
                        occupied = [];
                        localOccupied.Add(reservation.ResourceId, occupied);
                    }
                    occupied.Add(reservation);
                }
            }

            var candidateStart = backwardResults.Min(result => result.StartsAt);
            var forward = ScheduleLockedGroup(
                node, operations, candidateStart, machineWindows, setupWindows,
                resources, machineSkills, occupiedResources, dayShiftWindows, downtimes);
            if (forward is not null && forward.FinishesAt <= latest)
            {
                return forward;
            }

            if (candidateStart >= commonFinish)
            {
                return null;
            }
            commonFinish = candidateStart;
        }

        return null;
    }

    private static ScheduledMember? ScheduleMemberBackward(
        BacklogEntry entry,
        DateTimeOffset latest,
        IReadOnlyList<InstantWindow> machineAvailability,
        IReadOnlyList<InstantWindow> setupWindows,
        IReadOnlyList<ResourceAvailability> resources,
        IReadOnlyList<string> machineSkills,
        IReadOnlyDictionary<string, List<ResourceReservation>> occupiedResources,
        IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> dayShiftWindows,
        IReadOnlyList<TimelineDowntime> downtimes)
    {
        var productionAvailability = entry.Operation.DayShiftOnly
            ? dayShiftWindows.TryGetValue(entry.MachineId, out var dayWindows)
                ? Intersect(machineAvailability, dayWindows)
                : []
            : machineAvailability;
        var reservations = new List<ResourceReservation>();
        var productionAllocations = new List<Allocation>();
        var loadAllocations = new List<ScheduledLoad>();
        var phaseLatest = latest;
        var productionRuns = ProductionRuns(entry.Operation);
        var loadOccurrenceCount = LoadUnloadOccurrenceCount(entry.Operation);
        for (var runIndex = productionRuns.Count - 1; runIndex >= 0; runIndex--)
        {
            var run = productionRuns[runIndex];
            var production = AllocateBackward(
                ProductionRunDuration(entry.Operation, run),
                phaseLatest,
                productionAvailability);
            if (production is null) return null;
            productionAllocations.Insert(0, production);
            phaseLatest = AllocationStart(production, phaseLatest);

            if (!run.RequiresLoadUnload)
            {
                continue;
            }

            ResourcePhase? loadPhase = null;
            Allocation? loadUnload;
            if (entry.Operation.LoadUnloadRequiresWorker)
            {
                loadPhase = AllocateResourcePhaseBackward(
                    entry.Operation.LoadUnloadDuration, phaseLatest, machineAvailability,
                    TimelineResourceRole.RegularWorker, resources, occupiedResources, [], entry.Operation);
                loadUnload = loadPhase?.Allocation;
                if (loadPhase is not null) AddReservation(reservations, loadPhase, entry.Operation);
            }
            else
            {
                loadUnload = AllocateBackward(
                    entry.Operation.LoadUnloadDuration, phaseLatest, machineAvailability);
            }
            if (loadUnload is null) return null;
            loadAllocations.Insert(0, new ScheduledLoad(
                loadUnload,
                LoadUnloadDetail(
                    entry.Operation, runIndex + 1, loadOccurrenceCount,
                    loadPhase?.ResourceId)));
            phaseLatest = AllocationStart(loadUnload, phaseLatest);
        }

        var qaLatest = phaseLatest;
        var qaPhase = AllocateResourcePhaseBackward(
            entry.Operation.QaDuration, qaLatest, machineAvailability,
            TimelineResourceRole.QaWorker, resources, occupiedResources, [], entry.Operation);
        if (qaPhase is null) return null;
        AddReservation(reservations, qaPhase, entry.Operation);

        var setupLatest = AllocationStart(qaPhase.Allocation, qaLatest);
        var setupAvailability = Intersect(machineAvailability, setupWindows);
        var setupPhase = AllocateResourcePhaseBackward(
            entry.Operation.SetupDuration, setupLatest, setupAvailability,
            TimelineResourceRole.SetupWorker, resources, occupiedResources, machineSkills,
            entry.Operation);
        if (setupPhase is null) return null;
        AddReservation(reservations, setupPhase, entry.Operation);

        var startsAt = AllocationStart(setupPhase.Allocation, setupLatest);
        if (entry.Operation.EarliestStart is { } earliest
            && startsAt < earliest.ToUniversalTime())
        {
            return null;
        }

        var setupIntervals = ProjectBackwardIntervals(
            setupPhase.Allocation, TimelineIntervalType.Setup, entry,
            $"Setup worker: {setupPhase.ResourceId}");
        var qaIntervals = ProjectBackwardIntervals(
            qaPhase.Allocation, TimelineIntervalType.Qa, entry,
            $"QA worker: {qaPhase.ResourceId}");
        var loadIntervals = loadAllocations.SelectMany(load => ProjectBackwardIntervals(
            load.Allocation, TimelineIntervalType.LoadUnload, entry, load.Detail)).ToArray();
        var productionIntervals = productionAllocations.SelectMany(production =>
            ProjectBackwardIntervals(
                production, TimelineIntervalType.Production, entry, null)).ToArray();
        var work = setupIntervals.Concat(qaIntervals).Concat(loadIntervals)
            .Concat(productionIntervals).OrderBy(value => value.StartsAt).ToArray();
        var finish = work.Length == 0 ? latest : work.Max(value => value.EndsAt);
        var waiting = BackwardWaitingIntervals(entry, startsAt, finish, work, downtimes);
        var result = new TimelineOperationResult(
            entry.Operation.OperationId, entry.MachineId, entry.BacklogPosition,
            startsAt, finish, setupIntervals, productionIntervals, [], waiting,
            qaIntervals, loadIntervals);
        return new ScheduledMember(
            result,
            reservations
                .OrderBy(reservation => reservation.Intervals[0].StartsAt)
                .ThenBy(reservation => reservation.ResourceId, StringComparer.Ordinal)
                .ToArray());
    }

    private static ResourcePhase? AllocateResourcePhaseBackward(
        TimeSpan duration,
        DateTimeOffset latest,
        IReadOnlyList<InstantWindow> phaseAvailability,
        TimelineResourceRole role,
        IReadOnlyList<ResourceAvailability> resources,
        IReadOnlyDictionary<string, List<ResourceReservation>> occupiedResources,
        IReadOnlyList<string> requiredSkills,
        TimelineOperationInput operation)
    {
        if (duration == TimeSpan.Zero)
        {
            return new ResourcePhase(string.Empty, new Allocation([], latest), null);
        }

        return resources
            .Where(resource => resource.Role == role)
            .Where(resource => role != TimelineResourceRole.SetupWorker
                || HasRequiredSkill(resource.Skills, requiredSkills))
            .Select(resource =>
            {
                var occupations = occupiedResources.GetValueOrDefault(resource.ResourceId, []);
                var available = Subtract(
                    Intersect(phaseAvailability, resource.Availability),
                    occupations.SelectMany(value => value.Intervals).ToArray());
                var allocation = AllocateBackward(duration, latest, available);
                return allocation is null ? null : new ResourcePhase(resource.ResourceId, allocation, null);
            })
            .Where(value => value is not null)
            .Cast<ResourcePhase>()
            .OrderByDescending(value => value.Allocation.FinishesAt)
            .ThenByDescending(value => AllocationStart(value.Allocation, latest))
            .ThenBy(value => value.ResourceId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static TimelineInterval[] ProjectBackwardIntervals(
        Allocation allocation,
        TimelineIntervalType type,
        BacklogEntry entry,
        string? detail) => allocation.Intervals.Select(window => new TimelineInterval(
            type, entry.MachineId, entry.Operation.OperationId,
            window.StartsAt, window.EndsAt, detail)).ToArray();

    private static IReadOnlyList<TimelineInterval> BackwardWaitingIntervals(
        BacklogEntry entry,
        DateTimeOffset startsAt,
        DateTimeOffset finishesAt,
        IReadOnlyList<TimelineInterval> work,
        IReadOnlyList<TimelineDowntime> downtimes)
    {
        if (finishesAt <= startsAt) return [];
        return Subtract(
                [new InstantWindow(startsAt, finishesAt)],
                work.Select(value => new InstantWindow(value.StartsAt, value.EndsAt)).ToArray())
            .Select(window =>
            {
                var downtime = downtimes.FirstOrDefault(value => value.MachineId == entry.MachineId
                    && value.EndsAt > window.StartsAt && value.StartsAt < window.EndsAt);
                var detail = downtime is null
                    ? "resource:Backward placement waiting for configured calendar or worker availability."
                    : $"resource:Waiting because of {downtime.Reason}.";
                return new TimelineInterval(
                    TimelineIntervalType.Waiting, entry.MachineId, entry.Operation.OperationId,
                    window.StartsAt, window.EndsAt, detail);
            }).ToArray();
    }

    private static ScheduledNode? ScheduleLockedGroup(
        ScheduleNode node,
        IReadOnlyDictionary<string, BacklogEntry> operations,
        DateTimeOffset earliest,
        IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> machineWindows,
        IReadOnlyList<InstantWindow> setupWindows,
        IReadOnlyList<ResourceAvailability> resources,
        IReadOnlyDictionary<string, IReadOnlyList<string>> machineSkills,
        IReadOnlyDictionary<string, List<ResourceReservation>> occupiedResources,
        IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> dayShiftWindows,
        IReadOnlyList<TimelineDowntime> downtimes)
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
            else if (member.Operation.ProductionDuration > TimeSpan.Zero
                && member.Operation.PlannedQuantity > 0)
            {
                phaseWindows.Add(availability);
            }
        }

        var commonStart = FindCommonStart(earliest, phaseWindows);
        if (commonStart is null)
        {
            return null;
        }

        List<TimelineOperationResult>? results = null;
        List<ResourceReservation>? reservations = null;
        for (var attempt = 0; attempt < 64; attempt++)
        {
            results = [];
            reservations = [];
            var localOccupied = occupiedResources.ToDictionary(
                pair => pair.Key, pair => new List<ResourceReservation>(pair.Value), StringComparer.Ordinal);
            foreach (var member in members)
            {
                var scheduled = ScheduleMember(
                    member,
                    commonStart.Value,
                    machineWindows[member.MachineId],
                    setupWindows,
                    resources,
                    machineSkills.GetValueOrDefault(member.MachineId, []),
                    localOccupied,
                    dayShiftWindows,
                    downtimes);
                if (scheduled is null)
                {
                    return null;
                }

                results.Add(scheduled.Result);
                reservations.AddRange(scheduled.ResourceReservations);
                foreach (var reservation in scheduled.ResourceReservations)
                {
                    if (!localOccupied.TryGetValue(reservation.ResourceId, out var occupied))
                    {
                        occupied = [];
                        localOccupied.Add(reservation.ResourceId, occupied);
                    }
                    occupied.Add(reservation);
                }
            }

            var nextStart = results.Max(result => result.StartsAt);
            if (results.All(result => result.StartsAt == commonStart.Value))
            {
                break;
            }

            commonStart = FindCommonStart(nextStart, phaseWindows);
            if (commonStart is null)
            {
                return null;
            }
            results = null;
            reservations = null;
        }

        if (results is null || reservations is null)
        {
            return null;
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
        return new ScheduledNode(final, groupFinish, reservations);
    }

    private static ScheduledMember? ScheduleMember(
        BacklogEntry entry,
        DateTimeOffset earliest,
        IReadOnlyList<InstantWindow> machineAvailability,
        IReadOnlyList<InstantWindow> setupWindows,
        IReadOnlyList<ResourceAvailability> resources,
        IReadOnlyList<string> machineSkills,
        IReadOnlyDictionary<string, List<ResourceReservation>> occupiedResources,
        IReadOnlyDictionary<string, IReadOnlyList<InstantWindow>> dayShiftWindows,
        IReadOnlyList<TimelineDowntime> downtimes)
    {
        var productionAvailability = entry.Operation.DayShiftOnly
            ? dayShiftWindows.TryGetValue(entry.MachineId, out var dayWindows)
                ? Intersect(machineAvailability, dayWindows)
                : []
            : machineAvailability;
        var reservations = new List<ResourceReservation>();
        var waitingIntervals = new List<TimelineInterval>();

        var setupBaseAvailability = Intersect(machineAvailability, setupWindows);
        var setupPhase = AllocateResourcePhase(
            entry.Operation.SetupDuration, earliest, setupBaseAvailability,
            TimelineResourceRole.SetupWorker, resources, occupiedResources, machineSkills,
            entry.Operation);
        if (setupPhase is null)
        {
            return null;
        }
        AddReservation(reservations, setupPhase, entry.Operation);
        waitingIntervals.AddRange(PhaseWaiting(
            entry, earliest, setupPhase.Allocation,
            machineAvailability, machineAvailability, setupWindows, downtimes,
            setupPhase.WaitingDetail ?? "Waiting for a skilled setup worker."));

        var qaPhase = AllocateResourcePhase(
            entry.Operation.QaDuration, setupPhase.Allocation.FinishesAt, machineAvailability,
            TimelineResourceRole.QaWorker, resources, occupiedResources, [], entry.Operation);
        if (qaPhase is null)
        {
            return null;
        }
        AddReservation(reservations, qaPhase, entry.Operation);
        waitingIntervals.AddRange(PhaseWaiting(
            entry, setupPhase.Allocation.FinishesAt, qaPhase.Allocation,
            machineAvailability, machineAvailability, null, downtimes,
            qaPhase.WaitingDetail ?? "Waiting for a QA worker."));

        var productionAllocations = new List<Allocation>();
        var loadAllocations = new List<ScheduledLoad>();
        var phaseEarliest = qaPhase.Allocation.FinishesAt;
        var productionRuns = ProductionRuns(entry.Operation);
        var loadOccurrenceCount = LoadUnloadOccurrenceCount(entry.Operation);
        for (var runIndex = 0; runIndex < productionRuns.Count; runIndex++)
        {
            var run = productionRuns[runIndex];
            if (run.RequiresLoadUnload)
            {
                ResourcePhase? loadPhase = null;
                Allocation? loadUnload;
                if (entry.Operation.LoadUnloadRequiresWorker)
                {
                    loadPhase = AllocateResourcePhase(
                        entry.Operation.LoadUnloadDuration, phaseEarliest, machineAvailability,
                        TimelineResourceRole.RegularWorker, resources, occupiedResources, [], entry.Operation);
                    loadUnload = loadPhase?.Allocation;
                    if (loadPhase is not null)
                    {
                        AddReservation(reservations, loadPhase, entry.Operation);
                        waitingIntervals.AddRange(PhaseWaiting(
                            entry, phaseEarliest, loadPhase.Allocation,
                            machineAvailability, machineAvailability, null, downtimes,
                            loadPhase.WaitingDetail ?? "Waiting for a regular worker for load/unload."));
                    }
                }
                else
                {
                    loadUnload = Allocate(
                        entry.Operation.LoadUnloadDuration,
                        phaseEarliest,
                        machineAvailability);
                }
                if (loadUnload is null)
                {
                    return null;
                }
                if (!entry.Operation.LoadUnloadRequiresWorker)
                {
                    waitingIntervals.AddRange(PhaseWaiting(
                        entry, phaseEarliest, loadUnload,
                        machineAvailability, machineAvailability, null, downtimes,
                        "Waiting for Machine availability for load/unload."));
                }
                loadAllocations.Add(new ScheduledLoad(
                    loadUnload,
                    LoadUnloadDetail(
                        entry.Operation, runIndex + 1, loadOccurrenceCount,
                        loadPhase?.ResourceId)));
                phaseEarliest = loadUnload.FinishesAt;
            }

            var production = Allocate(
                ProductionRunDuration(entry.Operation, run),
                phaseEarliest,
                productionAvailability);
            if (production is null)
            {
                return null;
            }
            waitingIntervals.AddRange(PhaseWaiting(
                entry, phaseEarliest, production,
                machineAvailability, productionAvailability, null, downtimes,
                "Waiting for Machine availability for production."));
            productionAllocations.Add(production);
            phaseEarliest = production.FinishesAt;
        }

        var startsAt = setupPhase.Allocation.Intervals.FirstOrDefault()?.StartsAt
            ?? qaPhase.Allocation.Intervals.FirstOrDefault()?.StartsAt
            ?? loadAllocations.SelectMany(load => load.Allocation.Intervals)
                .FirstOrDefault()?.StartsAt
            ?? productionAllocations.SelectMany(production => production.Intervals)
                .FirstOrDefault()?.StartsAt
            ?? earliest;
        var setupIntervals = setupPhase.Allocation.Intervals.Select(window => new TimelineInterval(
            TimelineIntervalType.Setup,
            entry.MachineId,
            entry.Operation.OperationId,
            window.StartsAt,
            window.EndsAt,
            $"Setup worker: {setupPhase.ResourceId}")).ToArray();
        var productionIntervals = productionAllocations
            .SelectMany(production => production.Intervals)
            .Select(window => new TimelineInterval(
            TimelineIntervalType.Production,
            entry.MachineId,
            entry.Operation.OperationId,
            window.StartsAt,
            window.EndsAt)).ToArray();
        var qaIntervals = qaPhase.Allocation.Intervals.Select(window => new TimelineInterval(
            TimelineIntervalType.Qa, entry.MachineId, entry.Operation.OperationId,
            window.StartsAt, window.EndsAt, $"QA worker: {qaPhase.ResourceId}")).ToArray();
        var loadUnloadIntervals = loadAllocations.SelectMany(load =>
            load.Allocation.Intervals.Select(window => new TimelineInterval(
                TimelineIntervalType.LoadUnload, entry.MachineId, entry.Operation.OperationId,
                window.StartsAt, window.EndsAt, load.Detail))).ToArray();
        var result = new TimelineOperationResult(
            entry.Operation.OperationId,
            entry.MachineId,
            entry.BacklogPosition,
            startsAt,
            phaseEarliest,
            setupIntervals,
            productionIntervals,
            [],
            waitingIntervals,
            qaIntervals,
            loadUnloadIntervals);
        return new ScheduledMember(result, reservations);
    }

    private static ResourcePhase? AllocateResourcePhase(
        TimeSpan duration,
        DateTimeOffset earliest,
        IReadOnlyList<InstantWindow> phaseAvailability,
        TimelineResourceRole role,
        IReadOnlyList<ResourceAvailability> resources,
        IReadOnlyDictionary<string, List<ResourceReservation>> occupiedResources,
        IReadOnlyList<string> requiredSkills,
        TimelineOperationInput operation)
    {
        if (duration == TimeSpan.Zero)
        {
            return new ResourcePhase(string.Empty, new Allocation([], earliest), null);
        }

        return resources
            .Where(resource => resource.Role == role)
            .Where(resource => role != TimelineResourceRole.SetupWorker
                || HasRequiredSkill(resource.Skills, requiredSkills))
            .Select(resource =>
            {
                var occupations = occupiedResources.GetValueOrDefault(resource.ResourceId, []);
                var available = Subtract(
                    Intersect(phaseAvailability, resource.Availability),
                    occupations.SelectMany(value => value.Intervals).ToArray());
                var allocation = Allocate(duration, earliest, available);
                return allocation is null ? null : new ResourcePhase(
                    resource.ResourceId, allocation,
                    ResourcePriorityWaitingDetail(role, operation, earliest, allocation, occupations));
            })
            .Where(value => value is not null)
            .Cast<ResourcePhase>()
            .OrderBy(value => value.Allocation.Intervals[0].StartsAt)
            .ThenBy(value => value.Allocation.FinishesAt)
            .ThenBy(value => value.ResourceId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static bool HasRequiredSkill(
        IReadOnlyList<string> employeeSkills,
        IReadOnlyList<string> machineSkills)
    {
        if (employeeSkills.Any(value => string.Equals(value.Trim(), "*", StringComparison.Ordinal)))
        {
            return true;
        }

        return employeeSkills.Any(employeeSkill => machineSkills.Any(machineSkill =>
            string.Equals(employeeSkill.Trim(), machineSkill.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    private static int CompareReadyNodes(
        string left,
        string right,
        IReadOnlyDictionary<string, ScheduleNode> nodes,
        IReadOnlyDictionary<string, BacklogEntry> operations)
    {
        var comparer = Comparer<TimelineOperationInput>.Create(CompareOperationPriorities);
        var leftPriority = nodes[left].Members.Select(id => operations[id].Operation)
            .OrderBy(value => value, comparer).First();
        var rightPriority = nodes[right].Members.Select(id => operations[id].Operation)
            .OrderBy(value => value, comparer).First();
        var comparison = CompareOperationPriorities(leftPriority, rightPriority);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left, right);
    }

    private static int CompareOperationPriorities(
        TimelineOperationInput left,
        TimelineOperationInput right)
    {
        var dueComparison = Nullable.Compare(left.PriorityWorkFinishDate, right.PriorityWorkFinishDate);
        if (!left.PriorityWorkFinishDate.HasValue && right.PriorityWorkFinishDate.HasValue) dueComparison = 1;
        else if (left.PriorityWorkFinishDate.HasValue && !right.PriorityWorkFinishDate.HasValue) dueComparison = -1;
        if (dueComparison != 0) return dueComparison;

        var orderComparison = TimelinePriorityComparer.CompareOrderNumbers(
            left.PriorityOrderNumber, right.PriorityOrderNumber);
        return orderComparison != 0
            ? orderComparison
            : StringComparer.Ordinal.Compare(left.OperationId, right.OperationId);
    }

    private static string? ResourcePriorityWaitingDetail(
        TimelineResourceRole role,
        TimelineOperationInput operation,
        DateTimeOffset earliest,
        Allocation allocation,
        IReadOnlyList<ResourceReservation> occupations)
    {
        if (allocation.Intervals.Count == 0) return null;
        var blockers = occupations
            .Where(value => value.Intervals.Any(interval =>
                interval.EndsAt > earliest && interval.StartsAt < allocation.FinishesAt))
            .OrderBy(value => value.PriorityWorkFinishDate.HasValue ? 0 : 1)
            .ThenBy(value => value.PriorityWorkFinishDate)
            .ThenBy(value => value.PriorityOrderNumber,
                Comparer<string?>.Create(TimelinePriorityComparer.CompareOrderNumbers))
            .ThenBy(value => value.OperationId, StringComparer.Ordinal)
            .ToArray();
        if (blockers.Length == 0) return null;

        var winner = blockers[0];
        var roleLabel = role switch
        {
            TimelineResourceRole.SetupWorker => "a skilled setup worker",
            TimelineResourceRole.QaWorker => "a QA worker",
            TimelineResourceRole.RegularWorker => "a regular worker for load/unload",
            _ => "a worker"
        };
        if (winner.PriorityWorkFinishDate.HasValue
            && (!operation.PriorityWorkFinishDate.HasValue
                || winner.PriorityWorkFinishDate < operation.PriorityWorkFinishDate))
        {
            return $"Waiting for {roleLabel}; operation '{winner.OperationId}' received the resource first because its Work Finish Date {winner.PriorityWorkFinishDate:yyyy-MM-dd} is earlier.";
        }

        if (winner.PriorityWorkFinishDate == operation.PriorityWorkFinishDate
            && TimelinePriorityComparer.CompareOrderNumbers(
                winner.PriorityOrderNumber, operation.PriorityOrderNumber) < 0)
        {
            return $"Waiting for {roleLabel}; Work Finish Dates are equal, so smaller Order number {winner.PriorityOrderNumber} received the resource first.";
        }

        return $"Waiting for {roleLabel}; operation '{winner.OperationId}' received the resource first under the deterministic priority tie-break.";
    }

    private static void AddReservation(
        ICollection<ResourceReservation> reservations,
        ResourcePhase phase,
        TimelineOperationInput operation)
    {
        if (phase.ResourceId.Length > 0 && phase.Allocation.Intervals.Count > 0)
        {
            reservations.Add(new ResourceReservation(
                phase.ResourceId,
                phase.Allocation.Intervals,
                operation.OperationId,
                operation.PriorityWorkFinishDate,
                operation.PriorityOrderNumber));
        }
    }

    private static IReadOnlyList<TimelineInterval> PhaseWaiting(
        BacklogEntry entry,
        DateTimeOffset requestedAt,
        Allocation allocation,
        IReadOnlyList<InstantWindow> machineAvailability,
        IReadOnlyList<InstantWindow> operationAvailability,
        IReadOnlyList<InstantWindow>? phaseCalendarAvailability,
        IReadOnlyList<TimelineDowntime> downtimes,
        string fallbackDetail)
    {
        if (allocation.Intervals.Count == 0 || allocation.FinishesAt <= requestedAt)
        {
            return [];
        }

        var boundaries = new List<DateTimeOffset> { requestedAt, allocation.FinishesAt };
        AddBoundaries(boundaries, allocation.Intervals, requestedAt, allocation.FinishesAt);
        AddBoundaries(boundaries, machineAvailability, requestedAt, allocation.FinishesAt);
        AddBoundaries(boundaries, operationAvailability, requestedAt, allocation.FinishesAt);
        if (phaseCalendarAvailability is not null)
        {
            AddBoundaries(boundaries, phaseCalendarAvailability, requestedAt, allocation.FinishesAt);
        }
        foreach (var downtime in downtimes.Where(value =>
                     value.MachineId == entry.MachineId
                     && value.EndsAt > requestedAt
                     && value.StartsAt < allocation.FinishesAt))
        {
            boundaries.Add(downtime.StartsAt < requestedAt ? requestedAt : downtime.StartsAt);
            boundaries.Add(downtime.EndsAt > allocation.FinishesAt
                ? allocation.FinishesAt
                : downtime.EndsAt);
        }

        var ordered = boundaries.Distinct().OrderBy(value => value).ToArray();
        var waits = new List<TimelineInterval>();
        for (var index = 0; index < ordered.Length - 1; index++)
        {
            var start = ordered[index];
            var end = ordered[index + 1];
            if (end <= start)
            {
                continue;
            }

            var sample = start.AddTicks((end - start).Ticks / 2);
            if (Contains(allocation.Intervals, sample))
            {
                continue;
            }

            var downtime = downtimes.FirstOrDefault(value =>
                value.MachineId == entry.MachineId
                && sample >= value.StartsAt
                && sample < value.EndsAt);
            var detail = downtime is not null
                ? $"Waiting because of {downtime.Reason}."
                : !Contains(machineAvailability, sample)
                    ? "Waiting for machine working calendar."
                    : entry.Operation.DayShiftOnly && !Contains(operationAvailability, sample)
                        ? "Waiting for day-shift availability."
                        : phaseCalendarAvailability is not null
                          && !Contains(phaseCalendarAvailability, sample)
                            ? "Waiting for setup calendar."
                            : fallbackDetail;
            if (waits.Count > 0
                && waits[^1].EndsAt == start
                && string.Equals(waits[^1].Detail, detail, StringComparison.Ordinal))
            {
                waits[^1] = waits[^1] with { EndsAt = end };
            }
            else
            {
                waits.Add(new TimelineInterval(
                    TimelineIntervalType.Waiting,
                    entry.MachineId,
                    entry.Operation.OperationId,
                    start,
                    end,
                    $"resource:{detail}"));
            }
        }

        return waits;
    }

    private static void AddBoundaries(
        ICollection<DateTimeOffset> boundaries,
        IReadOnlyList<InstantWindow> windows,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd)
    {
        foreach (var window in windows.Where(value =>
                     value.EndsAt > rangeStart && value.StartsAt < rangeEnd))
        {
            boundaries.Add(window.StartsAt < rangeStart ? rangeStart : window.StartsAt);
            boundaries.Add(window.EndsAt > rangeEnd ? rangeEnd : window.EndsAt);
        }
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
            var machineOperations = operationResults
                .Where(operation => string.Equals(
                    operation.MachineId,
                    backlog.MachineId,
                    StringComparison.Ordinal))
                .ToArray();
            var operationIntervals = machineOperations
                .SelectMany(operation => operation.SetupIntervals
                    .Concat(operation.QaIntervals ?? [])
                    .Concat(operation.LoadUnloadIntervals ?? [])
                    .Concat(operation.ProductionIntervals)
                    .Concat(operation.ReservedIntervals)
                    .Concat(operation.WaitingIntervals))
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
                .Select(value =>
                {
                    if (Clip(value.StartsAt, value.EndsAt, horizonStart, horizonEnd) is not { } window)
                        return null;
                    var delayed = machineOperations.FirstOrDefault(operation =>
                    {
                        var work = operation.SetupIntervals
                            .Concat(operation.QaIntervals ?? [])
                            .Concat(operation.LoadUnloadIntervals ?? [])
                            .Concat(operation.ProductionIntervals)
                            .ToArray();
                        return work.Any(interval => interval.EndsAt <= window.StartsAt)
                            && work.Any(interval => interval.StartsAt >= window.EndsAt);
                    });
                    return new TimelineInterval(
                        TimelineIntervalType.Downtime,
                        backlog.MachineId,
                        delayed?.OperationId,
                        window.StartsAt,
                        window.EndsAt,
                        delayed is null ? value.Reason : $"Operation delayed by {value.Reason}");
                })
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

    private static Allocation? AllocateBackward(
        TimeSpan duration,
        DateTimeOffset latest,
        IReadOnlyList<InstantWindow> windows)
    {
        if (duration == TimeSpan.Zero)
        {
            return new Allocation([], latest);
        }

        var remainingTicks = duration.Ticks;
        var intervals = new List<InstantWindow>();
        for (var index = windows.Count - 1; index >= 0; index--)
        {
            var window = windows[index];
            if (window.StartsAt >= latest)
            {
                continue;
            }

            var end = window.EndsAt < latest ? window.EndsAt : latest;
            var availableTicks = (end - window.StartsAt).Ticks;
            if (availableTicks <= 0)
            {
                continue;
            }

            var usedTicks = Math.Min(availableTicks, remainingTicks);
            var start = end.AddTicks(-usedTicks);
            intervals.Insert(0, new InstantWindow(start, end));
            remainingTicks -= usedTicks;
            latest = start;
            if (remainingTicks == 0)
            {
                return new Allocation(intervals, intervals[^1].EndsAt);
            }
        }

        return null;
    }

    private static DateTimeOffset AllocationStart(Allocation allocation, DateTimeOffset fallback) =>
        allocation.Intervals.Count == 0 ? fallback : allocation.Intervals[0].StartsAt;

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
        var normalizedBlocked = Merge(blocked);
        var result = new List<InstantWindow>();
        foreach (var window in source)
        {
            var cursor = window.StartsAt;
            foreach (var block in normalizedBlocked.Where(block =>
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
        DateTimeOffset FinishesAt,
        IReadOnlyList<ResourceReservation> ResourceReservations);

    private sealed record ScheduledMember(
        TimelineOperationResult Result,
        IReadOnlyList<ResourceReservation> ResourceReservations);

    private sealed record ResourceAvailability(
        string ResourceId,
        TimelineResourceRole Role,
        IReadOnlyList<InstantWindow> Availability,
        IReadOnlyList<string> Skills);

    private sealed record ResourcePhase(
        string ResourceId,
        Allocation Allocation,
        string? WaitingDetail);

    private sealed record ProductionRun(int PartCount, bool RequiresLoadUnload);

    private sealed record ScheduledLoad(Allocation Allocation, string Detail);

    private sealed record ResourceReservation(
        string ResourceId,
        IReadOnlyList<InstantWindow> Intervals,
        string OperationId,
        DateOnly? PriorityWorkFinishDate,
        string? PriorityOrderNumber);

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
