using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV34MachineExecutionAndPostprocessorsMigration : IDatabaseMigration
{
    public int Version => 34;

    public string Name => "machine_execution_and_postprocessors";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE machines ADD COLUMN execution_mode TEXT NOT NULL DEFAULT 'MANUAL'
                CHECK (execution_mode IN ('CNC_GCODE', 'MANUAL'));
            ALTER TABLE machines ADD COLUMN usable_tool_positions INTEGER
                CHECK (usable_tool_positions IS NULL OR usable_tool_positions > 0);
            ALTER TABLE machines ADD COLUMN rapid_rate_mm_per_min REAL
                CHECK (rapid_rate_mm_per_min IS NULL OR rapid_rate_mm_per_min > 0);
            ALTER TABLE machines ADD COLUMN tool_change_time_seconds REAL
                CHECK (tool_change_time_seconds IS NULL OR tool_change_time_seconds >= 0);
            ALTER TABLE machines ADD COLUMN machine_time_factor REAL NOT NULL DEFAULT 1.0
                CHECK (machine_time_factor > 0);

            CREATE TABLE postprocessors (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL CHECK (length(trim(name)) > 0),
                description TEXT,
                is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );

            CREATE UNIQUE INDEX ux_postprocessors_name
            ON postprocessors (name COLLATE NOCASE);

            CREATE TABLE machine_supported_postprocessors (
                machine_id TEXT NOT NULL,
                postprocessor_id TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                PRIMARY KEY (machine_id, postprocessor_id),
                FOREIGN KEY (machine_id) REFERENCES machines (id) ON DELETE RESTRICT,
                FOREIGN KEY (postprocessor_id) REFERENCES postprocessors (id) ON DELETE RESTRICT
            );

            CREATE INDEX ix_machine_supported_postprocessors_postprocessor
            ON machine_supported_postprocessors (postprocessor_id, machine_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
