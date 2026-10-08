using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Application.ProductionPackages;
using Meimad.Planner.Server.Domain.GCode;
using Meimad.Planner.Server.Domain.ProductionRuns;
using Meimad.Planner.Server.Domain.Readiness;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteProductionRunReadinessRepository(SqliteDatabase database) : IProductionRunReadinessRepository
{
    public async Task<ProductionRunReadiness> ReadAsync(string runId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var result = await ReadAsync(connection, transaction, runId, token);
        await transaction.CommitAsync(token);
        return result;
    }

    internal static async Task<ProductionRunReadiness> ReadAsync(SqliteConnection connection,
        SqliteTransaction transaction, string runId, CancellationToken token)
    {
        var run = await SqliteProductionRunRepository.ReadAsync(connection, transaction, runId, token)
            ?? throw new ProductionRunNotFoundException(runId);
        var programs = new List<ProductionRunProgramReadiness>();
        var stamps = new List<string>();
        var physicalReasons = new List<ProductionActionReason>();
        foreach (var program in run.Programs.Where(x => x.Status is not ("COMPLETED" or "CANCELLED" or "ABORTED")))
        {
            var components = new List<ReadinessComponent>();
            if (!program.IsLegacyUnmanaged && program.ProcessRevisionId is null)
                components.Add(Block("programRevision", "Manufacturing revision", "No exact manufacturing revision is selected."));
            var outputs = new List<ProductionRunOutputReadiness>();
            foreach (var output in program.Outputs)
            {
                ProductionReadinessContext? context;
                if (run.Assignment is { } assignment)
                {
                    var exact = new ProductionPackageContext(assignment.MachineAssignmentId, runId,
                        program.ProductionRunProgramId, output.ProductionRunOutputId, output.BatchOperationId,
                        assignment.MachineId, program.ProcessRevisionId, "", output.TargetQuantity);
                    if (output == program.Outputs[0])
                    {
                        try { await SqliteProductionPackageContext.ValidateAsync(connection, transaction, exact, token); }
                        catch (ProductionPackageBuildException invalid)
                        { components.Add(Block("allocation", "Program output allocation", invalid.Message)); }
                    }
                    context = await SqliteProductionReadinessContextReader.ReadForPackageAsync(connection, transaction, exact, token, useProgramRevision: true);
                }
                else context = await SqliteProductionReadinessContextReader.ReadAsync(connection, transaction, output.BatchOperationId, token);
                if (context is null) throw new ProductionRunStateException("output_missing", "The Run output no longer exists.");
                var readiness = ProductionReadinessEvaluator.Evaluate(context);
                var decision = ProductionActionPolicy.Evaluate(context, readiness, ProductionAction.RecordProduction);
                stamps.Add(decision.ContextStamp);
                physicalReasons.AddRange(decision.Reasons);
                var facts = readiness.Components.ToList();
                if (output.TargetQuantity <= output.ProducedQuantity ||
                    (long)output.TargetQuantity != (long)program.TargetCycleCount * output.QuantityPerCycle)
                    facts.Add(Block("allocation", "Output allocation", "The output must have an exact cycle allocation and remaining quantity."));
                var ready = facts.All(x => !x.IsBlocking);
                physicalReasons.AddRange(facts.Where(x => x.Key == "allocation").Select(Reason));
                outputs.Add(new(output.ProductionRunOutputId, output.BatchOperationId,
                    ready ? ReadinessStates.Ready : ReadinessStates.Blocked, ready, facts));
            }
            if (outputs.Count == 0) components.Add(Block("allocation", "Output allocation", "The program has no outputs."));
            physicalReasons.AddRange(components.Select(Reason));
            var allowed = components.All(x => !x.IsBlocking) && outputs.All(x => x.IsReady);
            programs.Add(new(program.ProductionRunProgramId, allowed ? ReadinessStates.Ready : ReadinessStates.Blocked,
                allowed, components, outputs));
        }
        var tooling = await SqliteProductionRunToolingRepository.ReadAsync(connection, transaction, runId, token);
        var runComponents = new List<ReadinessComponent>();
        var capacity = ToolCapacityEvaluator.Evaluate(tooling.RequiredDistinctTools, tooling.AvailableCapacity);
        if (!capacity.IsSatisfied) runComponents.Add(Block(ReadinessComponentKeys.ToolCapacity, "Combined tool capacity", capacity.Message));
        foreach (var conflict in tooling.Conflicts) runComponents.Add(Block("toolPositionConflict", "Tool position conflict", conflict));
        if (run.Assignment is null) runComponents.Add(Block("machineAssignment", "Machine assignment", "Assign the Run to a Machine before Start."));
        if (programs.Count == 0) runComponents.Add(Block("allocation", "Output allocation", "The Run has no remaining executable programs."));
        var isReady = programs.All(x => x.IsReady) && runComponents.All(x => !x.IsBlocking);
        var stamp = ProductionActionPolicy.Stamp(new { run, stamps, tooling });
        physicalReasons.AddRange(runComponents.Select(Reason));
        return new(runId, isReady ? OverallReadinessStates.ReadyForProduction : OverallReadinessStates.NotReady,
            isReady, programs, runComponents, stamp,
            [new("RunStart", isReady, stamp, physicalReasons.Where(x => x.Code != "controller_verification").ToArray()),
             new("RecordProduction", isReady && physicalReasons.All(x => x.Classification != "BLOCKING"), stamp, physicalReasons)]);
    }

    internal static async Task EnsureAsync(SqliteConnection connection, SqliteTransaction transaction,
        string runId, CancellationToken token, string? observedStamp = null, bool physicalProduction = false)
    {
        var current = await ReadAsync(connection, transaction, runId, token);
        if (observedStamp is not null && observedStamp != current.ContextStamp)
            throw new ProductionRunStateException("production_readiness_changed", "Readiness evidence changed. Refresh the Run and review its prerequisites before retrying.");
        if (!current.IsReadyForProduction)
            throw new ProductionRunStateException("production_not_ready", "Production Run readiness has blocking components. Refresh readiness to inspect the exact output prerequisites.");
        if (physicalProduction && current.Actions!.Single(x => x.Action == "RecordProduction").IsAllowed == false)
            throw new ProductionRunStateException("production_verification_required", "Physical production requires successful verification of the exact current Run/Machine/NC/Offset Loader binding.");
    }

    private static ReadinessComponent Block(string key, string label, string message) => new(key, label, ReadinessStates.Blocked, message, true);
    private static ProductionActionReason Reason(ReadinessComponent fact) => new(fact.Key, fact.Label, fact.State,
        fact.IsBlocking ? "BLOCKING" : "SATISFIED", fact.Message);
}
