namespace Meimad.Planner.Server.Domain.ResourcePlanning;

/// <summary>
/// Deterministic finite-capacity allocator. It does not mutate Machine assignments or backlog order.
/// Machine work is supplied as an anchor; preparation is latest-fit backward and post-work is
/// earliest-fit forward. Stable resource IDs are the final tie-breaker.
/// </summary>
internal sealed class AutomaticResourceScheduler
{
    internal ResourcePlanningResult Calculate(ResourcePlanningInput input)
    {
        Validate(input);
        var reservations = (input.FixedReservations ?? []).ToList();
        var assignments = new List<ProvisionalResourceAssignment>();
        var issues = new List<ResourcePlanningIssue>();
        var byId = input.Work.ToDictionary(value => value.WorkId, StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);

        while (completed.Count < input.Work.Count)
        {
            var ready = input.Work
                .Where(value => !completed.Contains(value.WorkId)
                    && (value.DependsOnWorkId is null || completed.Contains(value.DependsOnWorkId)))
                .OrderBy(value => value.Direction == ResourceScheduleDirection.Backward ? 0 : 1)
                .ThenBy(value => value.Anchor)
                .ThenBy(value => value.WorkId, StringComparer.Ordinal)
                .ToArray();
            if (ready.Length == 0)
                throw new ResourcePlanningException("resource_dependency_cycle", "Resource work dependencies contain a cycle.");

            foreach (var work in ready)
            {
                var anchor = ResolveAnchor(work, assignments, byId);
                var result = Allocate(input, work, anchor, reservations);
                if (result.Assignment is null)
                {
                    issues.Add(new(work.WorkId, result.Code!, result.Message!));
                }
                else
                {
                    assignments.Add(result.Assignment);
                    AddReservations(result.Assignment, work.Requirement.WorkstationCapacity, reservations);
                }
                completed.Add(work.WorkId);
            }
        }

        var predictedCompletion = assignments.Count == 0
            ? input.HorizonStart
            : assignments.Max(value => value.EndsAt);
        var anchoredBackward = input.Work.Where(value => value.Direction == ResourceScheduleDirection.Backward).ToArray();
        var predictedShift = anchoredBackward.Length == 0 ? TimeSpan.Zero : anchoredBackward
            .Select(work => assignments.SingleOrDefault(value => value.WorkId == work.WorkId))
            .Where(value => value is not null && value.EndsAt > byId[value.WorkId].Anchor)
            .Select(value => value!.EndsAt - byId[value.WorkId].Anchor)
            .DefaultIfEmpty(TimeSpan.Zero).Max();
        var delivery = input.Work.Where(value => value.RequiredDeliveryAt is not null)
            .Select(value => value.RequiredDeliveryAt!.Value).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();

        var load = reservations.Select(value => new ResourceLoadInterval(value.ResourceClass, value.ResourceId, value.WorkId,
                value.StartsAt, value.EndsAt, value.Capacity))
            .OrderBy(value => value.ResourceClass).ThenBy(value => value.ResourceId, StringComparer.Ordinal)
            .ThenBy(value => value.StartsAt).ThenBy(value => value.WorkId, StringComparer.Ordinal).ToArray();
        return new(assignments.OrderBy(value => value.StartsAt).ThenBy(value => value.WorkId, StringComparer.Ordinal).ToArray(),
            load, issues, predictedShift, predictedCompletion, predictedCompletion > delivery);
    }

    private static DateTimeOffset ResolveAnchor(
        ResourceWorkItem work,
        IReadOnlyList<ProvisionalResourceAssignment> assignments,
        IReadOnlyDictionary<string, ResourceWorkItem> byId)
    {
        if (work.DependsOnWorkId is null) return work.Anchor;
        var predecessor = assignments.SingleOrDefault(value => value.WorkId == work.DependsOnWorkId);
        if (predecessor is null) return work.Anchor;
        return work.Direction == ResourceScheduleDirection.Forward
            ? predecessor.EndsAt
            : (byId[work.DependsOnWorkId].Direction == ResourceScheduleDirection.Backward
                ? predecessor.StartsAt : work.Anchor);
    }

