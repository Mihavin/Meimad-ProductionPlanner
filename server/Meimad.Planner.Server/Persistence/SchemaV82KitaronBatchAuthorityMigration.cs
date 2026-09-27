using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// The owner decided (2026-09-25) that Production Batches come from Kitaron work orders and that
/// the existing planner-created batches, the Machine backlogs, and every assignment built on them
/// are removed once so planning restarts from the imported batches ("I will replan it all after").
/// Every removed row is first copied as JSON into `wipe_v82_archive`, so the pre-wipe facts stay
/// recoverable inside the database without keeping the live tables populated.
///
/// The migration also retires `kitaron_suppressed_operations` (planner deletion of a Kitaron
/// route Operation is now rejected instead of remembered), marks Cases whose route the Kitaron
/// route master owns with `kitaron_route_locked`, and adds the per-batch Kitaron material check.
/// </summary>
internal sealed class SchemaV82KitaronBatchAuthorityMigration : IDatabaseMigration
{
    /// <summary>Children first, so every DELETE satisfies the RESTRICT foreign keys.</summary>
    internal static readonly string[] WipedTables =
    [
        "production_package_invalidations",
        "production_package_artifacts",
        "production_package_current",
        "production_packages",
        "eink_package_files",
        "eink_package_revisions",
        "haas_events",
        "haas_bench_state_intervals",
        "haas_bench_sessions",
        "tool_preparation_components",
        "tool_preparation_tools",
        "tool_preparations",
        "external_resource_executions",
        "resource_schedule_assignments",
        "resource_schedule_work",
        "cnc_setup_verification_sessions",
        "production_run_cycle_attempt_outcomes",
        "production_run_cycle_attempts",
        "production_run_cycle_events",
        "production_run_session_closures",
        "production_run_workflow_anomalies",
        "production_run_current_offset_loaders",
        "offset_loader_releases",
        "production_run_workflow_events",
        "production_run_outputs",
        "production_run_programs",
        "machine_assignment_overrides",
        "machine_assignments",
        "production_runs",
        "batch_operation_material_readiness",
        "tool_offset_readiness_records",
        "operation_pause_events",
        "batch_material_reservations",
        "batch_allocations",
        "batch_operations",
        "production_batches"
    ];

    public int Version => 82;

    public string Name => "kitaron_batch_authority_reset";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            // The reset removes whole linked ledgers (packages supersede packages, sessions point at
            // workflow events, ...). Foreign keys are checked once at commit, on the final state.
            create.CommandText = """
                PRAGMA defer_foreign_keys = ON;
                CREATE TABLE IF NOT EXISTS wipe_v82_archive (
                    table_name TEXT NOT NULL,
                    row_json TEXT NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        // Several wiped ledgers carry immutability triggers that would abort the reset. They are
        // recreated exactly as stored once the one-time decision is applied.
        var triggers = new List<(string Name, string Sql)>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT name, sql FROM sqlite_master
                WHERE type = 'trigger' AND sql IS NOT NULL
                  AND tbl_name IN (SELECT value FROM json_each($tables))
                ORDER BY name;
                """;
            read.Parameters.AddWithValue("$tables", JsonSerializer.Serialize(
                WipedTables.Append("operational_anomalies")));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                triggers.Add((reader.GetString(0), reader.GetString(1)));
        }

        foreach (var trigger in triggers)
        {
            await using var drop = connection.CreateCommand();
            drop.Transaction = transaction;
            drop.CommandText = $"DROP TRIGGER \"{trigger.Name.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
            await drop.ExecuteNonQueryAsync(cancellationToken);
        }

        // Operational anomalies tied to the removed Production Runs go first (they reference the
        // runs and workflow events); Machine-level anomalies without such a reference stay.
        await ArchiveTableAsync(
            connection, transaction, "operational_anomalies", cancellationToken,
            "production_run_id IS NOT NULL OR workflow_event_id IS NOT NULL");
        await using (var anomalies = connection.CreateCommand())
        {
            anomalies.Transaction = transaction;
            anomalies.CommandText = """
                DELETE FROM operational_anomalies
                WHERE production_run_id IS NOT NULL OR workflow_event_id IS NOT NULL;
                """;
            await anomalies.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var table in WipedTables)
        {
            await ArchiveTableAsync(connection, transaction, table, cancellationToken);
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {table};";
            try { await delete.ExecuteNonQueryAsync(cancellationToken); }
            catch (SqliteException exception)
            {
                throw new InvalidOperationException(
                    $"Schema v82 could not clear {table}: {exception.Message}", exception);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                DROP TABLE kitaron_suppressed_operations;

                ALTER TABLE kitaron_sync_links RENAME TO kitaron_sync_links_v81;
                CREATE TABLE kitaron_sync_links (
                    source_entity TEXT NOT NULL
                        CHECK (source_entity IN ('case', 'order', 'case_operation', 'case_component',
                                                 'operation_requirement', 'production_batch')),
                    source_key TEXT NOT NULL,
                    target_id TEXT NOT NULL,
                    owns_target INTEGER NOT NULL CHECK (owns_target IN (0, 1)),
                    source_hash TEXT NOT NULL,
                    first_seen_at TEXT NOT NULL,
                    last_seen_at TEXT NOT NULL,
                    PRIMARY KEY (source_entity, source_key),
                    UNIQUE (source_entity, target_id)
                );
                INSERT INTO kitaron_sync_links (
                    source_entity, source_key, target_id, owns_target,
                    source_hash, first_seen_at, last_seen_at)
                SELECT source_entity, source_key, target_id, owns_target,
                       source_hash, first_seen_at, last_seen_at
                FROM kitaron_sync_links_v81;
                DROP TABLE kitaron_sync_links_v81;
                CREATE INDEX ix_kitaron_sync_links_target
                    ON kitaron_sync_links (source_entity, target_id);

                ALTER TABLE cases ADD COLUMN kitaron_route_locked INTEGER NOT NULL DEFAULT 0
                    CHECK (kitaron_route_locked IN (0, 1));

                CREATE TABLE kitaron_batch_material_checks (
                    production_batch_id TEXT PRIMARY KEY
                        REFERENCES production_batches(id) ON DELETE CASCADE,
                    state TEXT NOT NULL
                        CHECK (state IN ('available', 'on_order', 'missing', 'unknown')),
                    detail TEXT NULL CHECK (detail IS NULL OR length(detail) <= 2000),
                    updated_at TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var trigger in triggers)
        {
            await using var recreate = connection.CreateCommand();
            recreate.Transaction = transaction;
            recreate.CommandText = trigger.Sql;
            await recreate.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ArchiveTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken cancellationToken,
        string? filter = null)
    {
        var rows = new List<string>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = $"SELECT * FROM {table}" + (filter is null ? ";" : $" WHERE {filter};");
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var values = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
                for (var index = 0; index < reader.FieldCount; index++)
                    values[reader.GetName(index)] = await reader.IsDBNullAsync(index, cancellationToken)
                        ? null
                        : reader.GetValue(index);
                rows.Add(JsonSerializer.Serialize(values));
            }
        }

        foreach (var row in rows)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO wipe_v82_archive (table_name, row_json) VALUES ($table, $row);";
            insert.Parameters.AddWithValue("$table", table);
            insert.Parameters.AddWithValue("$row", row);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
