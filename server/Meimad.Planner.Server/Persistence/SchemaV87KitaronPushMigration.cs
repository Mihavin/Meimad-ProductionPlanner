using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Kitaron write-back (owner decision 2026-09-28): the Planner pushes chosen values of its Work Order
/// operations into Kitaron's <c>TSubRootCard</c>. One settings row holds the switch, the interval and
/// the column mappings; every run and every value written is logged. Pushing starts switched off.
/// </summary>
internal sealed class SchemaV87KitaronPushMigration : IDatabaseMigration
{
    internal const string DefaultMappingsJson = """
        [{"kitaronColumn":"OperationQty","plannerValue":"good_quantity","enabled":true},
         {"kitaronColumn":"StartDateReal","plannerValue":"actual_start","enabled":true},
         {"kitaronColumn":"FinishDateCalc","plannerValue":"forecast_finish","enabled":true},
         {"kitaronColumn":"SetupTimeReal","plannerValue":"setup_minutes","enabled":true}]
        """;

    public int Version => 87;

    public string Name => "kitaron_push";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE kitaron_push_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                enabled INTEGER NOT NULL DEFAULT 0 CHECK (enabled IN (0, 1)),
                interval_minutes INTEGER NOT NULL DEFAULT 15 CHECK (interval_minutes BETWEEN 5 AND 1440),
                mappings_json TEXT NOT NULL CHECK (json_valid(mappings_json)),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                updated_at TEXT NOT NULL,
                updated_by TEXT NULL
            );

            CREATE TABLE kitaron_push_runs (
                id TEXT PRIMARY KEY,
                trigger TEXT NOT NULL CHECK (trigger IN ('automatic', 'manual')),
                requested_by TEXT NULL,
                started_at TEXT NOT NULL,
                finished_at TEXT NULL,
                status TEXT NOT NULL CHECK (status IN ('running', 'succeeded', 'failed')),
                operations_matched INTEGER NOT NULL DEFAULT 0 CHECK (operations_matched >= 0),
                values_written INTEGER NOT NULL DEFAULT 0 CHECK (values_written >= 0),
                operations_skipped INTEGER NOT NULL DEFAULT 0 CHECK (operations_skipped >= 0),
                message TEXT NULL
            );

            CREATE INDEX ix_kitaron_push_runs_started ON kitaron_push_runs (started_at);

            CREATE TABLE kitaron_push_changes (
                run_id TEXT NOT NULL REFERENCES kitaron_push_runs(id) ON DELETE CASCADE,
                kitaron_row_id INTEGER NOT NULL,
                work_order_number INTEGER NOT NULL,
                action_number TEXT NOT NULL,
                kitaron_column TEXT NOT NULL,
                old_value TEXT NULL,
                new_value TEXT NOT NULL,
                PRIMARY KEY (run_id, kitaron_row_id, kitaron_column)
            );

            INSERT INTO kitaron_push_settings (id, enabled, interval_minutes, mappings_json, updated_at)
            VALUES (1, 0, 15, $mappings, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        command.Parameters.AddWithValue("$mappings", DefaultMappingsJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
