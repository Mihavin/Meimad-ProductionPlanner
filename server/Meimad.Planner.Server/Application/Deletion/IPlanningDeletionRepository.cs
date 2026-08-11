using Meimad.Planner.Server.Application.EditMode;

namespace Meimad.Planner.Server.Application.Deletion;

internal interface IPlanningDeletionRepository
{
    Task<bool> DeleteCaseAsync(string caseId, EditAuthority authority, CancellationToken cancellationToken);
    Task<bool> DeleteCaseOperationAsync(string caseId, string operationId, EditAuthority authority, CancellationToken cancellationToken);
    Task<bool> DeleteOrderAsync(string orderId, EditAuthority authority, CancellationToken cancellationToken);
    Task<bool> DeleteBatchAsync(string batchId, EditAuthority authority, CancellationToken cancellationToken);
    Task<bool> DeleteMachineAsync(string machineId, EditAuthority authority, CancellationToken cancellationToken);
}

internal sealed class PlanningDeletionBlockedException : Exception
{
    internal PlanningDeletionBlockedException(string message) : base(message) { }
}
