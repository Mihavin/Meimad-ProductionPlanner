using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV47ProductionRunExecutionMigration : IDatabaseMigration
{
    public int Version => 47;
    public string Name => "production_run_execution_events";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE production_run_cycle_events (
                id TEXT PRIMARY KEY,
                production_run_id TEXT NOT NULL,
                production_run_program_id TEXT NOT NULL,
                source TEXT NOT NULL CHECK(length(trim(source)) > 0),
                source_event_id TEXT NOT NULL CHECK(length(trim(source_event_id)) > 0),
                observed_at TEXT NOT NULL,
                completed_cycle_count INTEGER NOT NULL CHECK(completed_cycle_count > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(source, source_event_id),
                FOREIGN KEY(production_run_id) REFERENCES production_runs(id) ON DELETE RESTRICT,
                FOREIGN KEY(production_run_program_id) REFERENCES production_run_programs(id) ON DELETE RESTRICT
            );
            CREATE INDEX ix_production_run_cycle_events_program
            ON production_run_cycle_events(production_run_program_id, completed_cycle_count);
        """;
        await command.ExecuteNonQueryAsync(token);
    }
}
