using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV42HaasNgcIntegrationMigration : IDatabaseMigration
{
    public int Version => 42;
    public string Name => "haas_ngc_integration";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE nc_program_headers (
                gcode_release_id TEXT PRIMARY KEY,
                status TEXT NOT NULL CHECK (status IN ('VALID', 'HEADER_INVALID')),
                part_name TEXT,
                case_number TEXT,
                operation TEXT,
                revision TEXT,
                program_number TEXT,
                raw_header TEXT NOT NULL,
                parser_version TEXT NOT NULL,
                parsed_at TEXT NOT NULL,
                CHECK ((status = 'VALID' AND length(trim(part_name)) > 0)
                    OR (status = 'HEADER_INVALID' AND part_name IS NULL)),
                FOREIGN KEY (gcode_release_id) REFERENCES gcode_releases(id) ON DELETE RESTRICT
            );

            CREATE TABLE haas_connection_settings (
                machine_id TEXT PRIMARY KEY,
                host TEXT NOT NULL CHECK (length(trim(host)) > 0),
                mdc_port INTEGER NOT NULL DEFAULT 5051 CHECK (mdc_port BETWEEN 1 AND 65535),
                mtconnect_port INTEGER NOT NULL DEFAULT 8082 CHECK (mtconnect_port BETWEEN 1 AND 65535),
                local_net_share_enabled INTEGER NOT NULL DEFAULT 0 CHECK (local_net_share_enabled IN (0, 1)),
                local_net_share_path TEXT,
                credentials_reference TEXT,
                production_mode_variable INTEGER NOT NULL DEFAULT 10605 CHECK (production_mode_variable BETWEEN 10000 AND 10999),
                legacy_variable_alias INTEGER NOT NULL DEFAULT 605 CHECK (legacy_variable_alias BETWEEN 600 AND 699),
                part_counter_source TEXT NOT NULL DEFAULT 'Q500' CHECK (part_counter_source IN ('Q500', 'M30_COUNTER_1', 'M30_COUNTER_2')),
                polling_interval_ms INTEGER NOT NULL DEFAULT 2000 CHECK (polling_interval_ms BETWEEN 500 AND 60000),
                connection_timeout_ms INTEGER NOT NULL DEFAULT 3000 CHECK (connection_timeout_ms BETWEEN 250 AND 60000),
                stable_program_polls INTEGER NOT NULL DEFAULT 2 CHECK (stable_program_polls BETWEEN 1 AND 10),
                header_line_limit INTEGER NOT NULL DEFAULT 50 CHECK (header_line_limit BETWEEN 1 AND 200),
                header_byte_limit INTEGER NOT NULL DEFAULT 32768 CHECK (header_byte_limit BETWEEN 1024 AND 262144),
                header_part_patterns_json TEXT NOT NULL DEFAULT '["PART\\s*[:=]\\s*([^()]+)"]' CHECK (json_valid(header_part_patterns_json)),
                enabled INTEGER NOT NULL DEFAULT 0 CHECK (enabled IN (0, 1)),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE CASCADE
            );

            CREATE TABLE haas_machine_snapshots (
                machine_id TEXT PRIMARY KEY,
                observed_at TEXT NOT NULL,
                connectivity_state TEXT NOT NULL CHECK (connectivity_state IN ('ONLINE', 'OFFLINE', 'ERROR')),
                machine_status TEXT,
                program_number TEXT,
                machine_header_part_name TEXT,
                machine_header_source_path TEXT,
                header_read_at TEXT,
                production_variable_number INTEGER NOT NULL,
                production_variable_value INTEGER NOT NULL CHECK (production_variable_value IN (0, 1)),
                production_variable_changed_at TEXT,
                part_counter INTEGER CHECK (part_counter IS NULL OR part_counter >= 0),
                raw_mdc_status TEXT,
                last_error TEXT,
                last_seen_at TEXT,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE CASCADE
            );

            CREATE TABLE haas_bench_sessions (
                id TEXT PRIMARY KEY,
                batch_operation_id TEXT NOT NULL,
                machine_id TEXT NOT NULL,
                state TEXT NOT NULL CHECK (state IN ('WAITING', 'SETUP', 'PRODUCTION', 'COMPLETED')),
                auto_start_source TEXT NOT NULL CHECK (auto_start_source = 'CNC_HEADER'),
                machine_program_number TEXT NOT NULL,
                machine_part_name TEXT NOT NULL,
                setup_started_at TEXT NOT NULL,
                setup_ended_at TEXT,
                production_started_at TEXT,
                part_counting_enabled INTEGER NOT NULL DEFAULT 0 CHECK (part_counting_enabled IN (0, 1)),
                part_counter_baseline INTEGER,
                previous_part_counter INTEGER,
                produced_quantity INTEGER NOT NULL DEFAULT 0 CHECK (produced_quantity >= 0),
                completed_at TEXT,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (batch_operation_id) REFERENCES batch_operations(id) ON DELETE RESTRICT,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX ux_haas_bench_sessions_active_machine
                ON haas_bench_sessions(machine_id)
                WHERE state IN ('SETUP', 'PRODUCTION');

            CREATE TABLE haas_bench_state_intervals (
                id TEXT PRIMARY KEY,
                bench_id TEXT NOT NULL,
                state TEXT NOT NULL CHECK (state IN ('SETUP', 'PRODUCTION')),
                started_at TEXT NOT NULL,
                ended_at TEXT,
                source TEXT NOT NULL,
                FOREIGN KEY (bench_id) REFERENCES haas_bench_sessions(id) ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX ux_haas_bench_intervals_open
                ON haas_bench_state_intervals(bench_id) WHERE ended_at IS NULL;

            CREATE TABLE haas_events (
                id TEXT PRIMARY KEY,
                event_type TEXT NOT NULL,
                machine_id TEXT NOT NULL,
                bench_id TEXT,
                occurred_at TEXT NOT NULL,
                payload_json TEXT NOT NULL CHECK (json_valid(payload_json)),
                dedupe_key TEXT NOT NULL UNIQUE,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                FOREIGN KEY (bench_id) REFERENCES haas_bench_sessions(id) ON DELETE RESTRICT
            );
            CREATE INDEX ix_haas_events_machine_time ON haas_events(machine_id, occurred_at DESC);

            CREATE TABLE haas_macro_write_audits (
                id TEXT PRIMARY KEY,
                machine_id TEXT NOT NULL,
                bench_id TEXT,
                tool_table_id TEXT,
                variable_number INTEGER NOT NULL,
                old_value INTEGER,
                new_value INTEGER NOT NULL,
                reason TEXT NOT NULL,
                initiated_by TEXT NOT NULL,
                requested_at TEXT NOT NULL,
                completed_at TEXT,
                status TEXT NOT NULL CHECK (status IN ('PENDING', 'SUCCEEDED', 'FAILED')),
                raw_haas_response TEXT,
                error_message TEXT,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                FOREIGN KEY (bench_id) REFERENCES haas_bench_sessions(id) ON DELETE RESTRICT
            );

            CREATE TRIGGER nc_program_headers_immutable_update BEFORE UPDATE ON nc_program_headers
            BEGIN SELECT RAISE(ABORT, 'NC header metadata is immutable'); END;
            CREATE TRIGGER nc_program_headers_immutable_delete BEFORE DELETE ON nc_program_headers
            BEGIN SELECT RAISE(ABORT, 'NC header metadata is immutable'); END;
            CREATE TRIGGER haas_events_immutable_update BEFORE UPDATE ON haas_events
            BEGIN SELECT RAISE(ABORT, 'Haas events are immutable'); END;
            CREATE TRIGGER haas_events_immutable_delete BEFORE DELETE ON haas_events
            BEGIN SELECT RAISE(ABORT, 'Haas events are immutable'); END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
