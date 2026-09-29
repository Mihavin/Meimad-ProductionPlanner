using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class ShiftRotationMigrationTests
{
    [Fact]
    public async Task Version_95_keeps_employees_without_a_crew_and_adds_a_unique_cascading_shift_roster()
    {
        await using var fixture = TemporaryDatabase.CreateUnmigrated();
        var migrator = new DatabaseMigrator(fixture.Database, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync(94);
        await using (var v94 = await fixture.Database.OpenConnectionAsync())
        {
            await ExecuteAsync(v94, """
                INSERT INTO working_calendars(id,name,time_zone_id,calendar_json) VALUES('calendar-1','Day','UTC','{}');
                INSERT INTO employee_resources (id, employee_number, name, resource_type, first_name, last_name, skills_json, assigned_calendar_id, is_active, version, created_at, updated_at)
                VALUES ('employee-1','E-1','Dana Levi','regular_worker','Dana','Levi','[]','calendar-1',1,1,'2026-09-01T00:00:00Z','2026-09-01T00:00:00Z');
                """);
        }
        SqliteConnection.ClearAllPools();

        await migrator.MigrateAsync();

        await using var v95 = await fixture.Database.OpenConnectionAsync();
        Assert.Equal(DBNull.Value, await ScalarAsync(v95, "SELECT shift_crew_code FROM employee_resources WHERE id = 'employee-1';"));
        await ExecuteAsync(v95, """
            INSERT INTO employee_shift_roster_entries (id, resource_id, roster_date, shift_code, version, created_at, updated_at, updated_by)
            VALUES ('entry-1', 'employee-1', '2026-10-04', 'night', 1, '2026-09-29T00:00:00Z', '2026-09-29T00:00:00Z', 'planner');
            """);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(v95, """
            INSERT INTO employee_shift_roster_entries (id, resource_id, roster_date, shift_code, version, created_at, updated_at)
            VALUES ('entry-2', 'employee-1', '2026-10-04', 'day', 1, '2026-09-29T00:00:00Z', '2026-09-29T00:00:00Z');
            """));
        await ExecuteAsync(v95, "PRAGMA foreign_keys = ON; DELETE FROM employee_resources WHERE id = 'employee-1';");
        Assert.Equal(0L, await ScalarAsync(v95, "SELECT COUNT(*) FROM employee_shift_roster_entries;"));
        Assert.Equal(0L, await ScalarAsync(v95, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
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
