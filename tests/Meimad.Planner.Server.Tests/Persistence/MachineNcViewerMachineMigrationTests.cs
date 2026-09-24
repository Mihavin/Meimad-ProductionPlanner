using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class MachineNcViewerMachineMigrationTests
{
    [Fact]
    public async Task Version_78_adds_a_nullable_nc_viewer_machine_id_with_the_engine_identifier_grammar()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await ExecuteAsync(connection, """
            INSERT INTO working_calendars (id, name, time_zone_id) VALUES ('calendar-v', 'Viewer', 'UTC');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES ('machine-auto', '10', 'Auto', 'mill', 'calendar-v', 'active', 1);
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active, nc_viewer_machine)
            VALUES ('machine-vf3', '11', 'VF-3SS', 'mill', 'calendar-v', 'active', 1, 'haas-vf-3ss');
            """);

        Assert.Equal("machine_nc_viewer_machine", await ScalarAsync(connection, "SELECT name FROM schema_migrations WHERE version = 78;"));
        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "SELECT nc_viewer_machine FROM machines WHERE id = 'machine-auto';"));
        Assert.Equal("haas-vf-3ss", await ScalarAsync(connection, "SELECT nc_viewer_machine FROM machines WHERE id = 'machine-vf3';"));

        // The engine registry accepts lower-case letters, digits and hyphens only.
        foreach (var invalid in new[] { "Haas VF-3SS", "haas_vf_3ss", "-leading", "" })
        {
            await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
                $"UPDATE machines SET nc_viewer_machine = '{invalid}' WHERE id = 'machine-auto';"));
        }
        await ExecuteAsync(connection, "UPDATE machines SET nc_viewer_machine = 'okuma-genos-l200e-m' WHERE id = 'machine-auto';");
        await ExecuteAsync(connection, "UPDATE machines SET nc_viewer_machine = NULL WHERE id = 'machine-vf3';");
        Assert.Equal("okuma-genos-l200e-m", await ScalarAsync(connection, "SELECT nc_viewer_machine FROM machines WHERE id = 'machine-auto';"));
        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "SELECT nc_viewer_machine FROM machines WHERE id = 'machine-vf3';"));
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
