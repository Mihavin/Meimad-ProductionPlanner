using Meimad.Planner.Server.Application.Readiness;
using Meimad.Planner.Server.Domain.ProductionRuns;
using Meimad.Planner.Server.Domain.Readiness;

namespace Meimad.Planner.Server.Application.ProductionRuns;

internal interface IProductionRunToolingRepository
{
    Task<ProductionRunToolingFacts> ReadAsync(string productionRunId, CancellationToken token);
}

internal sealed class ProductionRunReadinessService(
    IProductionRunRepository runs,
    IProductionReadinessRepository operationReadiness,
    IProductionRunToolingRepository tooling)
{
    internal async Task<ProductionRunReadiness> ReadAsync(string runId, CancellationToken token = default)
    {
        var run = await runs.GetAsync(runId.Trim(), token) ?? throw new ProductionRunNotFoundException(runId);
        var programResults = new List<ProductionRunProgramReadiness>();
        foreach (var program in run.Programs)
        {
            var outputs = new List<ProductionRunOutputReadiness>();
            var components = new List<ReadinessComponent>();
            if (program.ProcessRevisionId is null)
                components.Add(Block("programRevision", "Manufacturing revision", "No exact manufacturing revision is selected."));
            if (!program.IsLegacyUnmanaged && program.SelectedGCodeReleaseId is null)
                components.Add(Block("gcodeSelection", "G-code selection", "Select one compatible G-code release explicitly."));
            foreach (var output in program.Outputs)
            {
                var result = await operationReadiness.ReadAsync(output.BatchOperationId, token);
                var outputComponents = result.Components
                    .Where(value => value.Key is ReadinessComponentKeys.Material)
                    .ToArray();
                var allocationValid = output.TargetQuantity > output.ProducedQuantity;
                if (!allocationValid)
                    outputComponents = [.. outputComponents, Block("allocation", "Output allocation", "No unproduced quantity remains in this output allocation.")];
                var outputReady = outputComponents.All(value => !value.IsBlocking);
                outputs.Add(new(output.ProductionRunOutputId, output.BatchOperationId,
                    outputReady ? ReadinessStates.Ready : ReadinessStates.Blocked, outputReady, outputComponents));
            }
            var isReady = components.All(value => !value.IsBlocking) && outputs.All(value => value.IsReady);
            programResults.Add(new(program.ProductionRunProgramId,
                isReady ? ReadinessStates.Ready : ReadinessStates.Blocked,
                isReady, components, outputs));
        }

        var toolFacts = await tooling.ReadAsync(run.ProductionRunId, token);
        var runComponents = new List<ReadinessComponent>();
        if (toolFacts.AvailableCapacity is int capacity && toolFacts.RequiredDistinctTools > capacity)
            runComponents.Add(Block(ReadinessComponentKeys.ToolCapacity, "Combined tool capacity",
                $"The run needs {toolFacts.RequiredDistinctTools} distinct magazine tools; the Machine has {capacity} usable positions."));
        foreach (var conflict in toolFacts.Conflicts)
            runComponents.Add(Block("toolPositionConflict", "Tool position conflict", conflict));
        if (run.Assignment is null)
            runComponents.Add(Block("machineAssignment", "Machine assignment", "Assign the Production Run to a Machine before Start."));
        var ready = programResults.All(value => value.IsReady) && runComponents.All(value => !value.IsBlocking);
        return new(run.ProductionRunId,
            ready ? OverallReadinessStates.ReadyForProduction : OverallReadinessStates.NotReady,
            ready, programResults, runComponents);
    }

    private static ReadinessComponent Block(string key, string label, string message) =>
        new(key, label, ReadinessStates.Blocked, message, true);
}
