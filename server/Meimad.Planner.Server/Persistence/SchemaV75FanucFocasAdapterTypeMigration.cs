using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Admits the FANUC_FOCAS adapter type. SQLite cannot alter a CHECK constraint, so the
/// primary connection table is rebuilt in place while the migrator keeps foreign-key
/// enforcement off: dependent state/history/telemetry rows keep referencing
/// <c>machine_connections</c> by name and survive the rebuild.
/// </summary>
internal sealed class SchemaV75FanucFocasAdapterTypeMigration : IDatabaseMigration
{
    public int Version => 75;
    public string Name => "fanuc_focas_adapter_type";
    public bool DisablesForeignKeyEnforcement => true;

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE machine_connections_v75 (
                id TEXT PRIMARY KEY,
                machine_id TEXT NOT NULL,
                adapter_type TEXT NOT NULL CHECK (adapter_type IN ('HAAS_NGC', 'FANUC_FOCAS', 'MTCONNECT', 'OPCUA', 'CUSTOM')),
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
            INSERT INTO machine_connections_v75 (
                id, machine_id, adapter_type, enabled, connection_status,
                last_connection_attempt_at, last_connected_at, last_disconnected_at, last_successful_poll_at,
                polling_interval_ms, connection_timeout_ms, maximum_reconnect_backoff_ms,
                allow_read, allow_write, configuration_json, username_secret_id, password_secret_id,
                raw_telemetry_retention_days, version, created_at, updated_at)
            SELECT id, machine_id, adapter_type, enabled, connection_status,
                last_connection_attempt_at, last_connected_at, last_disconnected_at, last_successful_poll_at,
                polling_interval_ms, connection_timeout_ms, maximum_reconnect_backoff_ms,
                allow_read, allow_write, configuration_json, username_secret_id, password_secret_id,
                raw_telemetry_retention_days, version, created_at, updated_at
            FROM machine_connections;
            DROP TABLE machine_connections;
            ALTER TABLE machine_connections_v75 RENAME TO machine_connections;
            CREATE UNIQUE INDEX ux_machine_connections_primary_machine ON machine_connections(machine_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
