using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class FanucFocasAdapterTypeMigrationTests
{
    [Fact]
    public async Task Version_75_rebuilds_machine_connections_without_losing_dependent_rows_and_admits_FANUC_FOCAS()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE machines (id TEXT PRIMARY KEY);
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
                    FOREIGN KEY (connection_id) REFERENCES machine_connections(id) ON DELETE CASCADE
                );
                INSERT INTO machines VALUES ('machine-a');
                INSERT INTO machine_connections
                    (id, machine_id, adapter_type, enabled, connection_status, polling_interval_ms,
                     connection_timeout_ms, configuration_json, version, created_at, updated_at)
                VALUES ('cnc-machine-a', 'machine-a', 'HAAS_NGC', 1, 'ONLINE', 2000, 3000,
                        '{"host":"192.0.2.1"}', 3, '2026-09-17T00:00:00Z', '2026-09-17T00:00:00Z');
                INSERT INTO machine_current_state VALUES ('machine-a', 'cnc-machine-a');
                """;
            await setup.ExecuteNonQueryAsync();
        }

        // Mirrors DatabaseMigrator: enforcement off around the transaction, integrity checked before commit.
        await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;");
        await using (var transaction = connection.BeginTransaction())
        {
            await new SchemaV75FanucFocasAdapterTypeMigration().ApplyAsync(connection, transaction, default);
            await using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = "PRAGMA foreign_key_check;";
            Assert.Null(await check.ExecuteScalarAsync());
            await transaction.CommitAsync();
        }
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;");

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM machine_current_state;"));
        Assert.Equal("HAAS_NGC", await ScalarAsync(connection, "SELECT adapter_type FROM machine_connections WHERE id = 'cnc-machine-a';"));
        Assert.Equal(3L, await ScalarAsync(connection, "SELECT version FROM machine_connections WHERE id = 'cnc-machine-a';"));
        var childSql = (string)(await ScalarAsync(connection, "SELECT sql FROM sqlite_master WHERE name = 'machine_current_state';"))!;
        Assert.Contains("REFERENCES machine_connections(id)", childSql, StringComparison.Ordinal);
        Assert.Equal(1L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ux_machine_connections_primary_machine';"));

        await ExecuteAsync(connection, """
            INSERT INTO machines VALUES ('machine-b');
            INSERT INTO machine_connections
                (id, machine_id, adapter_type, enabled, connection_status, polling_interval_ms,
                 connection_timeout_ms, configuration_json, version, created_at, updated_at)
            VALUES ('cnc-machine-b', 'machine-b', 'FANUC_FOCAS', 0, 'DISABLED', 2000, 3000,
                    '{"host":"192.0.2.2"}', 1, '2026-09-17T00:00:00Z', '2026-09-17T00:00:00Z');
            """);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO machines VALUES ('machine-c');
            INSERT INTO machine_connections
                (id, machine_id, adapter_type, enabled, connection_status, polling_interval_ms,
                 connection_timeout_ms, configuration_json, version, created_at, updated_at)
            VALUES ('cnc-machine-c', 'machine-c', 'BOGUS', 0, 'DISABLED', 2000, 3000,
                    '{}', 1, '2026-09-17T00:00:00Z', '2026-09-17T00:00:00Z');
            """));

        // The rebuilt table is again the parent of the dependent rows.
        await ExecuteAsync(connection, "DELETE FROM machine_connections WHERE id = 'cnc-machine-a';");
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM machine_current_state;"));
    }

    [Fact]
    public async Task Fresh_database_accepts_a_FANUC_FOCAS_connection_and_keeps_foreign_keys_enforced()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using var connection = await fixture.Database.OpenConnectionAsync();

        Assert.Equal(1L, await ScalarAsync(connection, "PRAGMA foreign_keys;"));
        await ExecuteAsync(connection, """
            INSERT INTO working_calendars (id, name, time_zone_id) VALUES ('calendar-f', 'Fanuc', 'UTC');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES ('machine-f', 'M-F', 'FANUC 0i-F', 'mill', 'calendar-f', 'active', 1);
            INSERT INTO machine_connections
                (id, machine_id, adapter_type, enabled, connection_status, polling_interval_ms,
                 connection_timeout_ms, configuration_json, version, created_at, updated_at)
            VALUES ('cnc-machine-f', 'machine-f', 'FANUC_FOCAS', 0, 'DISABLED', 2000, 3000,
                    '{"host":"192.0.2.3"}', 1, '2026-09-17T00:00:00Z', '2026-09-17T00:00:00Z');
            """);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO machine_connections
                (id, machine_id, adapter_type, enabled, connection_status, polling_interval_ms,
                 connection_timeout_ms, configuration_json, version, created_at, updated_at)
            VALUES ('cnc-orphan', 'missing-machine', 'FANUC_FOCAS', 0, 'DISABLED', 2000, 3000,
                    '{}', 1, '2026-09-17T00:00:00Z', '2026-09-17T00:00:00Z');
            """));
        Assert.Equal("fanuc_focas_adapter_type",
            await ScalarAsync(connection, "SELECT name FROM schema_migrations WHERE version = 75;"));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
