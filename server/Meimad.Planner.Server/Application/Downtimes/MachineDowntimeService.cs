using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Downtimes;

namespace Meimad.Planner.Server.Application.Downtimes;

internal sealed class MachineDowntimeService
{
    private readonly IMachineDowntimeRepository repository;
    private readonly TimeProvider timeProvider;

    public MachineDowntimeService(IMachineDowntimeRepository repository, TimeProvider timeProvider)
    {
        this.repository = repository;
        this.timeProvider = timeProvider;
    }

    internal Task<IReadOnlyList<MachineDowntime>> ListAsync(string? machineId, CancellationToken token = default) =>
        repository.ListAsync(string.IsNullOrWhiteSpace(machineId) ? null : machineId.Trim(), token);

    internal Task<MachineDowntime?> GetAsync(string id, CancellationToken token = default) => repository.GetAsync(id, token);

    internal Task<MachineDowntime> CreatePlannedAsync(CreatePlannedMaintenanceCommand command, EditAuthority authority, CancellationToken token = default)
    {
        var now = timeProvider.GetUtcNow();
        var value = MachineDowntimeValidator.Validate(new MachineDowntime(
            Guid.NewGuid().ToString("N"), command.MachineId ?? string.Empty,
            MachineDowntimeType.PlannedMaintenance, command.StartsAt, command.EndsAt,
            command.Reason ?? string.Empty, command.PlannedBy, null, null,
            MachineDowntimeStatus.Planned, 1, now, now));
        return repository.CreateAsync(value, authority, token);
    }

    internal Task<MachineDowntime> ReportBreakdownAsync(ReportBreakdownCommand command, EditAuthority authority, CancellationToken token = default)
    {
        var now = timeProvider.GetUtcNow();
        var value = MachineDowntimeValidator.Validate(new MachineDowntime(
            Guid.NewGuid().ToString("N"), command.MachineId ?? string.Empty,
            MachineDowntimeType.Breakdown, command.StartsAt, null,
            command.Reason ?? string.Empty, null, null, command.ReportedBy,
            MachineDowntimeStatus.Active, 1, now, now));
        return repository.CreateAsync(value, authority, token);
    }

    internal async Task<MachineDowntime> UpdatePlannedAsync(
        string id, int expectedVersion, UpdatePlannedMaintenanceCommand command,
        EditAuthority authority, CancellationToken token = default)
    {
        var current = await RequiredAsync(id, token);
        if (current.DowntimeType != MachineDowntimeType.PlannedMaintenance)
            throw new MachineDowntimeStateException("Only planned maintenance can use the edit action.");
        var candidate = MachineDowntimeValidator.Validate(current with
        {
            MachineId = command.MachineId ?? string.Empty,
            StartsAt = command.StartsAt,
            EndsAt = command.EndsAt,
            Reason = command.Reason ?? string.Empty,
            PlannedBy = command.PlannedBy,
            Version = expectedVersion + 1,
            UpdatedAt = timeProvider.GetUtcNow()
        });
        return await repository.UpdateAsync(candidate, expectedVersion, authority, token)
            ?? throw new MachineDowntimeVersionException(id);
    }

    internal async Task<MachineDowntime> RestoreAsync(
        string id, int expectedVersion, RestoreBreakdownCommand command,
        EditAuthority authority, CancellationToken token = default)
    {
        var current = await RequiredAsync(id, token);
        if (current.DowntimeType != MachineDowntimeType.Breakdown || current.Status != MachineDowntimeStatus.Active)
            throw new MachineDowntimeStateException("Only an active breakdown can be marked restored.");
        var candidate = MachineDowntimeValidator.Validate(current with
        {
            EndsAt = command.RestoredAt,
            RepairNote = command.RepairNote,
            Status = MachineDowntimeStatus.Restored,
            Version = expectedVersion + 1,
            UpdatedAt = timeProvider.GetUtcNow()
        });
        return await repository.UpdateAsync(candidate, expectedVersion, authority, token)
            ?? throw new MachineDowntimeVersionException(id);
    }

    private async Task<MachineDowntime> RequiredAsync(string id, CancellationToken token) =>
        await repository.GetAsync(id, token) ?? throw new MachineDowntimeNotFoundException(id);
}

internal sealed class MachineDowntimeNotFoundException(string id) : Exception($"Machine downtime '{id}' was not found.");
internal sealed class MachineDowntimeVersionException(string id) : Exception($"Machine downtime '{id}' was changed by another editor.");
internal sealed class MachineDowntimeMachineException(string id) : Exception($"Machine '{id}' was not found.");
internal sealed class MachineDowntimeStateException(string message) : Exception(message);
