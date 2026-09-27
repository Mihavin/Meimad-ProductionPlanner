using System.Globalization;
using Meimad.Planner.Server.Application.Deletion;
using Meimad.Planner.Server.Application.EditMode;
using Microsoft.Data.Sqlite;
using Meimad.Planner.Server.Application.Kitaron;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqlitePlanningDeletionRepository : IPlanningDeletionRepository
{
    private readonly SqliteDatabase database;
    private readonly TimeProvider timeProvider;

    public SqlitePlanningDeletionRepository(SqliteDatabase database, TimeProvider timeProvider)
    {
        this.database = database;
        this.timeProvider = timeProvider;
    }

    public Task<bool> DeleteCaseAsync(string id, EditAuthority authority, CancellationToken token) =>
        ExecuteAsync(id, authority, async (c, t) =>
        {
            if (!await ExistsAsync(c, t, "cases", id, token)) return false;
            await ThrowIfKitaronManagedAsync(c, t, "case", id, "Case", token);
            await BlockIfAnyAsync(c, t, "orders", "case_id", id, "Delete the Case's Orders first.", token);
            await BlockIfAnyAsync(c, t, "production_batches", "case_id", id, "Delete the Case's Production Batches first.", token);
            await BlockIfAnyAsync(c, t, "verified_material_receipts", "case_id", id,
                "The Case has verified material receipt history and cannot be deleted.", token);
            await BlockIfAnyAsync(c, t, "case_operations", "case_id", id, "Delete the Case's Operations first.", token);
            await BlockBySqlAsync(c, t,
                "SELECT EXISTS(SELECT 1 FROM case_components WHERE is_active=1 AND (parent_case_id=$id OR child_case_id=$id));",
                id, "Deactivate the Case's active component relationships first.", token);
            await using (var removeComponents = c.CreateCommand())
            {
                removeComponents.Transaction = t;
                removeComponents.CommandText = """
                    DELETE FROM kitaron_sync_links
                    WHERE source_entity='case_component' AND target_id IN (
                        SELECT id FROM case_components
                        WHERE is_active=0 AND (parent_case_id=$id OR child_case_id=$id));
                    DELETE FROM case_components
                    WHERE is_active=0 AND (parent_case_id=$id OR child_case_id=$id);
                    DELETE FROM case_model_files WHERE case_id=$id;
                    """;
                removeComponents.Parameters.AddWithValue("$id", id);
                await removeComponents.ExecuteNonQueryAsync(token);
            }
            return await DeleteRowAsync(c, t, "cases", id, token);
        }, token);

    public Task<bool> DeleteOrderAsync(string id, EditAuthority authority, CancellationToken token) =>
        ExecuteAsync(id, authority, async (c, t) =>
        {
            if (!await ExistsAsync(c, t, "orders", id, token)) return false;
            await ThrowIfKitaronManagedAsync(c, t, "order", id, "Order", token);
            await BlockIfAnyAsync(c, t, "batch_allocations", "order_id", id, "The Order is allocated to a Production Batch.", token);
            await BlockBySqlAsync(c, t,
                "SELECT EXISTS(SELECT 1 FROM batch_allocations WHERE allocation_type='derived_order' AND instr(derived_order_key, 'derived:' || $id || ':')=1);",
                id, "The Order supplies derived demand to a child Production Batch.", token);
            return await DeleteRowAsync(c, t, "orders", id, token);
        }, token);

    public Task<bool> DeleteMachineAsync(string id, EditAuthority authority, CancellationToken token) =>
        ExecuteAsync(id, authority, async (c, t) =>
        {
            if (!await ExistsAsync(c, t, "machines", id, token)) return false;
            await BlockBySqlAsync(c, t,
                "SELECT EXISTS(SELECT 1 FROM machine_assignments WHERE machine_id = $id AND released_at IS NULL);",
                id, "Unassign all Machine backlog operations first.", token);
            await BlockIfAnyAsync(c, t, "downtimes", "machine_id", id, "Delete the Machine's Downtime records first.", token);
            await BlockIfAnyAsync(c, t, "device_registry", "machine_id", id, "Unbind or delete the Machine's registered device first.", token);
            await BlockIfAnyAsync(c, t, "eink_package_revisions", "machine_id", id, "The Machine is referenced by an official job package.", token);
            await BlockBySqlAsync(c, t,
                "SELECT EXISTS(SELECT 1 FROM employee_resources, json_each(employee_resources.skills_json) WHERE json_each.value = $id);",
                id, "Remove this Machine from Employee qualifications first.", token);
            await ExecuteDeleteAsync(
                c,
                t,
                "DELETE FROM machine_supported_postprocessors WHERE machine_id = $id;",
                id,
                token);
            return await DeleteRowAsync(c, t, "machines", id, token);
        }, token);

    private static async Task ThrowIfKitaronManagedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceEntity,
        string targetId,
        string resourceType,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM kitaron_sync_links
                WHERE source_entity=$entity AND target_id=$id
                UNION ALL
                SELECT 1 FROM orders
                WHERE $entity='order' AND id=$id AND kitaron_history_only=1
                UNION ALL
                SELECT 1
                FROM orders
                JOIN kitaron_sync_links case_link
                  ON case_link.source_entity='case'
                 AND case_link.target_id=orders.case_id
                WHERE $entity='order' AND orders.id=$id);
            """;
        command.Parameters.AddWithValue("$entity", sourceEntity);
        command.Parameters.AddWithValue("$id", targetId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1)
        {
            throw new KitaronManagedResourceException(resourceType, targetId);
        }
    }

    public Task<bool> DeleteBatchAsync(string id, EditAuthority authority, CancellationToken token) =>
        ExecuteAsync(id, authority, async (c, t) =>
        {
            // A batch imported from a Kitaron work order leaves with its work order, not by hand.
            await using (var link = c.CreateCommand())
            {
                link.Transaction = t;
                link.CommandText = "SELECT EXISTS(SELECT 1 FROM kitaron_sync_links WHERE source_entity = 'production_batch' AND target_id = $id);";
                link.Parameters.AddWithValue("$id", id);
                if (Convert.ToInt32(await link.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1)
                    throw new KitaronManagedResourceException("Production Batch", id);
            }
            return await DeleteBatchGraphAsync(c, t, id, timeProvider.GetUtcNow(), token);
        }, token);

    internal static async Task<bool> DeleteBatchGraphAsync(
        SqliteConnection c,
        SqliteTransaction t,
        string id,
        DateTimeOffset now,
        CancellationToken token)
    {
            if (!await ExistsAsync(c, t, "production_batches", id, token)) return false;
            await BlockBySqlAsync(c, t, """
                SELECT EXISTS(
                    SELECT 1
                    FROM production_runs run
                    WHERE run.structure_locked_at IS NOT NULL
                      AND (
                        run.legacy_batch_operation_id IN (
                            SELECT operation.id
                            FROM batch_operations operation
                            WHERE operation.production_batch_id=$id)
                        OR EXISTS (
                            SELECT 1
                            FROM production_run_outputs output
                            JOIN production_run_programs program
                              ON program.id=output.production_run_program_id
                            JOIN batch_operations operation
                              ON operation.id=output.batch_operation_id
                            WHERE program.production_run_id=run.id
                              AND operation.production_batch_id=$id)));
                """, id,
                "This Production Batch has a started or completed Production Run and is immutable. " +
                "Use the supported completion/cancellation workflow; recorded production history cannot be deleted.",
                token);
            await BlockBySqlAsync(c, t,
                "SELECT EXISTS(SELECT 1 FROM production_packages WHERE batch_operation_id IN (SELECT id FROM batch_operations WHERE production_batch_id=$id));",
                id,
                "This Production Batch has published Production Packages and cannot be deleted. " +
                "Production Packages are permanent audit/QC records.",
                token);
            var affectedOrders = await SqliteOrderLifecycle.ReadCandidatesForBatchAsync(
                c,
                t,
                id,
                token);
            var affectedMachines = await ReadBatchMachineIdsAsync(c, t, id, token);

            // A confirmed Batch deletion owns its complete instantiated planning/execution graph.
            // Published package rows are immutable during normal use, but are intentionally removed
            // together with their Batch here so no restrictive foreign key can leave a ghost Batch.
            await ExecuteSqlAsync(c, t, "DROP TRIGGER IF EXISTS eink_package_files_immutable_delete; DROP TRIGGER IF EXISTS eink_package_revisions_immutable_delete;", token);
            await ExecuteDeleteAsync(c, t, "DELETE FROM eink_package_files WHERE package_revision_id IN (SELECT id FROM eink_package_revisions WHERE production_batch_id = $id OR batch_operation_id IN (SELECT id FROM batch_operations WHERE production_batch_id = $id));", id, token);
            await ExecuteDeleteAsync(c, t, "DELETE FROM eink_package_revisions WHERE production_batch_id = $id OR batch_operation_id IN (SELECT id FROM batch_operations WHERE production_batch_id = $id);", id, token);
            await ExecuteSqlAsync(c, t, """
                CREATE TRIGGER eink_package_revisions_immutable_delete BEFORE DELETE ON eink_package_revisions BEGIN SELECT RAISE(ABORT, 'published E-Ink package revisions are immutable'); END;
                CREATE TRIGGER eink_package_files_immutable_delete BEFORE DELETE ON eink_package_files BEGIN SELECT RAISE(ABORT, 'published E-Ink package files are immutable'); END;
                """, token);
            await ExecuteDeleteAsync(c, t, "DELETE FROM operation_pause_events WHERE batch_operation_id IN (SELECT id FROM batch_operations WHERE production_batch_id = $id);", id, token);
            await ExecuteDeleteAsync(c, t, "DELETE FROM machine_assignment_overrides WHERE batch_operation_id IN (SELECT id FROM batch_operations WHERE production_batch_id = $id);", id, token);
            // A full Batch deletion owns its whole graph outright: the block above already
            // guarantees no Production Package (immutable, RESTRICT to machine_assignments and to
            // batch_operations) was ever built against any operation in this Batch, so hard-deleting
            // the assignments here is safe.
            await ExecuteDeleteAsync(c, t, "DELETE FROM machine_assignments WHERE batch_operation_id IN (SELECT id FROM batch_operations WHERE production_batch_id = $id);", id, token);
            await ExecuteDeleteAsync(c, t, "DELETE FROM production_run_outputs WHERE batch_operation_id IN (SELECT id FROM batch_operations WHERE production_batch_id = $id);", id, token);
            await ExecuteDeleteAsync(c, t, "DELETE FROM production_run_programs WHERE production_run_id IN (SELECT id FROM production_runs WHERE legacy_batch_operation_id IN (SELECT id FROM batch_operations WHERE production_batch_id = $id));", id, token);
            await ExecuteDeleteAsync(c, t, "DELETE FROM production_runs WHERE legacy_batch_operation_id IN (SELECT id FROM batch_operations WHERE production_batch_id = $id);", id, token);
            foreach (var machineId in affectedMachines)
            {
                await CompactMachineBacklogAsync(c, t, machineId, token);
            }
            await ExecuteDeleteAsync(c, t, "DELETE FROM batch_allocations WHERE production_batch_id = $id;", id, token);
            await ExecuteDeleteAsync(c, t, "DELETE FROM batch_operations WHERE production_batch_id = $id;", id, token);
            if (!await DeleteRowAsync(c, t, "production_batches", id, token)) return false;
            await SqliteOrderLifecycle.RecomputeAsync(
                c,
                t,
                affectedOrders,
                now,
                token);
            return true;
    }

    private static async Task<IReadOnlyList<string>> ReadBatchMachineIdsAsync(SqliteConnection c, SqliteTransaction t, string batchId, CancellationToken token)
    {
        await using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = "SELECT DISTINCT machine_assignments.machine_id FROM machine_assignments JOIN batch_operations ON batch_operations.id = machine_assignments.batch_operation_id WHERE batch_operations.production_batch_id = $id AND machine_assignments.released_at IS NULL ORDER BY machine_assignments.machine_id;";
        command.Parameters.AddWithValue("$id", batchId);
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) ids.Add(reader.GetString(0));
        return ids;
    }

    private static async Task CompactMachineBacklogAsync(SqliteConnection c, SqliteTransaction t, string machineId, CancellationToken token)
    {
        await using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = """
            UPDATE machine_assignments SET backlog_position = backlog_position + 1000000 WHERE machine_id = $machineId AND released_at IS NULL;
            WITH ranked AS (
                SELECT id, ROW_NUMBER() OVER (ORDER BY backlog_position, id) - 1 AS position
                FROM machine_assignments WHERE machine_id = $machineId AND released_at IS NULL)
            UPDATE machine_assignments
            SET backlog_position = (SELECT position FROM ranked WHERE ranked.id = machine_assignments.id)
            WHERE machine_id = $machineId AND released_at IS NULL;
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task ExecuteSqlAsync(SqliteConnection c, SqliteTransaction t, string sql, CancellationToken token)
    {
        await using var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    public Task<bool> DeleteCaseOperationAsync(string caseId, string id, EditAuthority authority, CancellationToken token) =>
        ExecuteAsync(id, authority, async (c, t) =>
        {
            await using var read = c.CreateCommand();
            read.Transaction = t;
            read.CommandText = "SELECT route_position, simultaneous_group_key, operation_number, name FROM case_operations WHERE id = $id AND case_id = $caseId;";
            read.Parameters.AddWithValue("$id", id);
            read.Parameters.AddWithValue("$caseId", caseId);
            await using var reader = await read.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return false;
            var position = reader.GetInt32(0);
            var group = reader.IsDBNull(1) ? null : reader.GetString(1);
            await reader.DisposeAsync();
            // The operation list of a synchronized Case mirrors Kitaron: an operation Kitaron
            // produces cannot be deleted in Meimad Planner. Remap its station or change the route
            // in Kitaron instead; the synchronization then removes it.
            await using (var kitaronLink = c.CreateCommand())
            {
                kitaronLink.Transaction = t;
                kitaronLink.CommandText = "SELECT EXISTS(SELECT 1 FROM kitaron_sync_links WHERE source_entity = 'case_operation' AND target_id = $id);";
                kitaronLink.Parameters.AddWithValue("$id", id);
                if (Convert.ToInt32(await kitaronLink.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1)
                    throw new KitaronManagedResourceException("Case Operation", id);
            }
            await BlockIfAnyAsync(c, t, "batch_operations", "source_case_operation_id", id, "The Operation has already been instantiated in a Production Batch.", token);
            await BlockIfAnyAsync(c, t, "process_revisions", "case_operation_id", id, "The Operation has immutable process or G-code release history.", token);
            // An imported Kitaron route is a sequence. Removing one of its operations re-links the
            // Kitaron-owned operations that followed it to the operation before it (or lets them start
            // the route), as the next synchronization would; any other dependent still blocks.
            await using (var relink = c.CreateCommand())
            {
                relink.Transaction = t;
                relink.CommandText = """
                    UPDATE case_operations
                    SET predecessor_case_operation_id = (SELECT removed.predecessor_case_operation_id FROM case_operations removed WHERE removed.id = $id),
                        dependency_type = CASE
                            WHEN (SELECT removed.predecessor_case_operation_id FROM case_operations removed WHERE removed.id = $id) IS NULL
                            THEN 'independent' ELSE dependency_type END,
                        version = version + 1, updated_at = $now
                    WHERE predecessor_case_operation_id = $id
                      AND case_id = $caseId
                      AND dependency_type IN ('sequential', 'parallel_capable')
                      AND EXISTS (
                          SELECT 1 FROM kitaron_sync_links link
                          WHERE link.source_entity = 'case_operation' AND link.target_id = case_operations.id AND link.owns_target = 1);
                    """;
                relink.Parameters.AddWithValue("$id", id);
                relink.Parameters.AddWithValue("$caseId", caseId);
                relink.Parameters.AddWithValue("$now", timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
                await relink.ExecuteNonQueryAsync(token);
            }
            await BlockIfAnyAsync(c, t, "case_operations", "predecessor_case_operation_id", id, "Another Case Operation depends on this Operation.", token);
            if (group is not null)
            {
                await BlockBySqlAsync(c, t, "SELECT EXISTS(SELECT 1 FROM case_operations WHERE case_id = $caseId AND simultaneous_group_key = $group AND id <> $id);", id, "Remove the locked-simultaneous group relationship before deleting this Operation.", token,
                    ("$caseId", caseId), ("$group", group));
            }
            // Auxiliary requirements belong to the Operation and go with it, with their links.
            await using (var unlinkRequirements = c.CreateCommand())
            {
                unlinkRequirements.Transaction = t;
                unlinkRequirements.CommandText = """
                    DELETE FROM kitaron_sync_links
                    WHERE source_entity = 'operation_requirement' AND target_id IN (
                        SELECT id FROM operation_resource_requirements WHERE case_operation_id = $id);
                    """;
                unlinkRequirements.Parameters.AddWithValue("$id", id);
                await unlinkRequirements.ExecuteNonQueryAsync(token);
            }
            await using (var removeRequirements = c.CreateCommand())
            {
                removeRequirements.Transaction = t;
                removeRequirements.CommandText = """
                    DELETE FROM external_resource_executions WHERE schedule_work_id IN (
                        SELECT work.id FROM resource_schedule_work work
                        JOIN operation_resource_requirements requirement ON requirement.id = work.requirement_id
                        WHERE requirement.case_operation_id = $id);
                    DELETE FROM resource_schedule_assignments WHERE schedule_work_id IN (
                        SELECT work.id FROM resource_schedule_work work
                        JOIN operation_resource_requirements requirement ON requirement.id = work.requirement_id
                        WHERE requirement.case_operation_id = $id);
                    DELETE FROM resource_schedule_work WHERE requirement_id IN (
                        SELECT id FROM operation_resource_requirements WHERE case_operation_id = $id);
                    UPDATE operation_resource_requirements SET predecessor_requirement_id = NULL WHERE case_operation_id = $id;
                    DELETE FROM operation_resource_requirements WHERE case_operation_id = $id;
                    """;
                removeRequirements.Parameters.AddWithValue("$id", id);
                await removeRequirements.ExecuteNonQueryAsync(token);
            }
            // Model files attached to the Operation stay with the Case; they just lose the link.
            await using (var detachModels = c.CreateCommand())
            {
                detachModels.Transaction = t;
                detachModels.CommandText = "UPDATE case_model_files SET case_operation_id = NULL WHERE case_operation_id = $id;";
                detachModels.Parameters.AddWithValue("$id", id);
                await detachModels.ExecuteNonQueryAsync(token);
            }
            await DeleteRowAsync(c, t, "case_operations", id, token);
            await using var stage = c.CreateCommand();
            stage.Transaction = t;
            stage.CommandText = "UPDATE case_operations SET route_position = route_position + 1000000 WHERE case_id = $caseId AND route_position > $position; UPDATE case_operations SET route_position = route_position - 1000001 WHERE case_id = $caseId AND route_position > 1000000;";
            stage.Parameters.AddWithValue("$caseId", caseId);
            stage.Parameters.AddWithValue("$position", position);
            await stage.ExecuteNonQueryAsync(token);
            return true;
        }, token);

    private async Task<bool> ExecuteAsync(string id, EditAuthority authority, Func<SqliteConnection, SqliteTransaction, Task<bool>> action, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        var deleted = await action(connection, transaction);
        await transaction.CommitAsync(token);
        return deleted;
    }

    private static async Task<bool> ExistsAsync(SqliteConnection c, SqliteTransaction t, string table, string id, CancellationToken token) =>
        await ScalarExistsAsync(c, t, $"SELECT EXISTS(SELECT 1 FROM {table} WHERE id = $id);", id, token);

    private static async Task BlockIfAnyAsync(SqliteConnection c, SqliteTransaction t, string table, string column, string id, string message, CancellationToken token) =>
        await BlockBySqlAsync(c, t, $"SELECT EXISTS(SELECT 1 FROM {table} WHERE {column} = $id);", id, message, token);

    private static async Task BlockBySqlAsync(SqliteConnection c, SqliteTransaction t, string sql, string id, string message, CancellationToken token, params (string Name, object Value)[] extra)
    {
        await using var command = c.CreateCommand();
        command.Transaction = t;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        foreach (var parameter in extra) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1)
            throw new PlanningDeletionBlockedException(message);
    }

    private static async Task<bool> ScalarExistsAsync(SqliteConnection c, SqliteTransaction t, string sql, string id, CancellationToken token)
    {
        await using var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql; command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1;
    }

    private static Task<int> ExecuteDeleteAsync(SqliteConnection c, SqliteTransaction t, string sql, string id, CancellationToken token)
    {
        var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql; command.Parameters.AddWithValue("$id", id);
        return ExecuteAndDisposeAsync(command, token);
    }


    private static async Task<int> ExecuteAndDisposeAsync(SqliteCommand command, CancellationToken token)
    { await using (command) return await command.ExecuteNonQueryAsync(token); }

    private static async Task<bool> DeleteRowAsync(SqliteConnection c, SqliteTransaction t, string table, string id, CancellationToken token) =>
        await ExecuteDeleteAsync(c, t, $"DELETE FROM {table} WHERE id = $id;", id, token) == 1;

    private static async Task EnsureEditAuthorityAsync(SqliteConnection c, SqliteTransaction t, EditAuthority authority, CancellationToken token)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(c, t, DateTimeOffset.UtcNow, token);
        await using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = "SELECT holder_client_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0)) throw new EditModeMutationException("edit_mode_required", "No Windows client currently holds Edit Mode.");
        if (reader.GetString(0) != authority.ClientId || reader.GetInt64(1) != authority.Generation) throw new EditModeMutationException("edit_generation_stale", "This client does not hold the active Edit Mode generation.");
    }
}
