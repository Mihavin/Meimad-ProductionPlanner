using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class ToolCatalogMigrationTests
{
    [Fact]
    public async Task Version_80_adds_the_tool_catalog_and_rebuilds_the_prepared_tools_keeping_their_rows()
    {
        await using var fixture = TemporaryDatabase.CreateUnmigrated();
        var migrator = new DatabaseMigrator(fixture.Database, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync(79);
        await using (var v79 = await fixture.Database.OpenConnectionAsync())
        {
            await ExecuteAsync(v79, """
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
                INSERT INTO tool_preparations (id, batch_operation_id, machine_id, tool_table_release_id, version_number, saved_at, saved_by, content_hash)
                VALUES ('preparation-1', 'batch-operation-t', 'machine-t', 'tools-t', 1, '2026-09-02T08:00:00Z', 'tool-room', lower(hex(zeroblob(32))));
                INSERT INTO tool_preparation_tools (id, tool_preparation_id, row_number, tool_identifier, offset_number, measured_length, measured_diameter, shape_type, shape_json, notes)
                VALUES ('tool-1', 'preparation-1', 1, 'T1', 1, 120.5, 10, 'END_MILL', '{"cuttingDiameter":10}', 'regrind');
                INSERT INTO tool_preparation_components (id, tool_preparation_tool_id, sequence, component_type, name, length, diameter)
                VALUES ('component-1', 'tool-1', 1, 'HOLDER', 'BT40 ER32', 60, 63);
                """);
        }
        SqliteConnection.ClearAllPools();

        await migrator.MigrateAsync();

        await using var connection = await fixture.Database.OpenConnectionAsync();
        Assert.Equal("tool_catalog", await ScalarAsync(connection, "SELECT name FROM schema_migrations WHERE version = 80;"));
        Assert.Equal(80L, await ScalarAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));

        // The rows of version 79 survived the rebuild, with the new columns empty.
        Assert.Equal(120.5, await ScalarAsync(connection, "SELECT measured_length FROM tool_preparation_tools WHERE id = 'tool-1';"));
        Assert.Equal("regrind", await ScalarAsync(connection, "SELECT notes FROM tool_preparation_tools WHERE id = 'tool-1';"));
        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "SELECT hand FROM tool_preparation_tools WHERE id = 'tool-1';"));
        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "SELECT catalog_tool_id FROM tool_preparation_tools WHERE id = 'tool-1';"));
        Assert.Equal("BT40 ER32", await ScalarAsync(connection,
            "SELECT component.name FROM tool_preparation_components component JOIN tool_preparation_tools tool ON tool.id = component.tool_preparation_tool_id WHERE tool.id = 'tool-1';"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%_v80';"));

        // The catalog: stable numbers and codes, checked types and hands, external ids unique per tool.
        await ExecuteAsync(connection, """
            INSERT INTO catalog_tools (id, internal_number, internal_code, name, tool_type, hand, shape_json, attributes_json, version, created_at, updated_at, updated_by)
            VALUES ('catalog-1', 1, 'MT-00001', 'PCLNR 2525 M12', 'TURNING_TOOL', 'RIGHT', '{"cornerRadius":0.8}', '{"insertCode":"CNMG120408"}', 1, '2026-09-25T08:00:00Z', '2026-09-25T08:00:00Z', 'tool-room');
            INSERT INTO catalog_tool_external_ids (id, catalog_tool_id, system, value) VALUES ('external-1', 'catalog-1', 'ERP', 'K-4711');
            INSERT INTO catalog_tool_external_ids (id, catalog_tool_id, system, value) VALUES ('external-2', 'catalog-1', 'Sandvik', 'PCLNR 2525M 12');
            """);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "INSERT INTO catalog_tool_external_ids (id, catalog_tool_id, system, value) VALUES ('external-dup', 'catalog-1', 'ERP', 'K-4711');"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO catalog_tools (id, internal_number, internal_code, name, tool_type, shape_json, attributes_json, version, created_at, updated_at, updated_by)
            VALUES ('catalog-bad', 2, 'MT-00002', 'Laser', 'LASER', '{}', '{}', 1, '2026-09-25T08:00:00Z', '2026-09-25T08:00:00Z', 'tool-room');
            """));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO catalog_tools (id, internal_number, internal_code, name, tool_type, hand, shape_json, attributes_json, version, created_at, updated_at, updated_by)
            VALUES ('catalog-bad', 2, 'MT-00002', 'Odd hand', 'TURNING_TOOL', 'UP', '{}', '{}', 1, '2026-09-25T08:00:00Z', '2026-09-25T08:00:00Z', 'tool-room');
            """));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO catalog_tools (id, internal_number, internal_code, name, tool_type, shape_json, attributes_json, version, created_at, updated_at, updated_by)
            VALUES ('catalog-dup', 1, 'MT-00099', 'Duplicate number', 'END_MILL', '{}', '{}', 1, '2026-09-25T08:00:00Z', '2026-09-25T08:00:00Z', 'tool-room');
            """));

        // Prepared tools take the enlarged type list, a hand and a restrictive catalog link.
        await ExecuteAsync(connection, """
            INSERT INTO tool_preparations (id, batch_operation_id, machine_id, tool_table_release_id, version_number, saved_at, saved_by, content_hash)
            VALUES ('preparation-2', 'batch-operation-t', 'machine-t', 'tools-t', 2, '2026-09-25T08:00:00Z', 'tool-room', lower(hex(zeroblob(32))));
            INSERT INTO tool_preparation_tools (id, tool_preparation_id, row_number, tool_identifier, shape_type, shape_json, hand, catalog_tool_id)
            VALUES ('tool-2', 'preparation-2', 1, 'T1', 'EXTERNAL_GROOVING', '{"cuttingWidth":3}', 'LEFT', 'catalog-1');
            """);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO tool_preparation_tools (id, tool_preparation_id, row_number, tool_identifier, shape_type, shape_json, hand)
            VALUES ('tool-bad', 'preparation-2', 2, 'T2', 'END_MILL', '{}', 'UP');
            """));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO tool_preparation_tools (id, tool_preparation_id, row_number, tool_identifier, shape_type, shape_json, catalog_tool_id)
            VALUES ('tool-bad', 'preparation-2', 2, 'T2', 'END_MILL', '{}', 'no-such-catalog-tool');
            """));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM catalog_tools WHERE id = 'catalog-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE tool_preparation_tools SET notes = 'edited' WHERE id = 'tool-2';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE tool_preparation_components SET name = 'edited' WHERE id = 'component-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM tool_preparations WHERE id = 'preparation-2';"));
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
