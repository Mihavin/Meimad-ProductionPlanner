using System.Globalization;
using Meimad.Planner.Server.Application.Deletion;
using Meimad.Planner.Server.Application.EditMode;
using Microsoft.Data.Sqlite;

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
            await BlockIfAnyAsync(c, t, "orders", "case_id", id, "Delete the Case's Orders first.", token);
            await BlockIfAnyAsync(c, t, "production_batches", "case_id", id, "Delete the Case's Production Batches first.", token);
            await BlockIfAnyAsync(c, t, "case_operations", "case_id", id, "Delete the Case's Operations first.", token);
            return await DeleteRowAsync(c, t, "cases", id, token);
        }, token);

    public Task<bool> DeleteOrderAsync(string id, EditAuthority authority, CancellationToken token) =>
        ExecuteAsync(id, authority, async (c, t) =>
        {
            if (!await ExistsAsync(c, t, "orders", id, token)) return false;
            await BlockIfAnyAsync(c, t, "batch_allocations", "order_id", id, "The Order is allocated to a Production Batch.", token);
            return await DeleteRowAsync(c, t, "orders", id, token);
        }, token);

    public Task<bool> DeleteMachineAsync(string id, EditAuthority authority, CancellationToken token) =>
        ExecuteAsync(id, authority, async (c, t) =>
        {
            if (!await ExistsAsync(c, t, "machines", id, token)) return false;
            await BlockIfAnyAsync(c, t, "machine_assignments", "machine_id", id, "Unassign all Machine backlog operations first.", token);
            await BlockIfAnyAsync(c, t, "downtimes", "machine_id", id, "Delete the Machine's Downtime records first.", token);
            await BlockIfAnyAsync(c, t, "device_registry", "machine_id", id, "Unbind or delete the Machine's registered device first.", token);
            await BlockIfAnyAsync(c, t, "eink_package_revisions", "machine_id", id, "The Machine is referenced by an official job package.", token);
            return await DeleteRowAsync(c, t, "machines", id, token);
        }, token);

    public Task<bool> DeleteBatchAsync(string id, EditAuthority authority, CancellationToken token) =>
        ExecuteAsync(id, authority, async (c, t) =>
        {
            if (!await ExistsAsync(c, t, "production_batches", id, token)) return false;
            await BlockBySqlAsync(c, t, "SELECT EXISTS(SELECT 1 FROM machine_assignments JOIN batch_operations ON batch_operations.id = machine_assignments.batch_operation_id WHERE batch_operations.production_batch_id = $id);", id, "Unassign all Batch Operations before deleting the Production Batch.", token);
            await BlockBySqlAsync(c, t, "SELECT EXISTS(SELECT 1 FROM operation_pause_events JOIN batch_operations ON batch_operations.id = operation_pause_events.batch_operation_id WHERE batch_operations.production_batch_id = $id);", id, "The Production Batch has retained Operation pause history and cannot be deleted.", token);
            await BlockIfAnyAsync(c, t, "eink_package_revisions", "production_batch_id", id, "The Production Batch is referenced by an official job package.", token);
            await BlockBySqlAsync(c, t, "SELECT EXISTS(SELECT 1 FROM eink_package_revisions JOIN batch_operations ON batch_operations.id = eink_package_revisions.batch_operation_id WHERE batch_operations.production_batch_id = $id);", id, "The Production Batch is referenced by an official job package.", token);
            var affectedOrders = await SqliteOrderLifecycle.ReadCandidatesForBatchAsync(
                c,
                t,
                id,
                token);
            await ExecuteDeleteAsync(c, t, "DELETE FROM batch_allocations WHERE production_batch_id = $id;", id, token);
            await ExecuteDeleteAsync(c, t, "DELETE FROM batch_operations WHERE production_batch_id = $id;", id, token);
            if (!await DeleteRowAsync(c, t, "production_batches", id, token)) return false;
            await SqliteOrderLifecycle.RecomputeAsync(
                c,
                t,
                affectedOrders,
                timeProvider.GetUtcNow(),
                token);
            return true;
        }, token);

    public Task<bool> DeleteCaseOperationAsync(string caseId, string id, EditAuthority authority, CancellationToken token) =>
        ExecuteAsync(id, authority, async (c, t) =>
        {
            await using var read = c.CreateCommand();
            read.Transaction = t;
            read.CommandText = "SELECT route_position, simultaneous_group_key FROM case_operations WHERE id = $id AND case_id = $caseId;";
            read.Parameters.AddWithValue("$id", id);
            read.Parameters.AddWithValue("$caseId", caseId);
            await using var reader = await read.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return false;
            var position = reader.GetInt32(0);
            var group = reader.IsDBNull(1) ? null : reader.GetString(1);
            await reader.DisposeAsync();
            await BlockIfAnyAsync(c, t, "batch_operations", "source_case_operation_id", id, "The Operation has already been instantiated in a Production Batch.", token);
            await BlockIfAnyAsync(c, t, "case_operations", "predecessor_case_operation_id", id, "Another Case Operation depends on this Operation.", token);
            if (group is not null)
            {
                await BlockBySqlAsync(c, t, "SELECT EXISTS(SELECT 1 FROM case_operations WHERE case_id = $caseId AND simultaneous_group_key = $group AND id <> $id);", id, "Remove the locked-simultaneous group relationship before deleting this Operation.", token,
                    ("$caseId", caseId), ("$group", group));
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
