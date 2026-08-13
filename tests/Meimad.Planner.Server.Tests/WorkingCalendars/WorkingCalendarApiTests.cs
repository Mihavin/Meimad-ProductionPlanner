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

            using var blockedDelete = await client.DeleteAsync($"/api/v1/working-calendars/{id}");
            Assert.Equal(HttpStatusCode.Conflict, blockedDelete.StatusCode);
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
    public async Task Editor_can_create_an_overnight_employee_calendar()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            using var create = await client.PostAsJsonAsync("/api/v1/working-calendars", new
            {
                name = "Night setup team",
                timeZoneId = "Asia/Jerusalem",
                workdays = new[] { "sunday", "monday", "tuesday", "wednesday", "thursday" },
                windows = new[] { new { startsAtLocal = "17:00", endsAtLocal = "07:00" } },
                usages = new[] { "setup_worker" }
            });

            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            using var calendar = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
            var window = Assert.Single(calendar.RootElement.GetProperty("windows").EnumerateArray());
            Assert.Equal("17:00", window.GetProperty("startsAtLocal").GetString());
            Assert.Equal("07:00", window.GetProperty("endsAtLocal").GetString());
            Assert.Equal("setup_worker", Assert.Single(calendar.RootElement.GetProperty("usages").EnumerateArray()).GetString());
        });
    }

    [Fact]
    public async Task Weekly_calendar_supports_multiple_non_overlapping_working_windows()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            using var create = await client.PostAsJsonAsync("/api/v1/working-calendars", new
            {
                name = "Split shift",
                timeZoneId = "Asia/Jerusalem",
                workdays = new[] { "sunday", "monday" },
                windows = new[]
                {
                    new { startsAtLocal = "12:30", endsAtLocal = "18:00" },
                    new { startsAtLocal = "06:00", endsAtLocal = "12:00" }
                }
            });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
            var windows = created.RootElement.GetProperty("windows");
            Assert.Equal(2, windows.GetArrayLength());
            Assert.Equal("06:00", windows[0].GetProperty("startsAtLocal").GetString());
            Assert.Equal(JsonValueKind.Null, created.RootElement.GetProperty("shiftStartsAtLocal").ValueKind);

            using var overlap = await client.PostAsJsonAsync("/api/v1/working-calendars", new
            {
                name = "Overlap",
                timeZoneId = "UTC",
                workdays = new[] { "monday" },
                windows = new[]
                {
                    new { startsAtLocal = "06:00", endsAtLocal = "12:00" },
                    new { startsAtLocal = "11:00", endsAtLocal = "14:00" }
                }
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, overlap.StatusCode);
        });
    }

    [Fact]
    public async Task Weekly_calendar_persists_usages_breaks_and_dated_exceptions()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            using var create = await client.PostAsJsonAsync("/api/v1/working-calendars", new
            {
                name = "Configured resources",
                timeZoneId = "Asia/Jerusalem",
                workdays = new[] { "sunday", "monday" },
                windows = new[] { new { startsAtLocal = "06:00", endsAtLocal = "18:00" } },
                breakWindows = new[] { new { startsAtLocal = "12:00", endsAtLocal = "12:30" } },
                exceptions = new object[]
                {
                    new { date = "2026-09-13", windows = Array.Empty<object>(), breakWindows = Array.Empty<object>(), name = "Closed" },
                    new
                    {
                        date = "2026-09-14",
                        windows = new[] { new { startsAtLocal = "08:00", endsAtLocal = "16:00" } },
                        breakWindows = new[] { new { startsAtLocal = "11:30", endsAtLocal = "12:00" } },
                        name = "Short day"
                    }
                },
                usages = new[] { "machine", "setup_worker", "regular_worker", "qa_worker" }
            });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
            Assert.Equal(1, created.RootElement.GetProperty("breakWindows").GetArrayLength());
            Assert.Equal(2, created.RootElement.GetProperty("exceptions").GetArrayLength());
            Assert.Equal(4, created.RootElement.GetProperty("usages").GetArrayLength());
            var id = created.RootElement.GetProperty("workingCalendarId").GetString()!;

            using var get = await client.GetAsync($"/api/v1/working-calendars/{id}");
            get.EnsureSuccessStatusCode();
            using var reopened = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            Assert.Equal("12:00", reopened.RootElement.GetProperty("breakWindows")[0]
                .GetProperty("startsAtLocal").GetString());
            Assert.Equal("Short day", reopened.RootElement.GetProperty("exceptions")[1]
                .GetProperty("name").GetString());

            using var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/working-calendars/{id}")
            {
                Content = JsonContent.Create(new
                {
                    breakWindows = new[] { new { startsAtLocal = "11:00", endsAtLocal = "11:30" } },
                    exceptions = new object[]
                    {
                        new { date = "2026-09-15", windows = Array.Empty<object>(), breakWindows = Array.Empty<object>(), name = "Closed revised" }
                    },
                    usages = new[] { "machine", "setup_worker", "qa_worker" }
                })
            };
            patch.Headers.TryAddWithoutValidation("If-Match", create.Headers.ETag?.Tag);
            using var patched = await client.SendAsync(patch);
            Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
            using var patchedJson = JsonDocument.Parse(await patched.Content.ReadAsStringAsync());
            Assert.Equal("11:00", patchedJson.RootElement.GetProperty("breakWindows")[0]
                .GetProperty("startsAtLocal").GetString());
            Assert.Equal("2026-09-15", patchedJson.RootElement.GetProperty("exceptions")[0]
                .GetProperty("date").GetString());
            Assert.Equal(3, patchedJson.RootElement.GetProperty("usages").GetArrayLength());

            using var invalidBreak = await client.PostAsJsonAsync("/api/v1/working-calendars", new
            {
                name = "Invalid break",
                timeZoneId = "UTC",
                workdays = new[] { "monday" },
                windows = new[] { new { startsAtLocal = "06:00", endsAtLocal = "18:00" } },
                breakWindows = new[] { new { startsAtLocal = "18:00", endsAtLocal = "19:00" } }
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidBreak.StatusCode);

            using var machineOnly = await client.PostAsJsonAsync("/api/v1/working-calendars", new
            {
                name = "Machine only",
                timeZoneId = "UTC",
                workdays = new[] { "monday" },
                windows = new[] { new { startsAtLocal = "06:00", endsAtLocal = "18:00" } },
                usages = new[] { "machine" }
            });
            machineOnly.EnsureSuccessStatusCode();
            using var machineOnlyJson = JsonDocument.Parse(await machineOnly.Content.ReadAsStringAsync());
            using var selectWrongUsage = await client.PutAsJsonAsync("/api/v1/setup-calendar", new
            {
                workingCalendarId = machineOnlyJson.RootElement.GetProperty("workingCalendarId").GetString()
            });
            Assert.Equal(HttpStatusCode.Conflict, selectWrongUsage.StatusCode);

            using var workerOnly = await client.PostAsJsonAsync("/api/v1/working-calendars", new
            {
                name = "Worker only",
                timeZoneId = "UTC",
                workdays = new[] { "monday" },
                windows = new[] { new { startsAtLocal = "06:00", endsAtLocal = "18:00" } },
                usages = new[] { "regular_worker" }
            });
            workerOnly.EnsureSuccessStatusCode();
            using var workerOnlyJson = JsonDocument.Parse(await workerOnly.Content.ReadAsStringAsync());
            using var invalidMachine = await client.PostAsJsonAsync("/api/v1/machines", new
            {
                number = "M-WRONG-CALENDAR",
                name = "Wrong Calendar Machine",
                processType = "mill",
                capabilities = Array.Empty<string>(),
                workingCalendarId = workerOnlyJson.RootElement.GetProperty("workingCalendarId").GetString(),
                isActive = true,
                displayEnabled = true
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidMachine.StatusCode);
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

    [Fact]
    public async Task Editor_can_update_select_clear_and_delete_setup_calendar()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            using var create = await client.PostAsJsonAsync("/api/v1/working-calendars", new
            {
                name = "Setup day",
                timeZoneId = "Asia/Jerusalem",
                workdays = new[] { "sunday", "monday" },
                shiftStartsAtLocal = "07:00",
                shiftEndsAtLocal = "15:00"
            });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
            var id = created.RootElement.GetProperty("workingCalendarId").GetString()!;
            var entityTag = create.Headers.ETag?.Tag;
            Assert.NotNull(entityTag);

            using var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/working-calendars/{id}")
            {
                Content = JsonContent.Create(new
                {
                    name = "Setup extended",
                    shiftEndsAtLocal = "17:00"
                })
            };
            patch.Headers.TryAddWithoutValidation("If-Match", entityTag);
            using var patched = await client.SendAsync(patch);
            Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
            using var patchedJson = JsonDocument.Parse(await patched.Content.ReadAsStringAsync());
            Assert.Equal("Setup extended", patchedJson.RootElement.GetProperty("name").GetString());
            Assert.Equal("17:00", patchedJson.RootElement.GetProperty("shiftEndsAtLocal").GetString());

            using var select = await client.PutAsJsonAsync("/api/v1/setup-calendar", new
            {
                workingCalendarId = id
            });
            Assert.Equal(HttpStatusCode.OK, select.StatusCode);
            using var selected = JsonDocument.Parse(await select.Content.ReadAsStringAsync());
            Assert.Equal(id, selected.RootElement.GetProperty("workingCalendarId").GetString());
            Assert.Equal("Setup extended", selected.RootElement.GetProperty("calendar").GetProperty("name").GetString());

            using var removeActiveUsage = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/working-calendars/{id}")
            {
                Content = JsonContent.Create(new { usages = new[] { "machine" } })
            };
            removeActiveUsage.Headers.TryAddWithoutValidation("If-Match", patched.Headers.ETag?.Tag);
            using var usageConflict = await client.SendAsync(removeActiveUsage);
            Assert.Equal(HttpStatusCode.Conflict, usageConflict.StatusCode);

            using var blockedDelete = await client.DeleteAsync($"/api/v1/working-calendars/{id}");
            Assert.Equal(HttpStatusCode.Conflict, blockedDelete.StatusCode);

            using var clear = await client.DeleteAsync("/api/v1/setup-calendar");
            Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
            using var empty = await client.GetAsync("/api/v1/setup-calendar");
            empty.EnsureSuccessStatusCode();
            using var emptyJson = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Null, emptyJson.RootElement.GetProperty("workingCalendarId").ValueKind);

            using var delete = await client.DeleteAsync($"/api/v1/working-calendars/{id}");
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
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
