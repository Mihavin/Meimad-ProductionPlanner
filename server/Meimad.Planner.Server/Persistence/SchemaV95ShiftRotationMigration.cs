using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// 12-hour shift rotation (owner decisions 2026-09-29). A rotation Working Calendar keeps its shifts,
/// pattern and crews in <c>calendar_json</c>, so the calendar table is unchanged. An employee on such
/// a Calendar records the crew whose pattern day they follow, and the Shift Roster keeps the dated
/// shift an employee starts when the planner decides it rather than the pattern.
/// </summary>
internal sealed class SchemaV95ShiftRotationMigration : IDatabaseMigration
{
    public int Version => 95;

    public string Name => "shift_rotation";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE employee_resources ADD COLUMN shift_crew_code TEXT;

            CREATE TABLE employee_shift_roster_entries (
                id TEXT PRIMARY KEY,
                resource_id TEXT NOT NULL REFERENCES employee_resources(id) ON DELETE CASCADE,
                roster_date TEXT NOT NULL,
                shift_code TEXT NOT NULL CHECK (length(shift_code) BETWEEN 1 AND 20),
                note TEXT,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                updated_by TEXT,
                UNIQUE (resource_id, roster_date)
            );
            CREATE INDEX ix_employee_shift_roster_entries_date
            ON employee_shift_roster_entries (roster_date, resource_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
