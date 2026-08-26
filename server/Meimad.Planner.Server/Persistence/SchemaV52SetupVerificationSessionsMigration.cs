using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>Stores one-time setup-verification sessions without treating CNC variables as workflow state.</summary>
internal sealed class SchemaV52SetupVerificationSessionsMigration : IDatabaseMigration
{
    public int Version => 52;
    public string Name => "setup_verification_sessions";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE cnc_setup_verification_sessions (
                id TEXT PRIMARY KEY,
                production_run_id TEXT NOT NULL,
                machine_id TEXT NOT NULL,
                nc_release_id TEXT NOT NULL,
                offset_loader_release_id TEXT NOT NULL,
                nonce INTEGER NOT NULL CHECK (nonce BETWEEN 100000 AND 999999),
                macro_version INTEGER NOT NULL CHECK (macro_version > 0),
                response_code_digits INTEGER NOT NULL CHECK (response_code_digits BETWEEN 4 AND 6),
                state TEXT NOT NULL CHECK (state IN ('PENDING','SUCCEEDED','FAILED','EXPIRED','SUPERSEDED')),
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                resolved_at TEXT,
                source_workflow_event_id TEXT NOT NULL UNIQUE,
                resolution_workflow_event_id TEXT UNIQUE,
                FOREIGN KEY (production_run_id) REFERENCES production_runs(id) ON DELETE RESTRICT,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                FOREIGN KEY (nc_release_id) REFERENCES gcode_releases(id) ON DELETE RESTRICT,
                FOREIGN KEY (offset_loader_release_id) REFERENCES offset_loader_releases(id) ON DELETE RESTRICT,
                FOREIGN KEY (source_workflow_event_id) REFERENCES production_run_workflow_events(id) ON DELETE RESTRICT,
                FOREIGN KEY (resolution_workflow_event_id) REFERENCES production_run_workflow_events(id) ON DELETE RESTRICT,
                CHECK (expires_at > created_at),
                CHECK ((state = 'PENDING' AND resolved_at IS NULL AND resolution_workflow_event_id IS NULL)
                    OR (state <> 'PENDING' AND resolved_at IS NOT NULL))
            );
            CREATE INDEX ix_cnc_setup_verification_sessions_context
                ON cnc_setup_verification_sessions(machine_id, production_run_id, created_at DESC, id);
            CREATE UNIQUE INDEX ux_cnc_setup_verification_sessions_live_machine
                ON cnc_setup_verification_sessions(machine_id)
                WHERE state IN ('PENDING','SUCCEEDED');
            CREATE TRIGGER cnc_setup_verification_sessions_context_immutable
            BEFORE UPDATE ON cnc_setup_verification_sessions
            WHEN NEW.id <> OLD.id
              OR NEW.production_run_id <> OLD.production_run_id
              OR NEW.machine_id <> OLD.machine_id
              OR NEW.nc_release_id <> OLD.nc_release_id
              OR NEW.offset_loader_release_id <> OLD.offset_loader_release_id
              OR NEW.nonce <> OLD.nonce
              OR NEW.macro_version <> OLD.macro_version
              OR NEW.response_code_digits <> OLD.response_code_digits
              OR NEW.created_at <> OLD.created_at
              OR NEW.expires_at <> OLD.expires_at
              OR NEW.source_workflow_event_id <> OLD.source_workflow_event_id
            BEGIN SELECT RAISE(ABORT, 'Setup verification session context is immutable'); END;
            CREATE TRIGGER cnc_setup_verification_sessions_no_delete
            BEFORE DELETE ON cnc_setup_verification_sessions
            BEGIN SELECT RAISE(ABORT, 'Setup verification sessions cannot be deleted'); END;
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
