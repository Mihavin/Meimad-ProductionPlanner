using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV24MachineAssignmentPlanningModeMigration : IDatabaseMigration
{
    public int Version => 24;

    public string Name => "machine_assignment_planning_mode";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE machine_assignments
            ADD COLUMN planning_mode TEXT NOT NULL DEFAULT 'manual'
                CHECK (planning_mode IN ('forward', 'backward', 'manual'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
