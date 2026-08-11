using System.Net;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Timeline;

public sealed class TimelineApiTests
{
    [Fact]
    public async Task Timeline_api_returns_server_calculation_without_changing_backlog()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;

            Assert.Equal("2026-08-11T08:00:00+00:00", root.GetProperty("horizonStart").GetString());
            Assert.Single(root.GetProperty("batches").EnumerateArray());
            var intervals = root.GetProperty("machines")[0].GetProperty("intervals")
                .EnumerateArray().ToArray();
            Assert.Contains(intervals, value => value.GetProperty("type").GetString() == "setup");
            Assert.Contains(intervals, value => value.GetProperty("type").GetString() == "production");
            Assert.Contains(intervals, value => value.GetProperty("type").GetString() == "idle");
            Assert.Contains(intervals, value => value.GetProperty("type").GetString() == "downtime");
            var dependency = Assert.Single(root.GetProperty("dependencies").EnumerateArray());
            Assert.Equal("SEQUENTIAL", dependency.GetProperty("type").GetString());
            Assert.Equal("batch-1", dependency.GetProperty("batchId").GetString());

            var positions = await ReadPositionsAsync(application.Services);
            Assert.Equal(["op-1:0", "op-2:1"], positions);
        });
    }

    [Fact]
    public async Task Timeline_api_explains_missing_calendar_configuration()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains(
                document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString()
                    == "setup_calendar_configuration_missing");
        });
    }

    [Fact]
    public async Task Timeline_api_rejects_invalid_horizon()
    {
        await RunWithServerAsync(async (_, client) =>
        {
            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-12T08:00:00Z&to=2026-08-11T18:00:00Z");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        });
    }

    [Fact]
    public async Task Timeline_expands_weekly_calendar_in_its_configured_time_zone()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE working_calendars
                SET time_zone_id = 'Asia/Jerusalem',
                    calendar_json = '{"weeklySchedule":{"workdays":["tuesday"],"shiftStartsAtLocal":"06:00","shiftEndsAtLocal":"18:00"}}'
                WHERE id = 'calendar-1';
                UPDATE application_settings
                SET value = '{"availability":[{"startsAt":"2026-08-11T02:00:00Z","endsAt":"2026-08-11T18:00:00Z"}]}'
                WHERE key = 'timeline.setup_calendar_json';
                """;
            await command.ExecuteNonQueryAsync();

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T02:00:00Z&to=2026-08-11T18:00:00Z");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var setup = document.RootElement.GetProperty("machines")[0].GetProperty("intervals")
                .EnumerateArray().First(value => value.GetProperty("type").GetString() == "setup");
            Assert.Equal("2026-08-11T03:00:00+00:00", setup.GetProperty("startsAt").GetString());
            Assert.DoesNotContain(
                document.RootElement.GetProperty("conflicts").EnumerateArray(),
                value => value.GetProperty("code").GetString() == "calendar_configuration_missing");
        });
    }

    private static async Task SeedTimelineAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id, calendar_json)
            VALUES (
                'calendar-1', 'Day shift', 'UTC',
                '{"availability":[{"startsAt":"2026-08-11T08:00:00Z","endsAt":"2026-08-11T18:00:00Z"}]}');
            INSERT INTO application_settings (key, value)
            VALUES (
                'timeline.setup_calendar_json',
                '{"availability":[{"startsAt":"2026-08-11T08:00:00Z","endsAt":"2026-08-11T18:00:00Z"}]}');
            INSERT INTO machines (
                id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES ('machine-1', 'M-1', 'Mill One', 'mill', 'calendar-1', 'active', 1);
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ('case-1', 'PN-1', 'Timeline Part', 'C:\Cases\PN-1');
            INSERT INTO production_batches (
                id, case_id, batch_number, status, planned_quantity)
            VALUES ('batch-1', 'case-1', 'B-1', 'planned', 2);
            INSERT INTO case_operations (
                id, case_id, operation_number, route_position, name,
                required_machine_type, setup_seconds, cycle_seconds,
                dependency_type, predecessor_case_operation_id)
            VALUES
                ('case-op-1', 'case-1', 10, 0, 'First', 'mill', 1800, 1800,
                 'independent', NULL),
                ('case-op-2', 'case-1', 20, 1, 'Second', 'mill', 0, 900,
                 'sequential', 'case-op-1');
            INSERT INTO batch_operations (
                id, production_batch_id, source_case_operation_id,
                operation_number, route_position, name, required_machine_type,
                setup_seconds, cycle_seconds, status)
            VALUES
                ('op-1', 'batch-1', 'case-op-1', 10, 0, 'First', 'mill', 1800, 1800, 'not_started'),
                ('op-2', 'batch-1', 'case-op-2', 20, 1, 'Second', 'mill', 0, 900, 'not_started');
            INSERT INTO machine_assignments (
                id, batch_operation_id, machine_id, backlog_position)
            VALUES
                ('assignment-1', 'op-1', 'machine-1', 0),
                ('assignment-2', 'op-2', 'machine-1', 1);
            INSERT INTO downtimes (
                id, machine_id, starts_at, ends_at, reason, status)
            VALUES (
                'downtime-1', 'machine-1', '2026-08-11T10:00:00.0000000+00:00',
                '2026-08-11T10:30:00.0000000+00:00', 'Inspection', 'planned');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string[]> ReadPositionsAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT batch_operation_id || ':' || backlog_position
            FROM machine_assignments
            ORDER BY backlog_position;
            """;
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result.ToArray();
    }

    private static async Task RunWithServerAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(), "MeimadPlanner.TimelineApi.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            [
                "--Server:Host=127.0.0.1",
                "--Server:Port=5099",
                $"--Database:Path={Path.Combine(directoryPath, "api-test.db")}"
            ],
            webHost => webHost.UseTestServer());
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            await test(application, client);
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }
}
