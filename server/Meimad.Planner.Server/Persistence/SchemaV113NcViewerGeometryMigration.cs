using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// NC viewer geometry. <c>machines.nc_viewer_models_json</c> lists the STL meshes the NC viewer draws
/// for a Machine, each attached to a kinematic node of the Machine's NC viewer machine definition and
/// placed in that node's frame (rotation about X, then Y, then Z in degrees, R = Rz·Ry·Rx, then the
/// millimetre offset). <c>case_operation_cam_setups</c> holds one Operation's CAM setup: the Work
/// coordinate system (the NC program zero, the Cimatron UCS of the NC procedures) expressed in the
/// part-model coordinates the Operation's fixture and rest-material files were exported in, so a
/// model point p maps into Work coordinates as Rᵀ(p − origin). The setup is planner-entered CAM
/// context: it is not a planning input, so its triggers advance the all-input revision and the
/// non-planning counter together and leave the planning revision unchanged.
/// </summary>
internal sealed class SchemaV113NcViewerGeometryMigration : IDatabaseMigration
{
    public int Version => 113;
    public string Name => "nc_viewer_geometry";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE machines ADD COLUMN nc_viewer_models_json TEXT NOT NULL DEFAULT '[]'
                CHECK (json_valid(nc_viewer_models_json) AND json_type(nc_viewer_models_json) = 'array');

            CREATE TABLE case_operation_cam_setups (
                case_operation_id TEXT NOT NULL PRIMARY KEY
                    REFERENCES case_operations (id) ON DELETE CASCADE,
                source TEXT NOT NULL CHECK (source IN ('cimatron', 'manual')),
                work_offset TEXT NULL CHECK (work_offset IS NULL OR length(work_offset) BETWEEN 1 AND 16),
                origin_x_mm REAL NOT NULL DEFAULT 0,
                origin_y_mm REAL NOT NULL DEFAULT 0,
                origin_z_mm REAL NOT NULL DEFAULT 0,
                rotation_x_deg REAL NOT NULL DEFAULT 0 CHECK (rotation_x_deg BETWEEN -360 AND 360),
                rotation_y_deg REAL NOT NULL DEFAULT 0 CHECK (rotation_y_deg BETWEEN -360 AND 360),
                rotation_z_deg REAL NOT NULL DEFAULT 0 CHECK (rotation_z_deg BETWEEN -360 AND 360),
                notes TEXT NULL CHECK (notes IS NULL OR length(notes) <= 500),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                updated_by TEXT
            );
            """;
        await command.ExecuteNonQueryAsync(token);
        foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            command.CommandText = $"""
                CREATE TRIGGER projection_revision_case_operation_cam_setups_{action.ToLowerInvariant()}
                AFTER {action} ON case_operation_cam_setups
                BEGIN UPDATE projection_revision
                      SET revision = revision + 1, non_planning_revision = non_planning_revision + 1
                      WHERE id = 1; END;
                """;
            await command.ExecuteNonQueryAsync(token);
        }
    }
}
