using Meimad.Planner.Server.Application.EditMode;

namespace Meimad.Planner.Server.Application.Deletion;

internal sealed class PlanningDeletionService
{
    private readonly IPlanningDeletionRepository repository;

    public PlanningDeletionService(IPlanningDeletionRepository repository) => this.repository = repository;

    internal Task<bool> DeleteCaseAsync(string id, EditAuthority authority, CancellationToken token = default) =>
        repository.DeleteCaseAsync(Required(id), authority, token);

    internal Task<bool> DeleteCaseOperationAsync(string caseId, string id, EditAuthority authority, CancellationToken token = default) =>
        repository.DeleteCaseOperationAsync(Required(caseId), Required(id), authority, token);

    internal Task<bool> DeleteOrderAsync(string id, EditAuthority authority, CancellationToken token = default) =>
        repository.DeleteOrderAsync(Required(id), authority, token);

    internal Task<bool> DeleteBatchAsync(string id, EditAuthority authority, CancellationToken token = default) =>
        repository.DeleteBatchAsync(Required(id), authority, token);

    internal Task<bool> DeleteMachineAsync(string id, EditAuthority authority, CancellationToken token = default) =>
        repository.DeleteMachineAsync(Required(id), authority, token);

    private static string Required(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A resource ID is required.")
            : value.Trim();
}
