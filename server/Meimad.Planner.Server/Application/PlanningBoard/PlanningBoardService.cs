namespace Meimad.Planner.Server.Application.PlanningBoard;

internal sealed class PlanningBoardService
{
    private readonly IPlanningBoardRepository repository;
    private readonly IProductionRunPlanningProjectionRepository runProjection;

    public PlanningBoardService(IPlanningBoardRepository repository, IProductionRunPlanningProjectionRepository runProjection)
    {
        this.repository = repository;
        this.runProjection = runProjection;
    }

    internal async Task<PlanningBoardSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await repository.ReadAsync(cancellationToken);
        return snapshot with { ProductionRuns = await runProjection.ReadAsync(cancellationToken) };
    }
}
