using System.Net;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.TvDashboard;

public sealed class TvDashboardApiTests
{
    [Fact]
    public async Task Read_projection_contains_current_operation_picture_status_and_setup_progress_without_conflicts()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);

            using var response = await client.GetAsync("/api/v1/tv-dashboard");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(response.Headers.ETag);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(15, root.GetProperty("refreshAfterSeconds").GetInt32());
            Assert.Equal(1, root.GetProperty("summary").GetProperty("machineCount").GetInt32());
            Assert.Equal(1, root.GetProperty("summary").GetProperty("urgentBatchCount").GetInt32());
            Assert.Equal(1, root.GetProperty("summary").GetProperty("downtimeMachineCount").GetInt32());
            Assert.Single(root.GetProperty("urgentBatches").EnumerateArray());

            var machine = Assert.Single(root.GetProperty("machines").EnumerateArray());
            Assert.Equal("M-TV-1", machine.GetProperty("number").GetString());
            Assert.Equal("op-current", machine.GetProperty("current").GetProperty("operationId").GetString());
            Assert.True(machine.GetProperty("current").GetProperty("urgent").GetBoolean());
            Assert.Equal("started", machine.GetProperty("current").GetProperty("progress").GetProperty("statusCode").GetString());
            Assert.Equal("setup", machine.GetProperty("current").GetProperty("progress").GetProperty("phase").GetString());
            Assert.InRange(machine.GetProperty("current").GetProperty("progress").GetProperty("setupPercent").GetInt32(), 45, 55);
            Assert.Contains("/api/v1/cases/case-tv/preview", machine.GetProperty("current").GetProperty("previewUrl").GetString(), StringComparison.Ordinal);
            Assert.True(machine.GetProperty("downtime").GetProperty("isCurrent").GetBoolean());
            Assert.Empty(machine.GetProperty("conflicts").EnumerateArray());

            using var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tv-dashboard");
            conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
            using var unchanged = await client.SendAsync(conditional);
            Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
            Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());
        });
    }

    [Fact]
    public async Task Dashboard_route_is_get_only_and_static_ui_has_no_edit_controls()
    {
        await RunWithServerAsync(async (_, client) =>
        {
            using var page = await client.GetAsync("/tv-dashboard/");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync();
            Assert.Contains("<h1>Machine status</h1>", html, StringComparison.Ordinal);
            Assert.Contains("id=\"server-status\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<button", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<form", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<input", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Server URL", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("host address", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Urgent batches", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Current job", html, StringComparison.OrdinalIgnoreCase);

            using var styles = await client.GetAsync("/tv-dashboard/styles.css");
            var css = await styles.Content.ReadAsStringAsync();
            Assert.Contains("overflow: hidden", css, StringComparison.Ordinal);
            Assert.Contains("grid-template-rows: repeat(var(--machine-count)", css, StringComparison.Ordinal);
            Assert.Contains(".machine-row", css, StringComparison.Ordinal);
            Assert.DoesNotContain(".machine-card", css, StringComparison.Ordinal);

            using var script = await client.GetAsync("/tv-dashboard/app.js");
            var javascript = await script.Content.ReadAsStringAsync();
            Assert.Contains("setTimeout(refresh", javascript, StringComparison.Ordinal);
            Assert.Contains("fitGrid(machines.length)", javascript, StringComparison.Ordinal);
            Assert.Contains("server-status-connected", css, StringComparison.Ordinal);
            Assert.Contains("machine.current", javascript, StringComparison.Ordinal);
            Assert.DoesNotContain("machine.next", javascript, StringComparison.Ordinal);
            Assert.DoesNotContain("machine.third", javascript, StringComparison.Ordinal);
            Assert.DoesNotContain("conflict", javascript, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("completionLabel", javascript, StringComparison.Ordinal);
            Assert.Contains("progress-track", css, StringComparison.Ordinal);
            Assert.DoesNotContain("urgentBatches", javascript, StringComparison.Ordinal);
            Assert.DoesNotContain("connection-banner", javascript, StringComparison.Ordinal);
            Assert.DoesNotContain("edit-mode", javascript, StringComparison.OrdinalIgnoreCase);

            using var post = await client.PostAsync("/api/v1/tv-dashboard", null);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        });
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var end = start.AddDays(7);
        var due = DateOnly.FromDateTime(now.UtcDateTime.AddDays(1));
        var calendarJson = JsonSerializer.Serialize(new
        {
            availability = new[] { new { startsAt = start, endsAt = end } }
        });
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id, calendar_json)
            VALUES ('calendar-tv', 'TV calendar', 'UTC', $calendarJson);
            INSERT INTO application_settings (key, value)
            VALUES ('timeline.setup_calendar_json', $calendarJson);
            INSERT INTO machines (
                id, number, name, machine_type, working_calendar_id, status,
                is_active, display_enabled)
            VALUES ('machine-tv', 'M-TV-1', 'Display Mill', 'mill', 'calendar-tv',
                    'active', 1, 1);
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ('case-tv', 'PN-TV', 'TV Part', 'C:\Cases\PN-TV');
            INSERT INTO orders (
                id, case_id, order_reference, quantity, work_finish_date, status)
            VALUES ('order-tv', 'case-tv', 'ORDER-TV', 2, $due, 'active');
            INSERT INTO production_batches (
                id, case_id, batch_number, status, planned_quantity)
            VALUES ('batch-tv', 'case-tv', 'B-TV', 'waiting', 2);
            INSERT INTO batch_allocations (
                id, production_batch_id, allocation_type, order_id, quantity)
            VALUES ('allocation-tv', 'batch-tv', 'order', 'order-tv', 2);
            INSERT INTO case_operations (
                id, case_id, operation_number, route_position, name,
                required_machine_type, setup_seconds, cycle_seconds)
            VALUES
                ('case-op-current', 'case-tv', 10, 0, 'Rough mill', 'mill', 600, 900),
                ('case-op-next', 'case-tv', 20, 1, 'Finish mill', 'mill', NULL, NULL);
            INSERT INTO batch_operations (
                id, production_batch_id, source_case_operation_id,
                operation_number, route_position, name, required_machine_type,
                setup_seconds, cycle_seconds, status, actual_start, actual_machine_id)
            VALUES
                ('op-current', 'batch-tv', 'case-op-current', 10, 0,
                 'Rough mill', 'mill', 600, 900, 'in_progress', $actualStart, 'machine-tv'),
                ('op-next', 'batch-tv', 'case-op-next', 20, 1,
                 'Finish mill', 'mill', NULL, NULL, 'not_started', NULL, NULL);
            INSERT INTO machine_assignments (
                id, batch_operation_id, machine_id, backlog_position)
            VALUES
                ('assignment-current', 'op-current', 'machine-tv', 0),
                ('assignment-next', 'op-next', 'machine-tv', 1);
            INSERT INTO downtimes (
                id, machine_id, starts_at, ends_at, reason, status)
            VALUES ('downtime-tv', 'machine-tv', $downtimeStart, $downtimeEnd,
                    'Planned inspection', 'planned');
            """;
        command.Parameters.AddWithValue("$calendarJson", calendarJson);
        command.Parameters.AddWithValue("$due", due.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$downtimeStart", now.AddMinutes(-10).ToString("O"));
        command.Parameters.AddWithValue("$downtimeEnd", now.AddMinutes(50).ToString("O"));
        command.Parameters.AddWithValue("$actualStart", now.AddMinutes(-5).ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunWithServerAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(), "MeimadPlanner.TvDashboard.Tests", Guid.NewGuid().ToString("N"));
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
