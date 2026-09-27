using System.Globalization;
using Meimad.Planner.Server.Domain.ResourcePlanning;
using Meimad.Planner.Server.Domain.Timeline;

namespace Meimad.Planner.Server.Application.Timeline;

/// <summary>
/// Hangs the auxiliary resource requirements of every scheduled Batch Operation off its Machine
/// anchor and lets the deterministic allocator (rule 34) place them: preparation steps latest-fit
/// before the Machine start, following steps earliest-fit after the Machine finish, on Workstations,
/// qualified Employees and External Resources. The result is a projection only: nothing is
/// persisted, Machine assignments and backlog order are untouched, and a missing or invalid
/// configuration becomes a conflict instead of a silent gap. Delivery risk is reported per Batch
/// once every one of its auxiliary steps has a feasible slot.
/// </summary>
internal static class TimelineAuxiliaryProjector
{
    internal const string WorkIdSeparator = "|";

    internal sealed record Input(
        DateTimeOffset HorizonStart,
        DateTimeOffset HorizonEnd,
        IReadOnlyList<TimelineSourceOperation> Operations,
        IReadOnlyDictionary<string, TimelineOperationResult> Results,
        IReadOnlyList<TimelineSourceRequirement> Requirements,
        IReadOnlyList<TimelineSourceWorkstation> Workstations,
        IReadOnlyList<TimelineSourceExternalResource> ExternalResources,
        IReadOnlyList<TimelineSourceResource> Employees,
        IReadOnlyList<TimelineResourceCalendar> EmployeeCalendars,
        IReadOnlyList<TimelineSourceAuxiliaryPin> Pins,
        Func<string, string?, string, IReadOnlyList<TimelineWindow>> ReadCalendar,
        string DisplayTimeZoneId);

    internal sealed record Output(
        IReadOnlyList<TimelineProjectionResourceLane> Lanes,
        IReadOnlyList<TimelineProjectionConflict> Conflicts,
        IReadOnlyDictionary<string, DateTimeOffset> PredictedCompletionByBatch);

    internal static string WorkId(string batchOperationId, string requirementId) =>
        $"{batchOperationId}{WorkIdSeparator}{requirementId}";

