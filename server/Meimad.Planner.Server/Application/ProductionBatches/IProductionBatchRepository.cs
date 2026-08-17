using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.ProductionBatches;

namespace Meimad.Planner.Server.Application.ProductionBatches;

internal interface IProductionBatchRepository
{
    Task<ProductionBatch> CreateAsync(
        ProductionBatch batch,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task<ProductionBatch?> UpdateAsync(
        ProductionBatch batch,
        int expectedVersion,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task<ProductionBatch?> GetByIdAsync(
        string batchId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductionBatch>> ListByCaseAsync(
        string caseId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BatchOperation>> ListOperationsAsync(
        string batchId,
        CancellationToken cancellationToken);
}
