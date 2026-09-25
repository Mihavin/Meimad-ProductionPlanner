using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class KitaronRouteImportMigrationTests
{
    [Fact]
    public async Task Version_81_adds_the_station_lookup_the_requirement_step_fields_and_the_requirement_link_entity()
    {
        await using var fixture = TemporaryDatabase.CreateUnmigrated();
        var migrator = new DatabaseMigrator(fixture.Database, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync(80);
        await using (var v80 = await fixture.Database.OpenConnectionAsync())
        {
            await ExecuteAsync(v80, """
                INSERT INTO cases (id, part_number, name, working_folder_path) VALUES ('case-r', 'PN-R', 'Part', 'C:\\Cases\\PN-R');
                INSERT INTO case_operations (id, case_id, operation_number, route_position, name, required_machine_type, setup_seconds, cycle_seconds)
                VALUES ('operation-r', 'case-r', 30, 0, 'Mill', 'mill', 60, 60);
                INSERT INTO workstation_types (id, name, created_at, updated_at) VALUES ('type-inspection', 'Inspection', '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z');
                INSERT INTO operation_resource_requirements (id, case_operation_id, sequence_position, resource_class, workstation_type_id,
                    capacity_required, estimated_duration_seconds, direction, created_at, updated_at)
                VALUES ('requirement-r', 'operation-r', 0, 'WORKSTATION', 'type-inspection', 1, 600, 'FORWARD', '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z');
                INSERT INTO kitaron_sync_links (source_entity, source_key, target_id, owns_target, source_hash, first_seen_at, last_seen_at)
                VALUES ('case_operation', 'PN-R' || char(31) || '30', 'operation-r', 1, 'hash', '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z');
                """);
        }
        SqliteConnection.ClearAllPools();

        await migrator.MigrateAsync();

        await using var connection = await fixture.Database.OpenConnectionAsync();
        Assert.Equal("kitaron_route_import", await ScalarAsync(connection, "SELECT name FROM schema_migrations WHERE version = 81;"));
        Assert.Equal(81L, await ScalarAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));

        // Existing rows and links survived; the new requirement columns default to empty / zero.
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM kitaron_sync_links WHERE source_entity = 'case_operation' AND target_id = 'operation-r';"));
        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "SELECT name FROM operation_resource_requirements WHERE id = 'requirement-r';"));
        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "SELECT step_number FROM operation_resource_requirements WHERE id = 'requirement-r';"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT duration_per_unit_seconds FROM operation_resource_requirements WHERE id = 'requirement-r';"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%_v31';"));

        // The link table admits the new entity and the sync state carries the new counters.
        await ExecuteAsync(connection, """
            INSERT INTO kitaron_sync_links (source_entity, source_key, target_id, owns_target, source_hash, first_seen_at, last_seen_at)
            VALUES ('operation_requirement', 'PN-R' || char(31) || 'step' || char(31) || '40', 'requirement-r', 1, 'hash', '2026-09-25T00:00:00Z', '2026-09-25T00:00:00Z');
            UPDATE kitaron_sync_state SET requirements_created = 3, route_steps_skipped = 7 WHERE id = 1;
            """);
        Assert.Equal(3L, await ScalarAsync(connection, "SELECT requirements_created FROM kitaron_sync_state WHERE id = 1;"));
        Assert.Equal(7L, await ScalarAsync(connection, "SELECT route_steps_skipped FROM kitaron_sync_state WHERE id = 1;"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO kitaron_sync_links (source_entity, source_key, target_id, owns_target, source_hash, first_seen_at, last_seen_at)
            VALUES ('unknown_entity', 'x', 'y', 1, 'hash', '2026-09-25T00:00:00Z', '2026-09-25T00:00:00Z');
            """));

        // Station decisions are checked: a Workstation role needs its type, an External role its resource.
        await ExecuteAsync(connection, """
            INSERT INTO kitaron_stations (kitaron_station_id, station_name, station_type, route_rows, planned_rows, supplier_rows,
                suggested_role, import_role, first_seen_at, last_seen_at, updated_at)
            VALUES (4, 'Inspection', 'Inspection room', 17576, 55, 0, 'WORKSTATION', 'UNDECIDED', '2026-09-25T00:00:00Z', '2026-09-25T00:00:00Z', '2026-09-25T00:00:00Z');
            UPDATE kitaron_stations SET import_role = 'WORKSTATION', workstation_type_id = 'type-inspection' WHERE kitaron_station_id = 4;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "UPDATE kitaron_stations SET import_role = 'EXTERNAL', workstation_type_id = NULL WHERE kitaron_station_id = 4;"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "UPDATE kitaron_stations SET import_role = 'WORKSTATION', workstation_type_id = 'missing-type' WHERE kitaron_station_id = 4;"));
        Assert.Equal("WORKSTATION", await ScalarAsync(connection, "SELECT import_role FROM kitaron_stations WHERE kitaron_station_id = 4;"));
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
