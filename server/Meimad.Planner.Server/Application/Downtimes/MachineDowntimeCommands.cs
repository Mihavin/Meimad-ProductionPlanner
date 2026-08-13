namespace Meimad.Planner.Server.Application.Downtimes;

internal sealed record CreatePlannedMaintenanceCommand(
    string? MachineId, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    string? Reason, string? PlannedBy);

internal sealed record UpdatePlannedMaintenanceCommand(
    string? MachineId, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    string? Reason, string? PlannedBy);

internal sealed record ReportBreakdownCommand(
    string? MachineId, DateTimeOffset StartsAt, string? Reason, string? ReportedBy);

internal sealed record RestoreBreakdownCommand(
    DateTimeOffset RestoredAt, string? RepairNote);
