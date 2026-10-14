using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class WorkOrderTargetTimeMigrationTests
{
    [Fact]
    public async Task Version_122_adds_optional_hourly_prices_and_the_target_time_table()
    {
        await using var fixture = TemporaryDatabase.CreateUnmigrated();
        var migrator = new DatabaseMigrator(fixture.Database, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync(121);
        await using (var v121 = await fixture.Database.OpenConnectionAsync())
        {
            await ExecuteAsync(v121, """
                INSERT INTO working_calendars (id, name, time_zone_id) VALUES ('calendar-1', 'Day', 'UTC');
                INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
                VALUES ('machine-1', 'M-1', 'Mill', 'mill', 'calendar-1', 'active', 1);
                INSERT INTO workstation_types (id, name, created_at, updated_at) VALUES ('type-1', 'Deburring', '2026-10-14', '2026-10-14');
                INSERT INTO cases (id, part_number, name, working_folder_path) VALUES ('case-1', 'P-1', 'Part', 'C:\Cases\P-1');
                INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity) VALUES ('b-1', 'case-1', 'B-1', 'planned', 5);
                """);
        }
        SqliteConnection.ClearAllPools();

        await migrator.MigrateAsync();

        await using var connection = await fixture.Database.OpenConnectionAsync();
        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "SELECT hourly_rate_nis FROM machines WHERE id = 'machine-1';"));
        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "SELECT hourly_rate_nis FROM workstation_types WHERE id = 'type-1';"));
        await ExecuteAsync(connection, "UPDATE machines SET hourly_rate_nis = 240; UPDATE workstation_types SET hourly_rate_nis = 0;");
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE machines SET hourly_rate_nis = -1;"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE workstation_types SET hourly_rate_nis = 100001;"));

        var before = (long)(await ScalarAsync(connection, "SELECT revision - non_planning_revision FROM projection_revision WHERE id = 1;"))!;
        await ExecuteAsync(connection, """
            INSERT INTO work_order_target_times (production_batch_id, part_price_nis, material_total_nis, quantity, margin_nis, resources_json,
                version, created_at, updated_at, updated_by)
            VALUES ('b-1', 100, 50, 5, 450, '[]', 1, '2026-10-14', '2026-10-14', 'planner');
            """);
        // Not a planning input: the planning revision does not move.
        Assert.Equal(before, (long)(await ScalarAsync(connection, "SELECT revision - non_planning_revision FROM projection_revision WHERE id = 1;"))!);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE work_order_target_times SET resources_json = '{}';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE work_order_target_times SET part_price_nis = -1;"));
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON; DELETE FROM production_batches WHERE id = 'b-1';");
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM work_order_target_times;"));
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
