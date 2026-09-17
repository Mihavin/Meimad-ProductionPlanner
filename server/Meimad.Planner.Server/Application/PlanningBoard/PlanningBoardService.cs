using Meimad.Planner.Server.Application.Timeline;

namespace Meimad.Planner.Server.Application.PlanningBoard;

internal sealed class PlanningBoardService
{
    internal static readonly TimeSpan ConflictHorizon = TimeSpan.FromDays(30);

    // The pool column already presents unassigned work; repeating it as a conflict
    // would bury the calculated Machine/dependency/timing conflicts.
    private static readonly HashSet<string> ExcludedConflictCodes =
        new(StringComparer.Ordinal) { "unassigned_operation" };

    private readonly IPlanningBoardRepository repository;
    private readonly IProductionRunPlanningProjectionRepository runProjection;
    private readonly TimelineProjectionService timeline;
    private readonly ILogger<PlanningBoardService> logger;

    public PlanningBoardService(
        IPlanningBoardRepository repository,
        IProductionRunPlanningProjectionRepository runProjection,
        TimelineProjectionService timeline,
        ILogger<PlanningBoardService> logger)
    {
        this.repository = repository;
        this.runProjection = runProjection;
        this.timeline = timeline;
        this.logger = logger;
    }

    internal async Task<PlanningBoardSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await repository.ReadAsync(cancellationToken);
        snapshot = snapshot with { ProductionRuns = await runProjection.ReadAsync(cancellationToken) };
        var horizonStart = snapshot.ReadAt.ToUniversalTime();
        var horizonEnd = horizonStart + ConflictHorizon;
        try
        {
            var projection = await timeline.CalculateAsync(
                horizonStart, horizonEnd, horizonStart, cancellationToken);
            var conflicts = projection.Conflicts
                .Where(conflict => !ExcludedConflictCodes.Contains(conflict.Code))
                .OrderBy(conflict => SeverityRank(conflict.Severity))
                .ThenBy(conflict => conflict.Code, StringComparer.Ordinal)
                .ThenBy(conflict => conflict.Message, StringComparer.Ordinal)
                .Select(conflict => new PlanningBoardConflict(
                    conflict.ConflictId,
                    conflict.Code,
                    conflict.Severity,
                    Title(conflict.Code),
                    conflict.Message,
                    conflict.OperationIds,
                    conflict.MachineIds))
                .ToArray();
            return snapshot with
            {
                ConflictCalculationStatus = "current",
                ConflictCalculationMessage =
                    $"Calculated by the Timeline engine at {horizonStart:yyyy-MM-dd HH:mm:ss} UTC over the next {ConflictHorizon.TotalDays:0} days.",
                Conflicts = conflicts
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Planning-board conflict calculation failed at {ReadAt}.", snapshot.ReadAt);
            return snapshot with
            {
                ConflictCalculationStatus = "unavailable",
                ConflictCalculationMessage =
                    "The Timeline conflict calculation failed for this read; see the Server log. The board itself is current.",
                Conflicts = []
            };
        }
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "blocking" => 0,
        "warning" => 1,
        "attention" => 2,
        _ => 3
    };

    private static string Title(string code)
    {
        var words = code.Replace('_', ' ');
        return words.Length == 0 ? code : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
