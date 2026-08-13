using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.MachineTypes;

namespace Meimad.Planner.Server.Application.MachineTypes;

internal interface IMachineTypeRepository
{
    Task<MachineType> CreateAsync(MachineType machineType, EditAuthority authority, CancellationToken token);
    Task<MachineType?> GetByIdAsync(string machineTypeId, CancellationToken token);
    Task<IReadOnlyList<MachineType>> ListAsync(CancellationToken token);
    Task<MachineType?> UpdateAsync(MachineType machineType, int expectedVersion, EditAuthority authority, CancellationToken token);
    Task<bool> DeleteAsync(string machineTypeId, EditAuthority authority, CancellationToken token);
}
