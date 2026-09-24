using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class ToolPreparationMigrationTests
{
    [Fact]
    public async Task Version_79_adds_immutable_tool_preparations_and_the_machine_diameter_offset_kind()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await ExecuteAsync(connection, """
            INSERT INTO working_calendars (id, name, time_zone_id) VALUES ('calendar-t', 'Tools', 'UTC');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES ('machine-t', '10', 'Mill', 'mill', 'calendar-t', 'active', 1);
            INSERT INTO cases (id, part_number, name, working_folder_path) VALUES ('case-t', 'PN-T', 'Part', 'C:\\Cases\\PN-T');
            INSERT INTO case_operations (id, case_id, operation_number, route_position, name, required_machine_type, setup_seconds, cycle_seconds)
            VALUES ('operation-t', 'case-t', 10, 0, 'Mill', 'mill', 60, 60);
            INSERT INTO tool_table_releases (id, case_operation_id, revision_number, original_file_name, stored_relative_path, file_size, file_hash,
                released_at, released_by, release_comment, created_at, updated_at, required_tool_count)
            VALUES ('tools-t', 'operation-t', 1, 'tools.csv', 'tools/tools.csv', 20, lower(hex(zeroblob(32))), '2026-09-01T08:00:00Z', 'tool-user', 'Initial',
                '2026-09-01T08:00:00Z', '2026-09-01T08:00:00Z', 0);
            INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity) VALUES ('batch-t', 'case-t', 'B-T', 'waiting', 5);
            INSERT INTO batch_operations (id, production_batch_id, source_case_operation_id, operation_number, route_position, name, required_machine_type, setup_seconds, cycle_seconds, status)
            VALUES ('batch-operation-t', 'batch-t', 'operation-t', 10, 0, 'Mill', 'mill', 60, 60, 'not_started');
            """);

        Assert.Equal("tool_preparation", await ScalarAsync(connection, "SELECT name FROM schema_migrations WHERE version = 79;"));
        Assert.Equal("RADIUS", await ScalarAsync(connection, "SELECT tool_diameter_offset_kind FROM machines WHERE id = 'machine-t';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "UPDATE machines SET tool_diameter_offset_kind = 'INCHES' WHERE id = 'machine-t';"));
        await ExecuteAsync(connection, "UPDATE machines SET tool_diameter_offset_kind = 'DIAMETER' WHERE id = 'machine-t';");

        await ExecuteAsync(connection, """
            INSERT INTO tool_preparations (id, batch_operation_id, machine_id, tool_table_release_id, version_number, saved_at, saved_by, content_hash)
            VALUES ('preparation-1', 'batch-operation-t', 'machine-t', 'tools-t', 1, '2026-09-02T08:00:00Z', 'tool-room', lower(hex(zeroblob(32))));
            INSERT INTO tool_preparation_tools (id, tool_preparation_id, row_number, tool_identifier, offset_number, measured_length, measured_diameter, shape_type, shape_json)
            VALUES ('tool-1', 'preparation-1', 1, 'T1', 1, 120.5, 10, 'END_MILL', '{"cuttingDiameter":10}');
            INSERT INTO tool_preparation_components (id, tool_preparation_tool_id, sequence, component_type, name, length, diameter)
            VALUES ('component-1', 'tool-1', 1, 'HOLDER', 'BT40 ER32', 60, 63);
            """);

        // Versions are immutable; the same version number cannot be saved twice; values are checked.
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "UPDATE tool_preparations SET comment = 'edited' WHERE id = 'preparation-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "UPDATE tool_preparation_tools SET measured_length = 1 WHERE id = 'tool-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "DELETE FROM tool_preparations WHERE id = 'preparation-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO tool_preparations (id, batch_operation_id, machine_id, tool_table_release_id, version_number, saved_at, saved_by, content_hash)
            VALUES ('preparation-dup', 'batch-operation-t', 'machine-t', 'tools-t', 1, '2026-09-02T09:00:00Z', 'tool-room', lower(hex(zeroblob(32))));
            """));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO tool_preparation_tools (id, tool_preparation_id, row_number, tool_identifier, measured_length, shape_type, shape_json)
            VALUES ('tool-bad', 'preparation-1', 2, 'T2', -1, 'END_MILL', '{}');
            """));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO tool_preparation_tools (id, tool_preparation_id, row_number, tool_identifier, shape_type, shape_json)
            VALUES ('tool-bad', 'preparation-1', 2, 'T2', 'LASER', '{}');
            """));

        // A package may bind the exact version it used.
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('production_packages') WHERE name = 'tool_preparation_id' AND \"notnull\" = 1;"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('production_packages') WHERE name = 'tool_preparation_id';"));
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
