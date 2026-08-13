using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Timeline;

public sealed class TimelineApiTests
{
    [Theory]
    [InlineData("vacation")]
    [InlineData("sick_day")]
    public async Task Employee_full_day_absence_removes_worker_from_timeline_availability(
        string exceptionType)
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO employee_calendar_exceptions (
                        id, resource_id, exception_date, exception_type, is_full_day,
                        starts_at_local, ends_at_local, note, version, created_at, updated_at)
                    VALUES ('absence-1', 'resource-setup', '2026-08-11', $type, 1,
                            NULL, NULL, 'Timeline availability test', 1,
                            '2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z');
                    """;
                command.Parameters.AddWithValue("$type", exceptionType);
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                value => value.GetProperty("code").GetString() == "insufficient_availability");
            Assert.DoesNotContain(document.RootElement.GetProperty("machines").EnumerateArray()
                    .SelectMany(value => value.GetProperty("intervals").EnumerateArray()),
                value => value.GetProperty("type").GetString() == "setup");
        });
    }

    [Fact]
    public async Task Partial_employee_unavailability_is_returned_as_visible_resource_waiting()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO employee_calendar_exceptions (
                        id, resource_id, exception_date, exception_type, is_full_day,
                        starts_at_local, ends_at_local, note, version, created_at, updated_at)
                    VALUES ('partial-absence', 'resource-setup', '2026-08-11', 'unavailable', 0,
                            '08:00', '09:00', 'Morning appointment', 1,
                            '2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var wait = Assert.Single(
                document.RootElement.GetProperty("machines").EnumerateArray()
                    .SelectMany(value => value.GetProperty("intervals").EnumerateArray()),
                value => value.GetProperty("type").GetString() == "waiting"
                    && value.GetProperty("detail").GetString()!
                        .Contains("skilled setup worker", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("2026-08-11T08:00:00+00:00", wait.GetProperty("startsAt").GetString());
            Assert.Equal("2026-08-11T09:00:00+00:00", wait.GetProperty("endsAt").GetString());
            using var logged = await client.GetAsync("/api/v1/event-log?eventType=resource_wait_detected");
            logged.EnsureSuccessStatusCode();
            using var loggedJson = JsonDocument.Parse(await logged.Content.ReadAsStringAsync());
            var resourceWait = Assert.Single(loggedJson.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("system", resourceWait.GetProperty("user").GetString());
            Assert.Equal("resource_unavailable_or_contended", resourceWait.GetProperty("reasonCode").GetString());
            Assert.Equal("op-1", resourceWait.GetProperty("relatedEntityIds").GetProperty("batchOperationId").GetString());
        });
    }


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
            Assert.Contains(intervals, value =>
                value.GetProperty("operationId").GetString() == "op-1"
                && value.GetProperty("operationName").GetString() == "First");
            var dependency = Assert.Single(root.GetProperty("dependencies").EnumerateArray());
            Assert.Equal("SEQUENTIAL", dependency.GetProperty("type").GetString());
            Assert.Equal("batch-1", dependency.GetProperty("batchId").GetString());

            var positions = await ReadPositionsAsync(application.Services);
            Assert.Equal(["op-1:0", "op-2:1"], positions);
        });
    }

    [Fact]
    public async Task Timeline_defaults_missing_setup_calendar_and_still_projects_operations()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "DELETE FROM application_settings WHERE key = 'timeline.setup_calendar_json';";
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains(
                document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString()
                    == "setup_calendar_defaulted");
            Assert.Contains(
                document.RootElement.GetProperty("machines")[0].GetProperty("intervals")
                    .EnumerateArray(),
                interval => interval.GetProperty("operationId").GetString() == "op-1"
                    && interval.GetProperty("type").GetString() == "setup");
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
                    calendar_json = '{"weeklySchedule":{"workdays":["tuesday"],"windows":[{"startsAtLocal":"06:00","endsAtLocal":"07:00"},{"startsAtLocal":"08:00","endsAtLocal":"18:00"}]}}'
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
            Assert.Equal(2, document.RootElement.GetProperty("machines")[0].GetProperty("intervals")
                .EnumerateArray().Count(value => value.GetProperty("type").GetString() == "production"
                    && value.GetProperty("operationId").GetString() == "op-1"));
            Assert.DoesNotContain(
                document.RootElement.GetProperty("conflicts").EnumerateArray(),
                value => value.GetProperty("code").GetString() == "calendar_configuration_missing");
        });
    }

    [Fact]
    public async Task Timeline_subtracts_breaks_and_applies_dated_calendar_exceptions()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE working_calendars
                    SET time_zone_id = 'UTC',
                        calendar_json = '{"weeklySchedule":{"workdays":["tuesday"],"windows":[{"startsAtLocal":"08:00","endsAtLocal":"18:00"}],"breakWindows":[{"startsAtLocal":"09:00","endsAtLocal":"10:00"}],"exceptions":[{"date":"2026-08-11","windows":[{"startsAtLocal":"11:00","endsAtLocal":"18:00"}],"breakWindows":[{"startsAtLocal":"12:00","endsAtLocal":"13:00"}],"name":"Late opening"}]},"usages":["machine","setup_worker"]}'
                    WHERE id = 'calendar-1';
                    UPDATE setup_calendar_settings
                    SET working_calendar_id = 'calendar-1', legacy_fallback_enabled = 0
                    WHERE id = 1;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var exceptionResponse = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            exceptionResponse.EnsureSuccessStatusCode();
            using var exceptionDocument = JsonDocument.Parse(await exceptionResponse.Content.ReadAsStringAsync());
            var exceptionWork = exceptionDocument.RootElement.GetProperty("machines")[0].GetProperty("intervals")
                .EnumerateArray().Where(IsWork).ToArray();
            Assert.NotEmpty(exceptionWork);
            Assert.Equal("2026-08-11T11:00:00+00:00", exceptionWork[0].GetProperty("startsAt").GetString());
            Assert.DoesNotContain(exceptionWork, interval => Overlaps(interval, "2026-08-11T12:00:00Z", "2026-08-11T13:00:00Z"));

            using var weeklyResponse = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-18T08:00:00Z&to=2026-08-18T18:00:00Z");
            weeklyResponse.EnsureSuccessStatusCode();
            using var weeklyDocument = JsonDocument.Parse(await weeklyResponse.Content.ReadAsStringAsync());
            var weeklyWork = weeklyDocument.RootElement.GetProperty("machines")[0].GetProperty("intervals")
                .EnumerateArray().Where(IsWork).ToArray();
            Assert.NotEmpty(weeklyWork);
            Assert.Equal("2026-08-18T08:00:00+00:00", weeklyWork[0].GetProperty("startsAt").GetString());
            Assert.DoesNotContain(weeklyWork, interval => Overlaps(interval, "2026-08-18T09:00:00Z", "2026-08-18T10:00:00Z"));
        });
    }

    [Fact]
    public async Task Timeline_uses_cached_partial_holiday_without_online_access()
    {
        await RunWithServerAsync(async (application,client)=>
        {
            await SeedTimelineAsync(application.Services);var database=application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection=await database.OpenConnectionAsync();await using var command=connection.CreateCommand();
            command.CommandText="""
                UPDATE working_calendars SET time_zone_id='UTC',calendar_json='{"weeklySchedule":{"workdays":["tuesday"],"windows":[{"startsAtLocal":"08:00","endsAtLocal":"18:00"}]},"usages":["machine","setup_worker"],"useIsraeliHolidays":true}' WHERE id='calendar-1';
                UPDATE setup_calendar_settings SET working_calendar_id='calendar-1',legacy_fallback_enabled=0 WHERE id=1;
                INSERT INTO israeli_holidays(id,holiday_date,name,holiday_status,starts_at_local,ends_at_local,source,is_manual_override,version,created_at,updated_at)
                VALUES('holiday-partial','2026-08-11','Partial holiday','partial_working','11:00','16:00','manual',1,1,'2026-08-01T00:00:00Z','2026-08-01T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
            using var response=await client.GetAsync("/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");response.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var work=json.RootElement.GetProperty("machines")[0].GetProperty("intervals").EnumerateArray().Where(IsWork).ToArray();
            Assert.NotEmpty(work);Assert.All(work,value=>Assert.True(value.GetProperty("startsAt").GetDateTimeOffset()>=DateTimeOffset.Parse("2026-08-11T11:00:00Z")));
            Assert.All(work,value=>Assert.True(value.GetProperty("endsAt").GetDateTimeOffset()<=DateTimeOffset.Parse("2026-08-11T16:00:00Z")));
        });
    }

    [Fact]
    public async Task Timeline_uses_selected_managed_weekly_setup_calendar()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO working_calendars (id, name, time_zone_id, calendar_json)
                    VALUES ('setup-calendar', 'Setup shift', 'UTC',
                            '{"weeklySchedule":{"workdays":["tuesday"],"shiftStartsAtLocal":"12:00","shiftEndsAtLocal":"14:00"}}');
                    UPDATE setup_calendar_settings
                    SET working_calendar_id = 'setup-calendar'
                    WHERE id = 1;
                    DELETE FROM application_settings
                    WHERE key = 'timeline.setup_calendar_json';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var setup = document.RootElement.GetProperty("machines")[0].GetProperty("intervals")
                .EnumerateArray().First(value => value.GetProperty("type").GetString() == "setup");
            Assert.Equal("2026-08-11T12:00:00+00:00", setup.GetProperty("startsAt").GetString());
            Assert.DoesNotContain(
                document.RootElement.GetProperty("conflicts").EnumerateArray(),
                value => value.GetProperty("code").GetString() == "setup_calendar_defaulted");
        });
    }

    [Fact]
    public async Task Explicitly_clearing_managed_setup_calendar_does_not_resurrect_legacy_json()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE edit_tokens
                    SET holder_client_id = 'timeline-client', holder_user_id = 'planner',
                        generation = 1, acquired_at = '2026-08-11T00:00:00Z', version = version + 1
                    WHERE id = 1;
                    """;
                await command.ExecuteNonQueryAsync();
            }
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "timeline-client");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");

            using var select = await client.PutAsJsonAsync(
                "/api/v1/setup-calendar",
                new { workingCalendarId = "calendar-1" });
            select.EnsureSuccessStatusCode();
            using var clear = await client.DeleteAsync("/api/v1/setup-calendar");
            Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains(
                document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString() == "setup_calendar_defaulted");
        });
    }

    [Fact]
    public async Task Timeline_api_projects_dependency_waiting_with_a_predecessor_tooltip_detail()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
                    VALUES ('machine-2', 'M-2', 'Free child mill', 'mill', 'calendar-1', 'active', 1);
                    UPDATE machine_assignments SET machine_id = 'machine-2', backlog_position = 0
                    WHERE batch_operation_id = 'op-2';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var childMachine = document.RootElement.GetProperty("machines").EnumerateArray()
                .Single(machine => machine.GetProperty("machineId").GetString() == "machine-2");
            var waiting = childMachine.GetProperty("intervals").EnumerateArray()
                .Single(interval => interval.GetProperty("type").GetString() == "waiting");

            Assert.Equal("2026-08-11T08:00:00+00:00", waiting.GetProperty("startsAt").GetString());
            Assert.Equal("2026-08-11T09:30:00+00:00", waiting.GetProperty("endsAt").GetString());
            Assert.Equal("Waiting for OP10 on Machine M-1 to finish.", waiting.GetProperty("detail").GetString());
        });
    }

    [Fact]
    public async Task Timeline_api_reports_unassigned_predecessor_and_does_not_project_the_child()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM machine_assignments WHERE batch_operation_id = 'op-1';";
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains(
                document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString() == "dependency_predecessor_unassigned");
            Assert.DoesNotContain(
                document.RootElement.GetProperty("machines").EnumerateArray()
                    .SelectMany(machine => machine.GetProperty("intervals").EnumerateArray()),
                interval => interval.GetProperty("operationId").GetString() == "op-2");
        });
    }

    private static bool IsWork(JsonElement interval) =>
        interval.GetProperty("type").GetString() is "setup" or "production";

    private static bool Overlaps(JsonElement interval, string startsAt, string endsAt)
    {
        var start = DateTimeOffset.Parse(startsAt);
        var end = DateTimeOffset.Parse(endsAt);
        return interval.GetProperty("endsAt").GetDateTimeOffset() > start
            && interval.GetProperty("startsAt").GetDateTimeOffset() < end;
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
            INSERT INTO employee_resources (
                id, employee_number, name, resource_type, first_name, last_name,
                skills_json, assigned_calendar_id, is_active)
            VALUES
                ('resource-setup', 'E-SETUP', 'Setup Worker', 'setup_worker', 'Setup', 'Worker', '["mill"]', 'calendar-1', 1),
                ('resource-qa', 'E-QA', 'QA Worker', 'qa_worker', 'QA', 'Worker', '[]', 'calendar-1', 1),
                ('resource-regular', 'E-REG', 'Regular Worker', 'regular_worker', 'Regular', 'Worker', '[]', 'calendar-1', 1);
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ('case-1', 'PN-1', 'Timeline Part', 'C:\Cases\PN-1');
            INSERT INTO orders (id, case_id, order_reference, quantity, work_finish_date, status)
            VALUES ('order-1', 'case-1', 'SO-10', 2, '2026-08-12', 'active');
            INSERT INTO production_batches (
                id, case_id, batch_number, status, planned_quantity)
            VALUES ('batch-1', 'case-1', 'B-1', 'waiting', 2);
            INSERT INTO batch_allocations (
                id, production_batch_id, allocation_type, order_id, quantity)
            VALUES ('allocation-1', 'batch-1', 'order', 'order-1', 2);
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
                setup_seconds, cycle_seconds, status, dependency_type,
                predecessor_source_case_operation_id)
            VALUES
                ('op-1', 'batch-1', 'case-op-1', 10, 0, 'First', 'mill', 1800, 1800,
                 'not_started', 'independent', NULL),
                ('op-2', 'batch-1', 'case-op-2', 20, 1, 'Second', 'mill', 0, 900,
                 'not_started', 'sequential', 'case-op-1');
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
