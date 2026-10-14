using Meimad.Planner.NcEngine.MachineDefinitions;
using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class MachineSimulationDefinitionMigrationTests
{
    [Fact]
    public async Task Version_114_carries_every_machine_value_into_a_published_definition_without_changing_the_machine()
    {
        await using var fixture = TemporaryDatabase.CreateUnmigrated();
        var migrator = new DatabaseMigrator(fixture.Database, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync(113);
        await using (var v113 = await fixture.Database.OpenConnectionAsync())
        {
            await ExecuteAsync(v113, """
                INSERT INTO working_calendars (id, name, time_zone_id) VALUES ('calendar-s', 'Simulation', 'UTC');
                INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active,
                    usable_tool_positions, rapid_rate_mm_per_min, tool_change_time_seconds, machine_time_factor,
                    nc_viewer_machine, nc_viewer_models_json)
                VALUES ('machine-14', '14', 'Mazak Variaxis', 'mill', 'calendar-s', 'active', 1, 28, 36000, 5.5, 1.15,
                        'mazak-variaxis-i-500',
                        '[{"node":"c-table","path":"machines\\Mazak\\table.stl","offsetMm":{"x":1,"y":2,"z":3},"rotationDeg":{"x":90,"y":0,"z":0},"color":"#336699","opacity":0.5}]'),
                       ('machine-old', '3', 'Manual mill', 'mill', 'calendar-s', 'active', 1, NULL, NULL, NULL, 1, NULL, '[]'),
                       ('machine-lathe', '20', 'Okuma', 'lathe', 'calendar-s', 'active', 1, 12, NULL, NULL, 1, 'okuma-genos-l200e-m', '[]');
                """);
        }
        var machinesBefore = await RowsAsync(fixture, "SELECT * FROM machines ORDER BY id;");
        SqliteConnection.ClearAllPools();

        // Version 114 itself leaves the machines untouched (later versions may add columns, such as v122's hourly price).
        await migrator.MigrateAsync(114);
        Assert.Equal(machinesBefore, await RowsAsync(fixture, "SELECT * FROM machines ORDER BY id;"));
        SqliteConnection.ClearAllPools();
        await migrator.MigrateAsync();

        await using var connection = await fixture.Database.OpenConnectionAsync();
        Assert.Equal("machine_simulation_definitions", await ScalarAsync(connection, "SELECT name FROM schema_migrations WHERE version = 114;"));
        Assert.Equal(3L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM machine_simulation_definitions WHERE draft_json = published_json AND draft_version = 1 AND published_version = 1;"));

        var mazak = MachineSimulationDefinition.Parse((string)(await ScalarAsync(connection,
            "SELECT published_json FROM machine_simulation_definitions WHERE machine_id = 'machine-14';"))!);
        Assert.Equal("mazak-variaxis-i-500", mazak.Template);
        Assert.Equal("B", mazak.InterpreterAxis("A"));
        Assert.Equal("A", mazak.Node("a-trunnion")!.Joint!.Axis);
        Assert.Equal(28, mazak.Magazine.UsablePositions);
        Assert.Equal(5.5, mazak.Magazine.ToolChangeSeconds);
        Assert.Equal(36000, mazak.Timing.RapidRateMillimetersPerMinute);
        Assert.Equal(1.15, mazak.Timing.MachineTimeFactor);
        var model = Assert.Single(mazak.Node("c-table")!.Models);
        Assert.Equal(@"machines\Mazak\table.stl", model.LegacyPath);
        Assert.Equal([1, 2, 3], model.Alignment.Position);
        Assert.Equal([90, 0, 0], model.Alignment.RotationDeg);
        Assert.Equal(0.5, model.Opacity);
        Assert.False(mazak.Calibration!.ZeroVerified);
        Assert.DoesNotContain(MachineSimulationValidator.Validate(mazak), issue => issue.IsError && issue.Code != "legacy_path");

        // No NC viewer machine: no components, auto-detection, unknown values stay unknown.
        var manual = MachineSimulationDefinition.Parse((string)(await ScalarAsync(connection,
            "SELECT draft_json FROM machine_simulation_definitions WHERE machine_id = 'machine-old';"))!);
        Assert.Empty(manual.Nodes);
        Assert.Null(manual.Template);
        Assert.Null(manual.Magazine.UsablePositions);
        Assert.Null(manual.Magazine.ToolChangeSeconds);
        Assert.Null(manual.Timing.RapidRateMillimetersPerMinute);

        var lathe = MachineSimulationDefinition.Parse((string)(await ScalarAsync(connection,
            "SELECT draft_json FROM machine_simulation_definitions WHERE machine_id = 'machine-lathe';"))!);
        Assert.Equal(12, lathe.Magazine.UsablePositions);
        Assert.Equal(0.5, lathe.Node("x-slide")!.Joint!.Scale);

        // Definitions go with their Machine and are not planning inputs.
        var planningBefore = await ScalarAsync(connection, "SELECT revision - non_planning_revision FROM projection_revision WHERE id = 1;");
        await ExecuteAsync(connection, "UPDATE machine_simulation_definitions SET draft_version = 2 WHERE machine_id = 'machine-old';");
        Assert.Equal(planningBefore, await ScalarAsync(connection, "SELECT revision - non_planning_revision FROM projection_revision WHERE id = 1;"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "UPDATE machine_simulation_definitions SET draft_json = '[]' WHERE machine_id = 'machine-old';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "INSERT INTO machine_simulation_assets (id, machine_id, file_name, media_type, file_size, file_hash, stored_relative_path, created_at) VALUES ('../x', 'machine-old', 'a.glb', 'model/gltf-binary', 1, '" + new string('a', 64) + "', 'p', 'x');"));
        await ExecuteAsync(connection, "DELETE FROM machines WHERE id = 'machine-old';");
        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM machine_simulation_definitions;"));
    }

    private static async Task<IReadOnlyList<string>> RowsAsync(TemporaryDatabase fixture, string sql)
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
                .Where(index => reader.GetName(index) is not ("version" or "updated_at"))
                .Select(index => reader.IsDBNull(index) ? "<null>" : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture))));
        }
        return rows;
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
