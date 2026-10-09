using Meimad.Planner.Server.Application.PlanningBoard;
using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Domain.ProductionRuns;
using Meimad.Planner.Server.Application.Projections;
using System.Diagnostics;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteProductionRunPlanningProjectionRepository(
    SqliteDatabase database, ProductionRunCyclePlanner planner,
    ILogger<SqliteProductionRunPlanningProjectionRepository> logger)
    : IProductionRunPlanningProjectionRepository
{
    public async Task<IReadOnlyList<ProductionRunPlanningCard>> ReadAsync(CancellationToken token)
    {
        var metadata = new Dictionary<string, (string Batch,string Case,string Part,int Operation)>(StringComparer.Ordinal);
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT operation.id,batch.batch_number,cases.id,cases.part_number,operation.operation_number
                FROM batch_operations operation JOIN production_batches batch ON batch.id=operation.production_batch_id
                JOIN cases ON cases.id=batch.case_id;
                """;
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) metadata[reader.GetString(0)] =
                (reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetInt32(4));
        }
        var cards = new List<ProductionRunPlanningCard>();
        var graphStarted = Stopwatch.GetTimestamp();
        var runList = await SqliteProductionRunGraphReader.ReadAllAsync(connection, transaction, token);
        PlannerPerformanceMetrics.RunGraph.Record(Stopwatch.GetElapsedTime(graphStarted).TotalMilliseconds);
        var readinessStarted = Stopwatch.GetTimestamp();
        IReadOnlyDictionary<string, Domain.Readiness.ProductionReadinessContext>? bases = null;
        IReadOnlySet<(string BatchOperationId, string MachineId)>? verifiedPackages = null;
        SqliteProductionReadinessContextReader.ReferenceFacts? references = null;
        SqliteProductionReadinessContextReader.OutputFacts? outputFacts = null;
        try
        {
            verifiedPackages = await SqliteMachineWorkflowReporting.ReadVerifiedPackagesAsync(connection, transaction, token);
            bases = await SqliteProductionReadinessContextReader.ReadManyAsync(connection, transaction,
                runList.Where(run => run.Status is not ("CANCELLED" or "ABORTED"))
                    .SelectMany(run => run.Programs.Where(program => program.Status is not ("COMPLETED" or "CANCELLED" or "ABORTED")))
                    .SelectMany(program => program.Outputs).Select(output => output.BatchOperationId)
                    .Distinct(StringComparer.Ordinal).ToArray(), token, verifiedPackages: verifiedPackages);
            var active = runList.Where(run => run.Status is not ("CANCELLED" or "ABORTED") && run.Assignment is not null).ToArray();
            var programs = active.SelectMany(run => run.Programs.Where(program => program.Status is not ("COMPLETED" or "CANCELLED" or "ABORTED"))).ToArray();
            references = await SqliteProductionReadinessContextReader.ReadReferencesAsync(connection, transaction,
                active.Select(run => run.Assignment!.MachineId),
                programs.Select(program => program.ProductionPins.ProcessRevisionId ?? program.ProcessRevisionId).OfType<string>(),
                programs.SelectMany(program => program.Outputs).Select(output => output.BatchOperationId), token);
            outputFacts = await SqliteProductionReadinessContextReader.ReadOutputFactsAsync(
                connection, transaction, runList, bases, references, verifiedPackages, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Preserve per-Run failure isolation if bulk input cannot be read.
            logger.LogWarning(exception, "Readiness preloading failed; resolving each Run independently in the same snapshot.");
        }
        foreach (var run in runList)
        {
            token.ThrowIfCancellationRequested();
            if (run.Status is "CANCELLED" or "ABORTED") continue;
            var plan = planner.Calculate(new(run.SharedSetupSeconds, run.StructureLockedAt is not null,
                run.Programs.Select(program => new ProductionRunProgramCycleInput(
                    program.ProductionRunProgramId, program.SequencePosition,
                    (decimal)(program.CycleSecondsSnapshot ?? 0), program.CompletedCycleCount,
                    program.Outputs.Select(output => new ProductionRunOutputCycleInput(
                        output.ProductionRunOutputId, output.QuantityPerCycle,
                        output.TargetQuantity, int.MaxValue)).ToArray())).ToArray()));
            ProductionRunReadiness ready;
            try { ready = await SqliteProductionRunReadinessRepository.ReadAsync(
                connection, transaction, run.ProductionRunId, token, run, bases, verifiedPackages, references, outputFacts); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception,
                    "Production Run readiness projection failed for {ProductionRunId}; the card is shown as not ready.",
                    run.ProductionRunId);
                ready = new(run.ProductionRunId, "NOT_READY", false, [], []);
            }
            cards.Add(new(run.ProductionRunId,run.Status,run.Assignment?.MachineId,run.Assignment?.BacklogPosition,
                run.SharedSetupSeconds,run.Programs.Count,(long)Math.Ceiling(plan.RemainingDuration.TotalSeconds),ready.OverallState,ready.IsReadyForProduction,
                plan.Programs.Select((planned) =>
                {
                    var program=run.Programs.Single(value=>value.ProductionRunProgramId==planned.ProgramId);
                    return new ProductionRunPlanningProgram(program.ProductionRunProgramId,program.ManufacturingProgramId,
                        program.SelectedGCodeReleaseId,program.SequencePosition,program.TargetCycleCount,program.CompletedCycleCount,
                        (long)Math.Ceiling(planned.ForecastCompletionOffset.TotalSeconds),program.Outputs.Select(output=>
                        {
                            var meta=metadata[output.BatchOperationId];return new ProductionRunPlanningOutput(output.ProductionRunOutputId,
                                output.BatchOperationId,meta.Batch,meta.Case,meta.Part,meta.Operation,output.QuantityPerCycle,
                                output.TargetQuantity,output.ProducedQuantity,output.TargetQuantity-output.ProducedQuantity);
                        }).ToArray());
                }).ToArray()));
        }
        PlannerPerformanceMetrics.RunReadiness.Record(Stopwatch.GetElapsedTime(readinessStarted).TotalMilliseconds);
        await transaction.CommitAsync(token);
        return cards.OrderBy(value=>value.MachineId is null).ThenBy(value=>value.MachineId).ThenBy(value=>value.BacklogPosition).ThenBy(value=>value.ProductionRunId).ToArray();
    }
}
