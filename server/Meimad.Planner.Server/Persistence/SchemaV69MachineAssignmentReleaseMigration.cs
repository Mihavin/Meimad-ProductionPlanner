using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Adds a soft-release marker to machine_assignments. production_packages.machine_assignment_id
/// is a NOT NULL, ON DELETE RESTRICT foreign key, and production_packages has hard immutability
/// triggers blocking any UPDATE/DELETE on it — so once a Production Package has ever been built
/// against an assignment, that assignment row can never be hard-deleted again. Unassign/cancel
/// paths now set released_at instead of deleting; every "current assignment/backlog" query must
/// filter released_at IS NULL. On release, backlog_position is also bumped to a rowid-based
/// offset and production_run_id cleared to NULL so the existing UNIQUE(machine_id,
/// backlog_position) and UNIQUE(production_run_id) constraints are never violated by a released
/// row colliding with a live one — deliberately avoiding a full table rebuild for this.
/// </summary>
internal sealed class SchemaV69MachineAssignmentReleaseMigration : IDatabaseMigration
{
    public int Version => 69;
    public string Name => "machine_assignment_soft_release";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE machine_assignments ADD COLUMN released_at TEXT;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
