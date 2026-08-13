namespace Meimad.Planner.Server.Domain.Downtimes;

internal static class MachineDowntimeType
{
    internal const string PlannedMaintenance = "planned_maintenance";
    internal const string Breakdown = "breakdown";
}

internal static class MachineDowntimeStatus
{
    internal const string Planned = "planned";
    internal const string Active = "active";
    internal const string Restored = "restored";
}

internal sealed record MachineDowntime(
    string DowntimeId,
    string MachineId,
    string DowntimeType,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    string Reason,
    string? PlannedBy,
    string? RepairNote,
    string? ReportedBy,
    string Status,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal static class MachineDowntimeValidator
{
    internal static MachineDowntime Validate(MachineDowntime value)
    {
        var issues = new List<string>();
        var machineId = Required(value.MachineId, "Machine", 128, issues);
        var reason = Required(value.Reason, "Reason", 1000, issues);
        var plannedBy = Optional(value.PlannedBy, "Planned by", 200, issues);
        var repairNote = Optional(value.RepairNote, "Repair note", 2000, issues);
        var reportedBy = Optional(value.ReportedBy, "Reported by", 200, issues);
        if (value.StartsAt == default) issues.Add("Start time is required.");
        if (value.DowntimeType is not (MachineDowntimeType.PlannedMaintenance or MachineDowntimeType.Breakdown))
            issues.Add("Downtime type must be planned_maintenance or breakdown.");
        if (value.EndsAt.HasValue && value.EndsAt <= value.StartsAt)
            issues.Add("End/restored time must be after the start time.");
        if (value.DowntimeType == MachineDowntimeType.PlannedMaintenance)
        {
            if (!value.EndsAt.HasValue) issues.Add("Planned maintenance requires an end time.");
            if (plannedBy is null) issues.Add("Planned maintenance requires Planned by.");
            if (value.Status != MachineDowntimeStatus.Planned)
                issues.Add("Planned maintenance must have planned status.");
        }
        else
        {
            if (reportedBy is null) issues.Add("A breakdown requires Reported by.");
            if (value.Status == MachineDowntimeStatus.Active && value.EndsAt.HasValue)
                issues.Add("An active breakdown cannot have a restored time.");
            if (value.Status == MachineDowntimeStatus.Restored && !value.EndsAt.HasValue)
                issues.Add("A restored breakdown requires a restored time.");
            if (value.Status is not (MachineDowntimeStatus.Active or MachineDowntimeStatus.Restored))
                issues.Add("Breakdown status must be active or restored.");
        }
        if (issues.Count > 0) throw new MachineDowntimeValidationException(issues);
        return value with
        {
            MachineId = machineId,
            Reason = reason,
            PlannedBy = plannedBy,
            RepairNote = repairNote,
            ReportedBy = reportedBy,
            StartsAt = value.StartsAt.ToUniversalTime(),
            EndsAt = value.EndsAt?.ToUniversalTime()
        };
    }

    private static string Required(string? value, string label, int maximum, ICollection<string> issues)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0) issues.Add($"{label} is required.");
        if (normalized.Length > maximum) issues.Add($"{label} cannot exceed {maximum} characters.");
        return normalized;
    }

    private static string? Optional(string? value, string label, int maximum, ICollection<string> issues)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized?.Length > maximum) issues.Add($"{label} cannot exceed {maximum} characters.");
        return normalized;
    }
}

internal sealed class MachineDowntimeValidationException(IReadOnlyList<string> issues)
    : Exception("Machine downtime validation failed.")
{
    internal IReadOnlyList<string> Issues { get; } = issues;
}
