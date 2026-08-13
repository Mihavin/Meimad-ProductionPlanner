using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Downtimes;

namespace Meimad.Planner.Server.Application.Downtimes;

internal interface IMachineDowntimeRepository
{
    Task<IReadOnlyList<MachineDowntime>> ListAsync(string? machineId, CancellationToken token);
    Task<MachineDowntime?> GetAsync(string id, CancellationToken token);
    Task<MachineDowntime> CreateAsync(MachineDowntime value, EditAuthority authority, CancellationToken token);
    Task<MachineDowntime?> UpdateAsync(MachineDowntime value, int expectedVersion, EditAuthority authority, CancellationToken token);
}
