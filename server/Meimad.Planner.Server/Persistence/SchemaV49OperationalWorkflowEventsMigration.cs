using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Removes the legacy persistent CNC mode-variable projection and introduces the
/// append-only Production Run operational workflow event stream.
/// </summary>
internal sealed class SchemaV49OperationalWorkflowEventsMigration : IDatabaseMigration
{
    public int Version => 49;
    public string Name => "operational_workflow_events_remove_cnc_mode_variable";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE production_run_workflow_events (
                id TEXT PRIMARY KEY,
                production_run_id TEXT NOT NULL,
                machine_id TEXT NOT NULL,
                event_type TEXT NOT NULL CHECK (event_type IN (
                    'OFFSET_LOADER_COMPLETED',
                    'SETUP_VERIFICATION_REQUESTED',
                    'SETUP_VERIFICATION_SUCCEEDED',
                    'SETUP_VERIFICATION_FAILED',
                    'SEND_TO_QC',
                    'QC_PASS',
                    'QC_FAIL',
                    'CYCLE_START',
                    'CYCLE_END',
                    'CYCLE_INTERRUPTED',
                    'PRODUCTION_SESSION_OPENED',
                    'PRODUCTION_SESSION_CLOSED')),
                source TEXT NOT NULL,
                source_event_id TEXT,
                source_sequence INTEGER,
                server_received_at TEXT NOT NULL,
                machine_timestamp TEXT,
                nc_release_id TEXT,
                offset_loader_release_id TEXT,
                tablet_device_id TEXT,
                user_id TEXT,
                metadata_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(metadata_json)),
                FOREIGN KEY (production_run_id) REFERENCES production_runs(id) ON DELETE RESTRICT,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                FOREIGN KEY (nc_release_id) REFERENCES gcode_releases(id) ON DELETE RESTRICT,
                FOREIGN KEY (tablet_device_id) REFERENCES device_registry(id) ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX ux_production_run_workflow_event_source
                ON production_run_workflow_events(source, source_event_id)
                WHERE source_event_id IS NOT NULL;
            CREATE INDEX ix_production_run_workflow_events_run_time
                ON production_run_workflow_events(production_run_id, server_received_at, id);
            CREATE INDEX ix_production_run_workflow_events_machine_time
                ON production_run_workflow_events(machine_id, server_received_at, id);
            CREATE TRIGGER production_run_workflow_events_immutable_update
            BEFORE UPDATE ON production_run_workflow_events
            BEGIN SELECT RAISE(ABORT, 'Production Run workflow events are immutable'); END;
            CREATE TRIGGER production_run_workflow_events_immutable_delete
            BEFORE DELETE ON production_run_workflow_events
            BEGIN SELECT RAISE(ABORT, 'Production Run workflow events are immutable'); END;

            INSERT INTO production_run_workflow_events (
                id, production_run_id, machine_id, event_type, source,
                source_event_id, server_received_at, machine_timestamp,
                tablet_device_id, metadata_json)
            SELECT id, production_run_id, machine_id, event_type, 'TABLET',
                   id, created_at, occurred_at, device_id,
                   json_object('migratedFrom', 'tablet_workflow_events')
            FROM tablet_workflow_events;
            DROP TABLE tablet_workflow_events;

            -- This table existed only to authorize/reset the removed persistent
            -- Setup/Production variable. Protected verification variables use the
            -- operational event model instead.
            DROP TABLE IF EXISTS haas_macro_write_audits;

            CREATE TABLE haas_connection_settings_v49 (
                machine_id TEXT PRIMARY KEY,
                host TEXT NOT NULL CHECK (length(trim(host)) > 0),
                mdc_port INTEGER NOT NULL CHECK (mdc_port BETWEEN 1 AND 65535),
                mtconnect_port INTEGER NOT NULL CHECK (mtconnect_port BETWEEN 1 AND 65535),
                dprnt_port INTEGER NOT NULL DEFAULT 8080 CHECK (dprnt_port BETWEEN 1 AND 65535),
                local_net_share_enabled INTEGER NOT NULL CHECK (local_net_share_enabled IN (0, 1)),
                local_net_share_path TEXT,
                credentials_reference TEXT,
                part_counter_source TEXT NOT NULL CHECK (part_counter_source IN ('Q500', 'M30_COUNTER_1', 'M30_COUNTER_2')),
                polling_interval_ms INTEGER NOT NULL CHECK (polling_interval_ms BETWEEN 500 AND 60000),
                connection_timeout_ms INTEGER NOT NULL CHECK (connection_timeout_ms BETWEEN 250 AND 60000),
                stable_program_polls INTEGER NOT NULL CHECK (stable_program_polls BETWEEN 1 AND 10),
                header_line_limit INTEGER NOT NULL CHECK (header_line_limit BETWEEN 1 AND 200),
                header_byte_limit INTEGER NOT NULL CHECK (header_byte_limit BETWEEN 1024 AND 262144),
                header_part_patterns_json TEXT NOT NULL CHECK (json_valid(header_part_patterns_json)),
                enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
                version INTEGER NOT NULL CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE CASCADE
            );
            INSERT INTO haas_connection_settings_v49 (
                machine_id, host, mdc_port, mtconnect_port, dprnt_port,
                local_net_share_enabled, local_net_share_path, credentials_reference,
                part_counter_source, polling_interval_ms, connection_timeout_ms,
                stable_program_polls, header_line_limit, header_byte_limit,
                header_part_patterns_json, enabled, version, created_at, updated_at)
            SELECT machine_id, host, mdc_port, mtconnect_port, dprnt_port,
                   local_net_share_enabled, local_net_share_path, credentials_reference,
                   part_counter_source, polling_interval_ms, connection_timeout_ms,
                   stable_program_polls, header_line_limit, header_byte_limit,
                   header_part_patterns_json, enabled, version, created_at, updated_at
            FROM haas_connection_settings;
            DROP TABLE haas_connection_settings;
            ALTER TABLE haas_connection_settings_v49 RENAME TO haas_connection_settings;