    private static AllocationResult Allocate(
        ResourcePlanningInput input,
        ResourceWorkItem work,
        DateTimeOffset anchor,
        IReadOnlyList<ExistingResourceReservation> reservations)
    {
        if (work.Requirement.ExternalResourceId is not null)
        {
            var resource = input.ExternalResources.SingleOrDefault(value => value.ResourceId == work.Requirement.ExternalResourceId);
            if (resource is null) return AllocationResult.Fail("external_resource_missing", "The configured External Resource does not exist.");
            var starts = work.Direction == ResourceScheduleDirection.Forward ? anchor :
                SubtractLead(anchor, resource.PromisedLeadTime + resource.SafetyBuffer, resource);
            var ends = work.Direction == ResourceScheduleDirection.Forward
                ? AddLead(starts, resource.PromisedLeadTime + resource.SafetyBuffer, resource) : anchor;
            return AllocationResult.Ok(new(work.WorkId, starts, ends, null, null, resource.ResourceId,
                work.PinnedStartsAt is not null, "External promised lead time plus Meimad safety buffer; no internal capacity reserved."));
        }

        var workstationRequired = work.Requirement.WorkstationTypeId is not null || work.Requirement.WorkstationCapability is not null;
        var employeeRequired = work.Requirement.EmployeeSkillId is not null;
        var workstations = workstationRequired
            ? input.Workstations.Where(value => value.ResourceClass == ResourceBaseClass.Workstation
                && value.Capacity >= work.Requirement.WorkstationCapacity
                && (work.Requirement.WorkstationTypeId is null || value.TypeId == work.Requirement.WorkstationTypeId)
                && (work.Requirement.WorkstationCapability is null ||
                    (value.Capabilities ?? []).Contains(work.Requirement.WorkstationCapability, StringComparer.OrdinalIgnoreCase))
                && (work.PinnedWorkstationId is null || value.ResourceId == work.PinnedWorkstationId))
                .OrderBy(value => value.ResourceId, StringComparer.Ordinal).Cast<ResourceCandidate?>().ToArray()
            : [null];
        var employees = employeeRequired
            ? input.Employees.Where(value => value.ResourceClass == ResourceBaseClass.Employee
                && (value.SkillIds ?? []).Contains(work.Requirement.EmployeeSkillId!, StringComparer.Ordinal)
                && (work.PinnedEmployeeId is null || value.ResourceId == work.PinnedEmployeeId))
                .OrderBy(value => value.ResourceId, StringComparer.Ordinal).Cast<ResourceCandidate?>().ToArray()
            : [null];
        if (workstationRequired && workstations.Length == 0)
            return AllocationResult.Fail("eligible_workstation_missing", "No active Workstation satisfies the required type/capability and capacity.");
        if (employeeRequired && employees.Length == 0)
            return AllocationResult.Fail("qualified_employee_missing", "No active Employee has the required Skill.");

        var candidates = new List<ProvisionalResourceAssignment>();
        foreach (var workstation in workstations)
        foreach (var employee in employees)
        {
            var slot = FindJointSlot(input, work, anchor, workstation, employee, reservations);
            if (slot is null) continue;
            candidates.Add(new(work.WorkId, slot.Value.Start, slot.Value.End,
                workstation?.ResourceId, employee?.ResourceId, null,
                work.PinnedStartsAt is not null || work.PinnedEmployeeId is not null || work.PinnedWorkstationId is not null,
                work.Direction == ResourceScheduleDirection.Backward
                    ? "Latest feasible preparation slot before the Machine anchor."
                    : "Earliest feasible slot after the predecessor/anchor."));
        }
        var selected = work.Direction == ResourceScheduleDirection.Backward
            ? candidates.OrderByDescending(value => value.EndsAt).ThenBy(value => value.WorkstationId, StringComparer.Ordinal)
                .ThenBy(value => value.EmployeeId, StringComparer.Ordinal).FirstOrDefault()
            : candidates.OrderBy(value => value.StartsAt).ThenBy(value => value.EndsAt)
                .ThenBy(value => value.WorkstationId, StringComparer.Ordinal)
                .ThenBy(value => value.EmployeeId, StringComparer.Ordinal).FirstOrDefault();
        if (selected is null && work.Direction == ResourceScheduleDirection.Backward)
        {
            var shifted = work with { Direction = ResourceScheduleDirection.Forward };
            foreach (var workstation in workstations)
            foreach (var employee in employees)
            {
                var slot = FindJointSlot(input, shifted, anchor, workstation, employee, reservations);
                if (slot is null) continue;
                candidates.Add(new(work.WorkId, slot.Value.Start, slot.Value.End,
                    workstation?.ResourceId, employee?.ResourceId, null,
                    work.PinnedEmployeeId is not null || work.PinnedWorkstationId is not null,
                    "No pre-anchor slot exists; earliest feasible preparation predicts a Machine-start shift."));
            }
            selected = candidates.OrderBy(value => value.EndsAt).ThenBy(value => value.StartsAt)
                .ThenBy(value => value.WorkstationId, StringComparer.Ordinal)
                .ThenBy(value => value.EmployeeId, StringComparer.Ordinal).FirstOrDefault();
        }
        return selected is null
            ? AllocationResult.Fail("no_feasible_resource_slot", "Eligible resources exist, but no joint calendar/capacity slot exists in the planning horizon.")
            : AllocationResult.Ok(selected);
    }

