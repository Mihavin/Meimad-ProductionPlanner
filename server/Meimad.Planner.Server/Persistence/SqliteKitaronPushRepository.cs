using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.Concurrency;
using Meimad.Planner.Server.Application.Kitaron.Push;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed partial class SqliteKitaronPushRepository(SqliteDatabase database) : IKitaronPushRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<KitaronPushSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ReadSettingsAsync(connection, null, cancellationToken);
    }

    public async Task<KitaronPushSettings> UpdateSettingsAsync(
        bool enabled, int intervalMinutes, IReadOnlyList<KitaronPushMapping> mappings, int expectedVersion,
        string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadSettingsAsync(connection, transaction, cancellationToken);
        if (current.Version != expectedVersion)
        {
            throw new EditConflictException(
                "Kitaron push settings",
                "Someone saved the Kitaron push settings after you opened them. Your settings were not saved.",
                "Refresh the Kitaron Push tab to see the current settings, then make your change again.",
                current.UpdatedBy,
                current.UpdatedAt);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE kitaron_push_settings
                SET enabled = $enabled, interval_minutes = $interval, mappings_json = $mappings,
                    version = version + 1, updated_at = $at, updated_by = $by
                WHERE id = 1;
                """;
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$interval", intervalMinutes);
            command.Parameters.AddWithValue("$mappings", JsonSerializer.Serialize(mappings, JsonOptions));
            command.Parameters.AddWithValue("$at", Format(now));
            command.Parameters.AddWithValue("$by", actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var saved = await ReadSettingsAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }

    public async Task<IReadOnlyList<KitaronPushOperation>> ReadOperationsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Work Orders linked to a Kitaron Work Order ("wo:<NUMBER>"); the first QC PASS after the
        // operation's start ends its setup; good quantity is what the Production Runs produced.
        command.CommandText = """
            SELECT operation.id, link.source_key, operation.operation_number, cases.part_number, operation.name,
                   operation.actual_start, operation.actual_end,
                   (SELECT MIN(event.server_received_at)
                    FROM production_run_outputs output
                    JOIN production_run_programs program ON program.id = output.production_run_program_id
                    JOIN production_run_workflow_events event
                      ON event.production_run_id = program.production_run_id AND event.event_type = 'QC_PASS'
                    WHERE output.batch_operation_id = operation.id
                      AND (operation.actual_start IS NULL OR event.server_received_at >= operation.actual_start)),
                   (SELECT COALESCE(SUM(output.produced_quantity), 0)
                    FROM production_run_outputs output WHERE output.batch_operation_id = operation.id),
                   batch.planned_quantity
            FROM kitaron_sync_links link
            JOIN production_batches batch ON batch.id = link.target_id
            JOIN cases ON cases.id = batch.case_id
            JOIN batch_operations operation ON operation.production_batch_id = batch.id
            WHERE link.source_entity = 'production_batch' AND link.source_key LIKE 'wo:%'
              AND batch.status <> 'cancelled' AND operation.status <> 'cancelled';
            """;
        var operations = new List<KitaronPushOperation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!int.TryParse(reader.GetString(1)["wo:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                continue;
            operations.Add(new KitaronPushOperation(
                reader.GetString(0),
                number,
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                Instant(reader, 5),
                Instant(reader, 6),
                Instant(reader, 7),
                reader.GetInt32(8),
                reader.GetInt32(9)));
        }
        return operations;
    }

    public async Task StartRunAsync(
        string runId, string trigger, string? requestedBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var guard = connection.CreateCommand())
        {
            guard.Transaction = transaction;
            guard.CommandText = "SELECT id FROM kitaron_push_runs WHERE lifecycle_state IN ('Preparing','Prepared','Writing','OutcomeUnknown') LIMIT 1";
            if (await guard.ExecuteScalarAsync(cancellationToken) is string unresolved)
                throw new KitaronPushBlockedException($"Push {unresolved} is active or unresolved. Inspect/reconcile it before another push.");
        }
        await using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = "DELETE FROM kitaron_push_runs WHERE started_at < $before AND lifecycle_state IN ('Succeeded','FailedBeforeCommit','Reconciled') AND NOT EXISTS(SELECT 1 FROM kitaron_push_reconciliations r WHERE r.run_id=kitaron_push_runs.id AND r.observed_at >= $before);";
            prune.Parameters.AddWithValue("$before", Format(now.AddDays(-90)));
            await prune.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO kitaron_push_runs (id, trigger, requested_by, started_at, status)
                VALUES ($id, $trigger, $by, $at, 'running');
                """;
            insert.Parameters.AddWithValue("$id", runId);
            insert.Parameters.AddWithValue("$trigger", trigger);
            insert.Parameters.AddWithValue("$by", (object?)requestedBy ?? DBNull.Value);
            insert.Parameters.AddWithValue("$at", Format(now));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task FinishRunAsync(
        string runId, bool succeeded, int operationsMatched, int operationsSkipped, string? message,
        IReadOnlyList<KitaronPushChange> writtenChanges, DateTimeOffset now, CancellationToken cancellationToken, string? lifecycleState = null)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE kitaron_push_runs
                SET finished_at = $at, status = $status, operations_matched = $matched, values_written = $written,
                    operations_skipped = $skipped, message = $message, lifecycle_state = $phase, intent_version = intent_version + 1
                WHERE id = $id AND lifecycle_state IN ('Preparing','Prepared','Writing');
                """;
            update.Parameters.AddWithValue("$id", runId);
            update.Parameters.AddWithValue("$at", Format(now));
            update.Parameters.AddWithValue("$status", succeeded ? "succeeded" : "failed");
            update.Parameters.AddWithValue("$phase", lifecycleState ?? (succeeded ? "Succeeded" : "FailedBeforeCommit"));
            update.Parameters.AddWithValue("$matched", operationsMatched);
            update.Parameters.AddWithValue("$written", writtenChanges.Count);
            update.Parameters.AddWithValue("$skipped", operationsSkipped);
            update.Parameters.AddWithValue("$message", (object?)message ?? DBNull.Value);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new KitaronPushConflictException("The push lifecycle changed; refresh its run record.");
        }
        foreach (var change in writtenChanges)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR REPLACE INTO kitaron_push_changes (
                    run_id, kitaron_row_id, work_order_number, action_number, kitaron_column, old_value, new_value)
                VALUES ($run, $row, $number, $action, $column, $old, $new);
                """;
            insert.Parameters.AddWithValue("$run", runId);
            insert.Parameters.AddWithValue("$row", change.KitaronRowId);
            insert.Parameters.AddWithValue("$number", change.WorkOrderNumber);
            insert.Parameters.AddWithValue("$action", change.ActionNumber);
            insert.Parameters.AddWithValue("$column", change.KitaronColumn);
            insert.Parameters.AddWithValue("$old", (object?)change.OldValue ?? DBNull.Value);
            insert.Parameters.AddWithValue("$new", change.NewValue);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<DateTimeOffset?> LastAutomaticRunStartedAtAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(started_at) FROM kitaron_push_runs WHERE trigger = 'automatic';";
        return await command.ExecuteScalarAsync(cancellationToken) is string value ? Parse(value) : null;
    }

    public async Task<IReadOnlyList<KitaronPushRunSummary>> ListRunsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, trigger, requested_by, started_at, finished_at, status, operations_matched, values_written,
                   operations_skipped, message, lifecycle_state, intent_version
            FROM kitaron_push_runs ORDER BY lifecycle_state IN ('OutcomeUnknown','Writing','Prepared','Preparing') DESC, started_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var runs = new List<KitaronPushRunSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            runs.Add(new KitaronPushRunSummary(
                reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                Parse(reader.GetString(3)), Instant(reader, 4), reader.GetString(5), reader.GetInt32(6),
                reader.GetInt32(7), reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(10), reader.GetInt32(11)));
        }
        return runs;
    }

    public async Task<IReadOnlyList<KitaronPushChange>> ListChangesAsync(string runId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT work_order_number, action_number, kitaron_row_id, kitaron_column, old_value, new_value
            FROM kitaron_push_changes WHERE run_id = $run
            ORDER BY work_order_number, action_number, kitaron_column;
            """;
        command.Parameters.AddWithValue("$run", runId);
        var changes = new List<KitaronPushChange>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            changes.Add(new KitaronPushChange(
                reader.GetInt32(0), reader.GetString(1), reader.GetInt64(2), string.Empty, string.Empty,
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5)));
        }
        return changes;
    }

    private static async Task<KitaronPushSettings> ReadSettingsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT enabled, interval_minutes, mappings_json, version, updated_at, updated_by
            FROM kitaron_push_settings WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("The Kitaron push settings row is missing.");
        var mappings = JsonSerializer.Deserialize<KitaronPushMapping[]>(reader.GetString(2), JsonOptions) ?? [];
        return new KitaronPushSettings(
            reader.GetInt32(0) == 1, reader.GetInt32(1), mappings, reader.GetInt32(3), Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static DateTimeOffset? Instant(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Parse(reader.GetString(ordinal));

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
