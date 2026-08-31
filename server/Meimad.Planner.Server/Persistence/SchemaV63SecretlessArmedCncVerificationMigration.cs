using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Removes the obsolete Machine verification secret and makes Offset Loader completion
/// arm, rather than start, the bounded operator-response window.
/// </summary>
internal sealed class SchemaV63SecretlessArmedCncVerificationMigration : IDatabaseMigration
{
    public int Version => 63;
    public string Name => "secretless_armed_cnc_verification";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DROP TRIGGER IF EXISTS operational_anomaly_from_expired_verification;
            DROP TRIGGER IF EXISTS cnc_setup_verification_sessions_context_immutable;
            DROP TRIGGER IF EXISTS cnc_setup_verification_sessions_no_delete;

            ALTER TABLE cnc_setup_verification_sessions
                RENAME TO cnc_setup_verification_sessions_v62;

            CREATE TABLE cnc_setup_verification_sessions (
                id TEXT PRIMARY KEY,
                production_run_id TEXT NOT NULL,
                machine_id TEXT NOT NULL,
                nc_release_id TEXT NOT NULL,
                offset_loader_release_id TEXT NOT NULL,
                nonce INTEGER NOT NULL CHECK (nonce BETWEEN 100000 AND 999999),
                macro_version INTEGER NOT NULL CHECK (macro_version > 0),
                response_code_digits INTEGER NOT NULL CHECK (response_code_digits BETWEEN 4 AND 6),
                state TEXT NOT NULL CHECK (state IN (
                    'ARMED','PENDING','SUCCEEDED','FAILED','EXPIRED','SUPERSEDED')),
                created_at TEXT NOT NULL,
                pending_started_at TEXT,
                expires_at TEXT,
                resolved_at TEXT,
                source_workflow_event_id TEXT NOT NULL UNIQUE,
                pending_workflow_event_id TEXT UNIQUE,
                resolution_workflow_event_id TEXT UNIQUE,
                FOREIGN KEY (production_run_id) REFERENCES production_runs(id) ON DELETE RESTRICT,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                FOREIGN KEY (nc_release_id) REFERENCES gcode_releases(id) ON DELETE RESTRICT,
                FOREIGN KEY (offset_loader_release_id) REFERENCES offset_loader_releases(id) ON DELETE RESTRICT,
                FOREIGN KEY (source_workflow_event_id) REFERENCES production_run_workflow_events(id) ON DELETE RESTRICT,
                FOREIGN KEY (pending_workflow_event_id) REFERENCES production_run_workflow_events(id) ON DELETE RESTRICT,
                FOREIGN KEY (resolution_workflow_event_id) REFERENCES production_run_workflow_events(id) ON DELETE RESTRICT,
                CHECK ((pending_started_at IS NULL AND expires_at IS NULL)
                    OR (pending_started_at IS NOT NULL AND expires_at > pending_started_at)),
                CHECK (
                    (state='ARMED' AND pending_started_at IS NULL AND expires_at IS NULL
                        AND resolved_at IS NULL AND pending_workflow_event_id IS NULL
                        AND resolution_workflow_event_id IS NULL)
                    OR (state='PENDING' AND pending_started_at IS NOT NULL AND expires_at IS NOT NULL
                        AND resolved_at IS NULL AND resolution_workflow_event_id IS NULL)
                    OR (state IN('SUCCEEDED','FAILED') AND pending_started_at IS NOT NULL
                        AND expires_at IS NOT NULL AND resolved_at IS NOT NULL
                        AND resolution_workflow_event_id IS NOT NULL)
                    OR (state='EXPIRED' AND pending_started_at IS NOT NULL
                        AND expires_at IS NOT NULL AND resolved_at IS NOT NULL
                        AND resolution_workflow_event_id IS NULL)
                    OR (state='SUPERSEDED' AND resolved_at IS NOT NULL
                        AND resolution_workflow_event_id IS NULL))
            );

