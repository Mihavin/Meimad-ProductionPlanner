using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>Introduces immutable Offset Loader releases, sequence anomalies, and Machine-scoped verification configuration.</summary>
internal sealed class SchemaV50CncVerificationFoundationMigration : IDatabaseMigration
{
    public int Version => 50;
    public string Name => "cnc_verification_foundation";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE offset_loader_releases (
                id TEXT PRIMARY KEY,
                production_run_id TEXT NOT NULL,
                machine_id TEXT NOT NULL,
                nc_release_id TEXT NOT NULL,
                tool_table_release_id TEXT NOT NULL,
                verification_release_token INTEGER NOT NULL CHECK (verification_release_token BETWEEN 1 AND 999999999),
                artifact_hash TEXT CHECK (artifact_hash IS NULL OR length(artifact_hash) = 64),
                created_at TEXT NOT NULL,
                created_by TEXT NOT NULL CHECK (length(trim(created_by)) > 0),
                metadata_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(metadata_json) AND json_type(metadata_json) = 'object'),
                UNIQUE (machine_id, verification_release_token),
                FOREIGN KEY (production_run_id) REFERENCES production_runs(id) ON DELETE RESTRICT,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                FOREIGN KEY (nc_release_id) REFERENCES gcode_releases(id) ON DELETE RESTRICT,
                FOREIGN KEY (tool_table_release_id) REFERENCES tool_table_releases(id) ON DELETE RESTRICT
            );
            CREATE INDEX ix_offset_loader_releases_run_time
                ON offset_loader_releases(production_run_id, created_at DESC, id);
            CREATE TRIGGER offset_loader_releases_immutable_update
            BEFORE UPDATE ON offset_loader_releases
            BEGIN SELECT RAISE(ABORT, 'Offset Loader releases are immutable'); END;
            CREATE TRIGGER offset_loader_releases_immutable_delete
            BEFORE DELETE ON offset_loader_releases
            BEGIN SELECT RAISE(ABORT, 'Offset Loader releases are immutable'); END;

            CREATE TABLE production_run_current_offset_loaders (
                production_run_id TEXT PRIMARY KEY,
                machine_id TEXT NOT NULL,
                offset_loader_release_id TEXT NOT NULL UNIQUE,
                selected_at TEXT NOT NULL,
                selected_by TEXT NOT NULL CHECK (length(trim(selected_by)) > 0),
                version INTEGER NOT NULL CHECK (version > 0),
                FOREIGN KEY (production_run_id) REFERENCES production_runs(id) ON DELETE RESTRICT,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                FOREIGN KEY (offset_loader_release_id) REFERENCES offset_loader_releases(id) ON DELETE RESTRICT
            );
            CREATE TRIGGER production_run_current_offset_loader_consistent_insert
            BEFORE INSERT ON production_run_current_offset_loaders
            WHEN NOT EXISTS (
                SELECT 1 FROM offset_loader_releases release
                WHERE release.id = NEW.offset_loader_release_id
                  AND release.production_run_id = NEW.production_run_id
                  AND release.machine_id = NEW.machine_id)
            BEGIN SELECT RAISE(ABORT, 'Current Offset Loader context is inconsistent'); END;
            CREATE TRIGGER production_run_current_offset_loader_consistent_update
            BEFORE UPDATE ON production_run_current_offset_loaders
            WHEN NOT EXISTS (
                SELECT 1 FROM offset_loader_releases release
                WHERE release.id = NEW.offset_loader_release_id
                  AND release.production_run_id = NEW.production_run_id
                  AND release.machine_id = NEW.machine_id)
            BEGIN SELECT RAISE(ABORT, 'Current Offset Loader context is inconsistent'); END;

            CREATE TABLE cnc_verification_settings (
                machine_id TEXT PRIMARY KEY,
                dprint_transport TEXT NOT NULL CHECK (dprint_transport IN ('HAAS_DPRNT_TCP')),
                dprint_port INTEGER NOT NULL CHECK (dprint_port BETWEEN 1 AND 65535),
                challenge_program_number INTEGER NOT NULL CHECK (challenge_program_number BETWEEN 9000 AND 9999),
                verify_program_number INTEGER NOT NULL CHECK (verify_program_number BETWEEN 9000 AND 9999),
                custom_gcode_alias INTEGER CHECK (custom_gcode_alias IS NULL OR custom_gcode_alias BETWEEN 1 AND 999),
                nonce_variable INTEGER NOT NULL CHECK (nonce_variable BETWEEN 1 AND 10999),
                response_variable INTEGER NOT NULL CHECK (response_variable BETWEEN 1 AND 10999),
                verification_state_variable INTEGER NOT NULL CHECK (verification_state_variable BETWEEN 1 AND 10999),
                release_token_variable INTEGER NOT NULL CHECK (release_token_variable BETWEEN 1 AND 10999),
                protected_secret TEXT NOT NULL CHECK (length(protected_secret) > 0),
                expected_macro_version INTEGER NOT NULL CHECK (expected_macro_version > 0),
                response_code_digits INTEGER NOT NULL CHECK (response_code_digits BETWEEN 4 AND 6),
                verification_timeout_seconds INTEGER NOT NULL CHECK (verification_timeout_seconds BETWEEN 30 AND 3600),
                enabled INTEGER NOT NULL CHECK (enabled IN (0,1)),
                version INTEGER NOT NULL CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE CASCADE,
                CHECK (challenge_program_number <> verify_program_number),
                CHECK (nonce_variable <> response_variable
                    AND nonce_variable <> verification_state_variable
                    AND nonce_variable <> release_token_variable
                    AND response_variable <> verification_state_variable
                    AND response_variable <> release_token_variable
                    AND verification_state_variable <> release_token_variable)
            );

            CREATE TABLE production_run_workflow_anomalies (
                id TEXT PRIMARY KEY,
                production_run_id TEXT NOT NULL,
                machine_id TEXT NOT NULL,
                source TEXT NOT NULL,
                source_event_id TEXT NOT NULL,
                anomaly_type TEXT NOT NULL CHECK (anomaly_type IN ('EVENT_SEQUENCE_GAP','EVENT_SEQUENCE_OUT_OF_ORDER')),
                previous_sequence INTEGER NOT NULL,
                expected_sequence INTEGER NOT NULL,
                received_sequence INTEGER NOT NULL,
                workflow_event_id TEXT NOT NULL,
                detected_at TEXT NOT NULL,
                details_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(details_json) AND json_type(details_json) = 'object'),
                UNIQUE (source, source_event_id, anomaly_type),
                FOREIGN KEY (production_run_id) REFERENCES production_runs(id) ON DELETE RESTRICT,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                FOREIGN KEY (workflow_event_id) REFERENCES production_run_workflow_events(id) ON DELETE RESTRICT
            );
            CREATE INDEX ix_production_run_workflow_anomalies_machine_time
                ON production_run_workflow_anomalies(machine_id, detected_at DESC, id);
            CREATE TRIGGER production_run_workflow_anomalies_immutable_update
            BEFORE UPDATE ON production_run_workflow_anomalies
            BEGIN SELECT RAISE(ABORT, 'Workflow anomalies are immutable'); END;
            CREATE TRIGGER production_run_workflow_anomalies_immutable_delete
            BEFORE DELETE ON production_run_workflow_anomalies
            BEGIN SELECT RAISE(ABORT, 'Workflow anomalies are immutable'); END;
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
