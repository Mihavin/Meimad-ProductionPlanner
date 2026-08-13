using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV11AdministrativeSetupMigration : IDatabaseMigration
{
    public int Version => 11;
    public string Name => "administrative_setup";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE employee_resources (
                id TEXT PRIMARY KEY,
                employee_number TEXT NOT NULL,
                name TEXT NOT NULL,
                resource_type TEXT NOT NULL,
                email TEXT,
                is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );

            CREATE UNIQUE INDEX ux_employee_resources_number_nocase
            ON employee_resources (employee_number COLLATE NOCASE);

            CREATE TABLE israeli_holidays (
                id TEXT PRIMARY KEY,
                holiday_date TEXT NOT NULL,
                name TEXT NOT NULL,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );

            CREATE UNIQUE INDEX ux_israeli_holidays_date
            ON israeli_holidays (holiday_date);

            CREATE TABLE report_email_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                sender_address TEXT,
                recipients_json TEXT NOT NULL DEFAULT '[]',
                smtp_host TEXT,
                smtp_port INTEGER,
                use_ssl INTEGER NOT NULL DEFAULT 1 CHECK (use_ssl IN (0, 1)),
                daily_report_enabled INTEGER NOT NULL DEFAULT 0 CHECK (daily_report_enabled IN (0, 1)),
                daily_report_time_local TEXT,
                time_zone_id TEXT,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );

            INSERT INTO report_email_settings (id) VALUES (1);
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
