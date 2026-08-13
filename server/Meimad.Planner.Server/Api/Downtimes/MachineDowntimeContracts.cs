using System.Text.Json.Serialization;
using Meimad.Planner.Server.Application.Downtimes;
using Meimad.Planner.Server.Domain.Downtimes;

namespace Meimad.Planner.Server.Api.Downtimes;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateMachineDowntimeRequest(
    string? DowntimeType,
    string? MachineId,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    string? Reason,
    string? PlannedBy,
    string? ReportedBy)
{
    internal object ToCommand() => DowntimeType switch
    {
        MachineDowntimeType.PlannedMaintenance when EndsAt.HasValue =>
            new CreatePlannedMaintenanceCommand(MachineId, StartsAt, EndsAt.Value, Reason, PlannedBy),
        MachineDowntimeType.Breakdown when !EndsAt.HasValue =>
            new ReportBreakdownCommand(MachineId, StartsAt, Reason, ReportedBy),
        MachineDowntimeType.PlannedMaintenance => throw new MachineDowntimeRequestException("Planned maintenance requires endsAt."),
        MachineDowntimeType.Breakdown => throw new MachineDowntimeRequestException("A new breakdown must not have endsAt; use Restore later."),
        _ => throw new MachineDowntimeRequestException("downtimeType must be planned_maintenance or breakdown.")
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record UpdatePlannedMaintenanceRequest(
    string? MachineId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Reason,
    string? PlannedBy)
{
    internal UpdatePlannedMaintenanceCommand ToCommand() => new(MachineId, StartsAt, EndsAt, Reason, PlannedBy);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RestoreBreakdownRequest(DateTimeOffset RestoredAt, string? RepairNote)
{
    internal RestoreBreakdownCommand ToCommand() => new(RestoredAt, RepairNote);
}

internal sealed record MachineDowntimeResponse(
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
    DateTimeOffset UpdatedAt)
{
    internal static MachineDowntimeResponse FromDomain(MachineDowntime value) => new(
        value.DowntimeId, value.MachineId, value.DowntimeType, value.StartsAt, value.EndsAt,
        value.Reason, value.PlannedBy, value.RepairNote, value.ReportedBy, value.Status,
        value.Version, value.CreatedAt, value.UpdatedAt);
}

internal sealed record MachineDowntimeListResponse(IReadOnlyList<MachineDowntimeResponse> Items, string? NextCursor);
internal sealed class MachineDowntimeRequestException(string message) : Exception(message);
