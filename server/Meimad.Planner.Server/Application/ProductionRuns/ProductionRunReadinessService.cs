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

internal sealed class ProductionRunReadinessService(IProductionRunReadinessRepository repository)
{
    internal Task<ProductionRunReadiness> ReadAsync(string runId, CancellationToken token = default) =>
        repository.ReadAsync(runId.Trim(), token);
}
