using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Postprocessors;

namespace Meimad.Planner.Server.Application.Postprocessors;

internal interface IPostprocessorRepository
{
    Task<Postprocessor> CreateAsync(Postprocessor value, EditAuthority authority, CancellationToken token);
    Task<Postprocessor?> GetByIdAsync(string id, CancellationToken token);
    Task<IReadOnlyList<Postprocessor>> ListAsync(CancellationToken token);
    Task<Postprocessor?> UpdateAsync(
        Postprocessor value,
        int expectedVersion,
        EditAuthority authority,
        CancellationToken token);
    Task<bool> DeleteAsync(string id, EditAuthority authority, CancellationToken token);
}
