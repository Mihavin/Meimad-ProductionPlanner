using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>Stores the explicit measured/inferred end projection for a closed Production Run session.</summary>
internal sealed class SchemaV57ProductionSessionClosureMigration : IDatabaseMigration
{
    public int Version => 57;
    public string Name => "production_session_closure";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE production_run_session_closures (
                id TEXT PRIMARY KEY,
                production_run_id TEXT NOT NULL UNIQUE,
                machine_id TEXT NOT NULL,
                triggering_production_run_id TEXT NOT NULL,
                triggering_workflow_event_id TEXT NOT NULL,
                closure_workflow_event_id TEXT NOT NULL UNIQUE,
                observed_end_at TEXT,
                effective_end_at TEXT,
                end_time_inferred INTEGER NOT NULL CHECK(end_time_inferred IN (0,1)),
                inference_basis_json TEXT NOT NULL DEFAULT '{}'
                    CHECK(json_valid(inference_basis_json) AND json_type(inference_basis_json)='object'),
                closed_at TEXT NOT NULL,
                CHECK(end_time_inferred=0 OR (observed_end_at IS NULL AND effective_end_at IS NOT NULL)),
                CHECK(observed_end_at IS NULL OR (effective_end_at=observed_end_at AND end_time_inferred=0)),
                FOREIGN KEY(production_run_id) REFERENCES production_runs(id) ON DELETE RESTRICT,
                FOREIGN KEY(machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                FOREIGN KEY(triggering_production_run_id) REFERENCES production_runs(id) ON DELETE RESTRICT,
                FOREIGN KEY(triggering_workflow_event_id) REFERENCES production_run_workflow_events(id) ON DELETE RESTRICT,
                FOREIGN KEY(closure_workflow_event_id) REFERENCES production_run_workflow_events(id) ON DELETE RESTRICT
            );
            CREATE INDEX ix_production_run_session_closures_machine_time
                ON production_run_session_closures(machine_id,closed_at DESC,id);
            CREATE TRIGGER production_run_session_closures_immutable_update
            BEFORE UPDATE ON production_run_session_closures
            BEGIN SELECT RAISE(ABORT,'Production session closures are immutable'); END;
            CREATE TRIGGER production_run_session_closures_immutable_delete
            BEFORE DELETE ON production_run_session_closures
            BEGIN SELECT RAISE(ABORT,'Production session closures are immutable'); END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
