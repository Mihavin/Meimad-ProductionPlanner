using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// The work offsets of a Case Operation in the NC viewer (owner decision 2026-10-13: one source for the toolpath and the
/// Cimatron geometry): <c>case_operation_viewer_work_offsets</c> keeps, per Case Operation and NC viewer machine (a
/// published Machine definition's engine id or an installed machine type), the absolute machine coordinates of each work
/// offset as the control would hold them (<c>offsets_json</c>: <c>{"G54": {"X": -300, "Z": -450.5}, ...}</c>, mm and
/// degrees). Every user's viewer places the program zero with these values; the Operation's Cimatron Work CS
/// (<c>case_operation_cam_setups</c>) still relates the geometry to that work offset. Measured offsets stay in
/// <c>machine_work_offsets</c> as evidence. Not a planning input: the triggers advance the all-input and non-planning
/// revisions only.
/// </summary>
internal sealed class SchemaV120OperationViewerWorkOffsetsMigration : IDatabaseMigration
{
    public int Version => 120;
    public string Name => "case_operation_viewer_work_offsets";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE case_operation_viewer_work_offsets (
                case_operation_id TEXT NOT NULL REFERENCES case_operations (id) ON DELETE CASCADE,
                viewer_machine TEXT NOT NULL CHECK (length(viewer_machine) BETWEEN 1 AND 100),
                offsets_json TEXT NOT NULL CHECK (json_valid(offsets_json) AND json_type(offsets_json) = 'object'),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                updated_by TEXT NULL,
                PRIMARY KEY (case_operation_id, viewer_machine)
            );
            """;
        await command.ExecuteNonQueryAsync(token);
        foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            command.CommandText = $"""
                CREATE TRIGGER projection_revision_case_operation_viewer_work_offsets_{action.ToLowerInvariant()}
                AFTER {action} ON case_operation_viewer_work_offsets
                BEGIN UPDATE projection_revision
                      SET revision = revision + 1, non_planning_revision = non_planning_revision + 1
                      WHERE id = 1; END;
                """;
            await command.ExecuteNonQueryAsync(token);
        }
    }
}
