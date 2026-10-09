using Meimad.Planner.Server.Domain.ProductionRuns;

namespace Meimad.Planner.Server.Application.ProductionRuns;

internal interface IProductionRunToolingRepository
{
    Task<ProductionRunToolingFacts> ReadAsync(string productionRunId, CancellationToken token);
}

internal interface IProductionRunReadinessRepository
{
    Task<ProductionRunReadiness> ReadAsync(string productionRunId, CancellationToken token);
}

internal sealed class ProductionRunReadinessService(IProductionRunReadinessRepository repository,
    Projections.ProjectionConsistency? consistency = null)
{
    internal Task<ProductionRunReadiness> ReadAsync(string runId, CancellationToken token = default) =>
        consistency is null ? repository.ReadAsync(runId.Trim(), token)
            : consistency.ReadAsync(ct => repository.ReadAsync(runId.Trim(), ct),
                (result, freshness) => result with { Freshness = freshness }, token);
}
