using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// The v46 trigger machine_assignment_run_required_update unconditionally aborts any UPDATE that
/// sets machine_assignments.production_run_id to NULL. That is correct for ordinary mutations, but
/// it also blocks the v69 soft-release UPDATE, which must clear production_run_id on the released
/// row so a later re-assignment of the same Batch Operation (auto re-linked to its permanent,
/// legacy_batch_operation_id-unique Production Run by the v46 AFTER INSERT trigger) does not
/// collide with UNIQUE(production_run_id) on the still-present released row. Narrow the guard so it
/// only blocks nulling production_run_id when the row is NOT simultaneously being released.
/// </summary>
internal sealed class SchemaV70MachineAssignmentReleaseAllowsRunClearMigration : IDatabaseMigration
{
    public int Version => 70;
    public string Name => "machine_assignment_release_allows_run_clear";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DROP TRIGGER machine_assignment_run_required_update;

            CREATE TRIGGER machine_assignment_run_required_update
            BEFORE UPDATE OF production_run_id ON machine_assignments
            WHEN NEW.production_run_id IS NULL AND NEW.released_at IS NULL
            BEGIN SELECT RAISE(ABORT, 'Machine Assignment requires a Production Run'); END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
