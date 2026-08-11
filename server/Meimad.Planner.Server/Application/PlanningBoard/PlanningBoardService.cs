namespace Meimad.Planner.Server.Application.PlanningBoard;

internal sealed class PlanningBoardService
{
    private readonly IPlanningBoardRepository repository;

    public PlanningBoardService(IPlanningBoardRepository repository)
    {
        this.repository = repository;
    }

    internal Task<PlanningBoardSnapshot> ReadAsync(
        CancellationToken cancellationToken = default) =>
        repository.ReadAsync(cancellationToken);
}