    private static (DateTimeOffset Start, DateTimeOffset End)? FindJointSlot(
        ResourcePlanningInput input, ResourceWorkItem work, DateTimeOffset anchor,
        ResourceCandidate? workstation, ResourceCandidate? employee,
        IReadOnlyList<ExistingResourceReservation> reservations)
    {
        if (work.PinnedStartsAt is not null)
        {
            var end = work.PinnedStartsAt.Value + work.Duration;
            return SlotAvailable(work.PinnedStartsAt.Value, end, workstation, employee, work.Requirement.WorkstationCapacity, reservations)
                ? (work.PinnedStartsAt.Value, end) : null;
        }
        var windows = Intersect(workstation?.Availability, employee?.Availability, input.HorizonStart, input.HorizonEnd);
        var points = new HashSet<DateTimeOffset>();
        foreach (var window in windows)
        {
            if (work.Direction == ResourceScheduleDirection.Forward)
            {
                points.Add(window.StartsAt > anchor ? window.StartsAt : anchor);
                foreach (var reservation in reservations.Where(value => value.EndsAt >= window.StartsAt && value.EndsAt <= window.EndsAt))
                    points.Add(reservation.EndsAt > anchor ? reservation.EndsAt : anchor);
            }
            else
            {
                points.Add((window.EndsAt < anchor ? window.EndsAt : anchor) - work.Duration);
                foreach (var reservation in reservations.Where(value => value.StartsAt >= window.StartsAt && value.StartsAt <= window.EndsAt))
                    points.Add((reservation.StartsAt < anchor ? reservation.StartsAt : anchor) - work.Duration);
            }
        }
        var ordered = work.Direction == ResourceScheduleDirection.Forward ? points.Order() : points.OrderDescending();
        foreach (var start in ordered)
        {
            var end = start + work.Duration;
            if (start < input.HorizonStart || end > input.HorizonEnd) continue;
            if (work.Direction == ResourceScheduleDirection.Forward && start < anchor) continue;
            if (work.Direction == ResourceScheduleDirection.Backward && end > anchor) continue;
            if (!windows.Any(window => window.StartsAt <= start && window.EndsAt >= end)) continue;
            if (SlotAvailable(start, end, workstation, employee, work.Requirement.WorkstationCapacity, reservations)) return (start, end);
        }
        return null;
    }

    private static bool SlotAvailable(DateTimeOffset start, DateTimeOffset end,
        ResourceCandidate? workstation, ResourceCandidate? employee, int workstationCapacity,
        IReadOnlyList<ExistingResourceReservation> reservations)
    {
        if (employee is not null && reservations.Any(value => value.ResourceClass == ResourceBaseClass.Employee
                && value.ResourceId == employee.ResourceId && Overlaps(start, end, value.StartsAt, value.EndsAt))) return false;
        if (workstation is not null)
        {
            var used = reservations.Where(value => value.ResourceClass == ResourceBaseClass.Workstation
                    && value.ResourceId == workstation.ResourceId && Overlaps(start, end, value.StartsAt, value.EndsAt))
                .Sum(value => value.Capacity);
            if (used + workstationCapacity > workstation.Capacity) return false;
        }
        return true;
    }

