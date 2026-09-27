using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// A pending Work Order (Production Batch) takes its operation list from its Case, and releasing it
/// freezes the list (owner decision 2026-09-27). The refresh brings pending Work Orders in line with
/// their Case: an operation that has not started takes its Case Operation's current data and keeps its
/// Machine placement, a Case Operation the Work Order lacks is added, and an operation whose Case
/// Operation is gone leaves the Work Order and its Machine. Started work is never changed, and an
/// operation that an official package, a bench session or locked production history references stays.
/// Released, complete and cancelled Work Orders are never touched.
/// </summary>
internal static class SqliteWorkOrderRouteRefresh
{
    private const string PendingWorkOrder =
        "batch.release_state = 'pending' AND batch.status NOT IN ('complete', 'completed', 'cancelled')";

    /// <summary>What a refresh changed; <see cref="NumberConflicts"/> lists Case Operations it could not add or renumber.</summary>
    internal sealed record Result(
        int WorkOrdersChanged,
        int OperationsAdded,
        int OperationsUpdated,
        int OperationsRemoved,
        IReadOnlyList<string> NumberConflicts);

    private sealed record Row(string Id, string SourceCaseOperationId, string Status, int Number, int Position);

    private sealed record CaseOperationKey(string Id, int Number);

    private sealed record Slot(Row? Existing, string? CaseOperationId, int Number);

