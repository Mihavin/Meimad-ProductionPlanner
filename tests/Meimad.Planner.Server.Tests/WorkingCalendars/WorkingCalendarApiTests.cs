using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.WorkingCalendars;

public sealed class WorkingCalendarApiTests
{
    [Fact]
    public async Task Editor_can_create_list_and_reopen_weekly_calendar()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            using var create = await client.PostAsJsonAsync("/api/v1/working-calendars", new
            {
                name = "Extended shift",
                timeZoneId = "Asia/Jerusalem",
                workdays = new[] { "sunday", "monday", "tuesday", "wednesday", "thursday" },
                shiftStartsAtLocal = "06:00",
                shiftEndsAtLocal = "22:00"
            });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
            var id = created.RootElement.GetProperty("workingCalendarId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.Equal("weekly", created.RootElement.GetProperty("scheduleKind").GetString());

            using var machine = await client.PostAsJsonAsync("/api/v1/machines", new
            {
                number = "M-CALENDAR",
                name = "Calendar Machine",
                processType = "mill",
                axisType = "3-axis",
                capabilities = Array.Empty<string>(),
                workingCalendarId = id,
                isActive = true,
                displayEnabled = true
            });
            Assert.Equal(HttpStatusCode.Created, machine.StatusCode);

            using var list = await client.GetAsync("/api/v1/working-calendars");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            using var listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            Assert.Equal(id, listed.RootElement.GetProperty("items")[0]
                .GetProperty("workingCalendarId").GetString());

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT calendar_json FROM working_calendars WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            using var stored = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
            Assert.Equal("22:00", stored.RootElement.GetProperty("weeklySchedule")
                .GetProperty("shiftEndsAtLocal").GetString());
        });
    }

    [Fact]
    public async Task Calendar_creation_requires_edit_mode_and_rejects_invalid_schedule()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            using var withoutHeaders = await client.PostAsJsonAsync("/api/v1/working-calendars", new
            {
                name = "Bad", timeZoneId = "UTC", workdays = new[] { "monday" },
                shiftStartsAtLocal = "18:00", shiftEndsAtLocal = "06:00"
            });
            Assert.Equal((HttpStatusCode)428, withoutHeaders.StatusCode);

            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            using var invalid = await client.PostAsJsonAsync("/api/v1/working-calendars", new
            {
                name = "Bad", timeZoneId = "UTC", workdays = new[] { "funday" },
                shiftStartsAtLocal = "18:00", shiftEndsAtLocal = "06:00"
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        });
    }

    [Fact]
    public async Task Duplicate_calendar_names_are_rejected_case_insensitively()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            var body = new
            {
                name = "Day shift", timeZoneId = "UTC", workdays = new[] { "monday" },
                shiftStartsAtLocal = "06:00", shiftEndsAtLocal = "18:00"
            };
            using var first = await client.PostAsJsonAsync("/api/v1/working-calendars", body);
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            using var second = await client.PostAsJsonAsync("/api/v1/working-calendars", body with { name = "day SHIFT" });
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        });
    }

    private static void AddEditHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "calendar-client");
        client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
    }

    private static async Task GrantEditModeAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE edit_tokens
            SET holder_client_id = 'calendar-client', holder_user_id = 'planner',
                generation = 1, acquired_at = '2026-08-11T00:00:00Z', version = version + 1
            WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunWithServerAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "MeimadPlanner.Calendar.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directoryPath, "test.db")}"],
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
            if (Directory.Exists(directoryPath)) Directory.Delete(directoryPath, true);
        }
    }
}
