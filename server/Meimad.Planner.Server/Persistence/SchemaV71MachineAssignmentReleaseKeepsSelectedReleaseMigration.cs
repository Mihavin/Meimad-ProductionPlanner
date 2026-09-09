using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// The v46 trigger machine_assignment_selected_release_matches_operation_update aborts any UPDATE of
/// selected_gcode_release_id or production_run_id whose selected G-code release does not belong to
/// the assignment's Production Run. That guard is correct for ordinary mutations, but the v69
/// soft-release UPDATE clears production_run_id on the released row while keeping its selected
/// release as history, so a row with a selected release could never be released: cancelling a
/// Production Batch or Production Run, or unassigning an operation, failed with
/// "selected G-code release must belong to the assigned Production Run". v70 narrowed the sibling
/// machine_assignment_run_required_update trigger the same way; this migration narrows the
/// selected-release guard so it only applies to rows that are NOT being released.
/// </summary>
internal sealed class SchemaV71MachineAssignmentReleaseKeepsSelectedReleaseMigration : IDatabaseMigration
{
    public int Version => 71;
    public string Name => "machine_assignment_release_keeps_selected_release";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DROP TRIGGER machine_assignment_selected_release_matches_operation_update;

            CREATE TRIGGER machine_assignment_selected_release_matches_operation_update
            BEFORE UPDATE OF selected_gcode_release_id, production_run_id ON machine_assignments
            FOR EACH ROW
            WHEN NEW.released_at IS NULL
             AND NEW.selected_gcode_release_id IS NOT NULL
             AND NOT EXISTS (
                 SELECT 1 FROM gcode_releases release
                 JOIN production_run_programs program
                   ON program.production_run_id = NEW.production_run_id
                  AND program.process_revision_id = release.process_revision_id
                 WHERE release.id = NEW.selected_gcode_release_id)
            BEGIN
                SELECT RAISE(ABORT, 'selected G-code release must belong to the assigned Production Run');
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
