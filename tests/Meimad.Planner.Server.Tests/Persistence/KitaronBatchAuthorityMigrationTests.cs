using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class KitaronBatchAuthorityMigrationTests
{
    /// <summary>
    /// The owner's 2026-09-25 decision: batches come from Kitaron work orders, and the planner
    /// re-plans from scratch. Version 82 archives and removes the planner-created batches with
    /// their whole planning/execution graph, retires the suppression table, locks route Cases
    /// through a new flag, and adds the per-batch Kitaron material check.
    /// </summary>
    [Fact]
    public async Task Version_82_archives_and_removes_the_planning_graph_once()
    {
        await using var fixture = TemporaryDatabase.CreateUnmigrated();
        var migrator = new DatabaseMigrator(fixture.Database, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync(81);
        await using (var seed = await fixture.Database.OpenConnectionAsync())
        {
            await ExecuteAsync(seed, """
                INSERT INTO working_calendars (id, name, time_zone_id, calendar_json) VALUES ('calendar-w', 'Shifts', 'UTC', '{}');
                INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
                VALUES ('machine-w', '10', 'Machine 10', 'Mill 3x', 'calendar-w', 'active', 1);
                INSERT INTO cases (id, part_number, name, working_folder_path) VALUES ('case-w', 'PN-W', 'Part', 'C:\\Cases\\PN-W');
                INSERT INTO case_operations (id, case_id, operation_number, route_position, name)
                VALUES ('operation-w', 'case-w', 30, 0, 'Mill');
                INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
                VALUES ('batch-w', 'case-w', 'B-1', 'waiting', 5);
                INSERT INTO batch_allocations (id, production_batch_id, allocation_type, quantity)
                VALUES ('allocation-w', 'batch-w', 'stock', 5);
                INSERT INTO batch_operations (id, production_batch_id, source_case_operation_id, operation_number,
                    route_position, name, status, dependency_type)
                VALUES ('batch-op-w', 'batch-w', 'operation-w', 30, 0, 'Mill', 'not_started', 'independent');
                INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position)
                VALUES ('assignment-w', 'batch-op-w', 'machine-w', 0);
                INSERT INTO kitaron_suppressed_operations (source_key, case_id, operation_number, name, suppressed_at)
                VALUES ('PN-W' || char(31) || '40', 'case-w', 40, 'Old suppression', '2026-09-01T00:00:00Z');
                """);
        }
        SqliteConnection.ClearAllPools();

        await migrator.MigrateAsync();

        await using var connection = await fixture.Database.OpenConnectionAsync();
        Assert.Equal("kitaron_batch_authority_reset", await ScalarAsync(connection, "SELECT name FROM schema_migrations WHERE version = 82;"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));

        // The planning graph is gone but archived row by row; the Case route itself stays.
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM production_batches;"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM batch_operations;"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM machine_assignments;"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM case_operations;"));
        Assert.Equal(1L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM wipe_v82_archive WHERE table_name = 'production_batches' AND row_json LIKE '%\"batch_number\":\"B-1\"%';"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM wipe_v82_archive WHERE table_name = 'machine_assignments';"));

        // The suppression era ended; the route lock and the material check arrived.
        Assert.Equal(0L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'kitaron_suppressed_operations';"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT kitaron_route_locked FROM cases WHERE id = 'case-w';"));
        await ExecuteAsync(connection, """
            INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
            VALUES ('batch-new', 'case-w', 'B-2', 'waiting', 3);
            INSERT INTO kitaron_batch_material_checks (production_batch_id, state, detail, updated_at)
            VALUES ('batch-new', 'on_order', 'MAT-1: need 3', '2026-09-25T10:00:00Z');
            """);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO kitaron_batch_material_checks (production_batch_id, state, detail, updated_at)
            VALUES ('batch-new', 'not-a-state', NULL, '2026-09-25T10:00:00Z');
            """));

        // The immutability triggers of the wiped ledgers were recreated exactly as stored.
        Assert.Equal(1L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name = 'operational_anomalies_immutable_delete';"));
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
