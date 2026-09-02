using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.ProductionBatches;
using Meimad.Planner.Server.Domain.ProductionBatches;
using Meimad.Planner.Server.Domain.Readiness;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteProductionBatchRepository : IProductionBatchRepository
{
    private const string BatchProjection = """
        id,
        case_id,
        batch_number,
        status,
        planned_quantity,
        route_revision,
        version,
        created_at,
        updated_at
        """;

    private const string AllocationProjection = """
        id,
        production_batch_id,
        allocation_type,
        order_id,
        derived_order_key,
        quantity,
        version,
        created_at,
        updated_at
        """;

    private const string OperationProjection = """
        id,
        production_batch_id,
        source_case_operation_id,
        operation_number,
        route_position,
        name,
        required_machine_type,
        setup_seconds,
        cycle_seconds,
        status,
        version,
        created_at,
        updated_at,
        qa_seconds,
        load_unload_seconds,
        load_unload_requires_worker,
        automatic_loading,
        load_unload_every_n_parts,
        day_shift_only,
        actual_start,
        actual_end,
        actual_machine_id,
        has_external_delay,
        external_delay_description,
        external_delay_duration,
        external_delay_duration_unit,
        external_delay_calendar_id,
        external_delay_respect_master_calendar
        """;

    private readonly SqliteDatabase database;

    public SqliteProductionBatchRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<ProductionBatch> CreateAsync(
        ProductionBatch batch,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        if (!await CaseExistsAsync(connection, transaction, batch.CaseId, cancellationToken))
        {
            throw new ProductionBatchCaseNotFoundException(batch.CaseId);
        }

        if (!await CaseHasOperationsAsync(connection, transaction, batch.CaseId, cancellationToken))
        {
            throw new ProductionBatchRouteRequiredException();
        }

        if (await BatchNumberExistsAsync(
                connection,
                transaction,
                batch.CaseId,
                batch.BatchNumber,
                cancellationToken))
        {
            throw new ProductionBatchNumberConflictException(batch.CaseId, batch.BatchNumber);
        }

        var orderReferences = await ReadOrderReferencesAsync(
            connection,
            transaction,
            batch.Allocations,
            cancellationToken);
        ProductionBatchValidator.ValidateOrderCaseOwnership(batch.CaseId, orderReferences);

        await InsertBatchAsync(connection, transaction, batch, cancellationToken);
        foreach (var allocation in batch.Allocations)
        {
            await InsertAllocationAsync(connection, transaction, allocation, cancellationToken);
        }

        var operations = await InstantiateOperationsAsync(
            connection,
            transaction,
            batch,
            cancellationToken);
        await SqliteOrderLifecycle.RecomputeForBatchAsync(
            connection,
            transaction,
            batch.BatchId,
            batch.UpdatedAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return batch with { Operations = operations };
    }

    public async Task<ProductionBatch?> UpdateAsync(
        ProductionBatch batch,
        int expectedVersion,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);

        if (!await ExistsAsync(
                connection,
                transaction,
                "SELECT EXISTS(SELECT 1 FROM production_batches WHERE id = $id AND version = $version);",
                "$id",
                batch.BatchId,
                cancellationToken,
                ("$version", expectedVersion)))
        {
            return null;
        }
        var readinessBefore = await ReadReadinessAsync(
            connection, transaction, batch.BatchId, cancellationToken);

        if (await BatchNumberExistsAsync(
                connection,
                transaction,
                batch.CaseId,
                batch.BatchNumber,
                cancellationToken,
                batch.BatchId))
        {
            throw new ProductionBatchNumberConflictException(batch.CaseId, batch.BatchNumber);
        }

        var reservedMaterial = await ReadReservedMaterialAsync(
            connection, transaction, batch.BatchId, cancellationToken);
        if (reservedMaterial > batch.PlannedQuantity)
        {
            throw new ProductionBatchValidationException(
            [
                new("plannedQuantity", "material_reservation_exceeded",
                    $"Release material reservations before reducing plannedQuantity below {reservedMaterial}.")
            ]);
        }

        var orderReferences = await ReadOrderReferencesAsync(
            connection, transaction, batch.Allocations, cancellationToken);
        ProductionBatchValidator.ValidateOrderCaseOwnership(batch.CaseId, orderReferences);
        var affectedOrders = await SqliteOrderLifecycle.ReadCandidatesForBatchAsync(
            connection, transaction, batch.BatchId, cancellationToken);

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE production_batches
                SET batch_number = $batchNumber,
                    planned_quantity = $plannedQuantity,
                    version = version + 1,
                    updated_at = $updatedAt
                WHERE id = $id AND version = $expectedVersion;
                """;
            update.Parameters.AddWithValue("$batchNumber", batch.BatchNumber);
            update.Parameters.AddWithValue("$plannedQuantity", batch.PlannedQuantity);
            update.Parameters.AddWithValue("$updatedAt", FormatInstant(batch.UpdatedAt));
            update.Parameters.AddWithValue("$id", batch.BatchId);
            update.Parameters.AddWithValue("$expectedVersion", expectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return null;
            }
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM batch_allocations WHERE production_batch_id = $id;";
            delete.Parameters.AddWithValue("$id", batch.BatchId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var allocation in batch.Allocations)
        {
            await InsertAllocationAsync(connection, transaction, allocation, cancellationToken);
        }

        var newCandidates = await SqliteOrderLifecycle.ReadCandidatesForBatchAsync(
            connection, transaction, batch.BatchId, cancellationToken);
        await SqliteOrderLifecycle.RecomputeAsync(
            connection,
            transaction,
            affectedOrders.Concat(newCandidates).ToArray(),
            batch.UpdatedAt,
            cancellationToken);
        var readinessAfter = await ReadReadinessAsync(
            connection, transaction, batch.BatchId, cancellationToken);
        foreach (var pair in readinessAfter)
        {
            readinessBefore.TryGetValue(pair.Key, out var before);
            await SqliteReadinessAudit.AppendEvaluationAsync(
                connection, transaction, pair.Value.Context, before?.Result, pair.Value.Result,
                batch.UpdatedAt, actor, "production_batch_quantity_or_allocations_changed",
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return batch;
    }

    public async Task<ProductionBatch?> CancelProductionAsync(
        string batchId,
        int expectedVersion,
        string reason,
        DateTimeOffset now,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAuthorityAsync(
            connection, transaction, editAuthority, cancellationToken);

        string status;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT status,version FROM production_batches WHERE id=$id;";
            read.Parameters.AddWithValue("$id", batchId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            status = reader.GetString(0);
            if (reader.GetInt32(1) != expectedVersion) return null;
        }
        if (status == "cancelled")
        {
            await transaction.CommitAsync(cancellationToken);
            return await GetByIdAsync(batchId, cancellationToken);
        }

        await using (var shared = connection.CreateCommand())
        {
            shared.Transaction = transaction;
            shared.CommandText = """
                SELECT run.id
                FROM production_runs run
                WHERE EXISTS (
                    SELECT 1
                    FROM production_run_programs program
                    JOIN production_run_outputs output
                      ON output.production_run_program_id=program.id
                    JOIN batch_operations operation ON operation.id=output.batch_operation_id
                    WHERE program.production_run_id=run.id
                      AND operation.production_batch_id=$batchId)
                  AND EXISTS (
                    SELECT 1
                    FROM production_run_programs program
                    JOIN production_run_outputs output
                      ON output.production_run_program_id=program.id
                    JOIN batch_operations operation ON operation.id=output.batch_operation_id
                    WHERE program.production_run_id=run.id
                      AND operation.production_batch_id<>$batchId)
                LIMIT 1;
                """;
            shared.Parameters.AddWithValue("$batchId", batchId);
            if (await shared.ExecuteScalarAsync(cancellationToken) is string sharedRunId)
                throw new ProductionBatchCancellationException(
                    "coupled_run_requires_joint_cancellation",
                    $"Production Run '{sharedRunId}' also produces another Batch. Cancel the coupled production through an explicit joint action.");
        }

        var affectedOrders = await SqliteOrderLifecycle.ReadCandidatesForBatchAsync(
            connection, transaction, batchId, cancellationToken);
        var machineIds = new List<string>();
        await using (var machines = connection.CreateCommand())
        {
            machines.Transaction = transaction;
            machines.CommandText = """
                SELECT DISTINCT assignment.machine_id
                FROM machine_assignments assignment
                LEFT JOIN batch_operations operation
                  ON operation.id=assignment.batch_operation_id
                WHERE operation.production_batch_id=$batchId
                   OR assignment.production_run_id IN (
                       SELECT DISTINCT program.production_run_id
                       FROM production_run_programs program
                       JOIN production_run_outputs output
                         ON output.production_run_program_id=program.id
                       JOIN batch_operations linked_operation
                         ON linked_operation.id=output.batch_operation_id
                       WHERE linked_operation.production_batch_id=$batchId)
                ORDER BY assignment.machine_id;
                """;
            machines.Parameters.AddWithValue("$batchId", batchId);
            await using var reader = await machines.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) machineIds.Add(reader.GetString(0));
        }

        long completedCycles;
        long producedParts;
        int runCount;
        await using (var counts = connection.CreateCommand())
        {
            counts.Transaction = transaction;
            counts.CommandText = """
                SELECT
                    (SELECT COALESCE(SUM(program.completed_cycle_count),0)
                     FROM production_run_programs program
                     WHERE EXISTS (
                         SELECT 1 FROM production_run_outputs output
                         JOIN batch_operations operation ON operation.id=output.batch_operation_id
                         WHERE output.production_run_program_id=program.id
                           AND operation.production_batch_id=$batchId)),
                    (SELECT COALESCE(SUM(output.produced_quantity),0)
                     FROM production_run_outputs output
                     JOIN batch_operations operation ON operation.id=output.batch_operation_id
                     WHERE operation.production_batch_id=$batchId),
                    (SELECT COUNT(DISTINCT program.production_run_id)
                     FROM production_run_programs program
                     WHERE EXISTS (
                         SELECT 1 FROM production_run_outputs output
                         JOIN batch_operations operation ON operation.id=output.batch_operation_id
                         WHERE output.production_run_program_id=program.id
                           AND operation.production_batch_id=$batchId));
                """;
            counts.Parameters.AddWithValue("$batchId", batchId);
            await using var reader = await counts.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            completedCycles = reader.GetInt64(0);
            producedParts = reader.GetInt64(1);
            runCount = reader.GetInt32(2);
        }

        var at = FormatInstant(now);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE operation_pause_events
                SET status='closed',pause_ended_at=$at,updated_at=$at,version=version+1
                WHERE status='active' AND batch_operation_id IN (
                    SELECT id FROM batch_operations WHERE production_batch_id=$batchId);

                UPDATE haas_bench_state_intervals
                SET ended_at=$at
                WHERE ended_at IS NULL AND bench_id IN (
                    SELECT bench.id FROM haas_bench_sessions bench
                    JOIN batch_operations operation ON operation.id=bench.batch_operation_id
                    WHERE operation.production_batch_id=$batchId);

                UPDATE haas_bench_sessions
                SET state='COMPLETED',part_counting_enabled=0,produced_quantity=0,
                    completed_at=COALESCE(completed_at,$at),version=version+1,updated_at=$at
                WHERE batch_operation_id IN (
                    SELECT id FROM batch_operations WHERE production_batch_id=$batchId);

                DELETE FROM machine_assignments
                WHERE batch_operation_id IN (
                    SELECT id FROM batch_operations WHERE production_batch_id=$batchId)
                   OR production_run_id IN (
                       SELECT DISTINCT program.production_run_id
                       FROM production_run_programs program
                       JOIN production_run_outputs output
                         ON output.production_run_program_id=program.id
                       JOIN batch_operations operation ON operation.id=output.batch_operation_id
                       WHERE operation.production_batch_id=$batchId);

                UPDATE production_run_outputs
                SET status=CASE WHEN produced_quantity>0
                                THEN 'ABORTED_REMAINDER_RELEASED' ELSE 'RELEASED' END,
                    produced_quantity=0,version=version+1,updated_at=$at
                WHERE production_run_program_id IN (
                    SELECT DISTINCT program.id
                    FROM production_run_programs program
                    JOIN production_run_outputs output
                      ON output.production_run_program_id=program.id
                    JOIN batch_operations operation ON operation.id=output.batch_operation_id
                    WHERE operation.production_batch_id=$batchId);

                UPDATE production_run_programs
                SET status='CANCELLED',completed_cycle_count=0,
                    version=version+1,updated_at=$at
                WHERE production_run_id IN (
                    SELECT DISTINCT program.production_run_id
                    FROM production_run_programs program
                    JOIN production_run_outputs output
                      ON output.production_run_program_id=program.id
                    JOIN batch_operations operation ON operation.id=output.batch_operation_id
                    WHERE operation.production_batch_id=$batchId);

                UPDATE production_runs
                SET status='CANCELLED',version=version+1,updated_at=$at
                WHERE id IN (
                    SELECT DISTINCT program.production_run_id
                    FROM production_run_programs program
                    JOIN production_run_outputs output
                      ON output.production_run_program_id=program.id
                    JOIN batch_operations operation ON operation.id=output.batch_operation_id
                    WHERE operation.production_batch_id=$batchId);

                UPDATE batch_operations
                SET status='cancelled',actual_end=CASE WHEN actual_start IS NULL THEN actual_end
                                                       ELSE COALESCE(actual_end,$at) END,
                    version=version+1,updated_at=$at
                WHERE production_batch_id=$batchId;

                DELETE FROM batch_material_reservations WHERE production_batch_id=$batchId;

                UPDATE production_batches
                SET status='cancelled',version=version+1,updated_at=$at
                WHERE id=$batchId AND version=$expectedVersion;
                """;
            update.Parameters.AddWithValue("$batchId", batchId);
            update.Parameters.AddWithValue("$expectedVersion", expectedVersion);
            update.Parameters.AddWithValue("$at", at);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var machineId in machineIds)
            await CompactMachineBacklogAsync(
                connection, transaction, machineId, now, cancellationToken);

        await SqliteOrderLifecycle.RecomputeAsync(
            connection, transaction, affectedOrders, now, cancellationToken);
        await SqliteStructuredEventLogRepository.AppendAsync(
            connection, transaction,
            new("production_batch_cancelled", now, actor,
                new Dictionary<string, string> { ["productionBatchId"] = batchId },
                "PLANNER_CANCELLED", reason,
                new { status, completedCycles, producedParts },
                new { status = "cancelled", completedCycles = 0, producedParts = 0, runCount }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetByIdAsync(batchId, cancellationToken);
    }

    private static async Task CompactMachineBacklogAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string machineId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE machine_assignments
            SET backlog_position=backlog_position+1000000
            WHERE machine_id=$machineId;
            WITH ranked AS (
                SELECT id,ROW_NUMBER() OVER(ORDER BY backlog_position,id)-1 AS position
                FROM machine_assignments WHERE machine_id=$machineId)
            UPDATE machine_assignments
            SET backlog_position=(SELECT position FROM ranked WHERE ranked.id=machine_assignments.id),
                version=version+1,updated_at=$at
            WHERE machine_id=$machineId;
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        command.Parameters.AddWithValue("$at", FormatInstant(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> CaseHasOperationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string caseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM case_operations WHERE case_id = $caseId);";
        command.Parameters.AddWithValue("$caseId", caseId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<int> ReadReservedMaterialAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(SUM(quantity), 0)
            FROM batch_material_reservations
            WHERE production_batch_id = $batchId;
            """;
        command.Parameters.AddWithValue("$batchId", batchId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<ProductionBatch?> GetByIdAsync(
        string batchId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        ProductionBatch? batch;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {BatchProjection} FROM production_batches WHERE id = $id;";
            command.Parameters.AddWithValue("$id", batchId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            batch = await reader.ReadAsync(cancellationToken) ? ReadBatch(reader) : null;
        }

        if (batch is null)
        {
            return null;
        }

        var allocations = await ReadAllocationsAsync(connection, batchId, cancellationToken);
        var operations = await ReadOperationsAsync(connection, batchId, cancellationToken);
        return batch with { Allocations = allocations, Operations = operations };
    }

    public async Task<IReadOnlyList<ProductionBatch>> ListByCaseAsync(
        string caseId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var batches = new List<ProductionBatch>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT {BatchProjection}
                FROM production_batches
                WHERE case_id = $caseId
                ORDER BY created_at DESC, batch_number COLLATE NOCASE, id;
                """;
            command.Parameters.AddWithValue("$caseId", caseId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                batches.Add(ReadBatch(reader));
            }
        }

        for (var index = 0; index < batches.Count; index++)
        {
            var batch = batches[index];
            var allocations = await ReadAllocationsAsync(
                connection,
                batch.BatchId,
                cancellationToken);
            var operations = await ReadOperationsAsync(
                connection,
                batch.BatchId,
                cancellationToken);
            batches[index] = batch with { Allocations = allocations, Operations = operations };
        }

        return batches;
    }

    public async Task<IReadOnlyList<BatchOperation>> ListOperationsAsync(
        string batchId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ReadOperationsAsync(connection, batchId, cancellationToken);
    }

    private static async Task InsertBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionBatch batch,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO production_batches (
                id, case_id, batch_number, status, planned_quantity, route_revision,
                version, created_at, updated_at)
            VALUES (
                $id, $caseId, $batchNumber, $status, $plannedQuantity, $routeRevision,
                $version, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", batch.BatchId);
        command.Parameters.AddWithValue("$caseId", batch.CaseId);
        command.Parameters.AddWithValue("$batchNumber", batch.BatchNumber);
        command.Parameters.AddWithValue("$status", batch.Status);
        command.Parameters.AddWithValue("$plannedQuantity", batch.PlannedQuantity);
        command.Parameters.AddWithValue(
            "$routeRevision",
            batch.RouteRevision.HasValue ? batch.RouteRevision.Value : DBNull.Value);
        command.Parameters.AddWithValue("$version", batch.Version);
        command.Parameters.AddWithValue("$createdAt", FormatInstant(batch.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatInstant(batch.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAllocationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BatchAllocation allocation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO batch_allocations (
                id, production_batch_id, allocation_type, order_id, derived_order_key, quantity,
                version, created_at, updated_at)
            VALUES (
                $id, $batchId, $allocationType, $orderId, $derivedOrderKey, $quantity,
                $version, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", allocation.AllocationId);
        command.Parameters.AddWithValue("$batchId", allocation.BatchId);
        command.Parameters.AddWithValue(
            "$allocationType",
            allocation.AllocationType.ToStorageToken());
        command.Parameters.AddWithValue(
            "$orderId",
            allocation.OrderId is null ? DBNull.Value : allocation.OrderId);
        command.Parameters.AddWithValue(
            "$derivedOrderKey",
            allocation.DerivedOrderKey is null ? DBNull.Value : allocation.DerivedOrderKey);
        command.Parameters.AddWithValue("$quantity", allocation.Quantity);
        command.Parameters.AddWithValue("$version", allocation.Version);
        command.Parameters.AddWithValue("$createdAt", FormatInstant(allocation.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatInstant(allocation.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<BatchOperation>> InstantiateOperationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionBatch batch,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<InstantiatedOperation>();
        await using (var readCommand = connection.CreateCommand())
        {
            readCommand.Transaction = transaction;
            readCommand.CommandText = """
                SELECT id, operation_number, route_position, name,
                       required_machine_type, setup_seconds, cycle_seconds,
                       dependency_type, predecessor_case_operation_id,
                       simultaneous_group_key,
                       qa_seconds, load_unload_seconds, load_unload_requires_worker,
                       automatic_loading, load_unload_every_n_parts, day_shift_only,
                       has_external_delay, external_delay_description, external_delay_duration,
                       external_delay_duration_unit, external_delay_calendar_id,
                       external_delay_respect_master_calendar
                FROM case_operations
                WHERE case_id = $caseId
                ORDER BY route_position, operation_number, id;
                """;
            readCommand.Parameters.AddWithValue("$caseId", batch.CaseId);
            await using var reader = await readCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                snapshots.Add(new InstantiatedOperation(
                    new BatchOperation(
                        Guid.NewGuid().ToString("N"),
                        batch.BatchId,
                        reader.GetString(0),
                        reader.GetInt32(1),
                        reader.GetInt32(2),
                        reader.GetString(3),
                        GetNullableString(reader, 4),
                        GetNullableInt32(reader, 5),
                        GetNullableInt32(reader, 6),
                        ProductionBatchValidator.BatchOperationNotStartedStatus,
                        1,
                        batch.CreatedAt,
                        batch.CreatedAt,
                        reader.GetInt32(10), reader.GetInt32(11), reader.GetInt32(12) == 1,
                        reader.GetInt32(13) == 1, GetNullableInt32(reader, 14), reader.GetInt32(15) == 1,
                        null, null, null,
                        reader.GetInt32(16) == 1, GetNullableString(reader, 17), reader.GetDouble(18),
                        reader.GetString(19), GetNullableString(reader, 20), reader.GetInt32(21) == 1),
                    reader.GetString(7),
                    GetNullableString(reader, 8),
                    GetNullableString(reader, 9)));
            }
        }

        foreach (var snapshot in snapshots)
        {
            var operation = snapshot.Operation;
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO batch_operations (
                    id, production_batch_id, source_case_operation_id,
                    operation_number, route_position, name, required_machine_type,
                    setup_seconds, cycle_seconds, status, version, created_at, updated_at,
                    dependency_type, predecessor_source_case_operation_id,
                    simultaneous_group_key,
                    qa_seconds, load_unload_seconds, load_unload_requires_worker,
                    automatic_loading, load_unload_every_n_parts, day_shift_only,
                    has_external_delay, external_delay_description, external_delay_duration,
                    external_delay_duration_unit, external_delay_calendar_id,
                    external_delay_respect_master_calendar)
                VALUES (
                    $id, $batchId, $sourceId,
                    $operationNumber, $routePosition, $name, $requiredMachineType,
                    $setupSeconds, $cycleSeconds, $status, $version, $createdAt, $updatedAt,
                    $dependencyType, $predecessorSourceId, $simultaneousGroupKey,
                    $qaSeconds, $loadUnloadSeconds, $loadUnloadRequiresWorker,
                    $automaticLoading, $loadUnloadEveryNParts, $dayShiftOnly,
                    $hasExternalDelay, $externalDelayDescription, $externalDelayDuration,
                    $externalDelayDurationUnit, $externalDelayCalendarId,
                    $externalDelayRespectMasterCalendar);
                """;
            insertCommand.Parameters.AddWithValue("$id", operation.BatchOperationId);
            insertCommand.Parameters.AddWithValue("$batchId", operation.BatchId);
            insertCommand.Parameters.AddWithValue("$sourceId", operation.SourceCaseOperationId);
            insertCommand.Parameters.AddWithValue("$operationNumber", operation.OperationNumber);
            insertCommand.Parameters.AddWithValue("$routePosition", operation.RoutePosition);
            insertCommand.Parameters.AddWithValue("$name", operation.Name);
            insertCommand.Parameters.AddWithValue(
                "$requiredMachineType",
                operation.RequiredMachineType is null ? DBNull.Value : operation.RequiredMachineType);
            insertCommand.Parameters.AddWithValue(
                "$setupSeconds",
                operation.SetupTimeSeconds.HasValue ? operation.SetupTimeSeconds.Value : DBNull.Value);
            insertCommand.Parameters.AddWithValue(
                "$cycleSeconds",
                operation.CycleTimePerPartSeconds.HasValue
                    ? operation.CycleTimePerPartSeconds.Value
                    : DBNull.Value);
            insertCommand.Parameters.AddWithValue("$status", operation.Status);
            insertCommand.Parameters.AddWithValue("$version", operation.Version);
            insertCommand.Parameters.AddWithValue("$createdAt", FormatInstant(operation.CreatedAt));
            insertCommand.Parameters.AddWithValue("$updatedAt", FormatInstant(operation.UpdatedAt));
            insertCommand.Parameters.AddWithValue("$qaSeconds", operation.QaTimeAfterSetupSeconds);
            insertCommand.Parameters.AddWithValue("$loadUnloadSeconds", operation.LoadUnloadTimeSeconds);
            insertCommand.Parameters.AddWithValue("$loadUnloadRequiresWorker", operation.LoadUnloadRequiresWorker ? 1 : 0);
            insertCommand.Parameters.AddWithValue("$automaticLoading", operation.AutomaticLoading ? 1 : 0);
            insertCommand.Parameters.AddWithValue("$loadUnloadEveryNParts", operation.LoadUnloadEveryNParts.HasValue ? operation.LoadUnloadEveryNParts.Value : DBNull.Value);
            insertCommand.Parameters.AddWithValue("$dayShiftOnly", operation.DayShiftOnly ? 1 : 0);
            insertCommand.Parameters.AddWithValue("$hasExternalDelay", operation.HasExternalDelay ? 1 : 0);
            insertCommand.Parameters.AddWithValue("$externalDelayDescription", operation.ExternalDelayDescription is null ? DBNull.Value : operation.ExternalDelayDescription);
            insertCommand.Parameters.AddWithValue("$externalDelayDuration", operation.ExternalDelayDuration);
            insertCommand.Parameters.AddWithValue("$externalDelayDurationUnit", operation.ExternalDelayDurationUnit);
            insertCommand.Parameters.AddWithValue("$externalDelayCalendarId", operation.ExternalDelayCalendarId is null ? DBNull.Value : operation.ExternalDelayCalendarId);
            insertCommand.Parameters.AddWithValue("$externalDelayRespectMasterCalendar", operation.RespectMasterCalendar ? 1 : 0);
            insertCommand.Parameters.AddWithValue("$dependencyType", snapshot.DependencyType);
            insertCommand.Parameters.AddWithValue(
                "$predecessorSourceId",
                snapshot.PredecessorSourceCaseOperationId is null
                    ? DBNull.Value
                    : snapshot.PredecessorSourceCaseOperationId);
            insertCommand.Parameters.AddWithValue(
                "$simultaneousGroupKey",
                snapshot.SimultaneousGroupKey is null
                    ? DBNull.Value
                    : snapshot.SimultaneousGroupKey);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return snapshots.Select(snapshot => snapshot.Operation).ToArray();
    }

    private static async Task<IReadOnlyList<OrderAllocationReference>> ReadOrderReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<BatchAllocation> allocations,
        CancellationToken cancellationToken)
    {
        var references = new List<OrderAllocationReference>();
        foreach (var allocation in allocations.Where(allocation =>
                     allocation.AllocationType == BatchAllocationType.Order))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT orders.case_id,
                       orders.status = 'cancelled',
                       NOT EXISTS (
                           SELECT 1
                           FROM kitaron_sync_links case_link
                           WHERE case_link.source_entity = 'case'
                             AND case_link.target_id = orders.case_id)
                       OR (
                           orders.kitaron_history_only = 0
                           AND EXISTS (
                               SELECT 1
                               FROM kitaron_sync_links order_link
                               WHERE order_link.source_entity = 'order'
                                 AND order_link.target_id = orders.id))
                           AS is_current_authoritative_demand
                FROM orders
                WHERE orders.id = $orderId;
                """;
            command.Parameters.AddWithValue("$orderId", allocation.OrderId!);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            references.Add(await reader.ReadAsync(cancellationToken)
                ? new OrderAllocationReference(
                    allocation.OrderId!,
                    reader.GetString(0),
                    reader.GetBoolean(1),
                    reader.GetBoolean(2))
                : new OrderAllocationReference(allocation.OrderId!, null));
        }

        return references;
    }

    private static async Task<IReadOnlyList<BatchAllocation>> ReadAllocationsAsync(
        SqliteConnection connection,
        string batchId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {AllocationProjection}
            FROM batch_allocations
            WHERE production_batch_id = $batchId
            ORDER BY CASE allocation_type
                         WHEN 'order' THEN 0
                         WHEN 'stock' THEN 1
                         ELSE 2
                     END,
                     order_id,
                     id;
            """;
        command.Parameters.AddWithValue("$batchId", batchId);
        var allocations = new List<BatchAllocation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            allocations.Add(ReadAllocation(reader));
        }

        return allocations;
    }

    private static async Task<IReadOnlyList<BatchOperation>> ReadOperationsAsync(
        SqliteConnection connection,
        string batchId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {OperationProjection}
            FROM batch_operations
            WHERE production_batch_id = $batchId
            ORDER BY route_position, operation_number, id;
            """;
        command.Parameters.AddWithValue("$batchId", batchId);
        var operations = new List<BatchOperation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            operations.Add(ReadOperation(reader));
        }

        return operations;
    }

    private static async Task<bool> CaseExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string caseId,
        CancellationToken cancellationToken) =>
        await ExistsAsync(
            connection,
            transaction,
            "SELECT EXISTS(SELECT 1 FROM cases WHERE id = $caseId);",
            "$caseId",
            caseId,
            cancellationToken);

    private static async Task<bool> BatchNumberExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string caseId,
        string batchNumber,
        CancellationToken cancellationToken,
        string? excludedBatchId = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM production_batches
                WHERE case_id = $caseId AND batch_number = $batchNumber
                  AND ($excludedBatchId IS NULL OR id <> $excludedBatchId));
            """;
        command.Parameters.AddWithValue("$caseId", caseId);
        command.Parameters.AddWithValue("$batchNumber", batchNumber);
        command.Parameters.AddWithValue("$excludedBatchId", excludedBatchId is null ? DBNull.Value : excludedBatchId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string parameterName,
        string parameterValue,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] extra)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue(parameterName, parameterValue);
        foreach (var parameter in extra)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<string> EnsureEditAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(
            connection,
            transaction,
            DateTimeOffset.UtcNow,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, holder_user_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            throw new EditModeMutationException(
                "edit_mode_required",
                "No Windows client currently holds Edit Mode.");
        }

        if (!string.Equals(reader.GetString(0), editAuthority.ClientId, StringComparison.Ordinal)
            || reader.GetInt64(2) != editAuthority.Generation)
        {
            throw new EditModeMutationException(
                "edit_generation_stale",
                "This client does not hold the active Edit Mode generation.");
        }
        return reader.IsDBNull(1) ? editAuthority.ClientId : reader.GetString(1);
    }

    private static async Task<IReadOnlyDictionary<string, ReadinessSnapshot>> ReadReadinessAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchId,
        CancellationToken token)
    {
        var ids = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id FROM batch_operations WHERE production_batch_id = $batchId ORDER BY id;";
            command.Parameters.AddWithValue("$batchId", batchId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) ids.Add(reader.GetString(0));
        }
        var values = new Dictionary<string, ReadinessSnapshot>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var context = await SqliteProductionReadinessContextReader.ReadAsync(
                connection, transaction, id, token);
            if (context is not null)
                values[id] = new(context, ProductionReadinessEvaluator.Evaluate(context));
        }
        return values;
    }

    private sealed record ReadinessSnapshot(
        ProductionReadinessContext Context,
        ProductionReadinessResult Result);

    private static ProductionBatch ReadBatch(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt32(4),
        GetNullableInt32(reader, 5),
        [],
        [],
        reader.GetInt32(6),
        ParseInstant(reader.GetString(7)),
        ParseInstant(reader.GetString(8)));

    private static BatchAllocation ReadAllocation(SqliteDataReader reader)
    {
        var token = reader.GetString(2);
        if (!BatchAllocationTypes.TryParseStorageToken(token, out var type))
        {
            throw new InvalidDataException($"Stored Batch Allocation type '{token}' is invalid.");
        }

        return new BatchAllocation(
            reader.GetString(0),
            reader.GetString(1),
            type,
            GetNullableString(reader, 3),
            reader.GetInt32(5),
            reader.GetInt32(6),
            ParseInstant(reader.GetString(7)),
            ParseInstant(reader.GetString(8)),
            GetNullableString(reader, 4));
    }

    private static BatchOperation ReadOperation(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt32(3),
        reader.GetInt32(4),
        reader.GetString(5),
        GetNullableString(reader, 6),
        GetNullableInt32(reader, 7),
        GetNullableInt32(reader, 8),
        reader.GetString(9),
        reader.GetInt32(10),
        ParseInstant(reader.GetString(11)),
        ParseInstant(reader.GetString(12)),
        reader.GetInt32(13), reader.GetInt32(14), reader.GetInt32(15) == 1,
        reader.GetInt32(16) == 1, GetNullableInt32(reader, 17), reader.GetInt32(18) == 1,
        GetNullableInstant(reader, 19), GetNullableInstant(reader, 20), GetNullableString(reader, 21),
        reader.GetInt32(22) == 1, GetNullableString(reader, 23), reader.GetDouble(24),
        reader.GetString(25), GetNullableString(reader, 26), reader.GetInt32(27) == 1);

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? GetNullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static DateTimeOffset? GetNullableInstant(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseInstant(reader.GetString(ordinal));

    private static string FormatInstant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseInstant(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record InstantiatedOperation(
        BatchOperation Operation,
        string DependencyType,
        string? PredecessorSourceCaseOperationId,
        string? SimultaneousGroupKey);
}
