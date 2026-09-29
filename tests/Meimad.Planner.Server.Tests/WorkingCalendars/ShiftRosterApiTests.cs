using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Tests.WorkingCalendars;

public sealed class ShiftRosterApiTests
{
    // 2026-10-04 is a Sunday; Israel keeps UTC+3 until 2026-10-25.
    private const string Sunday = "2026-10-04";

    [Fact]
    public async Task Rotation_calendar_crews_and_roster_decide_each_employees_shift()
    {
        await RunWithServerAsync(async client =>
        {
            var (calendarId, calendarTag) = await CreateRotationAsync(client);
            var employeeId = await CreateEmployeeAsync(client, calendarId, "E-1", "B");

            using var roster = await client.GetAsync($"/api/v1/shift-roster?from={Sunday}&to=2026-10-10");
            Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
            using var rosterJson = JsonDocument.Parse(await roster.Content.ReadAsStringAsync());
            var calendar = Assert.Single(rosterJson.RootElement.GetProperty("calendars").EnumerateArray());
            Assert.Equal(["day", "night"], calendar.GetProperty("shifts").EnumerateArray().Select(value => value.GetProperty("code").GetString()));
            var employee = Assert.Single(rosterJson.RootElement.GetProperty("employees").EnumerateArray());
            Assert.Equal("B", employee.GetProperty("shiftCrewCode").GetString());
            // Crew B starts the D D N N - - - - pattern two days after the anchor.
            Assert.Equal(["off", "off", "day", "day", "night", "night", "off"],
                employee.GetProperty("days").EnumerateArray().Select(day => day.GetProperty("effectiveShiftCode").GetString()));
            var thursday = employee.GetProperty("days")[4];
            Assert.Equal("2026-10-08T16:00:00+00:00", thursday.GetProperty("startsAt").GetDateTimeOffset().ToString("yyyy-MM-ddTHH:mm:sszzz"));
            Assert.Equal("2026-10-09T04:00:00+00:00", thursday.GetProperty("endsAt").GetDateTimeOffset().ToString("yyyy-MM-ddTHH:mm:sszzz"));

            // The planner swaps Sunday to a night shift.
            using var save = await client.PutAsJsonAsync("/api/v1/shift-roster", new
            {
                entries = new[] { new { resourceId = employeeId, date = Sunday, shiftCode = "night", note = "Cover", expectedVersion = (int?)null } }
            });
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
            using var saved = JsonDocument.Parse(await save.Content.ReadAsStringAsync());
            var entry = Assert.Single(saved.RootElement.GetProperty("entries").EnumerateArray());
            Assert.Equal(1, entry.GetProperty("version").GetInt32());

            using var after = await client.GetAsync($"/api/v1/shift-roster?from={Sunday}&to={Sunday}");
            using var afterJson = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
            var sunday = afterJson.RootElement.GetProperty("employees")[0].GetProperty("days")[0];
            Assert.Equal("off", sunday.GetProperty("patternShiftCode").GetString());
            Assert.Equal("night", sunday.GetProperty("effectiveShiftCode").GetString());
            Assert.Equal(1, sunday.GetProperty("entryVersion").GetInt32());

            // The availability endpoint uses the same shifts, including the hours after midnight.
            using var availability = await client.GetAsync(
                $"/api/v1/resources/{employeeId}/availability?from=2026-10-04T00:00:00Z&to=2026-10-05T12:00:00Z");
            using var availabilityJson = JsonDocument.Parse(await availability.Content.ReadAsStringAsync());
            var window = Assert.Single(availabilityJson.RootElement.GetProperty("windows").EnumerateArray());
            Assert.Equal(DateTimeOffset.Parse("2026-10-04T16:00:00Z"), window.GetProperty("startsAt").GetDateTimeOffset());
            Assert.Equal(DateTimeOffset.Parse("2026-10-05T04:00:00Z"), window.GetProperty("endsAt").GetDateTimeOffset());

            // Removing the entry lets the pattern apply again.
            using var clear = await client.PutAsJsonAsync("/api/v1/shift-roster", new
            {
                entries = new[] { new { resourceId = employeeId, date = Sunday, shiftCode = (string?)null, note = (string?)null, expectedVersion = (int?)1 } }
            });
            Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
            using var cleared = await client.GetAsync($"/api/v1/shift-roster?from={Sunday}&to={Sunday}");
            using var clearedJson = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync());
            Assert.Equal("off", clearedJson.RootElement.GetProperty("employees")[0].GetProperty("days")[0].GetProperty("effectiveShiftCode").GetString());
        });
    }

    [Fact]
    public async Task A_roster_change_based_on_a_stale_entry_is_refused_and_explained()
    {
        await RunWithServerAsync(async client =>
        {
            var (calendarId, _) = await CreateRotationAsync(client);
            var employeeId = await CreateEmployeeAsync(client, calendarId, "E-2", "A");
            object Change(string code, int? version) => new
            {
                entries = new[] { new { resourceId = employeeId, date = Sunday, shiftCode = code, note = (string?)null, expectedVersion = version } }
            };

            client.DefaultRequestHeaders.Add("X-Meimad-User-Id", "shift-manager");
            using var first = await client.PutAsJsonAsync("/api/v1/shift-roster", Change("night", null));
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            client.DefaultRequestHeaders.Remove("X-Meimad-User-Id");

            // A second planner opened the roster before the first save.
            using var stale = await client.PutAsJsonAsync("/api/v1/shift-roster", Change("day", null));
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            using var error = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
            Assert.Equal("edit_conflict", error.RootElement.GetProperty("error").GetProperty("code").GetString());
            var conflict = error.RootElement.GetProperty("error").GetProperty("conflict");
            Assert.Equal("Shift Roster", conflict.GetProperty("resource").GetString());
            Assert.Equal("shift-manager", conflict.GetProperty("changedBy").GetString());

            using var current = await client.PutAsJsonAsync("/api/v1/shift-roster", Change("day", 1));
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        });
    }

    [Fact]
    public async Task Roster_changes_need_the_planning_permission_and_valid_shifts()
    {
        await RunWithServerAsync(async client =>
        {
            var (calendarId, _) = await CreateRotationAsync(client);
            var employeeId = await CreateEmployeeAsync(client, calendarId, "E-3", "A");
            object Change(string code) => new
            {
                entries = new[] { new { resourceId = employeeId, date = Sunday, shiftCode = code, note = (string?)null, expectedVersion = (int?)null } }
            };

            using (client.SignedInWithOnly(Permissions.DecideQc))
            {
                using var read = await client.GetAsync($"/api/v1/shift-roster?from={Sunday}&to={Sunday}");
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                using var forbidden = await client.PutAsJsonAsync("/api/v1/shift-roster", Change("night"));
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            }

            using var unknown = await client.PutAsJsonAsync("/api/v1/shift-roster", Change("evening"));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
            Assert.Contains("unknown_shift", await unknown.Content.ReadAsStringAsync());

            using var tooLong = await client.GetAsync($"/api/v1/shift-roster?from={Sunday}&to=2026-12-31");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        });
    }

    [Fact]
    public async Task Rotation_calendars_guard_their_crews_shifts_and_employee_only_use()
    {
        await RunWithServerAsync(async client =>
        {
            var (calendarId, calendarTag) = await CreateRotationAsync(client);

            // An employee on a rotation with crews must follow one of them.
            using var withoutCrew = await client.PostAsJsonAsync("/api/v1/resources", Employee(calendarId, "E-4", null));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutCrew.StatusCode);
            using var unknownCrew = await client.PostAsJsonAsync("/api/v1/resources", Employee(calendarId, "E-4", "Z"));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownCrew.StatusCode);
            var employeeId = await CreateEmployeeAsync(client, calendarId, "E-4", "b");

            // A rotation belongs to employees only.
            using var setup = await client.PutAsJsonAsync("/api/v1/setup-calendar", new { workingCalendarId = calendarId });
            Assert.Equal(HttpStatusCode.Conflict, setup.StatusCode);
            using var master = await client.PutAsJsonAsync("/api/v1/master-calendar", new { workingCalendarId = calendarId });
            Assert.Equal(HttpStatusCode.Conflict, master.StatusCode);

            // The crew an employee follows and a shift the roster uses cannot be removed.
            using var dropCrew = await PatchAsync(client, calendarId, calendarTag, new
            {
                rotation = Rotation(crews: [new { code = "A", name = "Crew A", offsetDays = 0 }])
            });
            Assert.Equal(HttpStatusCode.Conflict, dropCrew.StatusCode);
            Assert.Contains("follows it", await dropCrew.Content.ReadAsStringAsync());

            using var save = await client.PutAsJsonAsync("/api/v1/shift-roster", new
            {
                entries = new[] { new { resourceId = employeeId, date = "2099-01-01", shiftCode = "night", note = (string?)null, expectedVersion = (int?)null } }
            });
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
            using var dropNight = await PatchAsync(client, calendarId, calendarTag, new
            {
                rotation = new
                {
                    anchorDate = Sunday,
                    shifts = new[] { new { code = "day", name = "Day", startsAtLocal = "07:00", endsAtLocal = "19:00" } },
                    pattern = new[] { "day", "off" },
                    crews = new[] { new { code = "A", name = "Crew A", offsetDays = 0 }, new { code = "B", name = "Crew B", offsetDays = 1 } }
                }
            });
            Assert.Equal(HttpStatusCode.Conflict, dropNight.StatusCode);
            Assert.Contains("Shift Roster uses it", await dropNight.Content.ReadAsStringAsync());

            using var changeKind = await PatchAsync(client, calendarId, calendarTag, new { scheduleKind = "weekly" });
            Assert.Equal(HttpStatusCode.BadRequest, changeKind.StatusCode);
            Assert.Contains("schedule_kind_immutable", await changeKind.Content.ReadAsStringAsync());

            // Machines cannot use a rotation: it has no machine usage.
            using var machine = await client.PostAsJsonAsync("/api/v1/machines", new
            {
                number = "M-ROT", name = "Rotation Machine", processType = "mill", axisType = "3-axis",
                capabilities = Array.Empty<string>(), workingCalendarId = calendarId, isActive = true, displayEnabled = true
            });
            Assert.NotEqual(HttpStatusCode.Created, machine.StatusCode);
        });
    }

    private static object Rotation(object[]? crews = null) => new
    {
        anchorDate = Sunday,
        shifts = new[]
        {
            new { code = "day", name = "Day", startsAtLocal = "07:00", endsAtLocal = "19:00" },
            new { code = "night", name = "Night", startsAtLocal = "19:00", endsAtLocal = "07:00" }
        },
        pattern = new[] { "day", "day", "night", "night", "off", "off", "off", "off" },
        crews = crews ?? [new { code = "A", name = "Crew A", offsetDays = 0 }, new { code = "B", name = "Crew B", offsetDays = 2 }]
    };

    private static async Task<(string Id, string Tag)> CreateRotationAsync(HttpClient client)
    {
        using var create = await client.PostAsJsonAsync("/api/v1/working-calendars", new
        {
            name = "12h rotation",
            timeZoneId = "Asia/Jerusalem",
            scheduleKind = "rotation",
            rotation = Rotation()
        });
        var body = await create.Content.ReadAsStringAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("rotation", json.RootElement.GetProperty("scheduleKind").GetString());
        Assert.Equal(["setup_worker", "regular_worker", "qa_worker"],
            json.RootElement.GetProperty("usages").EnumerateArray().Select(value => value.GetString()));
        return (json.RootElement.GetProperty("workingCalendarId").GetString()!, create.Headers.ETag!.Tag);
    }

    private static object Employee(string calendarId, string number, string? crew) => new
    {
        employeeNumber = number, firstName = "Shift", lastName = number, role = "regular_worker",
        skills = Array.Empty<string>(), assignedCalendarId = calendarId, isActive = true, shiftCrewCode = crew
    };

    private static async Task<string> CreateEmployeeAsync(HttpClient client, string calendarId, string number, string crew)
    {
        using var create = await client.PostAsJsonAsync("/api/v1/resources", Employee(calendarId, number, crew));
        var body = await create.Content.ReadAsStringAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(crew.ToUpperInvariant(), json.RootElement.GetProperty("shiftCrewCode").GetString());
        return json.RootElement.GetProperty("resourceId").GetString()!;
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string calendarId, string tag, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/working-calendars/{calendarId}")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("If-Match", tag);
        return await client.SendAsync(request);
    }

    private static async Task RunWithServerAsync(Func<HttpClient, Task> test)
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "MeimadPlanner.ShiftRoster.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directoryPath, "test.db")}"],
            webHost => webHost.UseSignedInTestServer());
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "roster-client");
            await test(client);
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