            INSERT INTO cnc_setup_verification_sessions(
                id,production_run_id,machine_id,nc_release_id,offset_loader_release_id,
                nonce,macro_version,response_code_digits,state,created_at,
                pending_started_at,expires_at,resolved_at,source_workflow_event_id,
                pending_workflow_event_id,resolution_workflow_event_id)
            SELECT id,production_run_id,machine_id,nc_release_id,offset_loader_release_id,
                   nonce,macro_version,response_code_digits,state,created_at,
                   created_at,expires_at,resolved_at,source_workflow_event_id,
                   NULL,CASE WHEN state='SUPERSEDED' THEN NULL
                             ELSE resolution_workflow_event_id END
            FROM cnc_setup_verification_sessions_v62;

            DROP TABLE cnc_setup_verification_sessions_v62;

            CREATE INDEX ix_cnc_setup_verification_sessions_context
                ON cnc_setup_verification_sessions(machine_id,production_run_id,created_at DESC,id);
            CREATE UNIQUE INDEX ux_cnc_setup_verification_sessions_live_machine
                ON cnc_setup_verification_sessions(machine_id)
                WHERE state IN('ARMED','PENDING','SUCCEEDED');

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
              OR NEW.source_workflow_event_id <> OLD.source_workflow_event_id
            BEGIN SELECT RAISE(ABORT, 'Setup verification session context is immutable'); END;

            CREATE TRIGGER cnc_setup_verification_sessions_transition_guard
            BEFORE UPDATE ON cnc_setup_verification_sessions
            WHEN NOT (
                (OLD.state='ARMED' AND NEW.state IN('PENDING','SUPERSEDED'))
                OR (OLD.state='PENDING' AND NEW.state IN('SUCCEEDED','FAILED','EXPIRED','SUPERSEDED'))
                OR (OLD.state='SUCCEEDED' AND NEW.state='SUPERSEDED')
                OR (OLD.state='EXPIRED' AND NEW.state='FAILED'))
            BEGIN SELECT RAISE(ABORT, 'Invalid setup verification session transition'); END;

            CREATE TRIGGER cnc_setup_verification_sessions_no_delete
            BEFORE DELETE ON cnc_setup_verification_sessions
            BEGIN SELECT RAISE(ABORT, 'Setup verification sessions cannot be deleted'); END;

            CREATE TRIGGER operational_anomaly_from_expired_verification
            AFTER UPDATE OF state ON cnc_setup_verification_sessions
            WHEN OLD.state <> 'EXPIRED' AND NEW.state='EXPIRED'
            BEGIN
                INSERT OR IGNORE INTO operational_anomalies(
                    id,anomaly_type,machine_id,production_run_id,source,source_event_id,
                    workflow_event_id,detected_at,details_json,dedupe_key)
                VALUES(
                    'verification-expired:' || NEW.id,'verification_expired',
                    NEW.machine_id,NEW.production_run_id,'SETUP_VERIFICATION_SESSION',NEW.id,
                    NEW.source_workflow_event_id,NEW.resolved_at,'{}',
                    'verification-expired:' || NEW.id);
            END;

            ALTER TABLE cnc_verification_settings DROP COLUMN protected_secret;

            -- Machine identity is the Server MachineID plus its configured fixed IP and
            -- physical MAC. Existing rows cannot be guessed safely, so Haas connections
            -- are disabled until an operator records the MAC and confirms the fixed IP.
            ALTER TABLE haas_connection_settings ADD COLUMN mac_address TEXT;
            UPDATE haas_connection_settings SET enabled=0 WHERE enabled=1;
            CREATE UNIQUE INDEX ux_haas_connection_settings_mac
            ON haas_connection_settings(mac_address)
            WHERE mac_address IS NOT NULL;
            CREATE UNIQUE INDEX ux_haas_connection_settings_enabled_host
            ON haas_connection_settings(host)
            WHERE enabled=1;
            UPDATE machine_connections
            SET enabled=0,connection_status='DISABLED'
            WHERE adapter_type='HAAS_NGC';
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
