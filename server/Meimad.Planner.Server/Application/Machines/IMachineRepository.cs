using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Machines;

namespace Meimad.Planner.Server.Application.Machines;

internal interface IMachineRepository
{
    Task<Machine> CreateAsync(
        Machine machine,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task<Machine?> GetByIdAsync(string machineId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Machine>> ListAsync(CancellationToken cancellationToken);

    Task<Machine?> UpdateAsync(
        Machine machine,
        int expectedVersion,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);
}
