using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV28KitaronConnectionMigration : IDatabaseMigration
{
    public int Version => 28;

    public string Name => "kitaron_server_connection";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE kitaron_connection_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                server_host TEXT NOT NULL,
                server_port INTEGER NOT NULL CHECK (server_port BETWEEN 1 AND 65535),
                database_name TEXT NOT NULL,
                view_schema TEXT NOT NULL,
                view_name TEXT NOT NULL,
                username TEXT NOT NULL,
                protected_password TEXT,
                enabled INTEGER NOT NULL DEFAULT 0 CHECK (enabled IN (0, 1)),
                refresh_interval_seconds INTEGER NOT NULL DEFAULT 300
                    CHECK (refresh_interval_seconds BETWEEN 30 AND 86400),
                last_test_status TEXT NOT NULL DEFAULT 'not_tested'
                    CHECK (last_test_status IN ('not_tested', 'testing', 'succeeded', 'failed')),
                last_test_at TEXT,
                last_test_message TEXT,
                last_test_column_count INTEGER CHECK (last_test_column_count IS NULL OR last_test_column_count >= 0),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );

            INSERT INTO kitaron_connection_settings (
                id, server_host, server_port, database_name, view_schema,
                view_name, username, enabled, refresh_interval_seconds)
            VALUES (
                1, '192.168.0.240', 1433, 'KitaronData229', 'dbo',
                'VQWorkPlanningForStationF4', 'kit', 0, 300);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