    internal static Output Project(Input input, AutomaticResourceScheduler scheduler)
    {
        var conflicts = new List<TimelineProjectionConflict>();
        var predictedByBatch = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var group in input.Operations
                     .Where(operation => input.Results.ContainsKey(operation.OperationId))
                     .GroupBy(operation => operation.BatchId, StringComparer.Ordinal))
        {
            predictedByBatch[group.Key] = group.Max(operation => input.Results[operation.OperationId].FinishesAt);
        }
        var requirementsByOperation = input.Requirements
            .Where(requirement => requirement.IsActive)
            .GroupBy(requirement => requirement.CaseOperationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.SequencePosition).ToArray(), StringComparer.Ordinal);
        if (requirementsByOperation.Count == 0)
        {
            return new Output([], conflicts, predictedByBatch);
        }

        var pins = input.Pins.ToDictionary(pin => WorkId(pin.BatchOperationId, pin.RequirementId), StringComparer.Ordinal);
        var work = new List<ResourceWorkItem>();
        var workContext = new Dictionary<string, WorkContext>(StringComparer.Ordinal);
        foreach (var operation in input.Operations.OrderBy(value => value.BatchId, StringComparer.Ordinal).ThenBy(value => value.RoutePositionOrNumber()))
        {
            if (!requirementsByOperation.TryGetValue(operation.SourceCaseOperationId, out var requirements)) continue;
            DateTimeOffset? machineStart = null;
            DateTimeOffset? machineFinish = null;
            if (input.Results.TryGetValue(operation.OperationId, out var machineResult))
            {
                machineStart = machineResult.StartsAt;
                machineFinish = machineResult.FinishesAt;
            }
            else if (operation.Status == "completed" && operation.ActualEnd is { } actualEnd)
            {
                machineFinish = actualEnd;
            }
            if (machineStart is null && machineFinish is null) continue;

            var delivery = DeliveryDeadline(operation.PriorityWorkFinishDate, input.DisplayTimeZoneId);
            var requirementIds = requirements.Select(item => item.RequirementId).ToHashSet(StringComparer.Ordinal);
            foreach (var requirement in requirements)
            {
                var backward = requirement.Direction == "BACKWARD";
                var anchor = backward ? machineStart : machineFinish;
                if (anchor is null || anchor < input.HorizonStart || anchor > input.HorizonEnd) continue;
                var seconds = (long)requirement.EstimatedDurationSeconds
                    + (long)requirement.DurationPerUnitSeconds * Math.Max(0, operation.PlannedQuantity);
                var workId = WorkId(operation.OperationId, requirement.RequirementId);
                pins.TryGetValue(workId, out var pin);
                var dependsOn = requirement.PredecessorRequirementId is { } predecessor && requirementIds.Contains(predecessor)
                    ? WorkId(operation.OperationId, predecessor)
                    : null;
                work.Add(new ResourceWorkItem(
                    workId,
                    TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds / 4)),
                    backward ? ResourceScheduleDirection.Backward : ResourceScheduleDirection.Forward,
                    anchor.Value,
                    new ResourceWorkRequirement(
                        requirement.ResourceClass == "WORKSTATION" ? requirement.WorkstationTypeId : null,
                        requirement.ResourceClass == "WORKSTATION" ? requirement.RequiredCapability : null,
                        requirement.RequiredSkillId,
                        Math.Max(1, requirement.CapacityRequired),
                        requirement.ResourceClass == "EXTERNAL" ? requirement.ExternalResourceId : null),
                    dependsOn,
                    delivery,
                    false,
                    pin?.WorkstationId,
                    pin?.EmployeeId,
                    pin?.StartsAt));
                workContext[workId] = new WorkContext(operation, requirement, pin is not null);
            }
        }
        // A dependency on a step that was left out (anchor outside the horizon) is dropped rather
        // than failing the whole calculation.
        var workIds = work.Select(item => item.WorkId).ToHashSet(StringComparer.Ordinal);
        work = work.Select(item => item.DependsOnWorkId is not null && !workIds.Contains(item.DependsOnWorkId)
            ? item with { DependsOnWorkId = null }
            : item).ToList();
        if (work.Count == 0)
        {
            return new Output([], conflicts, predictedByBatch);
        }

        var workstations = input.Workstations.Select(workstation => new ResourceCandidate(
            workstation.WorkstationId,
            ResourceBaseClass.Workstation,
            input.ReadCalendar(workstation.CalendarJson, workstation.TimeZoneId, $"Workstation {workstation.Name} calendar")
                .Select(window => new ResourceAvailabilityWindow(window.StartsAt, window.EndsAt)).ToArray(),
            workstation.Capacity,
            workstation.WorkstationTypeId,
            workstation.Capabilities)).ToArray();
        var employeeCalendars = input.EmployeeCalendars.ToDictionary(calendar => calendar.ResourceId, StringComparer.Ordinal);
        var employees = input.Employees
            .Where(employee => employeeCalendars.ContainsKey(employee.ResourceId))
            .Select(employee => new ResourceCandidate(
                employee.ResourceId,
                ResourceBaseClass.Employee,
                employeeCalendars[employee.ResourceId].Availability.Select(window => new ResourceAvailabilityWindow(window.StartsAt, window.EndsAt)).ToArray(),
                1,
                null,
                null,
                employee.OperationalSkillIds ?? []))
            .ToArray();
        var externals = input.ExternalResources.Select(external => new ExternalResourceCandidate(
            external.ExternalResourceId,
            TimeSpan.FromMinutes(external.PromisedLeadTimeMinutes),
            TimeSpan.FromMinutes(external.SafetyBufferMinutes),
            external.LeadTimeSemantics == "WORKING_TIME",
            external.LeadTimeSemantics == "WORKING_TIME" && external.CalendarJson is not null
                ? input.ReadCalendar(external.CalendarJson, external.TimeZoneId, $"External Resource {external.Name} calendar")
                    .Select(window => new ResourceAvailabilityWindow(window.StartsAt, window.EndsAt)).ToArray()
                : null)).ToArray();

        ResourcePlanningResult result;
        try
        {
            result = scheduler.Calculate(new ResourcePlanningInput(
                input.HorizonStart,
                input.HorizonEnd,
                work,
                workstations,
                employees,
                externals));
        }
        catch (ResourcePlanningException exception)
        {
            conflicts.Add(new TimelineProjectionConflict(
                "auxiliary_planning_failed", "auxiliary_planning_failed", "attention",
                $"Auxiliary resource planning was not calculated: {exception.Message} ({exception.Code}).",
                workContext.Values.Select(context => context.Operation.OperationId).Distinct(StringComparer.Ordinal).ToArray(),
                []));
            return new Output([], conflicts, predictedByBatch);
        }

        var batchesWithIssues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var issue in result.BlockingConfigurationErrors)
        {
            if (!workContext.TryGetValue(issue.WorkId, out var context)) continue;
            batchesWithIssues.Add(context.Operation.BatchId);
            var isConfiguration = issue.Code is "eligible_workstation_missing" or "qualified_employee_missing" or "external_resource_missing";
            conflicts.Add(new TimelineProjectionConflict(
                $"{(isConfiguration ? "auxiliary_resource_configuration_missing" : "auxiliary_slot_unavailable")}:{issue.WorkId}",
                isConfiguration ? "auxiliary_resource_configuration_missing" : "auxiliary_slot_unavailable",
                isConfiguration ? "warning" : "attention",
                $"Batch {context.Operation.BatchNumber} OP{context.Operation.OperationNumber} {StepLabel(context.Requirement)}: {issue.Message} ({issue.Code})",
                [context.Operation.OperationId],
                context.Operation.MachineId is null ? [] : [context.Operation.MachineId]));
        }

        var lanes = new Dictionary<string, (string Class, string Name, List<TimelineProjectionResourceInterval> Intervals)>(StringComparer.Ordinal);
        var workstationNames = input.Workstations.ToDictionary(item => item.WorkstationId, item => item.Name, StringComparer.Ordinal);
        var externalNames = input.ExternalResources.ToDictionary(item => item.ExternalResourceId, item => item.Name, StringComparer.Ordinal);
        var employeeNames = input.Employees.ToDictionary(item => item.ResourceId, item => item.Name ?? item.ResourceId, StringComparer.Ordinal);
        foreach (var assignment in result.Assignments)
        {
            if (!workContext.TryGetValue(assignment.WorkId, out var context)) continue;
            var operation = context.Operation;
            var interval = new TimelineProjectionResourceInterval(
                assignment.WorkId, operation.OperationId, context.Requirement.RequirementId, operation.BatchId,
                operation.BatchNumber, operation.PartNumber, operation.OperationNumber, operation.OperationName,
                context.Requirement.StepNumber, context.Requirement.Name ?? StepLabel(context.Requirement),
                context.Requirement.Direction, assignment.StartsAt, assignment.EndsAt, assignment.IsPinned,
                assignment.Explanation, assignment.WorkstationId, assignment.EmployeeId, assignment.ExternalResourceId,
                context.Requirement.ResourceClass);
            if (assignment.WorkstationId is not null)
                AddToLane(lanes, assignment.WorkstationId, "workstation", workstationNames.GetValueOrDefault(assignment.WorkstationId) ?? assignment.WorkstationId, interval);
            if (assignment.EmployeeId is not null)
                AddToLane(lanes, assignment.EmployeeId, "employee", employeeNames.GetValueOrDefault(assignment.EmployeeId) ?? assignment.EmployeeId, interval);
            if (assignment.ExternalResourceId is not null)
                AddToLane(lanes, assignment.ExternalResourceId, "external", externalNames.GetValueOrDefault(assignment.ExternalResourceId) ?? assignment.ExternalResourceId, interval);
            if (assignment.EndsAt > predictedByBatch.GetValueOrDefault(operation.BatchId, DateTimeOffset.MinValue))
                predictedByBatch[operation.BatchId] = assignment.EndsAt;
        }

        // Delivery risk only after a feasible resource-constrained timeline exists for the Batch.
        foreach (var batch in workContext.Values.GroupBy(context => context.Operation.BatchId, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            if (batchesWithIssues.Contains(batch.Key)) continue;
            var first = batch.First().Operation;
            var deadline = DeliveryDeadline(first.PriorityWorkFinishDate, input.DisplayTimeZoneId);
            if (deadline is null || !predictedByBatch.TryGetValue(batch.Key, out var predicted) || predicted <= deadline) continue;
            conflicts.Add(new TimelineProjectionConflict(
                $"delivery_at_risk:{batch.Key}", "delivery_at_risk", "attention",
                $"Batch {first.BatchNumber} ({first.PartNumber}) is predicted to finish its route, including auxiliary steps, at {predicted:O}, after the work finish date {first.PriorityWorkFinishDate:yyyy-MM-dd}.",
                batch.Select(context => context.Operation.OperationId).Distinct(StringComparer.Ordinal).ToArray(),
                batch.Select(context => context.Operation.MachineId).Where(id => id is not null).Distinct(StringComparer.Ordinal).ToArray()!));
        }

        var orderedLanes = lanes
            .OrderBy(pair => pair.Value.Class switch { "workstation" => 0, "external" => 1, _ => 2 })
            .ThenBy(pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new TimelineProjectionResourceLane(
                pair.Key, pair.Value.Class, pair.Value.Name,
                pair.Value.Intervals.OrderBy(interval => interval.StartsAt).ThenBy(interval => interval.WorkId, StringComparer.Ordinal).ToArray()))
            .ToArray();
        return new Output(orderedLanes, conflicts, predictedByBatch);
    }

    private static void AddToLane(
        Dictionary<string, (string Class, string Name, List<TimelineProjectionResourceInterval> Intervals)> lanes,
        string resourceId, string resourceClass, string name, TimelineProjectionResourceInterval interval)
    {
        if (!lanes.TryGetValue(resourceId, out var lane))
        {
            lane = (resourceClass, name, []);
            lanes[resourceId] = lane;
        }
        lane.Intervals.Add(interval);
    }

    private static string StepLabel(TimelineSourceRequirement requirement) =>
        requirement.StepNumber is { } step
            ? $"step {step.ToString(CultureInfo.InvariantCulture)} '{requirement.Name ?? requirement.ResourceClass}'"
            : $"'{requirement.Name ?? requirement.ResourceClass}'";

    /// <summary>The end of the work-finish day in the display time zone, or UTC when it is unknown.</summary>
    internal static DateTimeOffset? DeliveryDeadline(DateOnly? workFinishDate, string displayTimeZoneId)
    {
        if (workFinishDate is null) return null;
        var localEnd = workFinishDate.Value.ToDateTime(TimeOnly.MinValue).AddDays(1);
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(displayTimeZoneId);
            return new DateTimeOffset(localEnd, zone.GetUtcOffset(localEnd)).ToUniversalTime();
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return new DateTimeOffset(localEnd, TimeSpan.Zero);
        }
    }

    private static int RoutePositionOrNumber(this TimelineSourceOperation operation) => operation.OperationNumber;

    private sealed record WorkContext(TimelineSourceOperation Operation, TimelineSourceRequirement Requirement, bool IsPinned);
}
