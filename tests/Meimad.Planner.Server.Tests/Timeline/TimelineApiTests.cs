using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meimad.Planner.Server.Tests.Timeline;

public sealed class TimelineApiTests
{
    [Fact]
    public async Task Timeline_exposes_configured_time_scale_context_for_display_only()
    {
        await RunWithServerAsync(async (_, client) =>
        {
            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T00:00:00Z&to=2026-08-12T00:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal("UTC", document.RootElement.GetProperty("displayTimeZoneId").GetString());
            Assert.Equal("07:30", document.RootElement.GetProperty("dayStartsAtLocal").GetString());
            Assert.Equal("16:45", document.RootElement.GetProperty("dayEndsAtLocal").GetString());
        }, configurationArguments:
        [
            "--Timeline:TimeZoneId=UTC",
            "--Timeline:DayShiftStartsAtLocal=07:30",
            "--Timeline:DayShiftEndsAtLocal=16:45"
        ]);
    }

    [Fact]
    public async Task Timeline_mode_query_is_rejected_because_planning_mode_is_assignment_owned()
    {
        await RunWithServerAsync(async (_, client) =>
        {
            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-13T00:00:00Z&mode=backward&asOf=2026-08-11T08:00:00Z");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("timeline_mode_is_assignment_owned",
                document.RootElement.GetProperty("error").GetProperty("code").GetString());
        });
    }

    [Fact]
    public async Task Timeline_rejects_unknown_visual_mode()
    {
        await RunWithServerAsync(async (_, client) =>
        {
            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-12T08:00:00Z&mode=persisted");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("timeline_mode_is_assignment_owned",
                document.RootElement.GetProperty("error").GetProperty("code").GetString());
        });
    }

    [Fact]
    public async Task Timeline_projects_assignment_identity_mode_and_work_finish_date_once_per_active_assignment()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE machine_assignments SET planning_mode = 'backward' WHERE id = 'assignment-1';";
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-13T00:00:00Z&asOf=2026-08-11T08:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var operationBlocks = document.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray()
                .Where(interval => interval.GetProperty("operationId").ValueKind == JsonValueKind.String)
                .ToArray();
            Assert.Equal(operationBlocks.Length, operationBlocks
                .Select(interval => interval.GetProperty("operationId").GetString())
                .Distinct(StringComparer.Ordinal).Count());
            var first = Assert.Single(operationBlocks, interval =>
                interval.GetProperty("operationId").GetString() == "op-1");
            Assert.Equal("assignment-1", first.GetProperty("machineAssignmentId").GetString());
            Assert.Equal("backward", first.GetProperty("planningMode").GetString());
            Assert.Equal("2026-08-12", first.GetProperty("workFinishDate").GetString());
        });
    }

    [Fact]
    public async Task Persisted_mixed_modes_share_one_machine_without_overlap_or_plan_mutation()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE machine_assignments SET planning_mode = 'forward'
                    WHERE id = 'assignment-1';
                    UPDATE machine_assignments SET planning_mode = 'backward'
                    WHERE id = 'assignment-2';
                    """;
                await command.ExecuteNonQueryAsync();
            }
            var beforePositions = await ReadPositionsAsync(application.Services);
            var beforeState = await ReadOperationTimingStateAsync(application.Services);

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-13T00:00:00Z&asOf=2026-08-11T08:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var blocks = document.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray()
                .Where(interval => interval.GetProperty("type").GetString() == "operation")
                .ToArray();
            var forward = Assert.Single(blocks, interval =>
                interval.GetProperty("machineAssignmentId").GetString() == "assignment-1");
            var backward = Assert.Single(blocks, interval =>
                interval.GetProperty("machineAssignmentId").GetString() == "assignment-2");
            Assert.Equal("forward", forward.GetProperty("planningMode").GetString());
            Assert.Equal("backward", backward.GetProperty("planningMode").GetString());
            Assert.True(forward.GetProperty("endsAt").GetDateTimeOffset()
                <= backward.GetProperty("startsAt").GetDateTimeOffset());
            Assert.Equal(beforePositions, await ReadPositionsAsync(application.Services));
            Assert.Equal(beforeState, await ReadOperationTimingStateAsync(application.Services));
        });
    }

    [Fact]
    public async Task Completed_historical_interval_has_no_active_assignment_identity()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    DELETE FROM machine_assignments WHERE batch_operation_id = 'op-1';
                    UPDATE machine_assignments SET backlog_position = 0 WHERE batch_operation_id = 'op-2';
                    UPDATE batch_operations
                    SET status = 'completed', actual_start = '2026-08-11T08:00:00Z',
                        actual_end = '2026-08-11T09:00:00Z', actual_machine_id = 'machine-1'
                    WHERE id = 'op-1';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-13T00:00:00Z&asOf=2026-08-11T09:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var actual = Assert.Single(document.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray(), interval =>
                    interval.GetProperty("operationId").GetString() == "op-1"
                    && interval.GetProperty("timingKind").GetString() == "actual");
            Assert.Equal("actual_history", actual.GetProperty("type").GetString());
            Assert.Equal(JsonValueKind.Null, actual.GetProperty("machineAssignmentId").ValueKind);
        });
    }

    [Fact]
    public async Task Completed_operation_with_stale_assignment_is_one_history_block()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE batch_operations
                    SET status = 'completed', actual_start = '2026-08-11T08:00:00Z',
                        actual_end = '2026-08-11T09:00:00Z', actual_machine_id = 'machine-1'
                    WHERE id = 'op-1';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-13T00:00:00Z&asOf=2026-08-11T09:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var identified = Assert.Single(document.RootElement.GetProperty("machines")
                    .EnumerateArray().SelectMany(machine => machine.GetProperty("intervals").EnumerateArray()),
                interval => interval.GetProperty("operationId").GetString() == "op-1");
            Assert.Equal("actual_history", identified.GetProperty("type").GetString());
            Assert.Equal("actual", identified.GetProperty("timingKind").GetString());
            Assert.Equal(JsonValueKind.Null, identified.GetProperty("machineAssignmentId").ValueKind);
            Assert.Equal(JsonValueKind.Null, identified.GetProperty("planningMode").ValueKind);
        });
    }

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
                value => value.GetProperty("type").GetString() == "operation");
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
            AssertAnonymousTimelineInterval(wait);
            var identified = Assert.Single(document.RootElement.GetProperty("machines")
                    .EnumerateArray().SelectMany(machine => machine.GetProperty("intervals").EnumerateArray()),
                interval => interval.GetProperty("operationId").GetString() == "op-1");
            Assert.Contains(identified.GetProperty("phases").EnumerateArray(), phase =>
                phase.GetProperty("type").GetString() == "waiting"
                && phase.GetProperty("detail").GetString()!
                    .Contains("skilled setup worker", StringComparison.OrdinalIgnoreCase));
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
            Assert.Contains(intervals, value => value.GetProperty("type").GetString() == "operation"
                && value.GetProperty("detail").GetString()!.Contains("Setup", StringComparison.Ordinal)
                && value.GetProperty("detail").GetString()!.Contains("Production", StringComparison.Ordinal));
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
                    && interval.GetProperty("type").GetString() == "operation"
                    && interval.GetProperty("detail").GetString()!
                        .Contains("Setup", StringComparison.Ordinal));
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
            var operation = document.RootElement.GetProperty("machines")[0].GetProperty("intervals")
                .EnumerateArray().First(value => value.GetProperty("type").GetString() == "operation"
                    && value.GetProperty("operationId").GetString() == "op-1");
            Assert.Equal("2026-08-11T03:00:00+00:00", operation.GetProperty("startsAt").GetString());
            Assert.Contains("Setup 2026-08-11T03:00", operation.GetProperty("detail").GetString(),
                StringComparison.Ordinal);
            Assert.Equal(2, operation.GetProperty("detail").GetString()!
                .Split("Production", StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain(
                document.RootElement.GetProperty("conflicts").EnumerateArray(),
                value => value.GetProperty("code").GetString() == "calendar_configuration_missing");
        }, DateTimeOffset.Parse("2026-08-11T03:00:00Z"));
    }

    [Fact]
    public async Task Timeline_expands_an_overnight_employee_calendar_across_midnight()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE working_calendars
                SET time_zone_id = 'UTC',
                    calendar_json = '{"weeklySchedule":{"workdays":["monday"],"windows":[{"startsAtLocal":"17:00","endsAtLocal":"07:00"}]}}'
                WHERE id = 'calendar-1';
                UPDATE application_settings
                SET value = '{"availability":[{"startsAt":"2026-08-10T17:00:00Z","endsAt":"2026-08-11T07:00:00Z"}]}'
                WHERE key = 'timeline.setup_calendar_json';
                UPDATE production_batches SET planned_quantity = 20 WHERE id = 'batch-1';
                DELETE FROM machine_assignments WHERE batch_operation_id = 'op-2';
                """;
            await command.ExecuteNonQueryAsync();

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-10T16:00:00Z&to=2026-08-11T09:00:00Z&asOf=2026-08-10T16:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var operation = Assert.Single(document.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray(), value =>
                    value.GetProperty("operationId").GetString() == "op-1"
                    && value.GetProperty("type").GetString() == "operation");
            Assert.Equal("2026-08-10T17:00:00+00:00", operation.GetProperty("startsAt").GetString());
            Assert.Contains("Production 2026-08-10T17:30", operation.GetProperty("detail").GetString(),
                StringComparison.Ordinal);
            Assert.True(operation.GetProperty("endsAt").GetDateTimeOffset()
                > DateTimeOffset.Parse("2026-08-11T00:00:00Z"));
            Assert.DoesNotContain(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                value => value.GetProperty("code").GetString() == "calendar_configuration_invalid");
        });
    }

    [Fact]
    public async Task Timeline_exposes_weekend_and_custom_nonworking_weekday_as_anonymous_calendar_background()
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
                        calendar_json = '{"weeklySchedule":{"workdays":["friday","monday","wednesday"],"windows":[{"startsAtLocal":"08:00","endsAtLocal":"17:00"}]}}'
                    WHERE id = 'calendar-1';
                    DELETE FROM machine_assignments;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-14T08:00:00Z&to=2026-08-19T17:00:00Z&asOf=2026-08-14T08:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var machine = document.RootElement.GetProperty("machines")[0];
            var backgrounds = machine.GetProperty("nonWorkingWindows").EnumerateArray().ToArray();

            Assert.Collection(backgrounds,
                weekend => AssertCalendarBackground(
                    weekend,
                    "2026-08-14T17:00:00+00:00",
                    "2026-08-17T08:00:00+00:00"),
                closedTuesday => AssertCalendarBackground(
                    closedTuesday,
                    "2026-08-17T17:00:00+00:00",
                    "2026-08-19T08:00:00+00:00"));
            Assert.DoesNotContain(machine.GetProperty("intervals").EnumerateArray(), interval =>
                interval.GetProperty("type").GetString() == "non_working");
        });
    }

    [Fact]
    public async Task Timeline_calendar_background_includes_breaks_and_gaps_between_working_windows()
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
                        calendar_json = '{"weeklySchedule":{"workdays":["tuesday"],"windows":[{"startsAtLocal":"08:00","endsAtLocal":"12:00"},{"startsAtLocal":"13:00","endsAtLocal":"17:00"}],"breakWindows":[{"startsAtLocal":"10:00","endsAtLocal":"10:30"}]}}'
                    WHERE id = 'calendar-1';
                    DELETE FROM machine_assignments;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T07:00:00Z&to=2026-08-11T18:00:00Z&asOf=2026-08-11T07:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var machine = document.RootElement.GetProperty("machines")[0];
            var backgrounds = machine.GetProperty("nonWorkingWindows").EnumerateArray().ToArray();

            Assert.Collection(backgrounds,
                beforeShift => AssertCalendarBackground(
                    beforeShift, "2026-08-11T07:00:00+00:00", "2026-08-11T08:00:00+00:00"),
                breakWindow => AssertCalendarBackground(
                    breakWindow, "2026-08-11T10:00:00+00:00", "2026-08-11T10:30:00+00:00"),
                splitShiftGap => AssertCalendarBackground(
                    splitShiftGap, "2026-08-11T12:00:00+00:00", "2026-08-11T13:00:00+00:00"),
                afterShift => AssertCalendarBackground(
                    afterShift, "2026-08-11T17:00:00+00:00", "2026-08-11T18:00:00+00:00"));
            Assert.DoesNotContain(machine.GetProperty("intervals").EnumerateArray(), interval =>
                interval.GetProperty("type").GetString() == "non_working");
        });
    }

    [Fact]
    public async Task Timeline_calendar_background_keeps_overnight_working_window_available_across_midnight()
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
                        calendar_json = '{"weeklySchedule":{"workdays":["monday"],"windows":[{"startsAtLocal":"17:00","endsAtLocal":"07:00"}]}}'
                    WHERE id = 'calendar-1';
                    DELETE FROM machine_assignments;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-10T16:00:00Z&to=2026-08-11T09:00:00Z&asOf=2026-08-10T16:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var machine = document.RootElement.GetProperty("machines")[0];
            var backgrounds = machine.GetProperty("nonWorkingWindows").EnumerateArray().ToArray();

            Assert.Collection(backgrounds,
                beforeShift => AssertCalendarBackground(
                    beforeShift, "2026-08-10T16:00:00+00:00", "2026-08-10T17:00:00+00:00"),
                afterShift => AssertCalendarBackground(
                    afterShift, "2026-08-11T07:00:00+00:00", "2026-08-11T09:00:00+00:00"));
            Assert.DoesNotContain(backgrounds, interval =>
                interval.GetProperty("startsAt").GetDateTimeOffset() < DateTimeOffset.Parse("2026-08-11T00:00:00Z")
                && interval.GetProperty("endsAt").GetDateTimeOffset() > DateTimeOffset.Parse("2026-08-11T00:00:00Z"));
            Assert.DoesNotContain(machine.GetProperty("intervals").EnumerateArray(), interval =>
                interval.GetProperty("type").GetString() == "non_working");
        });
    }

    [Fact]
    public async Task Timeline_returns_row_specific_calendar_backgrounds_for_machines_with_different_calendars()
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
                        calendar_json = '{"weeklySchedule":{"workdays":["tuesday"],"windows":[{"startsAtLocal":"08:00","endsAtLocal":"12:00"}]}}'
                    WHERE id = 'calendar-1';
                    INSERT INTO working_calendars (id, name, time_zone_id, calendar_json)
                    VALUES ('calendar-2', 'Late shift', 'UTC',
                        '{"weeklySchedule":{"workdays":["tuesday"],"windows":[{"startsAtLocal":"10:00","endsAtLocal":"16:00"}]}}');
                    INSERT INTO machines (
                        id, number, name, machine_type, working_calendar_id, status, is_active)
                    VALUES ('machine-2', 'M-2', 'Mill Two', 'mill', 'calendar-2', 'active', 1);
                    DELETE FROM machine_assignments WHERE batch_operation_id = 'op-2';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T07:00:00Z&to=2026-08-11T18:00:00Z&asOf=2026-08-11T07:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var machines = document.RootElement.GetProperty("machines").EnumerateArray()
                .ToDictionary(machine => machine.GetProperty("machineId").GetString()!, StringComparer.Ordinal);

            Assert.Collection(machines["machine-1"].GetProperty("nonWorkingWindows").EnumerateArray(),
                beforeShift => AssertCalendarBackground(
                    beforeShift, "2026-08-11T07:00:00+00:00", "2026-08-11T08:00:00+00:00"),
                afterShift => AssertCalendarBackground(
                    afterShift, "2026-08-11T12:00:00+00:00", "2026-08-11T18:00:00+00:00"));
            Assert.Collection(machines["machine-2"].GetProperty("nonWorkingWindows").EnumerateArray(),
                beforeShift => AssertCalendarBackground(
                    beforeShift, "2026-08-11T07:00:00+00:00", "2026-08-11T10:00:00+00:00"),
                afterShift => AssertCalendarBackground(
                    afterShift, "2026-08-11T16:00:00+00:00", "2026-08-11T18:00:00+00:00"));

            var downtime = Assert.Single(machines["machine-1"].GetProperty("intervals").EnumerateArray(),
                interval => interval.GetProperty("type").GetString() == "downtime");
            Assert.Equal("2026-08-11T10:00:00+00:00", downtime.GetProperty("startsAt").GetString());
            Assert.Equal("2026-08-11T10:30:00+00:00", downtime.GetProperty("endsAt").GetString());
            AssertAnonymousTimelineInterval(downtime);
            Assert.All(machines.Values, machine =>
                Assert.DoesNotContain(machine.GetProperty("intervals").EnumerateArray(), interval =>
                    interval.GetProperty("type").GetString() == "non_working"));
        });
    }

    [Fact]
    public async Task Machine_calendar_can_include_or_ignore_the_Israel_Master_Calendar()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE working_calendars SET time_zone_id='UTC',
                      calendar_json='{"weeklySchedule":{"workdays":["tuesday"],"windows":[{"startsAtLocal":"08:00","endsAtLocal":"12:00"}]}}'
                    WHERE id='calendar-1';
                    INSERT INTO working_calendars(id,name,time_zone_id,calendar_json)
                    VALUES('israel-master','Israel Master Calendar','UTC',
                      '{"weeklySchedule":{"workdays":["tuesday"],"windows":[{"startsAtLocal":"09:00","endsAtLocal":"11:00"}]}}');
                    UPDATE application_settings SET value='israel-master' WHERE key='master_calendar_id';
                    UPDATE machines SET respect_master_calendar=1 WHERE id='machine-1';
                    DELETE FROM machine_assignments;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            async Task<JsonElement> MachineAsync()
            {
                using var response = await client.GetAsync("/api/v1/timeline?from=2026-08-11T07:00:00Z&to=2026-08-11T18:00:00Z&asOf=2026-08-11T07:00:00Z");
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return document.RootElement.GetProperty("machines").EnumerateArray()
                    .Single(value => value.GetProperty("machineId").GetString() == "machine-1").Clone();
            }

            var layered = await MachineAsync();
            Assert.Collection(layered.GetProperty("nonWorkingWindows").EnumerateArray(),
                before => AssertCalendarBackground(before, "2026-08-11T07:00:00+00:00", "2026-08-11T09:00:00+00:00"),
                after => AssertCalendarBackground(after, "2026-08-11T11:00:00+00:00", "2026-08-11T18:00:00+00:00"));

            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE machines SET respect_master_calendar=0 WHERE id='machine-1';";
                await command.ExecuteNonQueryAsync();
            }
            var independent = await MachineAsync();
            Assert.Collection(independent.GetProperty("nonWorkingWindows").EnumerateArray(),
                before => AssertCalendarBackground(before, "2026-08-11T07:00:00+00:00", "2026-08-11T08:00:00+00:00"),
                after => AssertCalendarBackground(after, "2026-08-11T12:00:00+00:00", "2026-08-11T18:00:00+00:00"));
        });
    }

    [Fact]
    public async Task Invalid_machine_calendar_returns_full_horizon_background_and_existing_blocking_conflict()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE working_calendars SET calendar_json = '{' WHERE id = 'calendar-1';
                    DELETE FROM machine_assignments;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z&asOf=2026-08-11T08:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var machine = document.RootElement.GetProperty("machines")[0];
            var background = Assert.Single(machine.GetProperty("nonWorkingWindows").EnumerateArray());
            AssertCalendarBackground(
                background, "2026-08-11T08:00:00+00:00", "2026-08-11T18:00:00+00:00");
            Assert.Contains(document.RootElement.GetProperty("conflicts").EnumerateArray(), conflict =>
                conflict.GetProperty("code").GetString() == "calendar_configuration_invalid"
                && conflict.GetProperty("severity").GetString() == "blocking");
            Assert.DoesNotContain(machine.GetProperty("intervals").EnumerateArray(), interval =>
                interval.GetProperty("type").GetString() == "non_working");
        });
    }

    [Fact]
    public async Task Not_started_forecast_floats_from_as_of_without_changing_status_or_backlog()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);

            using var originalResponse = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z&asOf=2026-08-11T08:00:00Z");
            originalResponse.EnsureSuccessStatusCode();
            using var original = JsonDocument.Parse(await originalResponse.Content.ReadAsStringAsync());
            var originalStart = original.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray()
                .Where(interval => interval.GetProperty("operationId").GetString() == "op-1"
                    && interval.GetProperty("type").GetString() == "operation")
                .Min(interval => interval.GetProperty("startsAt").GetDateTimeOffset());

            using var floatedResponse = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z&asOf=2026-08-11T10:30:00Z");
            floatedResponse.EnsureSuccessStatusCode();
            using var floated = JsonDocument.Parse(await floatedResponse.Content.ReadAsStringAsync());
            var floatedStart = floated.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray()
                .Where(interval => interval.GetProperty("operationId").GetString() == "op-1"
                    && interval.GetProperty("type").GetString() == "operation")
                .Min(interval => interval.GetProperty("startsAt").GetDateTimeOffset());
            Assert.True(floatedStart >= DateTimeOffset.Parse("2026-08-11T10:30:00Z"));
            Assert.True(floatedStart > originalStart);
            Assert.Contains(floated.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString() == "missed_forecast_start"
                    && conflict.GetProperty("severity").GetString() == "attention");

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            await using var status = connection.CreateCommand();
            status.CommandText = "SELECT status FROM batch_operations WHERE id = 'op-1';";
            Assert.Equal("not_started", await status.ExecuteScalarAsync());
            Assert.Equal(["op-1:0", "op-2:1"], await ReadPositionsAsync(application.Services));
        });
    }

    [Fact]
    public async Task Normal_refresh_uses_server_read_time_as_the_not_started_forecast_cursor()
    {
        var serverNow = DateTimeOffset.Parse("2026-08-11T09:00:00Z");
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(serverNow, document.RootElement.GetProperty("readAt").GetDateTimeOffset());
            var operationBlocks = document.RootElement.GetProperty("machines")
                .EnumerateArray().SelectMany(machine => machine.GetProperty("intervals").EnumerateArray())
                .Where(interval => interval.GetProperty("operationStatus").GetString() == "not_started"
                    && interval.GetProperty("type").GetString() == "operation")
                .ToArray();
            Assert.NotEmpty(operationBlocks);
            Assert.All(operationBlocks, interval => Assert.True(
                interval.GetProperty("startsAt").GetDateTimeOffset() >= serverNow));
            Assert.Equal(["op-1:0", "op-2:1"], await ReadPositionsAsync(application.Services));
        }, serverNow);
    }

    [Fact]
    public async Task Timeline_interleaves_automatic_part_reload_from_persisted_every_n_parts_parameters()
    {
        var serverNow = DateTimeOffset.Parse("2026-08-11T08:00:00Z");
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE orders SET quantity = 5 WHERE id = 'order-1';
                    UPDATE production_batches SET planned_quantity = 5 WHERE id = 'batch-1';
                    UPDATE batch_allocations SET quantity = 5 WHERE id = 'allocation-1';
                    UPDATE batch_operations
                    SET setup_seconds = 0,
                        cycle_seconds = 600,
                        qa_seconds = 0,
                        load_unload_seconds = 300,
                        load_unload_requires_worker = 0,
                        automatic_loading = 1,
                        load_unload_every_n_parts = 2
                    WHERE id = 'op-1';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var operationBlocks = document.RootElement.GetProperty("machines")
                .EnumerateArray()
                .SelectMany(machine => machine.GetProperty("intervals").EnumerateArray())
                .Where(interval => interval.GetProperty("operationId").GetString() == "op-1")
                .ToArray();
            var operation = Assert.Single(operationBlocks);
            Assert.Equal("operation", operation.GetProperty("type").GetString());

            var phases = operation.GetProperty("phases").EnumerateArray().ToArray();
            Assert.Equal(
                ["loadunload", "production", "loadunload", "production", "loadunload", "production"],
                phases.Select(phase => phase.GetProperty("type").GetString()!).ToArray());
            Assert.Equal(
                [
                    "2026-08-11T08:00:00+00:00/2026-08-11T08:05:00+00:00",
                    "2026-08-11T08:05:00+00:00/2026-08-11T08:25:00+00:00",
                    "2026-08-11T08:25:00+00:00/2026-08-11T08:30:00+00:00",
                    "2026-08-11T08:30:00+00:00/2026-08-11T08:50:00+00:00",
                    "2026-08-11T08:50:00+00:00/2026-08-11T08:55:00+00:00",
                    "2026-08-11T08:55:00+00:00/2026-08-11T09:05:00+00:00"
                ],
                phases.Select(phase =>
                    $"{phase.GetProperty("startsAt").GetDateTimeOffset():yyyy-MM-ddTHH:mm:sszzz}/"
                    + $"{phase.GetProperty("endsAt").GetDateTimeOffset():yyyy-MM-ddTHH:mm:sszzz}")
                    .ToArray());
            Assert.Equal(
                ["Part reload 1/3", "Part reload 2/3", "Part reload 3/3"],
                phases.Where(phase => phase.GetProperty("type").GetString() == "loadunload")
                    .Select(phase => phase.GetProperty("detail").GetString()!.Split(';')[0])
                    .ToArray());
        }, serverNow);
    }

    [Fact]
    public async Task Manual_and_forward_same_machine_chain_stays_at_or_after_server_now()
    {
        var serverNow = DateTimeOffset.Parse("2026-08-11T10:30:00Z");
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE machine_assignments SET planning_mode = 'manual'
                    WHERE id = 'assignment-1';
                    UPDATE machine_assignments SET planning_mode = 'forward'
                    WHERE id = 'assignment-2';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var blocks = document.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray()
                .Where(interval => interval.GetProperty("type").GetString() == "operation")
                .ToDictionary(
                    interval => interval.GetProperty("operationId").GetString()!,
                    interval => interval,
                    StringComparer.Ordinal);

            Assert.True(blocks["op-1"].GetProperty("startsAt").GetDateTimeOffset() >= serverNow);
            Assert.True(blocks["op-2"].GetProperty("startsAt").GetDateTimeOffset()
                >= blocks["op-1"].GetProperty("endsAt").GetDateTimeOffset());
            Assert.Equal("manual", blocks["op-1"].GetProperty("planningMode").GetString());
            Assert.Equal("forward", blocks["op-2"].GetProperty("planningMode").GetString());
            Assert.Equal(["op-1:0", "op-2:1"], await ReadPositionsAsync(application.Services));
        }, serverNow);
    }

    [Fact]
    public async Task Not_started_operation_waits_until_the_calendar_reopens_after_server_now()
    {
        var serverNow = DateTimeOffset.Parse("2026-08-11T11:00:00Z");
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE working_calendars
                    SET calendar_json = '{"availability":[{"startsAt":"2026-08-11T08:00:00Z","endsAt":"2026-08-11T10:00:00Z"},{"startsAt":"2026-08-11T13:00:00Z","endsAt":"2026-08-11T18:00:00Z"}]}'
                    WHERE id = 'calendar-1';
                    UPDATE application_settings
                    SET value = '{"availability":[{"startsAt":"2026-08-11T08:00:00Z","endsAt":"2026-08-11T10:00:00Z"},{"startsAt":"2026-08-11T13:00:00Z","endsAt":"2026-08-11T18:00:00Z"}]}'
                    WHERE key = 'timeline.setup_calendar_json';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var first = Assert.Single(document.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray(), interval =>
                    interval.GetProperty("operationId").GetString() == "op-1"
                    && interval.GetProperty("type").GetString() == "operation");
            Assert.Equal(DateTimeOffset.Parse("2026-08-11T13:00:00Z"),
                first.GetProperty("startsAt").GetDateTimeOffset());
        }, serverNow);
    }

    [Fact]
    public async Task Future_backward_slot_remains_latest_fit_when_it_is_after_server_now()
    {
        var serverNow = DateTimeOffset.Parse("2026-08-12T16:00:00Z");
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            await ConfigureThreeDayAvailabilityAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE machine_assignments SET planning_mode = 'backward';";
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-14T00:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var blocks = document.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray()
                .Where(interval => interval.GetProperty("type").GetString() == "operation")
                .ToArray();

            Assert.Equal(2, blocks.Length);
            Assert.Equal(serverNow, blocks.Single(interval =>
                    interval.GetProperty("operationId").GetString() == "op-1")
                .GetProperty("startsAt").GetDateTimeOffset());
            Assert.DoesNotContain(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString() == "backward_start_missed");
        }, serverNow);
    }

    [Fact]
    public async Task Expired_backward_cutoff_that_fits_forward_has_warnings_but_no_stale_blocking_conflict()
    {
        var serverNow = DateTimeOffset.Parse("2026-08-11T08:00:00Z");
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            await ConfigureThreeDayAvailabilityAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE orders SET work_finish_date = '2026-08-10' WHERE id = 'order-1';
                    UPDATE machine_assignments SET planning_mode = 'backward';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-14T00:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var blocks = document.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray()
                .Where(interval => interval.GetProperty("type").GetString() == "operation")
                .ToArray();

            Assert.Equal(2, blocks.Length);
            Assert.All(blocks, block => Assert.True(
                block.GetProperty("startsAt").GetDateTimeOffset() >= serverNow));
            Assert.Contains(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString()
                    == "backward_fallback_required");
            Assert.DoesNotContain(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString() == "backward_start_missed");
            Assert.Contains(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString() == "backward_deadline_missed");
            Assert.DoesNotContain(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString()
                    == "backward_schedule_cannot_fit");
        }, serverNow);
    }

    [Fact]
    public async Task Backward_with_no_pre_cutoff_capacity_uses_next_future_capacity_without_blocking()
    {
        var serverNow = DateTimeOffset.Parse("2026-08-11T08:00:00Z");
        var nextCapacity = DateTimeOffset.Parse("2026-08-12T08:00:00Z");
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE orders SET work_finish_date = '2026-08-11' WHERE id = 'order-1';
                    UPDATE machine_assignments SET planning_mode = 'backward';
                    UPDATE working_calendars
                    SET calendar_json = '{"availability":[{"startsAt":"2026-08-12T08:00:00Z","endsAt":"2026-08-12T18:00:00Z"},{"startsAt":"2026-08-13T08:00:00Z","endsAt":"2026-08-13T18:00:00Z"}]}'
                    WHERE id = 'calendar-1';
                    UPDATE application_settings
                    SET value = '{"availability":[{"startsAt":"2026-08-12T08:00:00Z","endsAt":"2026-08-12T18:00:00Z"},{"startsAt":"2026-08-13T08:00:00Z","endsAt":"2026-08-13T18:00:00Z"}]}'
                    WHERE key = 'timeline.setup_calendar_json';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-14T00:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var blocks = document.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray()
                .Where(interval => interval.GetProperty("type").GetString() == "operation")
                .OrderBy(interval => interval.GetProperty("startsAt").GetDateTimeOffset())
                .ToArray();

            Assert.Equal(2, blocks.Length);
            Assert.Equal(nextCapacity, blocks[0].GetProperty("startsAt").GetDateTimeOffset());
            Assert.True(blocks[1].GetProperty("startsAt").GetDateTimeOffset()
                >= blocks[0].GetProperty("endsAt").GetDateTimeOffset());
            Assert.Contains(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString()
                    == "backward_fallback_required");
            Assert.Contains(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString()
                    == "backward_deadline_missed");
            Assert.DoesNotContain(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString()
                    == "backward_schedule_cannot_fit");
        }, serverNow);
    }

    [Fact]
    public async Task Missed_backward_chain_falls_forward_and_shifts_its_same_machine_child()
    {
        var serverNow = DateTimeOffset.Parse("2026-08-12T16:30:00Z");
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            await ConfigureThreeDayAvailabilityAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE machine_assignments SET planning_mode = 'backward';";
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-14T00:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var blocks = document.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray()
                .Where(interval => interval.GetProperty("type").GetString() == "operation")
                .ToDictionary(
                    interval => interval.GetProperty("operationId").GetString()!,
                    interval => interval,
                    StringComparer.Ordinal);

            Assert.True(blocks["op-1"].GetProperty("startsAt").GetDateTimeOffset() >= serverNow);
            Assert.True(blocks["op-2"].GetProperty("startsAt").GetDateTimeOffset()
                >= blocks["op-1"].GetProperty("endsAt").GetDateTimeOffset());
            Assert.All(blocks.Values, block =>
                Assert.Equal("backward", block.GetProperty("planningMode").GetString()));
            Assert.Contains(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString() == "backward_start_missed"
                    && conflict.GetProperty("operationIds").EnumerateArray()
                        .Any(id => id.GetString() == "op-1"));
            Assert.DoesNotContain(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString() == "backward_start_missed"
                    && conflict.GetProperty("operationIds").EnumerateArray()
                        .Any(id => id.GetString() == "op-2"));
            Assert.Contains(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString()
                    == "backward_fallback_required"
                    && conflict.GetProperty("operationIds").EnumerateArray()
                        .Any(id => id.GetString() == "op-2"));
            Assert.Contains(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString() == "backward_deadline_missed");
            Assert.Equal(["op-1:0", "op-2:1"], await ReadPositionsAsync(application.Services));
        }, serverNow);
    }

    [Fact]
    public async Task Missed_backward_predecessor_shifts_cross_machine_sequential_child_after_its_finish()
    {
        var serverNow = DateTimeOffset.Parse("2026-08-12T16:30:00Z");
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            await ConfigureThreeDayAvailabilityAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO machines (
                        id, number, name, machine_type, working_calendar_id, status, is_active)
                    VALUES ('machine-2', 'M-2', 'Mill Two', 'mill', 'calendar-1', 'active', 1);
                    UPDATE employee_resources
                    SET skills_json = '["machine-1","machine-2"]'
                    WHERE id = 'resource-setup';
                    UPDATE machine_assignments SET planning_mode = 'backward'
                    WHERE id = 'assignment-1';
                    UPDATE machine_assignments
                    SET machine_id = 'machine-2', backlog_position = 0,
                        planning_mode = 'backward'
                    WHERE id = 'assignment-2';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-14T00:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var blocks = document.RootElement.GetProperty("machines")
                .EnumerateArray().SelectMany(machine => machine.GetProperty("intervals").EnumerateArray())
                .Where(interval => interval.GetProperty("type").GetString() == "operation")
                .ToDictionary(
                    interval => interval.GetProperty("operationId").GetString()!,
                    interval => interval,
                    StringComparer.Ordinal);

            Assert.True(blocks["op-1"].GetProperty("startsAt").GetDateTimeOffset() >= serverNow);
            Assert.True(blocks["op-2"].GetProperty("startsAt").GetDateTimeOffset()
                >= blocks["op-1"].GetProperty("endsAt").GetDateTimeOffset());
            Assert.Equal("machine-1", blocks["op-1"].GetProperty("machineId").GetString());
            Assert.Equal("machine-2", blocks["op-2"].GetProperty("machineId").GetString());
            Assert.Contains(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString() == "backward_start_missed"
                    && conflict.GetProperty("operationIds").EnumerateArray()
                        .Any(id => id.GetString() == "op-1"));
            Assert.DoesNotContain(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString() == "backward_start_missed"
                    && conflict.GetProperty("operationIds").EnumerateArray()
                        .Any(id => id.GetString() == "op-2"));
            Assert.Contains(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString()
                    == "backward_fallback_required"
                    && conflict.GetProperty("operationIds").EnumerateArray()
                        .Any(id => id.GetString() == "op-2"));

            await using var verify = await database.OpenConnectionAsync();
            await using var verifyCommand = verify.CreateCommand();
            verifyCommand.CommandText = """
                SELECT id || ':' || machine_id || ':' || backlog_position || ':' || planning_mode
                FROM machine_assignments ORDER BY id;
                """;
            var stored = new List<string>();
            await using var reader = await verifyCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync()) stored.Add(reader.GetString(0));
            Assert.Equal([
                "assignment-1:machine-1:0:backward",
                "assignment-2:machine-2:0:backward"
            ], stored);
        }, serverNow);
    }

    [Fact]
    public async Task Missed_all_backward_locked_group_falls_forward_together()
    {
        var serverNow = DateTimeOffset.Parse("2026-08-12T16:45:00Z");
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            await ConfigureThreeDayAvailabilityAsync(application.Services);
            await ConfigureLockedGroupAsync(application.Services, secondPlanningMode: "backward");

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-14T00:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var blocks = document.RootElement.GetProperty("machines")
                .EnumerateArray().SelectMany(machine => machine.GetProperty("intervals").EnumerateArray())
                .Where(interval => interval.GetProperty("type").GetString() == "operation")
                .ToDictionary(
                    interval => interval.GetProperty("operationId").GetString()!,
                    interval => interval,
                    StringComparer.Ordinal);

            Assert.Equal(2, blocks.Count);
            Assert.True(blocks["op-1"].GetProperty("startsAt").GetDateTimeOffset() >= serverNow);
            Assert.Equal(blocks["op-1"].GetProperty("startsAt").GetDateTimeOffset(),
                blocks["op-2"].GetProperty("startsAt").GetDateTimeOffset());
            Assert.Equal(blocks["op-1"].GetProperty("endsAt").GetDateTimeOffset(),
                blocks["op-2"].GetProperty("endsAt").GetDateTimeOffset());
            Assert.DoesNotContain(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString()
                    == "locked_group_planning_mode_conflict");
            Assert.Contains(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString() == "backward_start_missed");
        }, serverNow);
    }

    [Fact]
    public async Task Originally_mixed_locked_group_remains_a_structured_mode_conflict()
    {
        var serverNow = DateTimeOffset.Parse("2026-08-12T16:45:00Z");
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            await ConfigureThreeDayAvailabilityAsync(application.Services);
            await ConfigureLockedGroupAsync(application.Services, secondPlanningMode: "forward");

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-14T00:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Contains(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString()
                    == "locked_group_planning_mode_conflict");
            Assert.DoesNotContain(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString() == "backward_start_missed");
        }, serverNow);
    }

    [Fact]
    public async Task Missed_backward_without_future_capacity_is_blocked_at_server_now()
    {
        var serverNow = DateTimeOffset.Parse("2026-08-11T17:00:00Z");
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE machine_assignments SET planning_mode = 'backward';";
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var first = Assert.Single(document.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray(), interval =>
                    interval.GetProperty("operationId").GetString() == "op-1");

            Assert.Equal("waiting", first.GetProperty("type").GetString());
            Assert.Equal("blocked", first.GetProperty("timingKind").GetString());
            Assert.Equal(serverNow, first.GetProperty("startsAt").GetDateTimeOffset());
            Assert.Contains(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString()
                    == "backward_schedule_cannot_fit");
        }, serverNow);
    }

    [Fact]
    public async Task Elapsed_horizon_returns_end_boundary_blocks_instead_of_historical_forecasts()
    {
        var serverNow = DateTimeOffset.Parse("2026-08-14T08:00:00Z");
        var horizonEnd = DateTimeOffset.Parse("2026-08-11T18:00:00Z");
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var identified = document.RootElement.GetProperty("machines")
                .EnumerateArray().SelectMany(machine => machine.GetProperty("intervals").EnumerateArray())
                .Where(interval => interval.GetProperty("operationId").ValueKind == JsonValueKind.String)
                .ToArray();

            Assert.Equal(2, identified.Length);
            Assert.All(identified, interval =>
            {
                Assert.Equal("waiting", interval.GetProperty("type").GetString());
                Assert.Equal("blocked", interval.GetProperty("timingKind").GetString());
                Assert.Equal(horizonEnd, interval.GetProperty("startsAt").GetDateTimeOffset());
                Assert.Equal(horizonEnd, interval.GetProperty("endsAt").GetDateTimeOffset());
            });
            Assert.Contains(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString() == "timeline_horizon_elapsed");
            Assert.DoesNotContain(identified, interval =>
                interval.GetProperty("timingKind").GetString() == "forecast");
        }, serverNow);
    }

    [Fact]
    public async Task Timeline_rejects_invalid_optional_as_of()
    {
        await RunWithServerAsync(async (_, client) =>
        {
            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z&asOf=invalid");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        });
    }

    [Fact]
    public async Task In_progress_operation_keeps_actual_start_and_exposes_elapsed_actual_time()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE batch_operations
                    SET status = 'in_progress', actual_start = '2026-08-11T08:30:00Z',
                        actual_machine_id = 'machine-1'
                    WHERE id = 'op-1';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z&asOf=2026-08-11T09:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var intervals = document.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray().ToArray();
            var actual = Assert.Single(intervals, interval =>
                interval.GetProperty("operationId").GetString() == "op-1"
                && interval.GetProperty("timingKind").GetString() == "actual");
            Assert.Equal("2026-08-11T08:30:00+00:00", actual.GetProperty("actualStart").GetString());
            Assert.Equal("operation", actual.GetProperty("type").GetString());
            Assert.Equal("2026-08-11T08:30:00+00:00", actual.GetProperty("forecastStart").GetString());
            Assert.True(actual.GetProperty("endsAt").GetDateTimeOffset()
                >= DateTimeOffset.Parse("2026-08-11T09:00:00Z"));
            Assert.Single(intervals, interval =>
                interval.GetProperty("machineAssignmentId").GetString() == "assignment-1");
        });
    }

    [Fact]
    public async Task Backward_assignment_with_in_progress_work_keeps_actual_start_and_reports_fallback()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE batch_operations
                    SET status = 'in_progress', actual_start = '2026-08-11T08:30:00Z',
                        actual_machine_id = 'machine-1'
                    WHERE id = 'op-1';
                    UPDATE machine_assignments SET planning_mode = 'backward'
                    WHERE id = 'assignment-1';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-13T00:00:00Z&asOf=2026-08-11T09:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains(document.RootElement.GetProperty("conflicts").EnumerateArray(), conflict =>
                conflict.GetProperty("code").GetString() == "backward_in_progress_fallback");
            var calculated = Assert.Single(document.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray(), interval =>
                    interval.GetProperty("machineAssignmentId").GetString() == "assignment-1");
            Assert.Equal("backward", calculated.GetProperty("planningMode").GetString());
            Assert.Equal("2026-08-11T08:30:00+00:00",
                calculated.GetProperty("actualStart").GetString());
            Assert.Equal("2026-08-11T08:30:00+00:00",
                calculated.GetProperty("forecastStart").GetString());
        });
    }

    [Fact]
    public async Task Suspended_operation_keeps_elapsed_actual_and_one_current_assignment_block()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE batch_operations
                    SET status = 'suspended', actual_start = '2026-08-11T08:30:00Z',
                        actual_machine_id = 'machine-1'
                    WHERE id = 'op-1';
                    INSERT INTO operation_pause_events (
                        id, batch_operation_id, reason_type, comment, paused_by,
                        pause_started_at, status, version, created_at, updated_at)
                    VALUES ('pause-actual', 'op-1', 'other', 'Awaiting material', 'planner',
                            '2026-08-11T09:00:00Z', 'active', 1,
                            '2026-08-11T09:00:00Z', '2026-08-11T09:00:00Z');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z&asOf=2026-08-11T10:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var intervals = document.RootElement.GetProperty("machines").EnumerateArray()
                .SelectMany(machine => machine.GetProperty("intervals").EnumerateArray())
                .ToArray();
            var canonical = Assert.Single(intervals, interval =>
                interval.GetProperty("operationId").GetString() == "op-1"
                && interval.GetProperty("type").GetString() == "operation");
            Assert.Equal("hold", canonical.GetProperty("timingKind").GetString());
            Assert.Equal("2026-08-11T08:30:00+00:00",
                canonical.GetProperty("startsAt").GetString());
            Assert.Equal("2026-08-11T18:00:00+00:00",
                canonical.GetProperty("endsAt").GetString());
            Assert.Contains(canonical.GetProperty("phases").EnumerateArray(), phase =>
                phase.GetProperty("type").GetString() == "production"
                && phase.GetProperty("endsAt").GetDateTimeOffset()
                    == DateTimeOffset.Parse("2026-08-11T09:00:00Z"));
            Assert.Contains(canonical.GetProperty("phases").EnumerateArray(), phase =>
                phase.GetProperty("type").GetString() == "waiting"
                && phase.GetProperty("startsAt").GetDateTimeOffset()
                    == DateTimeOffset.Parse("2026-08-11T09:00:00Z"));
            Assert.DoesNotContain(intervals, interval =>
                interval.GetProperty("operationId").GetString() == "op-1"
                && interval.GetProperty("type").GetString() == "waiting");
            Assert.Single(intervals, interval =>
                interval.GetProperty("machineAssignmentId").GetString() == "assignment-1");
        });
    }

    [Fact]
    public async Task Moved_pause_and_resume_keep_one_identified_block_and_fold_old_machine_history()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO machines (
                        id, number, name, machine_type, working_calendar_id, status, is_active)
                    VALUES ('machine-2', 'M-2', 'Mill Two', 'mill', 'calendar-1', 'active', 1);
                    UPDATE employee_resources
                    SET skills_json = '["machine-1","machine-2"]'
                    WHERE id = 'resource-setup';
                    UPDATE batch_operations
                    SET status = 'suspended', actual_start = '2026-08-11T08:30:00Z',
                        actual_machine_id = 'machine-1'
                    WHERE id = 'op-1';
                    INSERT INTO operation_pause_events (
                        id, batch_operation_id, reason_type, comment, paused_by,
                        pause_started_at, status, version, created_at, updated_at)
                    VALUES ('pause-moved', 'op-1', 'other', 'Move while paused', 'planner',
                            '2026-08-11T09:00:00Z', 'active', 1,
                            '2026-08-11T09:00:00Z', '2026-08-11T09:00:00Z');
                    UPDATE machine_assignments
                    SET machine_id = 'machine-2', backlog_position = 0,
                        planning_mode = 'backward',
                        created_at = '2026-08-11T08:00:00Z',
                        updated_at = '2026-08-11T09:50:00Z'
                    WHERE id = 'assignment-1';
                    UPDATE machine_assignments SET backlog_position = 0
                    WHERE id = 'assignment-2';
                    INSERT INTO structured_event_log (
                        id, event_type, occurred_at, user_id,
                        related_entity_ids_json, before_data_json, after_data_json)
                    VALUES (
                        'timeline-machine-move', 'manual_backlog_reorder',
                        '2026-08-11T09:30:00Z', 'planner',
                        '{"batchOperationId":"op-1","machineId":"machine-2"}',
                        '{"machineId":"machine-1","backlogPosition":0}',
                        '{"machineId":"machine-2","backlogPosition":0}');
                    INSERT INTO structured_event_log (
                        id, event_type, occurred_at, user_id,
                        related_entity_ids_json, before_data_json, after_data_json)
                    VALUES (
                        'timeline-same-machine-reorder', 'manual_backlog_reorder',
                        '2026-08-11T09:45:00Z', 'planner',
                        '{"batchOperationId":"op-1","machineId":"machine-2"}',
                        '{"machineId":"machine-2","backlogPosition":1}',
                        '{"machineId":"machine-2","backlogPosition":0}');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using (var pausedResponse = await client.GetAsync(
                       "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z&asOf=2026-08-11T10:00:00Z"))
            {
                pausedResponse.EnsureSuccessStatusCode();
                using var pausedDocument = JsonDocument.Parse(
                    await pausedResponse.Content.ReadAsStringAsync());
                var pausedMachines = pausedDocument.RootElement.GetProperty("machines")
                    .EnumerateArray().ToArray();
                var pausedIntervals = pausedMachines
                    .SelectMany(machine => machine.GetProperty("intervals").EnumerateArray())
                    .ToArray();
                var current = Assert.Single(pausedIntervals, interval =>
                    interval.GetProperty("operationId").GetString() == "op-1"
                    && interval.GetProperty("type").GetString() == "operation");
                Assert.Equal("assignment-1", current.GetProperty("machineAssignmentId").GetString());
                Assert.Equal("machine-2", current.GetProperty("machineId").GetString());
                Assert.Equal("hold", current.GetProperty("timingKind").GetString());
                Assert.Equal("2026-08-11T09:30:00+00:00",
                    current.GetProperty("startsAt").GetString());
                Assert.Equal("backward", current.GetProperty("planningMode").GetString());
                Assert.DoesNotContain(pausedIntervals, interval =>
                    interval.GetProperty("operationId").GetString() == "op-1"
                    && interval.GetProperty("type").GetString() == "waiting");

                Assert.Single(pausedIntervals, interval =>
                    interval.GetProperty("operationId").GetString() == "op-1");
                var pausedHistory = Assert.Single(current.GetProperty("phases").EnumerateArray(),
                    phase => phase.GetProperty("type").GetString() == "actual_history");
                Assert.Equal("2026-08-11T08:30:00+00:00",
                    pausedHistory.GetProperty("startsAt").GetString());
                Assert.Equal("2026-08-11T09:00:00+00:00",
                    pausedHistory.GetProperty("endsAt").GetString());
                Assert.Contains("Actual history on M-1 — Mill One",
                    current.GetProperty("detail").GetString(), StringComparison.Ordinal);
                var oldMachineHistory = Assert.Single(pausedMachines
                        .Single(machine => machine.GetProperty("machineId").GetString() == "machine-1")
                        .GetProperty("intervals").EnumerateArray(),
                    interval => interval.GetProperty("type").GetString() == "actual_history");
                AssertAnonymousTimelineInterval(oldMachineHistory);
                Assert.Equal("2026-08-11T08:30:00+00:00",
                    oldMachineHistory.GetProperty("startsAt").GetString());
                Assert.Equal("2026-08-11T09:00:00+00:00",
                    oldMachineHistory.GetProperty("endsAt").GetString());
                Assert.Single(pausedIntervals, interval =>
                    interval.GetProperty("machineAssignmentId").GetString() == "assignment-1");
            }

            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE batch_operations SET status = 'in_progress' WHERE id = 'op-1';
                    UPDATE operation_pause_events
                    SET status = 'closed', pause_ended_at = '2026-08-11T10:00:00Z',
                        updated_at = '2026-08-11T10:00:00Z', version = version + 1
                    WHERE id = 'pause-moved';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var resumedResponse = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z&asOf=2026-08-11T10:30:00Z");
            resumedResponse.EnsureSuccessStatusCode();
            using var resumedDocument = JsonDocument.Parse(
                await resumedResponse.Content.ReadAsStringAsync());
            var resumedMachines = resumedDocument.RootElement.GetProperty("machines")
                .EnumerateArray().ToArray();
            var resumedIntervals = resumedMachines
                .SelectMany(machine => machine.GetProperty("intervals").EnumerateArray())
                .ToArray();
            var resumedCurrent = Assert.Single(resumedIntervals, interval =>
                interval.GetProperty("operationId").GetString() == "op-1"
                && interval.GetProperty("type").GetString() == "operation");
            Assert.Equal("machine-2", resumedCurrent.GetProperty("machineId").GetString());
            Assert.Equal("2026-08-11T10:00:00+00:00",
                resumedCurrent.GetProperty("startsAt").GetString());
            Assert.Single(resumedIntervals, interval =>
                interval.GetProperty("operationId").GetString() == "op-1");
            var resumedHistory = Assert.Single(resumedCurrent.GetProperty("phases").EnumerateArray(),
                phase => phase.GetProperty("type").GetString() == "actual_history");
            Assert.Equal("2026-08-11T09:00:00+00:00",
                resumedHistory.GetProperty("endsAt").GetString());
            Assert.True(resumedHistory.GetProperty("endsAt").GetDateTimeOffset()
                <= resumedCurrent.GetProperty("startsAt").GetDateTimeOffset());
            var resumedOldMachineHistory = Assert.Single(resumedMachines
                    .Single(machine => machine.GetProperty("machineId").GetString() == "machine-1")
                    .GetProperty("intervals").EnumerateArray(),
                interval => interval.GetProperty("type").GetString() == "actual_history");
            AssertAnonymousTimelineInterval(resumedOldMachineHistory);
            Assert.Single(resumedIntervals, interval =>
                interval.GetProperty("machineAssignmentId").GetString() == "assignment-1");
        });
    }

    [Fact]
    public async Task Suspended_unassign_then_assign_folds_history_without_backdating_current_block()
    {
        var reassignedAt = DateTimeOffset.Parse("2026-08-11T09:30:00Z");
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO machines (
                        id, number, name, machine_type, working_calendar_id, status, is_active)
                    VALUES ('machine-2', 'M-2', 'Mill Two', 'mill', 'calendar-1', 'active', 1);
                    UPDATE employee_resources
                    SET skills_json = '["machine-1","machine-2"]'
                    WHERE id = 'resource-setup';
                    UPDATE batch_operations
                    SET status = 'suspended', actual_start = '2026-08-11T08:30:00Z',
                        actual_machine_id = 'machine-1'
                    WHERE id = 'op-1';
                    INSERT INTO operation_pause_events (
                        id, batch_operation_id, reason_type, comment, paused_by,
                        pause_started_at, status, version, created_at, updated_at)
                    VALUES ('pause-reassigned', 'op-1', 'other', 'Reassign while paused', 'planner',
                            '2026-08-11T09:00:00Z', 'active', 1,
                            '2026-08-11T09:00:00Z', '2026-08-11T09:00:00Z');
                    UPDATE edit_tokens
                    SET holder_client_id = 'timeline-reassign-client',
                        holder_user_id = 'timeline-planner', generation = 1,
                        acquired_at = '2026-08-11T09:15:00Z', lease_expires_at = NULL
                    WHERE id = 1;
                    """;
                await command.ExecuteNonQueryAsync();
            }
            client.DefaultRequestHeaders.Add(
                "X-Meimad-Client-Id", "timeline-reassign-client");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");

            using var unassign = await client.DeleteAsync(
                "/api/v1/batch-operations/op-1/assignment");
            Assert.Equal(HttpStatusCode.NoContent, unassign.StatusCode);
            using var assign = await client.PutAsJsonAsync(
                "/api/v1/batch-operations/op-1/assignment",
                new { machineId = "machine-2", backlogPosition = 0 });
            Assert.Equal(HttpStatusCode.Created, assign.StatusCode);
            using var assignmentDocument = JsonDocument.Parse(
                await assign.Content.ReadAsStringAsync());
            var assignmentId = assignmentDocument.RootElement
                .GetProperty("machineAssignmentId").GetString();
            Assert.NotEqual("assignment-1", assignmentId);

            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT created_at,
                           (SELECT COUNT(*)
                            FROM structured_event_log
                            WHERE event_type = 'manual_backlog_reorder'
                              AND json_extract(related_entity_ids_json, '$.batchOperationId') = 'op-1')
                    FROM machine_assignments
                    WHERE batch_operation_id = 'op-1';
                    """;
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(reassignedAt, reader.GetDateTimeOffset(0));
                Assert.Equal(0, reader.GetInt32(1));
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z&asOf=2026-08-11T10:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var machines = document.RootElement.GetProperty("machines").EnumerateArray().ToArray();
            var intervals = machines
                .SelectMany(machine => machine.GetProperty("intervals").EnumerateArray())
                .ToArray();
            var current = Assert.Single(intervals, interval =>
                interval.GetProperty("operationId").GetString() == "op-1"
                && interval.GetProperty("type").GetString() == "operation");
            Assert.Equal("machine-2", current.GetProperty("machineId").GetString());
            Assert.Equal("hold", current.GetProperty("timingKind").GetString());
            Assert.Equal(reassignedAt, current.GetProperty("startsAt").GetDateTimeOffset());
            Assert.Equal(assignmentId, current.GetProperty("machineAssignmentId").GetString());

            var history = Assert.Single(current.GetProperty("phases").EnumerateArray(),
                phase => phase.GetProperty("type").GetString() == "actual_history");
            Assert.Equal(DateTimeOffset.Parse("2026-08-11T09:00:00Z"),
                history.GetProperty("endsAt").GetDateTimeOffset());
            Assert.True(history.GetProperty("endsAt").GetDateTimeOffset()
                <= current.GetProperty("startsAt").GetDateTimeOffset());
            var oldMachineHistory = Assert.Single(machines
                    .Single(machine => machine.GetProperty("machineId").GetString() == "machine-1")
                    .GetProperty("intervals").EnumerateArray(),
                interval => interval.GetProperty("type").GetString() == "actual_history");
            AssertAnonymousTimelineInterval(oldMachineHistory);
            Assert.Single(intervals, interval =>
                interval.GetProperty("operationId").GetString() == "op-1");
            Assert.Single(intervals, interval =>
                interval.GetProperty("machineAssignmentId").GetString() == assignmentId);
        }, reassignedAt);
    }

    [Fact]
    public async Task Unassigned_paused_operation_retains_actual_history_and_unassigned_conflict()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    DELETE FROM machine_assignments WHERE id = 'assignment-1';
                    UPDATE machine_assignments SET backlog_position = 0
                    WHERE id = 'assignment-2';
                    UPDATE batch_operations
                    SET status = 'suspended', actual_start = '2026-08-11T08:30:00Z',
                        actual_machine_id = 'machine-1'
                    WHERE id = 'op-1';
                    INSERT INTO operation_pause_events (
                        id, batch_operation_id, reason_type, comment, paused_by,
                        pause_started_at, status, version, created_at, updated_at)
                    VALUES ('pause-unassigned', 'op-1', 'other', 'Unassigned while paused', 'planner',
                            '2026-08-11T09:00:00Z', 'active', 1,
                            '2026-08-11T09:00:00Z', '2026-08-11T09:00:00Z');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z&asOf=2026-08-11T10:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var actual = Assert.Single(document.RootElement.GetProperty("machines")
                .EnumerateArray()
                .SelectMany(machine => machine.GetProperty("intervals").EnumerateArray()),
                interval => interval.GetProperty("operationId").GetString() == "op-1"
                    && interval.GetProperty("type").GetString() == "actual_history");
            Assert.Equal("2026-08-11T08:30:00+00:00", actual.GetProperty("startsAt").GetString());
            Assert.Equal("2026-08-11T09:00:00+00:00", actual.GetProperty("endsAt").GetString());
            Assert.Equal(JsonValueKind.Null,
                actual.GetProperty("machineAssignmentId").ValueKind);
            Assert.Contains(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString() == "unassigned_operation"
                    && conflict.GetProperty("operationIds").EnumerateArray()
                        .Any(id => id.GetString() == "op-1"));
        });
    }

    [Fact]
    public async Task Completed_parent_is_historical_and_child_uses_its_actual_finish()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    DELETE FROM machine_assignments WHERE batch_operation_id = 'op-1';
                    UPDATE machine_assignments SET backlog_position = 0 WHERE batch_operation_id = 'op-2';
                    UPDATE batch_operations
                    SET status = 'completed', actual_start = '2026-08-11T08:00:00Z',
                        actual_end = '2026-08-11T10:00:00Z', actual_machine_id = 'machine-1'
                    WHERE id = 'op-1';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z&asOf=2026-08-11T09:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var intervals = document.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray().ToArray();
            Assert.Contains(intervals, interval =>
                interval.GetProperty("operationId").GetString() == "op-1"
                && interval.GetProperty("timingKind").GetString() == "actual"
                && interval.GetProperty("actualEnd").GetDateTimeOffset()
                    == DateTimeOffset.Parse("2026-08-11T10:00:00Z"));
            Assert.True(intervals.Where(interval =>
                    interval.GetProperty("operationId").GetString() == "op-2"
                    && interval.GetProperty("timingKind").GetString() == "forecast")
                .Min(interval => interval.GetProperty("startsAt").GetDateTimeOffset())
                >= DateTimeOffset.Parse("2026-08-11T10:00:00Z"));
            Assert.Contains(document.RootElement.GetProperty("dependencies").EnumerateArray(),
                dependency => dependency.GetProperty("fromOperationId").GetString() == "op-1"
                    && dependency.GetProperty("toOperationId").GetString() == "op-2");
        });
    }

    [Fact]
    public async Task Api_assignments_persist_reload_and_project_same_case_operations_on_different_machines()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    DELETE FROM machine_assignments;
                    INSERT INTO machines (
                        id, number, name, machine_type, working_calendar_id, status, is_active)
                    VALUES ('machine-2', 'M-2', 'Second mill', 'mill', 'calendar-1', 'active', 1);
                    UPDATE edit_tokens
                    SET holder_client_id = 'timeline-assignment-client',
                        holder_user_id = 'timeline-planner', generation = 1,
                        acquired_at = '2026-08-11T00:00:00Z'
                    WHERE id = 1;
                    """;
                await command.ExecuteNonQueryAsync();
            }
            client.DefaultRequestHeaders.Add(
                "X-Meimad-Client-Id", "timeline-assignment-client");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");

            using var assignFirst = await client.PutAsJsonAsync(
                "/api/v1/batch-operations/op-1/assignment",
                new { machineId = "machine-1", backlogPosition = 0 });
            using var assignSecond = await client.PutAsJsonAsync(
                "/api/v1/batch-operations/op-2/assignment",
                new { machineId = "machine-2", backlogPosition = 0 });
            Assert.Equal(HttpStatusCode.Created, assignFirst.StatusCode);
            Assert.Equal(HttpStatusCode.Created, assignSecond.StatusCode);

            using var boardResponse = await client.GetAsync("/api/v1/planning-board");
            boardResponse.EnsureSuccessStatusCode();
            using var board = JsonDocument.Parse(await boardResponse.Content.ReadAsStringAsync());
            Assert.Equal("op-1", board.RootElement.GetProperty("machines").EnumerateArray()
                .Single(value => value.GetProperty("machineId").GetString() == "machine-1")
                .GetProperty("backlog")[0].GetProperty("batchOperationId").GetString());
            Assert.Equal("op-2", board.RootElement.GetProperty("machines").EnumerateArray()
                .Single(value => value.GetProperty("machineId").GetString() == "machine-2")
                .GetProperty("backlog")[0].GetProperty("batchOperationId").GetString());

            using var timelineResponse = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            timelineResponse.EnsureSuccessStatusCode();
            using var timeline = JsonDocument.Parse(await timelineResponse.Content.ReadAsStringAsync());
            var intervals = timeline.RootElement.GetProperty("machines").EnumerateArray()
                .SelectMany(machine => machine.GetProperty("intervals").EnumerateArray())
                .ToArray();
            Assert.Contains(intervals, value => value.GetProperty("operationId").GetString() == "op-1");
            Assert.Contains(intervals, value => value.GetProperty("operationId").GetString() == "op-2");
            var firstBlock = Assert.Single(intervals, value =>
                value.GetProperty("operationId").GetString() == "op-1"
                && value.GetProperty("type").GetString() == "operation");
            var secondBlock = Assert.Single(intervals, value =>
                value.GetProperty("operationId").GetString() == "op-2"
                && value.GetProperty("type").GetString() == "operation");
            Assert.Contains("Setup", firstBlock.GetProperty("detail").GetString(), StringComparison.Ordinal);
            Assert.Contains("Production", firstBlock.GetProperty("detail").GetString(), StringComparison.Ordinal);
            Assert.Contains("Production", secondBlock.GetProperty("detail").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain(timeline.RootElement.GetProperty("conflicts").EnumerateArray(),
                value => value.GetProperty("code").GetString() == "dependency_cycle");

            await using var verify = await database.OpenConnectionAsync();
            await using var persisted = verify.CreateCommand();
            persisted.CommandText = """
                SELECT batch_operation_id || ':' || machine_id || ':' || backlog_position
                FROM machine_assignments ORDER BY batch_operation_id;
                """;
            var saved = new List<string>();
            await using var reader = await persisted.ExecuteReaderAsync();
            while (await reader.ReadAsync()) saved.Add(reader.GetString(0));
            Assert.Equal(["op-1:machine-1:0", "op-2:machine-2:0"], saved);
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
            var exceptionPhases = exceptionWork[0].GetProperty("detail").GetString()!;
            Assert.Contains("to 2026-08-11T12:00:00", exceptionPhases, StringComparison.Ordinal);
            Assert.Contains("Production 2026-08-11T13:00:00", exceptionPhases, StringComparison.Ordinal);
            Assert.Contains(exceptionDocument.RootElement.GetProperty("machines")[0]
                    .GetProperty("intervals").EnumerateArray(),
                interval => interval.GetProperty("type").GetString() == "waiting"
                    && interval.GetProperty("operationId").ValueKind == JsonValueKind.Null
                    && interval.GetProperty("operationNumber").ValueKind == JsonValueKind.Null
                    && interval.GetProperty("detail").GetString()!
                        .Contains("machine working calendar", StringComparison.OrdinalIgnoreCase));
            Assert.Single(exceptionDocument.RootElement.GetProperty("machines")
                .EnumerateArray().SelectMany(machine => machine.GetProperty("intervals").EnumerateArray()),
                interval => interval.GetProperty("operationId").GetString() == "op-1");
            var exceptionIntervals = exceptionDocument.RootElement.GetProperty("machines")
                .EnumerateArray().SelectMany(machine => machine.GetProperty("intervals").EnumerateArray())
                .ToArray();
            Assert.All(exceptionIntervals.Where(interval =>
                    interval.GetProperty("type").GetString() is "waiting" or "downtime"),
                AssertAnonymousTimelineInterval);
            var op1 = Assert.Single(exceptionIntervals, interval =>
                interval.GetProperty("operationId").GetString() == "op-1");
            Assert.Contains(op1.GetProperty("phases").EnumerateArray(), phase =>
                phase.GetProperty("type").GetString() == "waiting"
                && phase.GetProperty("detail").GetString()!
                    .Contains("Planned maintenance", StringComparison.OrdinalIgnoreCase));

            using var weeklyResponse = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-18T08:00:00Z&to=2026-08-18T18:00:00Z");
            weeklyResponse.EnsureSuccessStatusCode();
            using var weeklyDocument = JsonDocument.Parse(await weeklyResponse.Content.ReadAsStringAsync());
            var weeklyWork = weeklyDocument.RootElement.GetProperty("machines")[0].GetProperty("intervals")
                .EnumerateArray().Where(IsWork).ToArray();
            Assert.NotEmpty(weeklyWork);
            Assert.Equal("2026-08-18T08:00:00+00:00", weeklyWork[0].GetProperty("startsAt").GetString());
            var weeklyPhases = weeklyWork[0].GetProperty("detail").GetString()!;
            Assert.Contains("to 2026-08-18T09:00:00", weeklyPhases, StringComparison.Ordinal);
            Assert.Contains("Production 2026-08-18T10:00:00", weeklyPhases, StringComparison.Ordinal);
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
            var operation = document.RootElement.GetProperty("machines")[0].GetProperty("intervals")
                .EnumerateArray().First(value => value.GetProperty("type").GetString() == "operation"
                    && value.GetProperty("operationId").GetString() == "op-1");
            Assert.Equal("2026-08-11T12:00:00+00:00", operation.GetProperty("startsAt").GetString());
            Assert.Contains("Setup 2026-08-11T12:00", operation.GetProperty("detail").GetString(),
                StringComparison.Ordinal);
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
    public async Task Timeline_api_loads_a_three_operation_chain_and_shifts_every_child_after_its_predecessor()
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
                    VALUES
                        ('machine-2', 'M-2', 'Second mill', 'mill', 'calendar-1', 'active', 1),
                        ('machine-3', 'M-3', 'Third mill', 'mill', 'calendar-1', 'active', 1);
                    UPDATE machine_assignments SET machine_id = 'machine-2', backlog_position = 0
                    WHERE batch_operation_id = 'op-2';
                    INSERT INTO case_operations (
                        id, case_id, operation_number, route_position, name, required_machine_type,
                        setup_seconds, cycle_seconds, dependency_type, predecessor_case_operation_id)
                    VALUES ('case-op-3', 'case-1', 30, 2, 'Third', 'mill', 0, 900,
                            'sequential', 'case-op-2');
                    INSERT INTO batch_operations (
                        id, production_batch_id, source_case_operation_id, operation_number, route_position,
                        name, required_machine_type, setup_seconds, cycle_seconds, status,
                        dependency_type, predecessor_source_case_operation_id)
                    VALUES ('op-3', 'batch-1', 'case-op-3', 30, 2, 'Third', 'mill', 0, 900,
                            'not_started', 'sequential', 'case-op-2');
                    INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position)
                    VALUES ('assignment-3', 'op-3', 'machine-3', 0);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z&asOf=2026-08-11T08:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var intervals = document.RootElement.GetProperty("machines").EnumerateArray()
                .SelectMany(machine => machine.GetProperty("intervals").EnumerateArray())
                .Where(interval => interval.GetProperty("operationId").GetString() is "op-1" or "op-2" or "op-3")
                .ToArray();
            var work = intervals.Where(interval => interval.GetProperty("type").GetString()
                == "operation").ToArray();
            var op1End = work.Where(interval => interval.GetProperty("operationId").GetString() == "op-1")
                .Max(interval => interval.GetProperty("endsAt").GetDateTimeOffset());
            var op2Start = work.Where(interval => interval.GetProperty("operationId").GetString() == "op-2")
                .Min(interval => interval.GetProperty("startsAt").GetDateTimeOffset());
            var op2End = work.Where(interval => interval.GetProperty("operationId").GetString() == "op-2")
                .Max(interval => interval.GetProperty("endsAt").GetDateTimeOffset());
            var op3Start = work.Where(interval => interval.GetProperty("operationId").GetString() == "op-3")
                .Min(interval => interval.GetProperty("startsAt").GetDateTimeOffset());

            Assert.True(op2Start >= op1End);
            Assert.True(op3Start >= op2End);
            Assert.Equal(3, intervals.Length);
            Assert.Contains(work.Single(value => value.GetProperty("operationId").GetString() == "op-2")
                    .GetProperty("phases").EnumerateArray(),
                phase => phase.GetProperty("type").GetString() == "waiting"
                    && phase.GetProperty("detail").GetString()!
                        .Contains("OP10", StringComparison.Ordinal));
            Assert.Contains(document.RootElement.GetProperty("dependencies").EnumerateArray(), value =>
                value.GetProperty("fromOperationId").GetString() == "op-1"
                && value.GetProperty("toOperationId").GetString() == "op-2");
            Assert.Contains(document.RootElement.GetProperty("dependencies").EnumerateArray(), value =>
                value.GetProperty("fromOperationId").GetString() == "op-2"
                && value.GetProperty("toOperationId").GetString() == "op-3");
        });
    }

    [Fact]
    public async Task Timeline_api_reports_unassigned_predecessor_and_projects_child_as_waiting()
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
            var waiting = Assert.Single(
                document.RootElement.GetProperty("machines").EnumerateArray()
                    .SelectMany(machine => machine.GetProperty("intervals").EnumerateArray()),
                interval => interval.GetProperty("operationId").GetString() == "op-2");
            Assert.Equal("waiting", waiting.GetProperty("type").GetString());
            Assert.Contains("not assigned", waiting.GetProperty("detail").GetString(),
                StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Paused_operation_and_later_backlog_operation_remain_visible_as_waiting()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE batch_operations SET status = 'suspended' WHERE id = 'op-1';
                    INSERT INTO operation_pause_events (
                        id, batch_operation_id, reason_type, comment, paused_by,
                        pause_started_at, status, version, created_at, updated_at)
                    VALUES ('pause-timeline', 'op-1', 'other', 'Awaiting supervisor', 'planner',
                            '2026-08-11T08:15:00Z', 'active', 1,
                            '2026-08-11T08:15:00Z', '2026-08-11T08:15:00Z');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var intervals = document.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray().ToArray();
            var paused = Assert.Single(intervals, interval =>
                interval.GetProperty("operationId").GetString() == "op-1"
                && interval.GetProperty("type").GetString() == "operation");
            Assert.Equal("hold", paused.GetProperty("timingKind").GetString());
            Assert.Contains("paused by planner", paused.GetProperty("detail").GetString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(intervals, interval =>
                interval.GetProperty("operationId").GetString() == "op-1"
                && interval.GetProperty("type").GetString() == "waiting");
            var laterBlocked = Assert.Single(intervals, interval =>
                interval.GetProperty("operationId").GetString() == "op-2"
                && interval.GetProperty("type").GetString() == "waiting"
                && interval.GetProperty("detail").GetString()!
                    .Contains("stored Machine backlog order", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("blocked", laterBlocked.GetProperty("timingKind").GetString());
            Assert.Equal("not_started", laterBlocked.GetProperty("operationStatus").GetString());
            Assert.Single(intervals, interval =>
                interval.GetProperty("type").GetString() == "operation");
            Assert.Equal(["op-1:0", "op-2:1"], await ReadPositionsAsync(application.Services));
        });
    }

    [Fact]
    public async Task Invalid_head_operation_blocks_later_backlog_without_reordering()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE batch_operations SET setup_seconds = NULL WHERE id = 'op-1';";
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var operationIntervals = document.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray()
                .Where(interval => interval.GetProperty("operationId").ValueKind == JsonValueKind.String)
                .ToArray();
            Assert.Contains(operationIntervals, interval =>
                interval.GetProperty("operationId").GetString() == "op-1"
                && interval.GetProperty("type").GetString() == "waiting"
                && interval.GetProperty("detail").GetString()!
                    .Contains("missing setup or cycle", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(operationIntervals, interval =>
                interval.GetProperty("operationId").GetString() == "op-2"
                && interval.GetProperty("type").GetString() == "waiting");
            Assert.DoesNotContain(operationIntervals, interval =>
                interval.GetProperty("operationId").GetString() == "op-2"
                && interval.GetProperty("type").GetString() == "operation");
            Assert.Equal(["op-1:0", "op-2:1"], await ReadPositionsAsync(application.Services));
        });
    }

    [Fact]
    public async Task Zero_duration_assigned_operation_is_visible_as_structured_blocked_waiting()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE batch_operations
                    SET setup_seconds = 0, cycle_seconds = 0, qa_seconds = 0,
                        load_unload_seconds = 0
                    WHERE id = 'op-1';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains(document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString() == "zero_duration");
            Assert.Contains(document.RootElement.GetProperty("machines")[0]
                    .GetProperty("intervals").EnumerateArray(),
                interval => interval.GetProperty("operationId").GetString() == "op-1"
                    && interval.GetProperty("type").GetString() == "waiting"
                    && interval.GetProperty("detail").GetString()!
                        .Contains("no calculable", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task Unplaceable_later_assignment_is_blocked_only_after_preceding_scheduled_work()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO case_operations (
                        id, case_id, operation_number, route_position, name,
                        required_machine_type, setup_seconds, cycle_seconds,
                        dependency_type, predecessor_case_operation_id)
                    VALUES ('case-op-3', 'case-1', 30, 2, 'Third', 'mill', NULL, 900,
                            'sequential', 'case-op-2');
                    INSERT INTO batch_operations (
                        id, production_batch_id, source_case_operation_id,
                        operation_number, route_position, name, required_machine_type,
                        setup_seconds, cycle_seconds, status, dependency_type,
                        predecessor_source_case_operation_id)
                    VALUES ('op-3', 'batch-1', 'case-op-3', 30, 2, 'Third', 'mill', NULL, 900,
                            'not_started', 'sequential', 'case-op-2');
                    INSERT INTO machine_assignments (
                        id, batch_operation_id, machine_id, backlog_position)
                    VALUES ('assignment-3', 'op-3', 'machine-1', 2);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z&asOf=2026-08-11T08:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var identified = document.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray()
                .Where(interval => interval.GetProperty("operationId").ValueKind == JsonValueKind.String)
                .ToArray();
            var scheduled = Assert.Single(identified, interval =>
                interval.GetProperty("operationId").GetString() == "op-1");
            var second = Assert.Single(identified, interval =>
                interval.GetProperty("operationId").GetString() == "op-2");
            var blocked = Assert.Single(identified, interval =>
                interval.GetProperty("operationId").GetString() == "op-3");

            Assert.Equal("operation", scheduled.GetProperty("type").GetString());
            Assert.Equal("operation", second.GetProperty("type").GetString());
            Assert.True(second.GetProperty("startsAt").GetDateTimeOffset()
                >= scheduled.GetProperty("endsAt").GetDateTimeOffset());
            Assert.Equal("waiting", blocked.GetProperty("type").GetString());
            Assert.Equal("blocked", blocked.GetProperty("timingKind").GetString());
            Assert.Equal("not_started", blocked.GetProperty("operationStatus").GetString());
            Assert.True(blocked.GetProperty("startsAt").GetDateTimeOffset()
                >= second.GetProperty("endsAt").GetDateTimeOffset());
            Assert.Single(identified, interval =>
                interval.GetProperty("machineAssignmentId").GetString() == "assignment-3");
            var work = identified.Where(interval =>
                interval.GetProperty("type").GetString() == "operation").ToArray();
            Assert.DoesNotContain(work.SelectMany((left, index) => work.Skip(index + 1)
                .Select(right => (Left: left, Right: right))), pair =>
                    pair.Left.GetProperty("startsAt").GetDateTimeOffset()
                        < pair.Right.GetProperty("endsAt").GetDateTimeOffset()
                    && pair.Right.GetProperty("startsAt").GetDateTimeOffset()
                        < pair.Left.GetProperty("endsAt").GetDateTimeOffset());
        });
    }

    [Fact]
    public async Task Authoritative_actual_overlap_demotes_conflicting_forecast_without_reordering()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE batch_operations
                    SET status = 'in_progress', actual_start = '2026-08-11T08:30:00Z',
                        actual_machine_id = 'machine-1'
                    WHERE id = 'op-2';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z&asOf=2026-08-11T09:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var intervals = document.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray().ToArray();
            var actual = Assert.Single(intervals, interval =>
                interval.GetProperty("operationId").GetString() == "op-2");
            var forecast = Assert.Single(intervals, interval =>
                interval.GetProperty("operationId").GetString() == "op-1");

            Assert.Equal("operation", actual.GetProperty("type").GetString());
            Assert.Equal("actual", actual.GetProperty("timingKind").GetString());
            Assert.Equal("waiting", forecast.GetProperty("type").GetString());
            Assert.Equal("blocked", forecast.GetProperty("timingKind").GetString());
            Assert.Contains(document.RootElement.GetProperty("conflicts").EnumerateArray(), conflict =>
                conflict.GetProperty("code").GetString() == "actual_backlog_overlap");
            Assert.Equal(["op-1:0", "op-2:1"], await ReadPositionsAsync(application.Services));

            using var events = await client.GetAsync(
                "/api/v1/event-log?eventType=timeline_conflict_detected&limit=50");
            events.EnsureSuccessStatusCode();
            using var eventDocument = JsonDocument.Parse(
                await events.Content.ReadAsStringAsync());
            var logged = Assert.Single(eventDocument.RootElement.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("reasonCode").GetString() == "actual_backlog_overlap");
            Assert.Equal("system", logged.GetProperty("user").GetString());
        });
    }

    [Fact]
    public async Task Blocked_assignment_at_horizon_boundary_remains_visible_without_backdating()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE batch_operations
                    SET setup_seconds = 0, cycle_seconds = 17100
                    WHERE id = 'op-1';
                    UPDATE batch_operations SET setup_seconds = NULL WHERE id = 'op-2';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z&asOf=2026-08-11T08:00:00Z");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var intervals = document.RootElement.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray().ToArray();
            var preceding = Assert.Single(intervals, interval =>
                interval.GetProperty("operationId").GetString() == "op-1");
            var marker = Assert.Single(intervals, interval =>
                interval.GetProperty("operationId").GetString() == "op-2");

            Assert.Equal("operation", preceding.GetProperty("type").GetString());
            Assert.Equal(DateTimeOffset.Parse("2026-08-11T18:00:00Z"),
                preceding.GetProperty("endsAt").GetDateTimeOffset());
            Assert.Equal("waiting", marker.GetProperty("type").GetString());
            Assert.Equal("blocked", marker.GetProperty("timingKind").GetString());
            Assert.Equal(DateTimeOffset.Parse("2026-08-11T18:00:00Z"),
                marker.GetProperty("startsAt").GetDateTimeOffset());
            Assert.Equal(DateTimeOffset.Parse("2026-08-11T18:00:00Z"),
                marker.GetProperty("endsAt").GetDateTimeOffset());
            Assert.True(marker.GetProperty("startsAt").GetDateTimeOffset()
                >= preceding.GetProperty("endsAt").GetDateTimeOffset());
        });
    }

    private static bool IsWork(JsonElement interval) =>
        interval.GetProperty("type").GetString() == "operation";

    private static void AssertCalendarBackground(
        JsonElement window,
        string expectedStart,
        string expectedEnd)
    {
        Assert.Equal(expectedStart, window.GetProperty("startsAt").GetString());
        Assert.Equal(expectedEnd, window.GetProperty("endsAt").GetString());
        Assert.Equal("Machine calendar: non-working time.", window.GetProperty("detail").GetString());
        Assert.False(window.TryGetProperty("operationId", out _));
        Assert.False(window.TryGetProperty("batchId", out _));
        Assert.False(window.TryGetProperty("machineAssignmentId", out _));
    }

    private static async Task ConfigureThreeDayAvailabilityAsync(IServiceProvider services)
    {
        const string availability = """
            {"availability":[
              {"startsAt":"2026-08-11T08:00:00Z","endsAt":"2026-08-11T18:00:00Z"},
              {"startsAt":"2026-08-12T08:00:00Z","endsAt":"2026-08-12T18:00:00Z"},
              {"startsAt":"2026-08-13T08:00:00Z","endsAt":"2026-08-13T18:00:00Z"}
            ]}
            """;
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE working_calendars SET calendar_json = $availability
            WHERE id = 'calendar-1';
            UPDATE application_settings SET value = $availability
            WHERE key = 'timeline.setup_calendar_json';
            """;
        command.Parameters.AddWithValue("$availability", availability);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ConfigureLockedGroupAsync(
        IServiceProvider services,
        string secondPlanningMode)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO machines (
                id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES ('machine-2', 'M-2', 'Mill Two', 'mill', 'calendar-1', 'active', 1);
            UPDATE employee_resources
            SET skills_json = '["machine-1","machine-2"]'
            WHERE id = 'resource-setup';
            UPDATE batch_operations
            SET dependency_type = 'locked_simultaneous',
                simultaneous_group_key = 'locked-1',
                predecessor_source_case_operation_id = NULL
            WHERE id IN ('op-1', 'op-2');
            UPDATE machine_assignments SET planning_mode = 'backward'
            WHERE id = 'assignment-1';
            UPDATE machine_assignments
            SET machine_id = 'machine-2', backlog_position = 0,
                planning_mode = $secondPlanningMode
            WHERE id = 'assignment-2';
            """;
        command.Parameters.AddWithValue("$secondPlanningMode", secondPlanningMode);
        await command.ExecuteNonQueryAsync();
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
                ('resource-setup', 'E-SETUP', 'Setup Worker', 'setup_worker', 'Setup', 'Worker', '["machine-1"]', 'calendar-1', 1),
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

    private static void AssertAnonymousTimelineInterval(JsonElement interval)
    {
        Assert.Equal(JsonValueKind.Null, interval.GetProperty("operationId").ValueKind);
        Assert.Equal(JsonValueKind.Null, interval.GetProperty("batchId").ValueKind);
        Assert.Equal(JsonValueKind.Null, interval.GetProperty("batchNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, interval.GetProperty("partNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, interval.GetProperty("operationNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, interval.GetProperty("operationName").ValueKind);
        Assert.Equal(JsonValueKind.Null, interval.GetProperty("machineAssignmentId").ValueKind);
        Assert.Equal(JsonValueKind.Null, interval.GetProperty("planningMode").ValueKind);
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

    private static async Task<string[]> ReadOperationTimingStateAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'operation|' || id || '|' || status || '|'
                || COALESCE(actual_start, '') || '|' || COALESCE(actual_end, '') || '|'
                || COALESCE(actual_machine_id, '') || '|' || CAST(version AS TEXT) AS state
            FROM batch_operations
            UNION ALL
            SELECT 'order|' || id || '|' || status || '|'
                || COALESCE(work_finish_date, '') || '|||' || CAST(version AS TEXT) AS state
            FROM orders
            ORDER BY state;
            """;
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result.ToArray();
    }

    private static async Task RunWithServerAsync(
        Func<WebApplication, HttpClient, Task> test,
        DateTimeOffset? fixedUtcNow = null,
        params string[] configurationArguments)
    {
        fixedUtcNow ??= DateTimeOffset.Parse("2026-08-11T08:00:00Z");
        var directoryPath = Path.Combine(
            Path.GetTempPath(), "MeimadPlanner.TimelineApi.Tests", Guid.NewGuid().ToString("N"));
        var arguments = new List<string>
        {
            "--Server:Host=127.0.0.1",
            "--Server:Port=5099",
            $"--Database:Path={Path.Combine(directoryPath, "api-test.db")}"
        };
        arguments.AddRange(configurationArguments);
        var application = ServerApplication.Build(
            [.. arguments],
            webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(
                        new FixedTimeProvider(fixedUtcNow.Value));
                });
            });
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
