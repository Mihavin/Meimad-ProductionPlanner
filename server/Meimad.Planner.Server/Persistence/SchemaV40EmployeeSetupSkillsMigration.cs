using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV40EmployeeSetupSkillsMigration : IDatabaseMigration
{
    public int Version => 40;
    public string Name => "employee_setup_skills";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE employee_resources ADD COLUMN tool_load_seconds_per_tool REAL NOT NULL DEFAULT 60;
            ALTER TABLE employee_resources ADD COLUMN fixture_assembly_seconds REAL;
            ALTER TABLE employee_resources ADD COLUMN first_part_running_speed_percent REAL NOT NULL DEFAULT 66.6666666667;
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
