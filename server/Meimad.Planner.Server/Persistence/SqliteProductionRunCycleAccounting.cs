using System.Globalization;
using Meimad.Planner.Server.Application.EventLogging;
using Meimad.Planner.Server.Application.ProductionRuns;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed record SqliteProductionRunCycleAccountingCommand(
    string ProductionRunId,
    string ProductionRunProgramId,
    string Source,
    string SourceEventId,
    DateTimeOffset ObservedAt,
    DateTimeOffset RecordedAt,
    string Actor,
    string EventCategory,
    string? MachineId = null);

internal sealed record SqliteProductionRunCycleAccountingResult(
    int CompletedCycleCount,
    int TargetCycleCount,
    bool ProgramComplete,
    bool RunComplete);

/// <summary>
/// The single schema-v47 cycle-accounting engine. Callers own authorization,
/// workflow validation, dedupe pre-checks, and the surrounding transaction.
/// </summary>
internal static class SqliteProductionRunCycleAccounting
{
    internal static async Task<SqliteProductionRunCycleAccountingResult> RecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteProductionRunCycleAccountingCommand command,
        CancellationToken cancellationToken)
    {
        int completed;
        int target;
        string status;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT completed_cycle_count,target_cycle_count,status
                FROM production_run_programs
                WHERE id=$programId AND production_run_id=$runId;
                """;
            query.Parameters.AddWithValue("$programId", command.ProductionRunProgramId);
            query.Parameters.AddWithValue("$runId", command.ProductionRunId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new ProductionRunNotFoundException(command.ProductionRunProgramId);
            completed = reader.GetInt32(0);
            target = reader.GetInt32(1);
            status = reader.GetString(2);
        }
        if (status != "ACTIVE")
            throw new ProductionRunStateException(
                "program_not_active",
                "Only the active Production Run Program can record a cycle.");
        if (completed >= target)
            throw new ProductionRunStateException(
                "program_cycle_overrun",
                "The program already reached its exact target-cycle count.");

        var next = checked(completed + 1);
        var programComplete = next == target;
        var at = Format(command.RecordedAt);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE production_run_outputs
                SET produced_quantity=produced_quantity+quantity_per_cycle,
                    status=CASE WHEN produced_quantity+quantity_per_cycle=target_quantity
                                THEN 'COMPLETED' ELSE 'IN_PRODUCTION' END,
                    version=version+1,updated_at=$at
                WHERE production_run_program_id=$programId
                  AND produced_quantity+quantity_per_cycle<=target_quantity;
                """;
            update.Parameters.AddWithValue("$programId", command.ProductionRunProgramId);
            update.Parameters.AddWithValue("$at", at);
            var changed = await update.ExecuteNonQueryAsync(cancellationToken);
            await using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM production_run_outputs WHERE production_run_program_id=$programId;";
            count.Parameters.AddWithValue("$programId", command.ProductionRunProgramId);
            if (changed != Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken)))
                throw new ProductionRunStateException(
                    "output_overproduction",
                    "The cycle would exceed an output target quantity.");
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE production_run_programs
                SET completed_cycle_count=$next,
                    status=CASE WHEN $next=target_cycle_count THEN 'COMPLETED' ELSE 'ACTIVE' END,
                    version=version+1,updated_at=$at
                WHERE id=$programId;
                """;
            update.Parameters.AddWithValue("$next", next);
            update.Parameters.AddWithValue("$at", at);
            update.Parameters.AddWithValue("$programId", command.ProductionRunProgramId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var operation = connection.CreateCommand())
        {
            operation.Transaction = transaction;
            operation.CommandText = """
                UPDATE batch_operations
                SET status=CASE WHEN NOT EXISTS(
                    SELECT 1 FROM production_run_outputs output
                    WHERE output.batch_operation_id=batch_operations.id
                      AND output.target_quantity>output.produced_quantity)
                    THEN 'completed' ELSE 'started' END,
                    version=version+1,updated_at=$at
                WHERE id IN (SELECT batch_operation_id FROM production_run_outputs
                             WHERE production_run_program_id=$programId);
                """;
            operation.Parameters.AddWithValue("$programId", command.ProductionRunProgramId);
            operation.Parameters.AddWithValue("$at", at);
            await operation.ExecuteNonQueryAsync(cancellationToken);
        }
        await PropagateParentsAsync(connection, transaction,
            command.ProductionRunProgramId, at, cancellationToken);

        var runComplete = await ScalarIntAsync(connection, transaction,
            "SELECT COUNT(*) FROM production_run_programs WHERE production_run_id=$id AND status<>'COMPLETED';",
            command.ProductionRunId, cancellationToken) == 0;
        await using (var runUpdate = connection.CreateCommand())
        {
            runUpdate.Transaction = transaction;
            runUpdate.CommandText = """
                UPDATE production_runs
                SET status=$status,version=version+1,updated_at=$at
                WHERE id=$runId;
                """;
            runUpdate.Parameters.AddWithValue("$status", runComplete ? "COMPLETED" : "IN_PROGRESS");
            runUpdate.Parameters.AddWithValue("$at", at);
            runUpdate.Parameters.AddWithValue("$runId", command.ProductionRunId);
            await runUpdate.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO production_run_cycle_events(
                    id,production_run_id,production_run_program_id,source,source_event_id,
                    observed_at,completed_cycle_count,created_at,updated_at)
                VALUES($id,$runId,$programId,$source,$sourceEventId,$observedAt,$next,$at,$at);
                """;
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("$runId", command.ProductionRunId);
            insert.Parameters.AddWithValue("$programId", command.ProductionRunProgramId);
            insert.Parameters.AddWithValue("$source", command.Source);
            insert.Parameters.AddWithValue("$sourceEventId", command.SourceEventId);
            insert.Parameters.AddWithValue("$observedAt", Format(command.ObservedAt));
            insert.Parameters.AddWithValue("$next", next);
            insert.Parameters.AddWithValue("$at", at);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        var related = new Dictionary<string, string>
        {
            ["productionRunId"] = command.ProductionRunId,
            ["productionRunProgramId"] = command.ProductionRunProgramId
        };
        if (command.MachineId is not null) related["machineId"] = command.MachineId;
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction,
            new("production_run_program_cycle_completed", command.RecordedAt,
                command.Actor, related, command.EventCategory, null, null,
                new
                {
                    command.Source,
                    command.SourceEventId,
                    completedCycleCount = next,
                    targetCycleCount = target,
                    programComplete,
                    runComplete
                }), cancellationToken);
        return new(next, target, programComplete, runComplete);
    }

    private static async Task PropagateParentsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string programId,
        string at,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE production_batches
            SET status=CASE WHEN NOT EXISTS(
                SELECT 1 FROM batch_operations operation
                WHERE operation.production_batch_id=production_batches.id
                  AND operation.status<>'completed')
                THEN 'completed' ELSE 'in_progress' END,
                version=version+1,updated_at=$at
            WHERE id IN(
                SELECT DISTINCT operation.production_batch_id
                FROM production_run_outputs output
                JOIN batch_operations operation ON operation.id=output.batch_operation_id
                WHERE output.production_run_program_id=$programId);
            UPDATE orders
            SET status=CASE WHEN NOT EXISTS(
                SELECT 1 FROM batch_allocations allocation
                JOIN production_batches batch ON batch.id=allocation.production_batch_id
                WHERE allocation.order_id=orders.id AND batch.status<>'completed')
                THEN 'complete' ELSE 'active' END,
                version=version+1,updated_at=$at
            WHERE id IN(
                SELECT DISTINCT allocation.order_id
                FROM production_run_outputs output
                JOIN batch_operations operation ON operation.id=output.batch_operation_id
                JOIN batch_allocations allocation
                  ON allocation.production_batch_id=operation.production_batch_id
                WHERE output.production_run_program_id=$programId
                  AND allocation.order_id IS NOT NULL);
            """;
        command.Parameters.AddWithValue("$programId", programId);
        command.Parameters.AddWithValue("$at", at);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ScalarIntAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
