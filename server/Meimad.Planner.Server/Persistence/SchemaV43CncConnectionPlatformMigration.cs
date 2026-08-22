using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV43CncConnectionPlatformMigration : IDatabaseMigration
{
    public int Version => 43;
    public string Name => "cnc_connection_platform";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE machine_connections (
                id TEXT PRIMARY KEY,
                machine_id TEXT NOT NULL,
                adapter_type TEXT NOT NULL CHECK (adapter_type IN ('HAAS_NGC', 'MTCONNECT', 'OPCUA', 'CUSTOM')),
                enabled INTEGER NOT NULL DEFAULT 0 CHECK (enabled IN (0, 1)),
                connection_status TEXT NOT NULL DEFAULT 'DISABLED'
                    CHECK (connection_status IN ('DISABLED', 'CONNECTING', 'ONLINE', 'DEGRADED', 'OFFLINE', 'ERROR')),
                last_connection_attempt_at TEXT,
                last_connected_at TEXT,
                last_disconnected_at TEXT,
                last_successful_poll_at TEXT,
                polling_interval_ms INTEGER NOT NULL CHECK (polling_interval_ms BETWEEN 500 AND 60000),
                connection_timeout_ms INTEGER NOT NULL CHECK (connection_timeout_ms BETWEEN 250 AND 60000),
                maximum_reconnect_backoff_ms INTEGER NOT NULL DEFAULT 30000
                    CHECK (maximum_reconnect_backoff_ms BETWEEN 1000 AND 300000),
                allow_read INTEGER NOT NULL DEFAULT 1 CHECK (allow_read IN (0, 1)),
                allow_write INTEGER NOT NULL DEFAULT 0 CHECK (allow_write IN (0, 1)),
                configuration_json TEXT NOT NULL CHECK (json_valid(configuration_json)),
                username_secret_id TEXT,
                password_secret_id TEXT,
                raw_telemetry_retention_days INTEGER NOT NULL DEFAULT 14
                    CHECK (raw_telemetry_retention_days BETWEEN 1 AND 90),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX ux_machine_connections_primary_machine ON machine_connections(machine_id);

            CREATE TABLE machine_current_state (
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
                production_mode TEXT,
                production_variable_number INTEGER,
                production_variable_value INTEGER,
                production_variable_read_at TEXT,
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
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE CASCADE,
                FOREIGN KEY (connection_id) REFERENCES machine_connections(id) ON DELETE CASCADE
            );

            CREATE TABLE machine_state_history (
                id TEXT PRIMARY KEY,
                machine_id TEXT NOT NULL,
                connection_id TEXT NOT NULL,
                observed_at TEXT NOT NULL,
                change_kind TEXT NOT NULL,
                snapshot_json TEXT NOT NULL CHECK (json_valid(snapshot_json)),
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                FOREIGN KEY (connection_id) REFERENCES machine_connections(id) ON DELETE RESTRICT
            );
            CREATE INDEX ix_machine_state_history_machine_time
                ON machine_state_history(machine_id, observed_at DESC);

            CREATE TABLE machine_connection_events (
                id TEXT PRIMARY KEY,
                connection_id TEXT NOT NULL,
                machine_id TEXT NOT NULL,
                event_type TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                detail_json TEXT NOT NULL CHECK (json_valid(detail_json)),
                FOREIGN KEY (connection_id) REFERENCES machine_connections(id) ON DELETE RESTRICT,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE RESTRICT
            );
            CREATE INDEX ix_machine_connection_events_machine_time
                ON machine_connection_events(machine_id, occurred_at DESC);

            CREATE TABLE machine_telemetry_raw (
                id TEXT PRIMARY KEY,
                connection_id TEXT NOT NULL,
                machine_id TEXT NOT NULL,
                adapter_type TEXT NOT NULL,
                observed_at TEXT NOT NULL,
                operation TEXT NOT NULL,
                raw_payload TEXT NOT NULL CHECK (length(raw_payload) <= 65536),
                FOREIGN KEY (connection_id) REFERENCES machine_connections(id) ON DELETE CASCADE,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE CASCADE
            );
            CREATE INDEX ix_machine_telemetry_raw_machine_time
                ON machine_telemetry_raw(machine_id, observed_at DESC);

            INSERT INTO machine_connections (
                id, machine_id, adapter_type, enabled, connection_status,
                polling_interval_ms, connection_timeout_ms, maximum_reconnect_backoff_ms,
                allow_read, allow_write, configuration_json, username_secret_id,
                raw_telemetry_retention_days, version, created_at, updated_at)
            SELECT 'cnc-' || machine_id, machine_id, 'HAAS_NGC', enabled,
                   CASE WHEN enabled = 1 THEN 'OFFLINE' ELSE 'DISABLED' END,
                   polling_interval_ms, connection_timeout_ms, 30000,
                   1, 1,
                   json_object(
                       'host', host,
                       'mdc', json_object('port', mdc_port, 'timeoutMs', connection_timeout_ms),
                       'programAccess', json_object(
                           'provider', CASE WHEN local_net_share_enabled = 1 THEN 'HAAS_LOCAL_NET_SHARE' ELSE 'NONE' END,
                           'enabled', CASE WHEN local_net_share_enabled = 1 THEN json('true') ELSE json('false') END,
                           'sharePath', local_net_share_path,
                           'usernameSecretId', credentials_reference,
                           'passwordSecretId', NULL,
                           'headerLineLimit', header_line_limit,
                           'headerByteLimit', header_byte_limit,
                           'headerPartPatterns', json(header_part_patterns_json)),
                       'production', json_object(
                           'variableNumber', production_mode_variable,
                           'legacyVariableAlias', legacy_variable_alias,
                           'partCounterSource', part_counter_source),
                       'monitoring', json_object(
                           'pollingIntervalMs', polling_interval_ms,
                           'stableProgramPolls', stable_program_polls,
                           'maximumReconnectBackoffMs', 30000,
                           'rawTelemetryRetentionDays', 14)),
                   credentials_reference, 14, version, created_at, updated_at
            FROM haas_connection_settings;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
