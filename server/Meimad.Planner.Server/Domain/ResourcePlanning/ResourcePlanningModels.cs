namespace Meimad.Planner.Server.Domain.ResourcePlanning;

internal enum ResourceBaseClass { Machine, Employee, Workstation, External }
internal enum ResourceScheduleDirection { Backward, Forward }

internal sealed record ResourceAvailabilityWindow(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

internal sealed record ResourceCandidate(
    string ResourceId,
    ResourceBaseClass ResourceClass,
    IReadOnlyList<ResourceAvailabilityWindow> Availability,
    int Capacity = 1,
    string? TypeId = null,
    IReadOnlyList<string>? Capabilities = null,
    IReadOnlyList<string>? SkillIds = null);

internal sealed record ExternalResourceCandidate(
    string ResourceId,
    TimeSpan PromisedLeadTime,
    TimeSpan SafetyBuffer,
    bool UsesWorkingTime,
    IReadOnlyList<ResourceAvailabilityWindow>? Availability = null);

internal sealed record ResourceWorkRequirement(
    string? WorkstationTypeId = null,
    string? WorkstationCapability = null,
    string? EmployeeSkillId = null,
    int WorkstationCapacity = 1,
    string? ExternalResourceId = null);

internal sealed record ResourceWorkItem(
    string WorkId,
    TimeSpan Duration,
    ResourceScheduleDirection Direction,
    DateTimeOffset Anchor,
    ResourceWorkRequirement Requirement,
    string? DependsOnWorkId = null,
    DateTimeOffset? RequiredDeliveryAt = null,
    bool IsConfirmed = false,
    string? PinnedWorkstationId = null,
    string? PinnedEmployeeId = null,
    DateTimeOffset? PinnedStartsAt = null);

internal sealed record ExistingResourceReservation(
    string WorkId,
    string ResourceId,
    ResourceBaseClass ResourceClass,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    int Capacity = 1,
    bool IsConfirmed = true);

internal sealed record ResourcePlanningInput(
    DateTimeOffset HorizonStart,
    DateTimeOffset HorizonEnd,
    IReadOnlyList<ResourceWorkItem> Work,
    IReadOnlyList<ResourceCandidate> Workstations,
    IReadOnlyList<ResourceCandidate> Employees,
    IReadOnlyList<ExternalResourceCandidate> ExternalResources,
    IReadOnlyList<ExistingResourceReservation>? FixedReservations = null);

internal sealed record ProvisionalResourceAssignment(
    string WorkId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? WorkstationId,
    string? EmployeeId,
    string? ExternalResourceId,
    bool IsPinned,
    string Explanation);

internal sealed record ResourceLoadInterval(
    ResourceBaseClass ResourceClass,
    string ResourceId,
    string WorkId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    int CapacityUsed);

internal sealed record ResourcePlanningIssue(string WorkId, string Code, string Message);

internal sealed record ResourcePlanningResult(
    IReadOnlyList<ProvisionalResourceAssignment> Assignments,
    IReadOnlyList<ResourceLoadInterval> Load,
    IReadOnlyList<ResourcePlanningIssue> BlockingConfigurationErrors,
    TimeSpan PredictedShift,
    DateTimeOffset PredictedCompletion,
    bool DeliveryAtRisk);
