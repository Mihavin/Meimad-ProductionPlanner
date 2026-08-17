using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.ProductionBatches;
using Meimad.Planner.Server.Domain.ProductionBatches;
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
        await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);

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
        await transaction.CommitAsync(cancellationToken);
        return batch;
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
                id, production_batch_id, allocation_type, order_id, quantity,
                version, created_at, updated_at)
            VALUES (
                $id, $batchId, $allocationType, $orderId, $quantity,
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
            command.CommandText = "SELECT case_id, status = 'cancelled' FROM orders WHERE id = $orderId;";
            command.Parameters.AddWithValue("$orderId", allocation.OrderId!);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            references.Add(await reader.ReadAsync(cancellationToken)
                ? new OrderAllocationReference(
                    allocation.OrderId!,
                    reader.GetString(0),
                    reader.GetBoolean(1))
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

    private static async Task EnsureEditAuthorityAsync(
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
        command.CommandText = "SELECT holder_client_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            throw new EditModeMutationException(
                "edit_mode_required",
                "No Windows client currently holds Edit Mode.");
        }

        if (!string.Equals(reader.GetString(0), editAuthority.ClientId, StringComparison.Ordinal)
            || reader.GetInt64(1) != editAuthority.Generation)
        {
            throw new EditModeMutationException(
                "edit_generation_stale",
                "This client does not hold the active Edit Mode generation.");
        }
    }

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
            reader.GetInt32(4),
            reader.GetInt32(5),
            ParseInstant(reader.GetString(6)),
            ParseInstant(reader.GetString(7)));
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