    /// <summary>Refreshes the pending Work Orders of one Case.</summary>
    internal static Task<Result> RefreshCaseAsync(
        SqliteConnection connection, SqliteTransaction transaction, string caseId, string actor,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        RefreshAsync(connection, transaction, "batch.case_id = $key", caseId, actor, now, cancellationToken);

    /// <summary>Refreshes one Work Order when it is pending.</summary>
    internal static Task<Result> RefreshWorkOrderAsync(
        SqliteConnection connection, SqliteTransaction transaction, string batchId, string actor,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        RefreshAsync(connection, transaction, "batch.id = $key", batchId, actor, now, cancellationToken);

    /// <summary>Refreshes every pending Work Order.</summary>
    internal static Task<Result> RefreshAllAsync(
        SqliteConnection connection, SqliteTransaction transaction, string actor,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        RefreshAsync(connection, transaction, "$key IS NULL", null, actor, now, cancellationToken);

    /// <summary>
    /// Whether a Work Order holds a Case Operation: a released, complete or cancelled one keeps its
    /// frozen copy, and so does a pending one whose copy has started or must be kept.
    /// </summary>
    internal static async Task<bool> IsHeldByWorkOrderAsync(
        SqliteConnection connection, SqliteTransaction transaction, string caseOperationId,
        CancellationToken cancellationToken)
    {
        foreach (var copy in await ReadCopiesAsync(connection, transaction, caseOperationId, cancellationToken))
        {
            if (!copy.Pending || copy.Status != "not_started"
                || !await IsRemovableAsync(connection, transaction, copy.Id, cancellationToken))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Takes a Case Operation out of the pending Work Orders that copied it, so the Case Operation can
    /// be deleted. Returns false, changing nothing, when a Work Order holds it.
    /// </summary>
    internal static async Task<bool> TryReleaseCaseOperationAsync(
        SqliteConnection connection, SqliteTransaction transaction, string caseOperationId,
        CancellationToken cancellationToken)
    {
        if (await IsHeldByWorkOrderAsync(connection, transaction, caseOperationId, cancellationToken)) return false;

        var machines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var copy in await ReadCopiesAsync(connection, transaction, caseOperationId, cancellationToken))
        {
            await RemoveAsync(connection, transaction, copy.Id, machines, cancellationToken);
        }
        foreach (var machineId in machines)
        {
            await SqlitePlanningDeletionRepository.CompactMachineBacklogAsync(
                connection, transaction, machineId, cancellationToken);
        }
        return true;
    }

    private static async Task<IReadOnlyList<(string Id, bool Pending, string Status)>> ReadCopiesAsync(
        SqliteConnection connection, SqliteTransaction transaction, string caseOperationId,
        CancellationToken cancellationToken)
    {
        var copies = new List<(string Id, bool Pending, string Status)>();
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = $"""
            SELECT operation.id, {PendingWorkOrder}, operation.status
            FROM batch_operations operation
            JOIN production_batches batch ON batch.id = operation.production_batch_id
            WHERE operation.source_case_operation_id = $id;
            """;
        read.Parameters.AddWithValue("$id", caseOperationId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            copies.Add((reader.GetString(0), reader.GetInt64(1) == 1, reader.GetString(2)));
        }
        return copies;
    }

    private static async Task<Result> RefreshAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string filter,
        string? key,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var workOrders = new List<(string BatchId, string CaseId)>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = $"""
                SELECT batch.id, batch.case_id
                FROM production_batches batch
                WHERE {PendingWorkOrder} AND {filter}
                ORDER BY batch.created_at, batch.id;
                """;
            read.Parameters.AddWithValue("$key", (object?)key ?? DBNull.Value);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                workOrders.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var changed = 0;
        var added = 0;
        var updated = 0;
        var removed = 0;
        var conflicts = new List<string>();
        var machines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (batchId, caseId) in workOrders)
        {
            var (batchAdded, batchUpdated, batchRemoved) = await RefreshWorkOrderRouteAsync(
                connection, transaction, batchId, caseId, now, machines, conflicts, cancellationToken);
            if (batchAdded + batchUpdated + batchRemoved == 0) continue;

            changed++;
            added += batchAdded;
            updated += batchUpdated;
            removed += batchRemoved;
            await using (var bump = connection.CreateCommand())
            {
                bump.Transaction = transaction;
                bump.CommandText = "UPDATE production_batches SET version = version + 1, updated_at = $at WHERE id = $id;";
                bump.Parameters.AddWithValue("$id", batchId);
                bump.Parameters.AddWithValue("$at", FormatInstant(now));
                await bump.ExecuteNonQueryAsync(cancellationToken);
            }
            await SqliteStructuredEventLogRepository.AppendAsync(
                connection,
                transaction,
                new(
                    "production_batch_route_refreshed",
                    now,
                    actor,
                    new Dictionary<string, string> { ["productionBatchId"] = batchId, ["caseId"] = caseId },
                    "PENDING_WORK_ORDER_FOLLOWS_CASE",
                    null,
                    null,
                    new { added = batchAdded, updated = batchUpdated, removed = batchRemoved }),
                cancellationToken);
        }

        foreach (var machineId in machines)
        {
            await SqlitePlanningDeletionRepository.CompactMachineBacklogAsync(
                connection, transaction, machineId, cancellationToken);
        }
        await SqliteMachineAssignmentRepository.ReleaseProductionNoteAssignmentsAsync(
            connection, transaction, now, cancellationToken);
        return new Result(changed, added, updated, removed, conflicts);
    }

    private static async Task<(int Added, int Updated, int Removed)> RefreshWorkOrderRouteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchId,
        string caseId,
        DateTimeOffset now,
        ISet<string> machines,
        ICollection<string> conflicts,
        CancellationToken cancellationToken)
    {
        var caseOperations = new List<CaseOperationKey>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT id, operation_number
                FROM case_operations
                WHERE case_id = $caseId
                ORDER BY route_position, operation_number, id;
                """;
            read.Parameters.AddWithValue("$caseId", caseId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                caseOperations.Add(new CaseOperationKey(reader.GetString(0), reader.GetInt32(1)));
            }
        }

        var rows = new List<Row>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT id, source_case_operation_id, status, operation_number, route_position
                FROM batch_operations
                WHERE production_batch_id = $batchId
                ORDER BY route_position, id;
                """;
            read.Parameters.AddWithValue("$batchId", batchId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new Row(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetInt32(3), reader.GetInt32(4)));
            }
        }

