using Meimad.Planner.Server.Application.PlanningBoard;
using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Domain.ProductionRuns;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteProductionRunPlanningProjectionRepository(
    SqliteDatabase database, IProductionRunRepository runs,
    ProductionRunCyclePlanner planner, ProductionRunReadinessService readiness,
    ILogger<SqliteProductionRunPlanningProjectionRepository> logger)
    : IProductionRunPlanningProjectionRepository
{
    public async Task<IReadOnlyList<ProductionRunPlanningCard>> ReadAsync(CancellationToken token)
    {
        var metadata = new Dictionary<string, (string Batch,string Case,string Part,int Operation)>(StringComparer.Ordinal);
        await using (var connection = await database.OpenConnectionAsync(token))
        await using (var query = connection.CreateCommand())
        {
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
        foreach (var run in await runs.ListAsync(token))
        {
            if (run.Status is "CANCELLED" or "ABORTED") continue;
            var plan = planner.Calculate(new(run.SharedSetupSeconds, run.StructureLockedAt is not null,
                run.Programs.Select(program => new ProductionRunProgramCycleInput(
                    program.ProductionRunProgramId, program.SequencePosition,
                    (decimal)(program.CycleSecondsSnapshot ?? 0), program.CompletedCycleCount,
                    program.Outputs.Select(output => new ProductionRunOutputCycleInput(
                        output.ProductionRunOutputId, output.QuantityPerCycle,
                        output.TargetQuantity, int.MaxValue)).ToArray())).ToArray()));
            ProductionRunReadiness ready;
            try { ready = await readiness.ReadAsync(run.ProductionRunId, token); }
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
        return cards.OrderBy(value=>value.MachineId is null).ThenBy(value=>value.MachineId).ThenBy(value=>value.BacklogPosition).ThenBy(value=>value.ProductionRunId).ToArray();
    }
}
