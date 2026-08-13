using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV13EmployeeCalendarExceptionsMigration : IDatabaseMigration
{
    public int Version => 13;
    public string Name => "employee_calendar_exceptions";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE employee_calendar_exceptions (
                id TEXT PRIMARY KEY,
                resource_id TEXT NOT NULL REFERENCES employee_resources(id) ON DELETE CASCADE,
                exception_date TEXT NOT NULL,
                exception_type TEXT NOT NULL CHECK (exception_type IN ('vacation', 'sick_day', 'personal_day', 'unavailable', 'custom_note')),
                is_full_day INTEGER NOT NULL CHECK (is_full_day IN (0, 1)),
                starts_at_local TEXT,
                ends_at_local TEXT,
                note TEXT,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                CHECK (
                    (is_full_day = 1 AND starts_at_local IS NULL AND ends_at_local IS NULL)
                    OR
                    (is_full_day = 0 AND starts_at_local IS NOT NULL AND ends_at_local IS NOT NULL)
                )
            );

            CREATE INDEX ix_employee_calendar_exceptions_resource_date
            ON employee_calendar_exceptions (resource_id, exception_date, id);
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