        // An operation whose Case Operation is gone leaves the Work Order unless it must stay.
        var caseIds = caseOperations.Select(operation => operation.Id).ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        var kept = new List<Row>();
        var bySource = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (caseIds.Contains(row.SourceCaseOperationId) && bySource.TryAdd(row.SourceCaseOperationId, row))
            {
                continue;
            }
            if (row.Status == "not_started"
                && await IsRemovableAsync(connection, transaction, row.Id, cancellationToken))
            {
                await RemoveAsync(connection, transaction, row.Id, machines, cancellationToken);
                removed++;
            }
            else
            {
                kept.Add(row);
            }
        }

        // The Case order gives the route. Started and kept operations keep their number; an operation
        // whose Case number one of them still uses keeps its own number too, which is then taken as well.
        var taken = kept.Select(row => row.Number)
            .Concat(bySource.Values.Where(row => row.Status != "not_started").Select(row => row.Number))
            .ToHashSet();
        var keepsOwnNumber = new HashSet<string>(StringComparer.Ordinal);
        for (var blocked = true; blocked;)
        {
            blocked = false;
            foreach (var operation in caseOperations)
            {
                if (bySource.TryGetValue(operation.Id, out var row) && row.Status == "not_started"
                    && row.Number != operation.Number && taken.Contains(operation.Number)
                    && keepsOwnNumber.Add(row.Id))
                {
                    taken.Add(row.Number);
                    blocked = true;
                }
            }
        }

        var slots = new List<Slot>();
        foreach (var operation in caseOperations)
        {
            if (bySource.TryGetValue(operation.Id, out var row))
            {
                var keepsNumber = row.Status != "not_started" || keepsOwnNumber.Contains(row.Id);
                if (keepsOwnNumber.Contains(row.Id))
                    conflicts.Add($"OP{operation.Number.ToString(CultureInfo.InvariantCulture)}");
                slots.Add(new Slot(row, operation.Id, keepsNumber ? row.Number : operation.Number));
            }
            else if (taken.Contains(operation.Number))
            {
                conflicts.Add($"OP{operation.Number.ToString(CultureInfo.InvariantCulture)}");
            }
            else
            {
                slots.Add(new Slot(null, operation.Id, operation.Number));
            }
        }
        slots.AddRange(kept.Select(row => new Slot(row, null, row.Number)));

        var updated = await UpdateSnapshotFieldsAsync(connection, transaction, batchId, now, cancellationToken);

        // Renumbered or moved operations pass through temporary values so the per-Work Order
        // uniqueness of numbers and positions holds at every step.
        var moves = slots
            .Select((slot, position) => (Slot: slot, Position: position))
            .Where(entry => entry.Slot.Existing is { } row
                && (row.Number != entry.Slot.Number || row.Position != entry.Position))
            .ToArray();
        foreach (var (slot, position) in moves)
        {
            await SetNumberAndPositionAsync(
                connection, transaction, slot.Existing!.Id, 1_000_000 + position, 1_000_000 + position, now, cancellationToken);
        }
        foreach (var (slot, position) in moves)
        {
            await SetNumberAndPositionAsync(
                connection, transaction, slot.Existing!.Id, slot.Number, position, now, cancellationToken);
        }

        var added = 0;
        foreach (var (slot, position) in slots.Select((slot, position) => (slot, position)))
        {
            if (slot.Existing is not null) continue;
            await SqliteBatchOperationRows.InsertFromCaseOperationAsync(
                connection, transaction, batchId, slot.CaseOperationId!, slot.Number, position, now, cancellationToken);
            added++;
        }

        return (added, updated + moves.Length, removed);
    }

    /// <summary>Copies every Case Operation field into the Work Order operations that have not started.</summary>
    private static async Task<int> UpdateSnapshotFieldsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string batchId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE batch_operations
            SET name = source_operation.name,
                required_machine_type = source_operation.required_machine_type,
                setup_seconds = source_operation.setup_seconds,
                cycle_seconds = source_operation.cycle_seconds,
                dependency_type = source_operation.dependency_type,
                predecessor_source_case_operation_id = source_operation.predecessor_case_operation_id,
                simultaneous_group_key = source_operation.simultaneous_group_key,
                qa_seconds = source_operation.qa_seconds,
                load_unload_seconds = source_operation.load_unload_seconds,
                load_unload_requires_worker = source_operation.load_unload_requires_worker,
                automatic_loading = source_operation.automatic_loading,
                load_unload_every_n_parts = source_operation.load_unload_every_n_parts,
                day_shift_only = source_operation.day_shift_only,
                has_external_delay = source_operation.has_external_delay,
                external_delay_description = source_operation.external_delay_description,
                external_delay_duration = source_operation.external_delay_duration,
                external_delay_duration_unit = source_operation.external_delay_duration_unit,
                external_delay_calendar_id = source_operation.external_delay_calendar_id,
                external_delay_respect_master_calendar = source_operation.external_delay_respect_master_calendar,
                version = batch_operations.version + 1,
                updated_at = $now
            FROM case_operations source_operation
            WHERE batch_operations.production_batch_id = $batchId
              AND batch_operations.status = 'not_started'
              AND source_operation.id = batch_operations.source_case_operation_id
              AND (batch_operations.name IS NOT source_operation.name
                OR batch_operations.required_machine_type IS NOT source_operation.required_machine_type
                OR batch_operations.setup_seconds IS NOT source_operation.setup_seconds
                OR batch_operations.cycle_seconds IS NOT source_operation.cycle_seconds
                OR batch_operations.dependency_type IS NOT source_operation.dependency_type
                OR batch_operations.predecessor_source_case_operation_id IS NOT source_operation.predecessor_case_operation_id
                OR batch_operations.simultaneous_group_key IS NOT source_operation.simultaneous_group_key
                OR batch_operations.qa_seconds IS NOT source_operation.qa_seconds
                OR batch_operations.load_unload_seconds IS NOT source_operation.load_unload_seconds
                OR batch_operations.load_unload_requires_worker IS NOT source_operation.load_unload_requires_worker
                OR batch_operations.automatic_loading IS NOT source_operation.automatic_loading
                OR batch_operations.load_unload_every_n_parts IS NOT source_operation.load_unload_every_n_parts
                OR batch_operations.day_shift_only IS NOT source_operation.day_shift_only
                OR batch_operations.has_external_delay IS NOT source_operation.has_external_delay
                OR batch_operations.external_delay_description IS NOT source_operation.external_delay_description
                OR batch_operations.external_delay_duration IS NOT source_operation.external_delay_duration
                OR batch_operations.external_delay_duration_unit IS NOT source_operation.external_delay_duration_unit
                OR batch_operations.external_delay_calendar_id IS NOT source_operation.external_delay_calendar_id
                OR batch_operations.external_delay_respect_master_calendar IS NOT source_operation.external_delay_respect_master_calendar);
            """;
        update.Parameters.AddWithValue("$batchId", batchId);
        update.Parameters.AddWithValue("$now", FormatInstant(now));
        return await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SetNumberAndPositionAsync(
        SqliteConnection connection, SqliteTransaction transaction, string operationId, int number, int position,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE batch_operations
            SET operation_number = $number, route_position = $position,
                version = version + 1, updated_at = $now
            WHERE id = $id;
            """;
        update.Parameters.AddWithValue("$id", operationId);
        update.Parameters.AddWithValue("$number", number);
        update.Parameters.AddWithValue("$position", position);
        update.Parameters.AddWithValue("$now", FormatInstant(now));
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// An operation can leave its Work Order when no official package, bench session or locked
    /// production history refers to it; a planned Production Run of its own goes with it.
    /// </summary>
    private static async Task<bool> IsRemovableAsync(
        SqliteConnection connection, SqliteTransaction transaction, string operationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT NOT EXISTS (
                SELECT 1 FROM production_packages WHERE batch_operation_id = $id
                UNION ALL SELECT 1 FROM production_package_current WHERE batch_operation_id = $id
                UNION ALL SELECT 1 FROM eink_package_revisions WHERE batch_operation_id = $id
                UNION ALL SELECT 1 FROM haas_bench_sessions WHERE batch_operation_id = $id
                UNION ALL SELECT 1 FROM production_runs
                    WHERE legacy_batch_operation_id = $id AND structure_locked_at IS NOT NULL
                UNION ALL SELECT 1 FROM production_run_outputs output
                    JOIN production_run_programs program ON program.id = output.production_run_program_id
                    JOIN production_runs run ON run.id = program.production_run_id
                    WHERE output.batch_operation_id = $id
                      AND (run.structure_locked_at IS NOT NULL OR run.legacy_batch_operation_id IS NOT $id));
            """;
        command.Parameters.AddWithValue("$id", operationId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>Removes a not-started operation with its planning graph, as a Batch deletion does.</summary>
    private static async Task RemoveAsync(
        SqliteConnection connection, SqliteTransaction transaction, string operationId, ISet<string> machines,
        CancellationToken cancellationToken)
    {
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT machine_id FROM machine_assignments WHERE batch_operation_id = $id AND released_at IS NULL;";
            read.Parameters.AddWithValue("$id", operationId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) machines.Add(reader.GetString(0));
        }

        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM operation_pause_events WHERE batch_operation_id = $id;
            DELETE FROM machine_assignment_overrides WHERE batch_operation_id = $id;
            DELETE FROM external_resource_executions WHERE schedule_work_id IN (
                SELECT id FROM resource_schedule_work WHERE batch_operation_id = $id);
            DELETE FROM resource_schedule_assignments WHERE schedule_work_id IN (
                SELECT id FROM resource_schedule_work WHERE batch_operation_id = $id);
            DELETE FROM resource_schedule_work WHERE batch_operation_id = $id;
            DELETE FROM machine_assignments WHERE batch_operation_id = $id;
            DELETE FROM production_run_outputs WHERE batch_operation_id = $id;
            DELETE FROM production_run_programs WHERE production_run_id IN (
                SELECT id FROM production_runs WHERE legacy_batch_operation_id = $id);
            DELETE FROM production_runs WHERE legacy_batch_operation_id = $id;
            DELETE FROM batch_operations WHERE id = $id;
            """;
        delete.Parameters.AddWithValue("$id", operationId);
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string FormatInstant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