    private static IReadOnlyList<ResourceAvailabilityWindow> Intersect(
        IReadOnlyList<ResourceAvailabilityWindow>? left,
        IReadOnlyList<ResourceAvailabilityWindow>? right,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd)
    {
        left ??= [new(horizonStart, horizonEnd)];
        right ??= [new(horizonStart, horizonEnd)];
        return (from a in left from b in right
                let start = a.StartsAt > b.StartsAt ? a.StartsAt : b.StartsAt
                let end = a.EndsAt < b.EndsAt ? a.EndsAt : b.EndsAt
                where start < end select new ResourceAvailabilityWindow(start, end))
            .OrderBy(value => value.StartsAt).ToArray();
    }

    private static void AddReservations(ProvisionalResourceAssignment assignment, int workstationCapacity,
        ICollection<ExistingResourceReservation> values)
    {
        if (assignment.WorkstationId is not null) values.Add(new(assignment.WorkId, assignment.WorkstationId,
            ResourceBaseClass.Workstation, assignment.StartsAt, assignment.EndsAt, workstationCapacity, false));
        if (assignment.EmployeeId is not null) values.Add(new(assignment.WorkId, assignment.EmployeeId,
            ResourceBaseClass.Employee, assignment.StartsAt, assignment.EndsAt, 1, false));
    }

    private static DateTimeOffset AddLead(DateTimeOffset start, TimeSpan duration, ExternalResourceCandidate resource) =>
        resource.UsesWorkingTime ? WalkWorkingTime(start, duration, resource.Availability ?? [], true) : start + duration;
    private static DateTimeOffset SubtractLead(DateTimeOffset end, TimeSpan duration, ExternalResourceCandidate resource) =>
        resource.UsesWorkingTime ? WalkWorkingTime(end, duration, resource.Availability ?? [], false) : end - duration;

    private static DateTimeOffset WalkWorkingTime(DateTimeOffset value, TimeSpan duration,
        IReadOnlyList<ResourceAvailabilityWindow> availability, bool forward)
    {
        var remaining = duration;
        var windows = forward ? availability.OrderBy(item => item.StartsAt) : availability.OrderByDescending(item => item.EndsAt);
        foreach (var window in windows)
        {
            var start = forward ? (value > window.StartsAt ? value : window.StartsAt) : window.StartsAt;
            var end = forward ? window.EndsAt : (value < window.EndsAt ? value : window.EndsAt);
            if (start >= end) continue;
            var span = end - start;
            if (span >= remaining) return forward ? start + remaining : end - remaining;
            remaining -= span;
        }
        throw new ResourcePlanningException("external_calendar_no_fit", "External working-time calendar has no sufficient future interval.");
    }

    private static bool Overlaps(DateTimeOffset aStart, DateTimeOffset aEnd, DateTimeOffset bStart, DateTimeOffset bEnd) =>
        aStart < bEnd && bStart < aEnd;

    private static void Validate(ResourcePlanningInput input)
    {
        if (input.HorizonStart >= input.HorizonEnd) throw new ResourcePlanningException("invalid_horizon", "Planning horizon must be positive.");
        if (input.Work.Select(value => value.WorkId).Distinct(StringComparer.Ordinal).Count() != input.Work.Count)
            throw new ResourcePlanningException("duplicate_work_id", "Resource work IDs must be unique.");
        if (input.Work.Any(value => value.Duration < TimeSpan.Zero))
            throw new ResourcePlanningException("invalid_duration", "Resource work duration cannot be negative.");
        if (input.Work.Any(value => value.DependsOnWorkId is not null && !input.Work.Any(other => other.WorkId == value.DependsOnWorkId)))
            throw new ResourcePlanningException("dependency_not_found", "A resource work dependency does not exist.");
    }

    private sealed record AllocationResult(ProvisionalResourceAssignment? Assignment, string? Code, string? Message)
    {
        internal static AllocationResult Ok(ProvisionalResourceAssignment value) => new(value, null, null);
        internal static AllocationResult Fail(string code, string message) => new(null, code, message);
    }
}

internal sealed class ResourcePlanningException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}
