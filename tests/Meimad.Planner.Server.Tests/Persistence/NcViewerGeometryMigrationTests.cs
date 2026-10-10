using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class NcViewerGeometryMigrationTests
{
    [Fact]
    public async Task Version_113_gives_existing_machines_no_models_and_adds_the_operation_cam_setup_table()
    {
        await using var fixture = TemporaryDatabase.CreateUnmigrated();
        var migrator = new DatabaseMigrator(fixture.Database, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync(112);
        await using (var v112 = await fixture.Database.OpenConnectionAsync())
        {
            await ExecuteAsync(v112, """
                INSERT INTO working_calendars (id, name, time_zone_id) VALUES ('calendar-g', 'Geometry', 'UTC');
                INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
                VALUES ('machine-old', '10', 'Existing', 'mill', 'calendar-g', 'active', 1);
                INSERT INTO cases (id, part_number, name, working_folder_path) VALUES ('case-g', 'PN-G', 'Part', 'C:\Cases\PN-G');
                INSERT INTO case_operations (id, case_id, operation_number, route_position, name)
                VALUES ('op-g', 'case-g', 10, 0, 'Mill'), ('op-h', 'case-g', 20, 1, 'Mill 2');
                """);
        }
        SqliteConnection.ClearAllPools();

        await migrator.MigrateAsync();

        await using var connection = await fixture.Database.OpenConnectionAsync();
        Assert.Equal("nc_viewer_geometry", await ScalarAsync(connection, "SELECT name FROM schema_migrations WHERE version = 113;"));
        Assert.Equal("[]", await ScalarAsync(connection, "SELECT nc_viewer_models_json FROM machines WHERE id = 'machine-old';"));
        // The column holds a JSON array only.
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "UPDATE machines SET nc_viewer_models_json = '{}' WHERE id = 'machine-old';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "UPDATE machines SET nc_viewer_models_json = 'not json' WHERE id = 'machine-old';"));

        var columns = new List<string>();
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "SELECT name FROM pragma_table_info('case_operation_cam_setups') ORDER BY cid;";
            await using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        }
        Assert.Equal(
            ["case_operation_id", "source", "work_offset", "origin_x_mm", "origin_y_mm", "origin_z_mm",
             "rotation_x_deg", "rotation_y_deg", "rotation_z_deg", "notes", "version", "created_at", "updated_at", "updated_by"],
            columns);

        // A CAM setup is not a planning input: the planning revision stays, the all-input revision moves.
        var planningBefore = await ScalarAsync(connection, "SELECT revision - non_planning_revision FROM projection_revision WHERE id = 1;");
        var allBefore = (long)(await ScalarAsync(connection, "SELECT revision FROM projection_revision WHERE id = 1;"))!;
        await ExecuteAsync(connection, """
            INSERT INTO case_operation_cam_setups (case_operation_id, source, work_offset, origin_x_mm, rotation_z_deg,
                version, created_at, updated_at, updated_by)
            VALUES ('op-g', 'cimatron', 'G54', 12.5, 90, 1, '2026-10-10T00:00:00Z', '2026-10-10T00:00:00Z', 'planner');
            INSERT INTO case_operation_cam_setups (case_operation_id, source, version, created_at, updated_at)
            VALUES ('op-h', 'manual', 1, '2026-10-10T00:00:00Z', '2026-10-10T00:00:00Z');
            """);
        Assert.Equal(planningBefore, await ScalarAsync(connection, "SELECT revision - non_planning_revision FROM projection_revision WHERE id = 1;"));
        Assert.Equal(allBefore + 2, (long)(await ScalarAsync(connection, "SELECT revision FROM projection_revision WHERE id = 1;"))!);
        Assert.Equal(0d, await ScalarAsync(connection, "SELECT origin_y_mm FROM case_operation_cam_setups WHERE case_operation_id = 'op-h';"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "UPDATE case_operation_cam_setups SET source = 'mastercam' WHERE case_operation_id = 'op-g';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "UPDATE case_operation_cam_setups SET rotation_x_deg = 361 WHERE case_operation_id = 'op-g';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "INSERT INTO case_operation_cam_setups (case_operation_id, source, version, created_at, updated_at) VALUES ('no-such-op', 'manual', 1, 'x', 'x');"));

        // The setup belongs to its Operation and goes with it.
        await ExecuteAsync(connection, "DELETE FROM case_operations WHERE id = 'op-h';");
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM case_operation_cam_setups;"));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
