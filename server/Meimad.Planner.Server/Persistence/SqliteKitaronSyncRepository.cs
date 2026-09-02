using System.Globalization;
using Meimad.Planner.Server.Application.Kitaron;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteKitaronSyncRepository(
    SqliteDatabase database) : IKitaronSyncRepository
{
    public async Task<KitaronSyncStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ReadStatusAsync(connection, null, cancellationToken);
    }

    public async Task<IReadOnlySet<string>> GetExistingCasePartNumbersAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT TRIM(part_number)
            FROM cases
            WHERE NULLIF(TRIM(part_number), '') IS NOT NULL;
            """;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    public async Task<KitaronSyncStatus> MarkStartedAsync(
        int mappingVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE kitaron_sync_state
            SET sync_status = 'running', message = 'Reading the configured Kitaron view.',
                last_started_at = $now, last_completed_at = NULL,
                mapping_version = $mappingVersion, version = version + 1,
                updated_at = $now
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$mappingVersion", mappingVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await ReadStatusAsync(connection, null, cancellationToken);
    }

    public async Task<KitaronSyncStatus> MarkFailedAsync(
        string status,
        string message,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (status is not ("failed" or "blocked")) throw new ArgumentOutOfRangeException(nameof(status));
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE kitaron_sync_state
            SET sync_status = $status, message = $message, last_completed_at = $now,
                version = version + 1, updated_at = $now
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$message", Limit(message, 2000));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await ReadStatusAsync(connection, null, cancellationToken);
    }

    public async Task<KitaronSyncStatus> ApplyAsync(
        KitaronSyncPlan plan,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var counts = new MutableCounts();
        var caseIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await DeactivateMaterialOrdersAsync(connection, transaction, now, cancellationToken);

        foreach (var item in plan.Cases)
        {
            var resolved = await ResolveCaseAsync(connection, transaction, item, now, counts, cancellationToken);
            caseIds[item.SourceKey] = resolved;
        }
        foreach (var item in plan.Orders)
        {
            if (!caseIds.TryGetValue(item.CaseSourceKey, out var caseId))
                throw new KitaronSyncDataException($"Order {item.SourceKey} references an unresolved Case.");
            await ResolveOrderAsync(connection, transaction, item, caseId, now, counts, cancellationToken);
        }
        await RemoveNonKitaronOrdersAsync(
            connection, transaction, caseIds, plan.Orders, now, counts, cancellationToken);
        foreach (var item in plan.Operations)
        {
            if (!caseIds.TryGetValue(item.CaseSourceKey, out var caseId))
                throw new KitaronSyncDataException($"Operation {item.SourceKey} references an unresolved Case.");
            await ResolveOperationAsync(connection, transaction, item, caseId, now, counts, cancellationToken);
        }
        foreach (var item in plan.Components)
        {
            if (!caseIds.TryGetValue(item.ParentCaseSourceKey, out var parentCaseId)
                || !caseIds.TryGetValue(item.ChildCaseSourceKey, out var childCaseId))
            {
                throw new KitaronSyncDataException($"Component {item.SourceKey} references an unresolved Case.");
            }
            await ResolveComponentAsync(
                connection, transaction, item, parentCaseId, childCaseId, now, counts, cancellationToken);
        }
        foreach (var item in plan.MaterialOrders ?? [])
            await UpsertMaterialOrderAsync(connection, transaction, item, now, cancellationToken);
        await DeactivateMissingComponentsAsync(
            connection, transaction, plan.KnownComponentSourceKeys, now, counts, cancellationToken);
        await SynchronizeNotStartedBatchOperationTimesAsync(
            connection, transaction, now, cancellationToken);

        var message = $"Synchronized {plan.SourceRows:N0} source rows: " +
            $"{counts.CasesCreated} Case(s), {counts.OrdersCreated} Order(s), and " +
            $"{counts.OperationsCreated} Case Operation(s), and {counts.ComponentsCreated} Case Component(s) created. " +
            $"{counts.BatchesDeleted} dependent Production Batch(es) and {counts.OrdersDeleted} non-Kitaron Order(s) removed. " +
            $"{counts.HistoricalOrdersRetained} superseded Order(s) retained only for locked production history. " +
            $"{plan.MaterialOrders?.Count ?? 0:N0} Kitaron material order line(s) with delivery approval data imported as advisory records. " +
            "Existing or linked records were reused safely.";
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE kitaron_sync_state
                SET sync_status = 'succeeded', message = $message, last_completed_at = $now,
                    source_rows = $sourceRows,
                    cases_created = $casesCreated, cases_updated = $casesUpdated, cases_matched = $casesMatched,
                    orders_created = $ordersCreated, orders_updated = $ordersUpdated, orders_matched = $ordersMatched,
                    operations_created = $operationsCreated, operations_updated = $operationsUpdated,
                    operations_matched = $operationsMatched, warning_count = $warnings,
                    components_created = $componentsCreated, components_updated = $componentsUpdated,
                    components_matched = $componentsMatched,
                    mapping_version = $mappingVersion, version = version + 1, updated_at = $now
                WHERE id = 1;
                """;
            command.Parameters.AddWithValue("$message", message);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$sourceRows", plan.SourceRows);
            command.Parameters.AddWithValue("$casesCreated", counts.CasesCreated);
            command.Parameters.AddWithValue("$casesUpdated", counts.CasesUpdated);
            command.Parameters.AddWithValue("$casesMatched", counts.CasesMatched);
            command.Parameters.AddWithValue("$ordersCreated", counts.OrdersCreated);
            command.Parameters.AddWithValue("$ordersUpdated", counts.OrdersUpdated);
            command.Parameters.AddWithValue("$ordersMatched", counts.OrdersMatched);
            command.Parameters.AddWithValue("$operationsCreated", counts.OperationsCreated);
            command.Parameters.AddWithValue("$operationsUpdated", counts.OperationsUpdated);
            command.Parameters.AddWithValue("$operationsMatched", counts.OperationsMatched);
            command.Parameters.AddWithValue("$componentsCreated", counts.ComponentsCreated);
            command.Parameters.AddWithValue("$componentsUpdated", counts.ComponentsUpdated);
            command.Parameters.AddWithValue("$componentsMatched", counts.ComponentsMatched);
            command.Parameters.AddWithValue("$warnings", plan.Warnings.Count + counts.Warnings);
            command.Parameters.AddWithValue("$mappingVersion", plan.MappingVersion);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var result = await ReadStatusAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task DeactivateMaterialOrdersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE kitaron_material_orders
            SET active = 0, updated_at = $now
            WHERE active = 1;
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RemoveNonKitaronOrdersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyDictionary<string, string> synchronizedCaseIds,
        IReadOnlyList<KitaronSyncOrder> currentOrders,
        DateTimeOffset now,
        MutableCounts counts,
        CancellationToken cancellationToken)
    {
        await using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS current_kitaron_cases (id TEXT PRIMARY KEY);
                DELETE FROM current_kitaron_cases;
                CREATE TEMP TABLE IF NOT EXISTS current_kitaron_orders (
                    source_key TEXT NOT NULL,
                    link_key TEXT NOT NULL,
                    case_id TEXT NOT NULL,
                    order_reference TEXT NOT NULL,
                    UNIQUE(case_id, order_reference COLLATE NOCASE));
                DELETE FROM current_kitaron_orders;
                CREATE TEMP TABLE IF NOT EXISTS protected_historical_orders (
                    id TEXT PRIMARY KEY);
                DELETE FROM protected_historical_orders;
                """;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var caseId in synchronizedCaseIds.Values.Distinct(StringComparer.Ordinal))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO current_kitaron_cases (id) VALUES ($id);";
            Add(insert, "$id", caseId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var order in currentOrders)
        {
            if (!synchronizedCaseIds.TryGetValue(order.CaseSourceKey, out var caseId))
                throw new KitaronSyncDataException(
                    $"Kitaron Order {order.OrderNumber} references an unresolved Case.");
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO current_kitaron_orders(source_key,link_key,case_id,order_reference)
                VALUES($sourceKey,$linkKey,$caseId,$reference);
                """;
            Add(insert, "$sourceKey", order.SourceKey);
            Add(insert, "$linkKey", OrderSourceKey(order));
            Add(insert, "$caseId", caseId);
            Add(insert, "$reference", order.OrderNumber);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var restoreCurrentDemand = connection.CreateCommand())
        {
            restoreCurrentDemand.Transaction = transaction;
            restoreCurrentDemand.CommandText = """
                UPDATE orders
                SET kitaron_history_only=0, version=version+1, updated_at=$now
                WHERE kitaron_history_only=1
                  AND EXISTS (
                      SELECT 1
                      FROM current_kitaron_orders expected
                      WHERE expected.case_id=orders.case_id
                        AND expected.order_reference=orders.order_reference COLLATE NOCASE);
                """;
            Add(restoreCurrentDemand, "$now", now.ToString("O"));
            await restoreCurrentDemand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var retainHistorical = connection.CreateCommand())
        {
            retainHistorical.Transaction = transaction;
            retainHistorical.CommandText = """
                INSERT OR IGNORE INTO protected_historical_orders(id)
                SELECT DISTINCT o.id
                FROM orders o
                JOIN current_kitaron_cases current_case ON current_case.id=o.case_id
                JOIN batch_allocations allocation
                  ON allocation.order_id=o.id
                  OR (allocation.allocation_type='derived_order'
                      AND instr(allocation.derived_order_key, 'derived:' || o.id || ':')=1)
                JOIN batch_operations operation
                  ON operation.production_batch_id=allocation.production_batch_id
                WHERE NOT EXISTS (
                    SELECT 1 FROM current_kitaron_orders expected
                    WHERE expected.case_id=o.case_id
                      AND expected.order_reference=o.order_reference COLLATE NOCASE)
                  AND (
                    EXISTS (
                        SELECT 1 FROM production_runs legacy_run
                        WHERE legacy_run.legacy_batch_operation_id=operation.id
                          AND legacy_run.structure_locked_at IS NOT NULL)
                    OR EXISTS (
                        SELECT 1
                        FROM production_run_outputs output
                        JOIN production_run_programs program
                          ON program.id=output.production_run_program_id
                        JOIN production_runs output_run
                          ON output_run.id=program.production_run_id
                        WHERE output.batch_operation_id=operation.id
                          AND output_run.structure_locked_at IS NOT NULL));
                """;
            counts.HistoricalOrdersRetained += await retainHistorical.ExecuteNonQueryAsync(cancellationToken);
            counts.Warnings += counts.HistoricalOrdersRetained;
        }

        await using (var markHistorical = connection.CreateCommand())
        {
            markHistorical.Transaction = transaction;
            markHistorical.CommandText = """
                UPDATE orders
                SET kitaron_history_only=1, version=version+1, updated_at=$now
                WHERE kitaron_history_only=0
                  AND id IN (SELECT id FROM protected_historical_orders);
                """;
            Add(markHistorical, "$now", now.ToString("O"));
            await markHistorical.ExecuteNonQueryAsync(cancellationToken);
        }

        // Repair targets retained by an old source link before comparing the exact
        // authoritative reference set. This is deliberately independent of hashes.
        await using (var repair = connection.CreateCommand())
        {
            repair.Transaction = transaction;
            repair.CommandText = """
                UPDATE orders AS target
                SET order_reference=(
                        SELECT expected.order_reference
                        FROM kitaron_sync_links link
                        JOIN current_kitaron_orders expected ON expected.link_key=link.source_key
                        WHERE link.source_entity='order' AND link.target_id=target.id
                          AND expected.case_id=target.case_id
                        ORDER BY expected.source_key LIMIT 1),
                    version=version+1,
                    updated_at=$now
                WHERE EXISTS (
                        SELECT 1
                        FROM kitaron_sync_links link
                        JOIN current_kitaron_orders expected ON expected.link_key=link.source_key
                        WHERE link.source_entity='order' AND link.target_id=target.id
                          AND target.case_id=expected.case_id
                          AND target.order_reference<>expected.order_reference COLLATE NOCASE)
                  AND NOT EXISTS (
                        SELECT 1
                        FROM kitaron_sync_links link
                        JOIN current_kitaron_orders expected ON expected.link_key=link.source_key
                        JOIN orders duplicate
                          ON duplicate.case_id=expected.case_id
                         AND duplicate.order_reference=expected.order_reference COLLATE NOCASE
                        WHERE link.source_entity='order' AND link.target_id=target.id
                          AND duplicate.id<>target.id);
                """;
            Add(repair, "$now", now.ToString("O"));
            counts.OrdersUpdated += await repair.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var missing = connection.CreateCommand())
        {
            missing.Transaction = transaction;
            missing.CommandText = """
                SELECT expected.order_reference
                FROM current_kitaron_orders expected
                WHERE NOT EXISTS (
                    SELECT 1 FROM orders actual
                    WHERE actual.case_id=expected.case_id
                      AND actual.order_reference=expected.order_reference COLLATE NOCASE)
                ORDER BY expected.order_reference LIMIT 1;
                """;
            var reference = await missing.ExecuteScalarAsync(cancellationToken) as string;
            if (reference is not null)
                throw new KitaronSyncDataException(
                    $"Authoritative Kitaron Order {reference} was not materialized; synchronization was rolled back.");
        }

        var batchIds = new List<string>();
        await using (var readBatches = connection.CreateCommand())
        {
            readBatches.Transaction = transaction;
            readBatches.CommandText = """
                SELECT DISTINCT allocation.production_batch_id
                FROM batch_allocations allocation
                JOIN orders o ON allocation.order_id=o.id
                JOIN current_kitaron_cases current_case ON current_case.id=o.case_id
                WHERE NOT EXISTS (
                    SELECT 1 FROM current_kitaron_orders expected
                    WHERE expected.case_id=o.case_id
                      AND expected.order_reference=o.order_reference COLLATE NOCASE)
                  AND NOT EXISTS (
                    SELECT 1
                    FROM batch_operations protected_operation
                    WHERE protected_operation.production_batch_id=allocation.production_batch_id
                      AND (
                        EXISTS (
                            SELECT 1 FROM production_runs protected_run
                            WHERE protected_run.legacy_batch_operation_id=protected_operation.id
                              AND protected_run.structure_locked_at IS NOT NULL)
                        OR EXISTS (
                            SELECT 1
                            FROM production_run_outputs protected_output
                            JOIN production_run_programs protected_program
                              ON protected_program.id=protected_output.production_run_program_id
                            JOIN production_runs protected_output_run
                              ON protected_output_run.id=protected_program.production_run_id
                            WHERE protected_output.batch_operation_id=protected_operation.id
                              AND protected_output_run.structure_locked_at IS NOT NULL)))
                UNION
                SELECT DISTINCT allocation.production_batch_id
                FROM batch_allocations allocation
                JOIN orders o
                  ON allocation.allocation_type='derived_order'
                 AND instr(allocation.derived_order_key, 'derived:' || o.id || ':')=1
                JOIN current_kitaron_cases current_case ON current_case.id=o.case_id
                WHERE NOT EXISTS (
                    SELECT 1 FROM current_kitaron_orders expected
                    WHERE expected.case_id=o.case_id
                      AND expected.order_reference=o.order_reference COLLATE NOCASE)
                  AND NOT EXISTS (
                    SELECT 1
                    FROM batch_operations protected_operation
                    WHERE protected_operation.production_batch_id=allocation.production_batch_id
                      AND (
                        EXISTS (
                            SELECT 1 FROM production_runs protected_run
                            WHERE protected_run.legacy_batch_operation_id=protected_operation.id
                              AND protected_run.structure_locked_at IS NOT NULL)
                        OR EXISTS (
                            SELECT 1
                            FROM production_run_outputs protected_output
                            JOIN production_run_programs protected_program
                              ON protected_program.id=protected_output.production_run_program_id
                            JOIN production_runs protected_output_run
                              ON protected_output_run.id=protected_program.production_run_id
                            WHERE protected_output.batch_operation_id=protected_operation.id
                              AND protected_output_run.structure_locked_at IS NOT NULL)))
                ORDER BY 1;
                """;
            await using var reader = await readBatches.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) batchIds.Add(reader.GetString(0));
        }
        foreach (var batchId in batchIds)
        {
            if (await SqlitePlanningDeletionRepository.DeleteBatchGraphAsync(
                    connection, transaction, batchId, now, cancellationToken))
                counts.BatchesDeleted++;
        }

        await using (var blocked = connection.CreateCommand())
        {
            blocked.Transaction = transaction;
            blocked.CommandText = """
                SELECT o.order_reference
                FROM orders o
                JOIN current_kitaron_cases current_case ON current_case.id=o.case_id
                WHERE NOT EXISTS (
                    SELECT 1 FROM current_kitaron_orders expected
                    WHERE expected.case_id=o.case_id
                      AND expected.order_reference=o.order_reference COLLATE NOCASE)
                  AND (
                    EXISTS (SELECT 1 FROM batch_allocations allocation WHERE allocation.order_id=o.id)
                    OR EXISTS (
                        SELECT 1 FROM batch_allocations allocation
                        WHERE allocation.allocation_type='derived_order'
                          AND instr(allocation.derived_order_key, 'derived:' || o.id || ':')=1))
                  AND o.id NOT IN (SELECT id FROM protected_historical_orders)
                ORDER BY o.order_reference
                LIMIT 1;
                """;
            var reference = await blocked.ExecuteScalarAsync(cancellationToken) as string;
            if (reference is not null)
                throw new KitaronSyncDataException(
                    $"Non-Kitaron Order {reference} still has protected production references after dependent Batch cleanup.");
        }

        await using (var unlink = connection.CreateCommand())
        {
            unlink.Transaction = transaction;
            unlink.CommandText = """
                DELETE FROM kitaron_sync_links
                WHERE source_entity='order' AND target_id IN (
                    SELECT o.id
                    FROM orders o
                    JOIN current_kitaron_cases current_case ON current_case.id=o.case_id
                    WHERE NOT EXISTS (
                        SELECT 1 FROM current_kitaron_orders expected
                        WHERE expected.case_id=o.case_id
                          AND expected.order_reference=o.order_reference COLLATE NOCASE)
                      AND o.id NOT IN (SELECT id FROM protected_historical_orders));
                """;
            await unlink.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var remove = connection.CreateCommand())
        {
            remove.Transaction = transaction;
            remove.CommandText = """
                DELETE FROM orders
                WHERE case_id IN (SELECT id FROM current_kitaron_cases)
                  AND NOT EXISTS (
                    SELECT 1 FROM current_kitaron_orders expected
                    WHERE expected.case_id=orders.case_id
                      AND expected.order_reference=orders.order_reference COLLATE NOCASE)
                  AND id NOT IN (SELECT id FROM protected_historical_orders);
                """;
            counts.OrdersDeleted += await remove.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpsertMaterialOrderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        KitaronSyncMaterialOrder item,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO kitaron_material_orders (
                source_key, purchase_order_number, line_number, material_number,
                description, supplier, ordered_quantity, received_quantity, unit,
                requested_delivery_date, approved_delivery_date, approved_quantity,
                approval_note, status, closed, active, source_hash,
                first_imported_at, last_imported_at, updated_at)
            VALUES (
                $sourceKey, $purchaseOrder, $line, $material,
                $description, $supplier, $ordered, $received, $unit,
                $requestedDate, $approvedDate, $approvedQuantity,
                $approvalNote, $status, $closed, 1, $sourceHash,
                $now, $now, $now)
            ON CONFLICT(source_key) DO UPDATE SET
                purchase_order_number = excluded.purchase_order_number,
                line_number = excluded.line_number,
                material_number = excluded.material_number,
                description = excluded.description,
                supplier = excluded.supplier,
                ordered_quantity = excluded.ordered_quantity,
                received_quantity = excluded.received_quantity,
                unit = excluded.unit,
                requested_delivery_date = excluded.requested_delivery_date,
                approved_delivery_date = excluded.approved_delivery_date,
                approved_quantity = excluded.approved_quantity,
                approval_note = excluded.approval_note,
                status = excluded.status,
                closed = excluded.closed,
                active = 1,
                source_hash = excluded.source_hash,
                last_imported_at = excluded.last_imported_at,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$sourceKey", item.SourceKey);
        command.Parameters.AddWithValue("$purchaseOrder", item.PurchaseOrderNumber);
        command.Parameters.AddWithValue("$line", item.LineNumber);
        command.Parameters.AddWithValue("$material", item.MaterialNumber);
        command.Parameters.AddWithValue("$description", (object?)item.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$supplier", (object?)item.Supplier ?? DBNull.Value);
        command.Parameters.AddWithValue("$ordered", item.OrderedQuantity);
        command.Parameters.AddWithValue("$received", (object?)item.ReceivedQuantity ?? DBNull.Value);
        command.Parameters.AddWithValue("$unit", (object?)item.Unit ?? DBNull.Value);
        command.Parameters.AddWithValue("$requestedDate", item.RequestedDeliveryDate?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$approvedDate", item.ApprovedDeliveryDate?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$approvedQuantity", (object?)item.ApprovedQuantity ?? DBNull.Value);
        command.Parameters.AddWithValue("$approvalNote", (object?)item.ApprovalNote ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (object?)item.Status ?? DBNull.Value);
        command.Parameters.AddWithValue("$closed", item.Closed ? 1 : 0);
        command.Parameters.AddWithValue("$sourceHash", item.SourceHash);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SynchronizeNotStartedBatchOperationTimesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE batch_operations
            SET setup_seconds = (
                    SELECT case_operations.setup_seconds
                    FROM case_operations
                    WHERE case_operations.id = batch_operations.source_case_operation_id),
                cycle_seconds = (
                    SELECT case_operations.cycle_seconds
                    FROM case_operations
                    WHERE case_operations.id = batch_operations.source_case_operation_id),
                version = version + 1,
                updated_at = $now
            WHERE status = 'not_started'
              AND EXISTS (
                    SELECT 1
                    FROM case_operations
                    WHERE case_operations.id = batch_operations.source_case_operation_id)
              AND (
                    setup_seconds IS NOT (
                        SELECT case_operations.setup_seconds
                        FROM case_operations
                        WHERE case_operations.id = batch_operations.source_case_operation_id)
                    OR cycle_seconds IS NOT (
                        SELECT case_operations.cycle_seconds
                        FROM case_operations
                        WHERE case_operations.id = batch_operations.source_case_operation_id));
            """;
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<string> ResolveCaseAsync(
        SqliteConnection connection, SqliteTransaction transaction, KitaronSyncCase item,
        DateTimeOffset now, MutableCounts counts, CancellationToken cancellationToken)
    {
        var link = await ReadValidLinkAsync(
            connection, transaction, "case", item.SourceKey, "cases", counts, cancellationToken);
        if (link is null)
        {
            var matches = await FindIdsAsync(connection, transaction,
                "SELECT id FROM cases WHERE part_number = $key COLLATE NOCASE ORDER BY id;",
                item.PartNumber, cancellationToken);
            if (matches.Count > 1)
                throw new KitaronSyncDataException($"Part {item.PartNumber} matches multiple Planner Cases.");
            var id = matches.Count == 1 ? matches[0] : StableId("kit-case", item.SourceKey);
            var owns = matches.Count == 0;
            if (owns)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO cases (id, part_number, name, revision, customer, working_folder_path,
                        version, created_at, updated_at)
                    VALUES ($id, $part, $name, $revision, $customer, $folder, 1, $now, $now);
                    """;
                Add(insert, "$id", id); Add(insert, "$part", item.PartNumber); Add(insert, "$name", item.Name);
                Add(insert, "$revision", item.Revision); Add(insert, "$customer", item.Customer);
                Add(insert, "$folder", item.WorkingFolderPath); Add(insert, "$now", now.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
                counts.CasesCreated++;
            }
            else counts.CasesMatched++;
            if (!owns)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE cases SET part_number=$part, name=$name, revision=$revision, customer=$customer,
                        working_folder_path=$folder, version=version+1, updated_at=$now WHERE id=$id;
                    """;
                Add(update, "$part", item.PartNumber); Add(update, "$name", item.Name);
                Add(update, "$revision", item.Revision); Add(update, "$customer", item.Customer);
                Add(update, "$folder", item.WorkingFolderPath); Add(update, "$now", now.ToString("O"));
                Add(update, "$id", id);
                await update.ExecuteNonQueryAsync(cancellationToken);
                counts.CasesMatched--;
                counts.CasesUpdated++;
            }
            await UpsertLinkAsync(connection, transaction, "case", item.SourceKey, id, owns, item.SourceHash, now, cancellationToken);
            return id;
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE cases SET part_number=$part, name=$name, revision=$revision, customer=$customer,
                    working_folder_path=$folder, version=version+1, updated_at=$now
                WHERE id=$id AND (
                    part_number IS NOT $part OR name IS NOT $name OR revision IS NOT $revision
                    OR customer IS NOT $customer OR working_folder_path IS NOT $folder);
                """;
            Add(update, "$part", item.PartNumber); Add(update, "$name", item.Name);
            Add(update, "$revision", item.Revision); Add(update, "$customer", item.Customer);
            Add(update, "$folder", item.WorkingFolderPath); Add(update, "$now", now.ToString("O"));
            Add(update, "$id", link.Value.TargetId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 1) counts.CasesUpdated++;
            else counts.CasesMatched++;
        }
        await UpsertLinkAsync(connection, transaction, "case", item.SourceKey, link.Value.TargetId,
            link.Value.OwnsTarget, item.SourceHash, now, cancellationToken);
        return link.Value.TargetId;
    }

    private static async Task ResolveOrderAsync(
        SqliteConnection connection, SqliteTransaction transaction, KitaronSyncOrder item, string caseId,
        DateTimeOffset now, MutableCounts counts, CancellationToken cancellationToken)
    {
        var sourceKey = OrderSourceKey(item);
        var link = await ReadValidLinkAsync(
            connection, transaction, "order", sourceKey, "orders", counts, cancellationToken);
        if (link is not null && !await OrderBelongsToCaseAsync(
                connection, transaction, link.Value.TargetId, caseId, cancellationToken))
        {
            await DeleteLinkAsync(connection, transaction, "order", sourceKey, cancellationToken);
            link = null;
        }
        if (link is null)
        {
            var oldLink = await ReadValidLinkAsync(
                connection, transaction, "order", item.SourceKey, "orders", counts, cancellationToken);
            if (oldLink is not null && await OrderBelongsToCaseAsync(
                    connection, transaction, oldLink.Value.TargetId, caseId, cancellationToken))
            {
                await DeleteLinkAsync(connection, transaction, "order", item.SourceKey, cancellationToken);
                await UpsertLinkAsync(connection, transaction, "order", sourceKey,
                    oldLink.Value.TargetId, oldLink.Value.OwnsTarget, oldLink.Value.SourceHash,
                    now, cancellationToken);
                link = oldLink;
            }
        }
        if (link is null)
        {
            var legacyKey = $"{item.CaseSourceKey}\u001f{item.CanonicalOrderNumber}";
            var legacyLink = await ReadValidLinkAsync(
                connection, transaction, "order", legacyKey, "orders", counts, cancellationToken);
            if (legacyLink is not null)
            {
                await DeleteLinkAsync(connection, transaction, "order", legacyKey, cancellationToken);
                await UpsertLinkAsync(connection, transaction, "order", sourceKey,
                    legacyLink.Value.TargetId, legacyLink.Value.OwnsTarget, legacyLink.Value.SourceHash,
                    now, cancellationToken);
                link = legacyLink;
            }
        }
        var normalizedLinkedReference = false;
        // A current Kitaron link makes the source row reference authoritative. Repair the
        // reference independently of the other stored fields and the saved source hash;
        // older importers could leave a plain header OrderNumber on an otherwise linked row.
        if (link is not null)
        {
            await using var normalize = connection.CreateCommand();
            normalize.Transaction = transaction;
            normalize.CommandText = """
                UPDATE orders
                SET order_reference=$number, version=version+1, updated_at=$now
                WHERE id=$id AND case_id=$caseId
                  AND order_reference<>$number COLLATE NOCASE;
                """;
            Add(normalize, "$number", item.OrderNumber);
            Add(normalize, "$now", now.ToString("O"));
            Add(normalize, "$id", link.Value.TargetId);
            Add(normalize, "$caseId", caseId);
            normalizedLinkedReference = await normalize.ExecuteNonQueryAsync(cancellationToken) == 1;
            if (normalizedLinkedReference) counts.OrdersUpdated++;
        }
        if (link is null)
        {
            var matches = await FindIdsAsync(connection, transaction,
                """
                SELECT id FROM orders
                WHERE case_id=$caseId AND order_reference=$key COLLATE NOCASE
                  AND NOT EXISTS (
                      SELECT 1 FROM kitaron_sync_links
                      WHERE source_entity='order' AND target_id=orders.id)
                ORDER BY id;
                """,
                item.OrderNumber, cancellationToken, caseId);
            if (matches.Count > 1)
                throw new KitaronSyncDataException($"Order {item.OrderNumber} matches multiple Planner Orders.");

            if (matches.Count == 0)
            {
                var legacyMatches = await FindIdsAsync(connection, transaction,
                    """
                    SELECT id FROM orders
                    WHERE case_id=$caseId AND order_reference=$key COLLATE NOCASE
                      AND quantity=$quantity AND work_finish_date=$date
                      AND status=$status COLLATE NOCASE
                      AND NOT EXISTS (
                          SELECT 1 FROM kitaron_sync_links
                          WHERE source_entity='order' AND target_id=orders.id)
                    ORDER BY id;
                    """,
                    item.CanonicalOrderNumber, cancellationToken, caseId,
                    ("$quantity", item.Quantity),
                    ("$date", item.WorkFinishDate.ToString("yyyy-MM-dd")),
                    ("$status", item.Status));
                if (legacyMatches.Count > 1)
                    throw new KitaronSyncDataException(
                        $"Order {item.CanonicalOrderNumber} has multiple exact legacy Planner matches.");
                if (legacyMatches.Count == 1)
                {
                    await using var adopt = connection.CreateCommand();
                    adopt.Transaction = transaction;
                    adopt.CommandText = """
                        UPDATE orders
                        SET order_reference=$number, quantity=$quantity, work_finish_date=$date,
                            status=$status, kitaron_status=$kitaronStatus, price=$price,
                            version=version+1, updated_at=$now
                        WHERE id=$id;
                        """;
                    Add(adopt, "$number", item.OrderNumber);
                    Add(adopt, "$quantity", item.Quantity);
                    Add(adopt, "$date", item.WorkFinishDate.ToString("yyyy-MM-dd"));
                    Add(adopt, "$status", item.Status);
                    Add(adopt, "$kitaronStatus", KitaronStatus(item.Status));
                    Add(adopt, "$price", item.Price);
                    Add(adopt, "$now", now.ToString("O"));
                    Add(adopt, "$id", legacyMatches[0]);
                    await adopt.ExecuteNonQueryAsync(cancellationToken);
                    await UpsertLinkAsync(connection, transaction, "order", sourceKey,
                        legacyMatches[0], true, item.SourceHash, now, cancellationToken);
                    counts.OrdersUpdated++;
                    return;
                }
            }

            var id = matches.Count == 1 ? matches[0] : StableId("kit-order", sourceKey);
            var owns = matches.Count == 0;
            if (owns)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO orders (id, case_id, order_reference, quantity, work_finish_date, status, kitaron_status, price,
                        version, created_at, updated_at)
                    VALUES ($id, $caseId, $number, $quantity, $date, $status, $kitaronStatus, $price, 1, $now, $now);
                    """;
                Add(insert, "$id", id); Add(insert, "$caseId", caseId); Add(insert, "$number", item.OrderNumber);
                Add(insert, "$quantity", item.Quantity); Add(insert, "$date", item.WorkFinishDate.ToString("yyyy-MM-dd"));
                Add(insert, "$status", item.Status);
                Add(insert, "$kitaronStatus", KitaronStatus(item.Status));
                Add(insert, "$price", item.Price);
                Add(insert, "$now", now.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
                counts.OrdersCreated++;
            }
            else
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE orders SET order_reference=$number, quantity=$quantity,
                        work_finish_date=$date, status=$status, kitaron_status=$kitaronStatus, price=$price,
                        version=version+1, updated_at=$now WHERE id=$id;
                    """;
                Add(update, "$number", item.OrderNumber); Add(update, "$quantity", item.Quantity);
                Add(update, "$date", item.WorkFinishDate.ToString("yyyy-MM-dd"));
                Add(update, "$status", item.Status); Add(update, "$price", item.Price);
                Add(update, "$kitaronStatus", KitaronStatus(item.Status));
                Add(update, "$now", now.ToString("O")); Add(update, "$id", id);
                await update.ExecuteNonQueryAsync(cancellationToken);
                counts.OrdersUpdated++;
            }
            await UpsertLinkAsync(connection, transaction, "order", sourceKey, id, owns, item.SourceHash, now, cancellationToken);
            return;
        }
        var orderNeedsUpdate = await OrderNeedsUpdateAsync(
            connection, transaction, link.Value.TargetId, item, cancellationToken);
        if (orderNeedsUpdate)
        {
            if (await OrderRequiresBatchRemovalAsync(
                    connection, transaction, link.Value.TargetId, item, cancellationToken))
            {
                var affectedBatchIds = await ReadOrderBatchIdsAsync(
                    connection, transaction, link.Value.TargetId, cancellationToken);
                foreach (var batchId in affectedBatchIds)
                {
                    await SqlitePlanningDeletionRepository.DeleteBatchGraphAsync(
                        connection, transaction, batchId, now, cancellationToken);
                }
            }
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE orders SET order_reference=$number, quantity=$quantity, work_finish_date=$date,
                    status=CASE WHEN $status='active' AND status IN ('in_production','complete')
                                THEN status ELSE $status END,
                    kitaron_status=$kitaronStatus, price=$price,
                    version=version+1, updated_at=$now WHERE id=$id;
                """;
            Add(update, "$number", item.OrderNumber); Add(update, "$quantity", item.Quantity);
            Add(update, "$date", item.WorkFinishDate.ToString("yyyy-MM-dd"));
            Add(update, "$status", item.Status); Add(update, "$kitaronStatus", KitaronStatus(item.Status));
            Add(update, "$price", item.Price); Add(update, "$now", now.ToString("O"));
            Add(update, "$id", link.Value.TargetId);
            await update.ExecuteNonQueryAsync(cancellationToken);
            if (!normalizedLinkedReference) counts.OrdersUpdated++;
        }
        else if (!normalizedLinkedReference) counts.OrdersMatched++;
        await UpsertLinkAsync(connection, transaction, "order", sourceKey, link.Value.TargetId,
            link.Value.OwnsTarget, item.SourceHash, now, cancellationToken);
    }

    private static string OrderSourceKey(KitaronSyncOrder item) =>
        $"{item.CaseSourceKey}\u001f{item.SourceKey}";

    private static async Task<bool> OrderBelongsToCaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string orderId,
        string caseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM orders WHERE id=$id AND case_id=$caseId);";
        Add(command, "$id", orderId);
        Add(command, "$caseId", caseId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static string KitaronStatus(string storageStatus) => storageStatus switch
    {
        "complete" => "inactive",
        "cancelled" => "cancelled",
        _ => "active"
    };

    private static async Task<IReadOnlyList<string>> ReadOrderBatchIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string orderId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT production_batch_id
            FROM batch_allocations
            WHERE order_id=$id
               OR (allocation_type='derived_order'
                   AND instr(derived_order_key, 'derived:' || $id || ':')=1)
            ORDER BY production_batch_id;
            """;
        command.Parameters.AddWithValue("$id", orderId);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<bool> OrderNeedsUpdateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string orderId,
        KitaronSyncOrder item,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM orders
                WHERE id=$id AND (
                    order_reference IS NOT $number OR quantity IS NOT $quantity
                    OR work_finish_date IS NOT $date
                    OR ($status<>'active' AND status IS NOT $status)
                    OR kitaron_status IS NOT $kitaronStatus OR price IS NOT $price));
            """;
        Add(command, "$id", orderId); Add(command, "$number", item.OrderNumber);
        Add(command, "$quantity", item.Quantity); Add(command, "$date", item.WorkFinishDate.ToString("yyyy-MM-dd"));
        Add(command, "$status", item.Status); Add(command, "$kitaronStatus", KitaronStatus(item.Status));
        Add(command, "$price", item.Price);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> OrderRequiresBatchRemovalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string orderId,
        KitaronSyncOrder item,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM orders
                WHERE id=$id AND (
                    quantity IS NOT $quantity
                    OR ($status<>'active' AND status IS NOT $status)));
            """;
        Add(command, "$id", orderId); Add(command, "$quantity", item.Quantity);
        Add(command, "$status", item.Status);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task ResolveOperationAsync(
        SqliteConnection connection, SqliteTransaction transaction, KitaronSyncOperation item, string caseId,
        DateTimeOffset now, MutableCounts counts, CancellationToken cancellationToken)
    {
        var link = await ReadValidLinkAsync(
            connection, transaction, "case_operation", item.SourceKey, "case_operations", counts, cancellationToken);
        if (link is null)
        {
            var matches = await FindIdsAsync(connection, transaction,
                "SELECT id FROM case_operations WHERE case_id=$caseId AND operation_number=$key ORDER BY id;",
                item.OperationNumber, cancellationToken, caseId);
            if (matches.Count > 1)
                throw new KitaronSyncDataException($"Operation {item.OperationNumber} matches multiple Case Operations.");
            var id = matches.Count == 1 ? matches[0] : StableId("kit-op", item.SourceKey);
            var owns = matches.Count == 0;
            if (owns)
            {
                var route = await NextRoutePositionAsync(connection, transaction, caseId, item.RoutePosition, cancellationToken);
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO case_operations (id, case_id, operation_number, route_position, name,
                        required_machine_type, setup_seconds, cycle_seconds, dependency_type,
                        version, created_at, updated_at)
                    VALUES ($id, $caseId, $number, $route, $name, $type, $setup, $cycle,
                        'independent', 1, $now, $now);
                    """;
                Add(insert, "$id", id); Add(insert, "$caseId", caseId); Add(insert, "$number", item.OperationNumber);
                Add(insert, "$route", route); Add(insert, "$name", item.Name); Add(insert, "$type", item.RequiredMachineType);
                Add(insert, "$setup", item.SetupSeconds); Add(insert, "$cycle", item.CycleSeconds);
                Add(insert, "$now", now.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
                counts.OperationsCreated++;
            }
            else counts.OperationsMatched++;
            await UpsertLinkAsync(connection, transaction, "case_operation", item.SourceKey, id, owns, item.SourceHash, now, cancellationToken);
            return;
        }
        if (link.Value.OwnsTarget && !StringComparer.Ordinal.Equals(link.Value.SourceHash, item.SourceHash))
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE case_operations SET name=$name, required_machine_type=$type,
                    setup_seconds=$setup, cycle_seconds=$cycle, version=version+1, updated_at=$now
                WHERE id=$id;
                """;
            Add(update, "$name", item.Name); Add(update, "$type", item.RequiredMachineType);
            Add(update, "$setup", item.SetupSeconds); Add(update, "$cycle", item.CycleSeconds);
            Add(update, "$now", now.ToString("O")); Add(update, "$id", link.Value.TargetId);
            await update.ExecuteNonQueryAsync(cancellationToken);
            counts.OperationsUpdated++;
        }
        else counts.OperationsMatched++;
        await UpsertLinkAsync(connection, transaction, "case_operation", item.SourceKey, link.Value.TargetId,
            link.Value.OwnsTarget, item.SourceHash, now, cancellationToken);
    }

    private static async Task ResolveComponentAsync(
        SqliteConnection connection, SqliteTransaction transaction, KitaronSyncComponent item,
        string parentCaseId, string childCaseId, DateTimeOffset now, MutableCounts counts,
        CancellationToken cancellationToken)
    {
        if (parentCaseId == childCaseId)
            throw new KitaronSyncDataException($"Kitaron component {item.SourceKey} contains itself.");
        if (await ScalarIntAsync(connection, transaction,
                "SELECT COUNT(*) FROM case_operations WHERE case_id=$caseId;",
                "$caseId", parentCaseId, cancellationToken) > 0)
        {
            counts.Warnings++;
            return;
        }

        var link = await ReadValidLinkAsync(
            connection, transaction, "case_component", item.SourceKey, "case_components", counts, cancellationToken);
        if (link is null)
        {
            var matches = await FindComponentIdsAsync(
                connection, transaction, parentCaseId, childCaseId, cancellationToken);
            if (matches.Count > 1)
                throw new KitaronSyncDataException($"Component {item.SourceKey} matches multiple Planner relationships.");
            var id = matches.Count == 1 ? matches[0] : StableId("kit-component", item.SourceKey);
            var owns = matches.Count == 0;
            if (!owns)
            {
                var linkedSourceKey = await ReadSourceKeyForTargetAsync(
                    connection, transaction, "case_component", id, cancellationToken);
                if (linkedSourceKey is not null
                    && !StringComparer.Ordinal.Equals(linkedSourceKey, item.SourceKey))
                {
                    counts.ComponentsMatched++;
                    counts.Warnings++;
                    return;
                }
            }
            if (owns)
            {
                await EnsureNoComponentCycleAsync(
                    connection, transaction, parentCaseId, childCaseId, null, cancellationToken);
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO case_components (
                        id, parent_case_id, child_case_id, quantity_per_parent, sort_order,
                        notes, is_active, version, created_at, updated_at)
                    VALUES ($id, $parent, $child, $quantity, $sort, NULL, 1, 1, $now, $now);
                    """;
                Add(insert, "$id", id); Add(insert, "$parent", parentCaseId); Add(insert, "$child", childCaseId);
                Add(insert, "$quantity", item.QuantityPerParent); Add(insert, "$sort", item.SortOrder);
                Add(insert, "$now", now.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
                counts.ComponentsCreated++;
            }
            else counts.ComponentsMatched++;
            await UpsertLinkAsync(connection, transaction, "case_component", item.SourceKey,
                id, owns, item.SourceHash, now, cancellationToken);
            return;
        }

        if (link.Value.OwnsTarget && !StringComparer.Ordinal.Equals(link.Value.SourceHash, item.SourceHash))
        {
            await EnsureNoComponentCycleAsync(
                connection, transaction, parentCaseId, childCaseId, link.Value.TargetId, cancellationToken);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE case_components
                SET parent_case_id=$parent, child_case_id=$child, quantity_per_parent=$quantity,
                    sort_order=$sort, is_active=1, version=version+1, updated_at=$now
                WHERE id=$id;
                """;
            Add(update, "$parent", parentCaseId); Add(update, "$child", childCaseId);
            Add(update, "$quantity", item.QuantityPerParent); Add(update, "$sort", item.SortOrder);
            Add(update, "$now", now.ToString("O")); Add(update, "$id", link.Value.TargetId);
            await update.ExecuteNonQueryAsync(cancellationToken);
            counts.ComponentsUpdated++;
        }
        else counts.ComponentsMatched++;
        await UpsertLinkAsync(connection, transaction, "case_component", item.SourceKey, link.Value.TargetId,
            link.Value.OwnsTarget, item.SourceHash, now, cancellationToken);
    }

    private static async Task DeactivateMissingComponentsAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlySet<string> seen, DateTimeOffset now, MutableCounts counts,
        CancellationToken cancellationToken)
    {
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT source_key, target_id FROM kitaron_sync_links
            WHERE source_entity='case_component' AND owns_target=1;
            """;
        var staleIds = new List<string>();
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!seen.Contains(reader.GetString(0))) staleIds.Add(reader.GetString(1));
            }
        }
        foreach (var id in staleIds)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE case_components SET is_active=0, version=version+1, updated_at=$now
                WHERE id=$id AND is_active=1;
                """;
            Add(update, "$now", now.ToString("O")); Add(update, "$id", id);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 1) counts.ComponentsUpdated++;
        }
    }

    private static async Task EnsureNoComponentCycleAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        string parentCaseId, string childCaseId, string? excludedId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH RECURSIVE descendants(case_id) AS (
                SELECT child_case_id FROM case_components
                WHERE parent_case_id=$child AND is_active=1 AND ($excluded IS NULL OR id<>$excluded)
                UNION
                SELECT component.child_case_id
                FROM case_components component
                JOIN descendants ON component.parent_case_id=descendants.case_id
                WHERE component.is_active=1 AND ($excluded IS NULL OR component.id<>$excluded)
            )
            SELECT EXISTS(SELECT 1 FROM descendants WHERE case_id=$parent);
            """;
        Add(command, "$child", childCaseId); Add(command, "$parent", parentCaseId);
        Add(command, "$excluded", excludedId);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1)
            throw new KitaronSyncDataException("The Kitaron component structure contains a circular relationship.");
    }

    private static async Task<IReadOnlyList<string>> FindComponentIdsAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        string parentCaseId, string childCaseId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id FROM case_components
            WHERE parent_case_id=$parent AND child_case_id=$child
            ORDER BY id;
            """;
        Add(command, "$parent", parentCaseId); Add(command, "$child", childCaseId);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<int> NextRoutePositionAsync(
        SqliteConnection connection, SqliteTransaction transaction, string caseId, int preferred,
        CancellationToken cancellationToken)
    {
        var used = await ScalarIntAsync(connection, transaction,
            "SELECT COUNT(*) FROM case_operations WHERE case_id=$caseId AND route_position=$route;",
            "$caseId", caseId, cancellationToken, ("$route", preferred));
        if (used == 0) return preferred;
        return await ScalarIntAsync(connection, transaction,
            "SELECT COALESCE(MAX(route_position),-1)+1 FROM case_operations WHERE case_id=$caseId;",
            "$caseId", caseId, cancellationToken);
    }

    private static async Task<(string TargetId, bool OwnsTarget, string SourceHash)?> ReadLinkAsync(
        SqliteConnection connection, SqliteTransaction transaction, string entity, string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT target_id, owns_target, source_hash FROM kitaron_sync_links WHERE source_entity=$entity AND source_key=$key;";
        Add(command, "$entity", entity); Add(command, "$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetString(0), reader.GetInt32(1) != 0, reader.GetString(2)) : null;
    }

    private static async Task<(string TargetId, bool OwnsTarget, string SourceHash)?> ReadValidLinkAsync(
        SqliteConnection connection, SqliteTransaction transaction, string entity, string key,
        string targetTable, MutableCounts counts, CancellationToken cancellationToken)
    {
        var link = await ReadLinkAsync(connection, transaction, entity, key, cancellationToken);
        if (link is null) return null;
        if (await TargetExistsAsync(
                connection, transaction, targetTable, link.Value.TargetId, cancellationToken))
        {
            return link;
        }

        await DeleteLinkAsync(connection, transaction, entity, key, cancellationToken);
        counts.Warnings++;
        return null;
    }

    private static async Task DeleteLinkAsync(
        SqliteConnection connection, SqliteTransaction transaction, string entity, string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM kitaron_sync_links WHERE source_entity=$entity AND source_key=$key;";
        Add(command, "$entity", entity); Add(command, "$key", key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> ReadSourceKeyForTargetAsync(
        SqliteConnection connection, SqliteTransaction transaction, string entity, string targetId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT source_key
            FROM kitaron_sync_links
            WHERE source_entity=$entity AND target_id=$target
            LIMIT 1;
            """;
        Add(command, "$entity", entity);
        Add(command, "$target", targetId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task UpsertLinkAsync(
        SqliteConnection connection, SqliteTransaction transaction, string entity, string key,
        string targetId, bool owns, string hash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO kitaron_sync_links (source_entity, source_key, target_id, owns_target,
                source_hash, first_seen_at, last_seen_at)
            VALUES ($entity, $key, $target, $owns, $hash, $now, $now)
            ON CONFLICT(source_entity, source_key) DO UPDATE SET
                target_id=excluded.target_id, owns_target=excluded.owns_target,
                source_hash=excluded.source_hash, last_seen_at=excluded.last_seen_at;
            """;
        Add(command, "$entity", entity); Add(command, "$key", key); Add(command, "$target", targetId);
        Add(command, "$owns", owns ? 1 : 0); Add(command, "$hash", hash); Add(command, "$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TargetExistsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string table, string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE id=$id;";
        Add(command, "$id", id);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<IReadOnlyList<string>> FindIdsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql, object key,
        CancellationToken cancellationToken, string? caseId = null,
        params (string Name, object Value)[] extras)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction; command.CommandText = sql;
        Add(command, "$key", key); if (caseId is not null) Add(command, "$caseId", caseId);
        foreach (var extra in extras) Add(command, extra.Name, extra.Value);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<int> ScalarIntAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql,
        string name, object value, CancellationToken cancellationToken,
        params (string Name, object Value)[] extras)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        Add(command, name, value); foreach (var extra in extras) Add(command, extra.Name, extra.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static string StableId(string prefix, string key)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return $"{prefix}-{hash[..24]}";
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private static async Task<KitaronSyncStatus> ReadStatusAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT sync_status, message, last_started_at, last_completed_at, source_rows,
                cases_created, cases_updated, cases_matched, orders_created, orders_updated, orders_matched,
                operations_created, operations_updated, operations_matched,
                components_created, components_updated, components_matched,
                warning_count, mapping_version, version
            FROM kitaron_sync_state WHERE id=1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Kitaron sync state is missing.");
        static DateTimeOffset? Date(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), System.Globalization.CultureInfo.InvariantCulture);
        return new KitaronSyncStatus(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            Date(reader, 2), Date(reader, 3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
            reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11),
            reader.GetInt32(12), reader.GetInt32(13), reader.GetInt32(14), reader.GetInt32(15), reader.GetInt32(16),
            reader.GetInt32(17), reader.IsDBNull(18) ? null : reader.GetInt32(18), reader.GetInt32(19));
    }

    private sealed class MutableCounts
    {
        internal int CasesCreated, CasesUpdated, CasesMatched, OrdersCreated, OrdersUpdated, OrdersMatched, OrdersDeleted, BatchesDeleted;
        internal int OperationsCreated, OperationsUpdated, OperationsMatched, Warnings;
        internal int HistoricalOrdersRetained;
        internal int ComponentsCreated, ComponentsUpdated, ComponentsMatched;
    }
}
