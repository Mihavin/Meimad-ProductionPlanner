using Meimad.Planner.Server.Domain.Readiness;

namespace Meimad.Planner.Server.Domain.ProductionRuns;

internal sealed record ProductionRunReadiness(
    string ProductionRunId,
    string OverallState,
    bool IsReadyForProduction,
    IReadOnlyList<ProductionRunProgramReadiness> Programs,
    IReadOnlyList<ReadinessComponent> RunComponents);

internal sealed record ProductionRunProgramReadiness(
    string ProductionRunProgramId,
    string State,
    bool IsReady,
    IReadOnlyList<ReadinessComponent> Components,
    IReadOnlyList<ProductionRunOutputReadiness> Outputs);

internal sealed record ProductionRunOutputReadiness(
    string ProductionRunOutputId,
    string BatchOperationId,
    string State,
    bool IsReady,
    IReadOnlyList<ReadinessComponent> Components);

internal sealed record ProductionRunToolingFacts(
    int? AvailableCapacity,
    int RequiredDistinctTools,
    IReadOnlyList<string> Conflicts);
