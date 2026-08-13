using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV12EmployeeResourceDetailsMigration : IDatabaseMigration
{
    public int Version => 12;
    public string Name => "employee_resource_details";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE employee_resources ADD COLUMN first_name TEXT NOT NULL DEFAULT '';
            ALTER TABLE employee_resources ADD COLUMN last_name TEXT NOT NULL DEFAULT '';
            ALTER TABLE employee_resources ADD COLUMN skills_json TEXT NOT NULL DEFAULT '[]';
            ALTER TABLE employee_resources ADD COLUMN assigned_calendar_id TEXT REFERENCES working_calendars(id) ON DELETE RESTRICT;
            ALTER TABLE employee_resources ADD COLUMN photo_path TEXT;
            ALTER TABLE employee_resources ADD COLUMN notes TEXT;

            UPDATE employee_resources
            SET first_name = CASE
                    WHEN instr(trim(name), ' ') > 0 THEN substr(trim(name), 1, instr(trim(name), ' ') - 1)
                    ELSE trim(name)
                END,
                last_name = CASE
                    WHEN instr(trim(name), ' ') > 0 THEN ltrim(substr(trim(name), instr(trim(name), ' ') + 1))
                    ELSE ''
                END,
                resource_type = CASE lower(trim(resource_type))
                    WHEN 'setup' THEN 'setup_worker'
                    WHEN 'setup_worker' THEN 'setup_worker'
                    WHEN 'qa' THEN 'qa_worker'
                    WHEN 'qa_worker' THEN 'qa_worker'
                    ELSE 'regular_worker'
                END;
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
