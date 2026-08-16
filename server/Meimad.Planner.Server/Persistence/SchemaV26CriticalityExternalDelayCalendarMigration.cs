using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV26CriticalityExternalDelayCalendarMigration : IDatabaseMigration
{
    public int Version => 26;
    public string Name => "criticality_external_delay_master_calendar";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        foreach (var table in new[] { "case_operations", "batch_operations" })
        {
            await AddColumnAsync(connection, transaction, table, "has_external_delay", "INTEGER NOT NULL DEFAULT 0 CHECK (has_external_delay IN (0, 1))", cancellationToken);
            await AddColumnAsync(connection, transaction, table, "external_delay_description", "TEXT NULL", cancellationToken);
            await AddColumnAsync(connection, transaction, table, "external_delay_duration", "REAL NOT NULL DEFAULT 0 CHECK (external_delay_duration >= 0)", cancellationToken);
            await AddColumnAsync(connection, transaction, table, "external_delay_duration_unit", "TEXT NOT NULL DEFAULT 'hours' CHECK (external_delay_duration_unit IN ('hours', 'days', 'working_days'))", cancellationToken);
            await AddColumnAsync(connection, transaction, table, "external_delay_calendar_id", "TEXT NULL REFERENCES working_calendars(id) ON DELETE RESTRICT", cancellationToken);
            await AddColumnAsync(connection, transaction, table, "external_delay_respect_master_calendar", "INTEGER NOT NULL DEFAULT 1 CHECK (external_delay_respect_master_calendar IN (0, 1))", cancellationToken);
        }

        await AddColumnAsync(connection, transaction, "machines", "respect_master_calendar", "INTEGER NOT NULL DEFAULT 1 CHECK (respect_master_calendar IN (0, 1))", cancellationToken);
        await AddColumnAsync(connection, transaction, "employee_resources", "respect_master_calendar", "INTEGER NOT NULL DEFAULT 1 CHECK (respect_master_calendar IN (0, 1))", cancellationToken);
        await ExecuteAsync(connection, transaction, "INSERT OR IGNORE INTO application_settings (key, value) VALUES ('master_calendar_id', '');", cancellationToken);
    }

    private static async Task AddColumnAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, string column, string declaration, CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.Transaction = transaction;
        inspect.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        await reader.DisposeAsync();
        await ExecuteAsync(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {column} {declaration};", cancellationToken);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