            CREATE TABLE haas_machine_snapshots_v49 (
                machine_id TEXT PRIMARY KEY,
                observed_at TEXT NOT NULL,
                connectivity_state TEXT NOT NULL CHECK (connectivity_state IN ('ONLINE', 'OFFLINE', 'ERROR')),
                machine_status TEXT,
                program_number TEXT,
                machine_header_part_name TEXT,
                machine_header_source_path TEXT,
                header_read_at TEXT,
                part_counter INTEGER CHECK (part_counter IS NULL OR part_counter >= 0),
                raw_mdc_status TEXT,
                last_error TEXT,
                last_seen_at TEXT,
                version INTEGER NOT NULL CHECK (version > 0),
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE CASCADE
            );
            INSERT INTO haas_machine_snapshots_v49 (
                machine_id, observed_at, connectivity_state, machine_status,
                program_number, machine_header_part_name, machine_header_source_path,
                header_read_at, part_counter, raw_mdc_status, last_error,
                last_seen_at, version)
            SELECT machine_id, observed_at, connectivity_state, machine_status,
                   program_number, machine_header_part_name, machine_header_source_path,
                   header_read_at, part_counter, raw_mdc_status, last_error,
                   last_seen_at, version
            FROM haas_machine_snapshots;
            DROP TABLE haas_machine_snapshots;
            ALTER TABLE haas_machine_snapshots_v49 RENAME TO haas_machine_snapshots;

            CREATE TABLE machine_current_state_v49 (
                machine_id TEXT PRIMARY KEY,
                connection_id TEXT NOT NULL,
                adapter_type TEXT NOT NULL,
                observed_at TEXT NOT NULL,
                connection_status TEXT NOT NULL,
                last_seen_at TEXT,
                machine_state TEXT,
                program_number TEXT,
                program_number_read_at TEXT,
                part_name TEXT,
                part_name_read_at TEXT,
                part_name_stale INTEGER NOT NULL DEFAULT 0 CHECK (part_name_stale IN (0, 1)),
                header_source_path TEXT,
                header_read_at TEXT,
                part_counter INTEGER,
                part_counter_read_at TEXT,
                spindle_running INTEGER,
                spindle_rpm REAL,
                feed_rate REAL,
                active_alarm_count INTEGER,
                component_health_json TEXT NOT NULL CHECK (json_valid(component_health_json)),
                capability_health_json TEXT NOT NULL CHECK (json_valid(capability_health_json)),
                snapshot_json TEXT NOT NULL CHECK (json_valid(snapshot_json)),
                last_error TEXT,
                version INTEGER NOT NULL CHECK (version > 0),
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE CASCADE,
                FOREIGN KEY (connection_id) REFERENCES machine_connections(id) ON DELETE CASCADE
            );
            INSERT INTO machine_current_state_v49 (
                machine_id, connection_id, adapter_type, observed_at, connection_status,
                last_seen_at, machine_state, program_number, program_number_read_at,
                part_name, part_name_read_at, part_name_stale, header_source_path,
                header_read_at, part_counter, part_counter_read_at, spindle_running,
                spindle_rpm, feed_rate, active_alarm_count, component_health_json,
                capability_health_json, snapshot_json, last_error, version)
            SELECT machine_id, connection_id, adapter_type, observed_at, connection_status,
                   last_seen_at, machine_state, program_number, program_number_read_at,
                   part_name, part_name_read_at, part_name_stale, header_source_path,
                   header_read_at, part_counter, part_counter_read_at, spindle_running,
                   spindle_rpm, feed_rate, active_alarm_count, component_health_json,
                   capability_health_json,
                   json_remove(snapshot_json, '$.production'), last_error, version
            FROM machine_current_state;
            DROP TABLE machine_current_state;
            ALTER TABLE machine_current_state_v49 RENAME TO machine_current_state;

            UPDATE machine_connections
            SET configuration_json = json_remove(
                    configuration_json,
                    '$.production.variableNumber',
                    '$.production.legacyVariableAlias')
            WHERE adapter_type = 'HAAS_NGC';
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
