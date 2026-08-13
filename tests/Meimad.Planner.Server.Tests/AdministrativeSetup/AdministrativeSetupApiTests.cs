using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Application.AdministrativeSetup;
using Meimad.Planner.Server.Domain.AdministrativeSetup;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meimad.Planner.Server.Tests.AdministrativeSetup;

public sealed class AdministrativeSetupApiTests
{
    [Fact]
    public async Task Holiday_refresh_is_cached_manual_overrides_survive_and_offline_failure_keeps_cache()
    {
        var source = new TestHolidaySource([
            new("hebcal:2026-09-12",new(2026,9,12),"Rosh Hashanah","non_working"),
            new("hebcal:2026-09-13",new(2026,9,13),"Second day","working")]);
        await RunAsync(async (application,client)=>
        {
            await GrantEditAsync(application.Services);AddEditHeaders(client);
            using var sync=await client.PostAsJsonAsync("/api/v1/israeli-holidays/sync",new{fromYear=2026,toYear=2026});
            sync.EnsureSuccessStatusCode();using var syncJson=JsonDocument.Parse(await sync.Content.ReadAsStringAsync());
            Assert.True(syncJson.RootElement.GetProperty("succeeded").GetBoolean());Assert.Equal(2,syncJson.RootElement.GetProperty("created").GetInt32());

            using var list=await client.GetAsync("/api/v1/israeli-holidays");using var listJson=JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            var first=listJson.RootElement.GetProperty("items").EnumerateArray().First();var id=first.GetProperty("israeliHolidayId").GetString()!;
            using var get=await client.GetAsync($"/api/v1/israeli-holidays/{id}");
            using var patch=new HttpRequestMessage(HttpMethod.Patch,$"/api/v1/israeli-holidays/{id}") {Content=JsonContent.Create(new{status="partial_working",startsAtLocal="08:00",endsAtLocal="13:00"})};
            patch.Headers.IfMatch.Add(new EntityTagHeaderValue(get.Headers.ETag!.Tag));using var patched=await client.SendAsync(patch);patched.EnsureSuccessStatusCode();

            source.Items=[new("hebcal:2026-09-12",new(2026,9,12),"Changed online name","non_working")];
            using var resync=await client.PostAsJsonAsync("/api/v1/israeli-holidays/sync",new{fromYear=2026,toYear=2026});
            using var resyncJson=JsonDocument.Parse(await resync.Content.ReadAsStringAsync());Assert.Equal(1,resyncJson.RootElement.GetProperty("preservedManual").GetInt32());
            using var preserved=await client.GetAsync($"/api/v1/israeli-holidays/{id}");using var preservedJson=JsonDocument.Parse(await preserved.Content.ReadAsStringAsync());
            Assert.Equal("partial_working",preservedJson.RootElement.GetProperty("status").GetString());

            source.Fail=true;using var offline=await client.PostAsJsonAsync("/api/v1/israeli-holidays/sync",new{fromYear=2026,toYear=2026});
            offline.EnsureSuccessStatusCode();using var offlineJson=JsonDocument.Parse(await offline.Content.ReadAsStringAsync());Assert.False(offlineJson.RootElement.GetProperty("succeeded").GetBoolean());
            using var cached=await client.GetAsync("/api/v1/israeli-holidays");using var cachedJson=JsonDocument.Parse(await cached.Content.ReadAsStringAsync());Assert.Equal(2,cachedJson.RootElement.GetProperty("items").GetArrayLength());
        },source);
    }

    [Fact]
    public async Task Editor_can_manage_resources_holidays_and_report_email_settings()
    {
        await RunAsync(async (application, client) =>
        {
            await GrantEditAsync(application.Services);
            AddEditHeaders(client);
            var calendarId = await CreateCalendarAsync(client, "machine", "setup_worker", "regular_worker", "qa_worker");
            var machineId = await CreateMachineAsync(client, calendarId, "M-SKILL");

            using var createResource = await client.PostAsJsonAsync("/api/v1/resources", new
            { employeeNumber="E-17",firstName="Miriam",lastName="Cohen",role="setup_worker",skills=new[]{machineId},assignedCalendarId=calendarId,photoPath="C:\\photos\\miriam.jpg",notes="Shift lead",email="miriam@example.com",isActive=true });
            Assert.Equal(HttpStatusCode.Created, createResource.StatusCode);
            using var resourceJson=JsonDocument.Parse(await createResource.Content.ReadAsStringAsync());
            var resourceId=resourceJson.RootElement.GetProperty("resourceId").GetString()!;
            var resourceTag=createResource.Headers.ETag!.Tag;

            using var protectedCalendarDelete = await client.DeleteAsync($"/api/v1/working-calendars/{calendarId}");
            Assert.Equal(HttpStatusCode.Conflict, protectedCalendarDelete.StatusCode);
            using var calendarForUsageChange = await client.GetAsync($"/api/v1/working-calendars/{calendarId}");
            using var removeSetupUsage = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/working-calendars/{calendarId}")
            { Content = JsonContent.Create(new { usages = new[] { "regular_worker", "qa_worker" } }) };
            removeSetupUsage.Headers.IfMatch.Add(new EntityTagHeaderValue(calendarForUsageChange.Headers.ETag!.Tag));
            using var usageChange = await client.SendAsync(removeSetupUsage);
            Assert.Equal(HttpStatusCode.Conflict, usageChange.StatusCode);

            using var patch=new HttpRequestMessage(HttpMethod.Patch,$"/api/v1/resources/{resourceId}") { Content=JsonContent.Create(new{lastName="Levi",skills=new[]{machineId},notes="Inactive until further notice",isActive=false}) };
            patch.Headers.IfMatch.Add(new EntityTagHeaderValue(resourceTag));
            using var patched=await client.SendAsync(patch);
            Assert.Equal(HttpStatusCode.OK,patched.StatusCode);
            using var patchedJson=JsonDocument.Parse(await patched.Content.ReadAsStringAsync());
            Assert.Equal("Miriam Levi",patchedJson.RootElement.GetProperty("name").GetString());
            Assert.Equal("C:\\photos\\miriam.jpg",patchedJson.RootElement.GetProperty("photoPath").GetString());
            Assert.Equal(machineId, patchedJson.RootElement.GetProperty("skills")[0].GetString());
            Assert.False(patchedJson.RootElement.GetProperty("isActive").GetBoolean());

            using var allResources = await client.GetAsync("/api/v1/resources");
            using var allResourcesJson = JsonDocument.Parse(await allResources.Content.ReadAsStringAsync());
            Assert.Single(allResourcesJson.RootElement.GetProperty("items").EnumerateArray());
            using var availableResources = await client.GetAsync("/api/v1/resources/available");
            using var availableResourcesJson = JsonDocument.Parse(await availableResources.Content.ReadAsStringAsync());
            Assert.Empty(availableResourcesJson.RootElement.GetProperty("items").EnumerateArray());

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var insertLegacy = connection.CreateCommand())
            {
                insertLegacy.CommandText = "INSERT INTO employee_resources (id, employee_number, name, resource_type, first_name, last_name, skills_json, assigned_calendar_id, is_active, version, created_at, updated_at) VALUES ('legacy-resource','E-LEGACY','Legacy Employee','regular_worker','Legacy','Employee','[]',NULL,1,1,'2026-08-12T00:00:00Z','2026-08-12T00:00:00Z');";
                await insertLegacy.ExecuteNonQueryAsync();
            }
            using var unavailableLegacy = await client.GetAsync("/api/v1/resources/available");
            using var unavailableLegacyJson = JsonDocument.Parse(await unavailableLegacy.Content.ReadAsStringAsync());
            Assert.Empty(unavailableLegacyJson.RootElement.GetProperty("items").EnumerateArray());

            using var holiday=await client.PostAsJsonAsync("/api/v1/israeli-holidays",new{date="2026-09-12",name="Rosh Hashanah"});
            Assert.Equal(HttpStatusCode.Created,holiday.StatusCode);
            using var duplicate=await client.PostAsJsonAsync("/api/v1/israeli-holidays",new{date="2026-09-12",name="Duplicate"});
            Assert.Equal(HttpStatusCode.Conflict,duplicate.StatusCode);

            using var getSettings=await client.GetAsync("/api/v1/report-email-settings");
            getSettings.EnsureSuccessStatusCode();
            var settingsTag=getSettings.Headers.ETag!.Tag;
            using var put=new HttpRequestMessage(HttpMethod.Put,"/api/v1/report-email-settings")
            { Content=JsonContent.Create(new{senderAddress="planner@example.com",recipients=new[]{"manager@example.com"},smtpHost="smtp.internal",smtpPort=587,useSsl=true,dailyReportEnabled=true,dailyReportTimeLocal="06:30",timeZoneId="Asia/Jerusalem"}) };
            put.Headers.IfMatch.Add(new EntityTagHeaderValue(settingsTag));
            using var updated=await client.SendAsync(put);
            Assert.Equal(HttpStatusCode.OK,updated.StatusCode);
            using var updatedJson=JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
            Assert.True(updatedJson.RootElement.GetProperty("dailyReportEnabled").GetBoolean());
        });
    }

    [Fact]
    public async Task Mutations_require_edit_mode_and_enabled_reports_require_complete_valid_delivery_settings()
    {
        await RunAsync(async (_,client)=>
        {
            using var unauthorized=await client.PostAsJsonAsync("/api/v1/resources",new{employeeNumber="E-1",firstName="No",lastName="Edit",role="regular_worker",assignedCalendarId="missing",isActive=true});
            Assert.Equal((HttpStatusCode)428,unauthorized.StatusCode);
        });
        await RunAsync(async (application,client)=>
        {
            await GrantEditAsync(application.Services); AddEditHeaders(client);
            using var settings=await client.GetAsync("/api/v1/report-email-settings");
            using var put=new HttpRequestMessage(HttpMethod.Put,"/api/v1/report-email-settings")
            { Content=JsonContent.Create(new{senderAddress=(string?)null,recipients=Array.Empty<string>(),smtpHost=(string?)null,smtpPort=(int?)null,useSsl=true,dailyReportEnabled=true,dailyReportTimeLocal=(string?)null,timeZoneId=(string?)null}) };
            put.Headers.IfMatch.Add(new EntityTagHeaderValue(settings.Headers.ETag!.Tag));
            using var invalid=await client.SendAsync(put);
            Assert.Equal(HttpStatusCode.UnprocessableEntity,invalid.StatusCode);
        });
    }

    [Fact]
    public async Task Resource_requires_a_matching_assigned_calendar_and_machine_id_skills()
    {
        await RunAsync(async (application, client) =>
        {
            await GrantEditAsync(application.Services); AddEditHeaders(client);
            var setupCalendar = await CreateCalendarAsync(client, "machine", "setup_worker");
            var machineId = await CreateMachineAsync(client, setupCalendar, "M-RESOURCE");
            using var invalid = await client.PostAsJsonAsync("/api/v1/resources", new
            { employeeNumber = "E-18", firstName = "Dana", lastName = "Bar", role = "qa_worker", skills = new[] { "Inspection" }, assignedCalendarId = setupCalendar, isActive = true });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);

            using var duplicateSkill = await client.PostAsJsonAsync("/api/v1/resources", new
            { employeeNumber = "E-19", firstName = "Dana", lastName = "Bar", role = "setup_worker", skills = new[] { machineId, machineId }, assignedCalendarId = setupCalendar, isActive = true });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicateSkill.StatusCode);

            using var unknownMachine = await client.PostAsJsonAsync("/api/v1/resources", new
            { employeeNumber = "E-20", firstName = "Dana", lastName = "Bar", role = "setup_worker", skills = new[] { "missing-machine" }, assignedCalendarId = setupCalendar, isActive = true });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownMachine.StatusCode);
            Assert.Contains("unknown_machine", await unknownMachine.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Employee_exceptions_are_loaded_and_subtracted_from_calendar_availability()
    {
        await RunAsync(async (application, client) =>
        {
            await GrantEditAsync(application.Services);
            AddEditHeaders(client);
            using var calendarResponse = await client.PostAsJsonAsync("/api/v1/working-calendars", new
            {
                name = $"Availability calendar {Guid.NewGuid():N}",
                timeZoneId = "UTC",
                workdays = new[] { "sunday", "monday", "tuesday", "wednesday", "thursday" },
                shiftStartsAtLocal = "06:00",
                shiftEndsAtLocal = "14:00",
                usages = new[] { "regular_worker" }
            });
            calendarResponse.EnsureSuccessStatusCode();
            using var calendarJson = JsonDocument.Parse(await calendarResponse.Content.ReadAsStringAsync());
            var calendarId = calendarJson.RootElement.GetProperty("workingCalendarId").GetString()!;

            using var resourceResponse = await client.PostAsJsonAsync("/api/v1/resources", new
            {
                employeeNumber = "E-20", firstName = "Avi", lastName = "Levy",
                role = "regular_worker", skills = Array.Empty<string>(), assignedCalendarId = calendarId,
                photoPath = (string?)null, notes = (string?)null, email = (string?)null, isActive = true
            });
            resourceResponse.EnsureSuccessStatusCode();
            using var resourceJson = JsonDocument.Parse(await resourceResponse.Content.ReadAsStringAsync());
            var resourceId = resourceJson.RootElement.GetProperty("resourceId").GetString()!;

            await CreateExceptionAsync(client, resourceId, "2026-08-16", "vacation", true);
            await CreateExceptionAsync(client, resourceId, "2026-08-17", "sick_day", true);
            var partial = await CreateExceptionAsync(client, resourceId, "2026-08-18", "unavailable", false, "10:00", "12:00", "Appointment");
            await CreateExceptionAsync(client, resourceId, "2026-08-20", "personal_day", true);
            var custom = await CreateExceptionAsync(client, resourceId, "2026-08-21", "custom_note", true, note: "Training day");

            using var patch = new HttpRequestMessage(HttpMethod.Patch,
                $"/api/v1/resources/{resourceId}/exceptions/{partial.Id}")
            { Content = JsonContent.Create(new { note = "Medical appointment" }) };
            patch.Headers.IfMatch.Add(new EntityTagHeaderValue(partial.Tag));
            using var patched = await client.SendAsync(patch);
            Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

            using var list = await client.GetAsync($"/api/v1/resources/{resourceId}/exceptions?from=2026-08-16&to=2026-08-21");
            list.EnsureSuccessStatusCode();
            using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            Assert.Equal(5, listJson.RootElement.GetProperty("items").GetArrayLength());
            Assert.Contains(listJson.RootElement.GetProperty("items").EnumerateArray(), value =>
                value.GetProperty("exceptionType").GetString() == "sick_day");

            using var availability = await client.GetAsync(
                $"/api/v1/resources/{resourceId}/availability?from=2026-08-16T00:00:00Z&to=2026-08-21T00:00:00Z");
            availability.EnsureSuccessStatusCode();
            using var availabilityJson = JsonDocument.Parse(await availability.Content.ReadAsStringAsync());
            var windows = availabilityJson.RootElement.GetProperty("windows").EnumerateArray()
                .Select(value => (Start: value.GetProperty("startsAt").GetDateTimeOffset(), End: value.GetProperty("endsAt").GetDateTimeOffset()))
                .ToArray();
            Assert.DoesNotContain(windows, value => value.Start.Date == new DateTime(2026, 8, 16));
            Assert.DoesNotContain(windows, value => value.Start.Date == new DateTime(2026, 8, 17));
            Assert.Contains(windows, value => value.Start == DateTimeOffset.Parse("2026-08-18T06:00:00Z") && value.End == DateTimeOffset.Parse("2026-08-18T10:00:00Z"));
            Assert.Contains(windows, value => value.Start == DateTimeOffset.Parse("2026-08-18T12:00:00Z") && value.End == DateTimeOffset.Parse("2026-08-18T14:00:00Z"));
            Assert.Contains(windows, value => value.Start == DateTimeOffset.Parse("2026-08-19T06:00:00Z") && value.End == DateTimeOffset.Parse("2026-08-19T14:00:00Z"));

            using var delete = await client.DeleteAsync($"/api/v1/resources/{resourceId}/exceptions/{custom.Id}");
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        });
    }

    [Fact]
    public async Task Cached_holiday_policies_affect_opted_in_employee_calendar_availability()
    {
        await RunAsync(async (application,client)=>
        {
            await GrantEditAsync(application.Services);AddEditHeaders(client);
            using var calendarResponse=await client.PostAsJsonAsync("/api/v1/working-calendars",new{name=$"Holiday calendar {Guid.NewGuid():N}",timeZoneId="UTC",workdays=new[]{"monday","tuesday","wednesday"},windows=new[]{new{startsAtLocal="06:00",endsAtLocal="14:00"}},usages=new[]{"regular_worker"},useIsraeliHolidays=true});
            calendarResponse.EnsureSuccessStatusCode();using var calendarJson=JsonDocument.Parse(await calendarResponse.Content.ReadAsStringAsync());var calendarId=calendarJson.RootElement.GetProperty("workingCalendarId").GetString()!;
            using var resourceResponse=await client.PostAsJsonAsync("/api/v1/resources",new{employeeNumber="E-HOL",firstName="Holiday",lastName="Worker",role="regular_worker",skills=Array.Empty<string>(),assignedCalendarId=calendarId,isActive=true});
            resourceResponse.EnsureSuccessStatusCode();using var resourceJson=JsonDocument.Parse(await resourceResponse.Content.ReadAsStringAsync());var resourceId=resourceJson.RootElement.GetProperty("resourceId").GetString()!;
            (await client.PostAsJsonAsync("/api/v1/israeli-holidays",new{date="2026-08-17",name="Closed",status="non_working"})).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync("/api/v1/israeli-holidays",new{date="2026-08-18",name="Half day",status="partial_working",startsAtLocal="08:00",endsAtLocal="12:00"})).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync("/api/v1/israeli-holidays",new{date="2026-08-19",name="Working holiday",status="working"})).EnsureSuccessStatusCode();
            using var response=await client.GetAsync($"/api/v1/resources/{resourceId}/availability?from=2026-08-17T00:00:00Z&to=2026-08-20T00:00:00Z");response.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var windows=json.RootElement.GetProperty("windows").EnumerateArray().Select(value=>(value.GetProperty("startsAt").GetDateTimeOffset(),value.GetProperty("endsAt").GetDateTimeOffset())).ToArray();
            Assert.DoesNotContain(windows,value=>value.Item1.Date==new DateTime(2026,8,17));
            Assert.Contains(windows,value=>value.Item1==DateTimeOffset.Parse("2026-08-18T08:00:00Z")&&value.Item2==DateTimeOffset.Parse("2026-08-18T12:00:00Z"));
            Assert.Contains(windows,value=>value.Item1==DateTimeOffset.Parse("2026-08-19T06:00:00Z")&&value.Item2==DateTimeOffset.Parse("2026-08-19T14:00:00Z"));
        });
    }

    private static async Task<(string Id, string Tag)> CreateExceptionAsync(
        HttpClient client, string resourceId, string date, string type, bool fullDay,
        string? startsAt = null, string? endsAt = null, string? note = null)
    {
        using var response = await client.PostAsJsonAsync($"/api/v1/resources/{resourceId}/exceptions", new
        { date, exceptionType = type, isFullDay = fullDay, startsAtLocal = startsAt, endsAtLocal = endsAt, note });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("exceptionId").GetString()!, response.Headers.ETag!.Tag);
    }

    private static async Task<string> CreateCalendarAsync(HttpClient client, params string[] usages)
    {
        using var calendar = await client.PostAsJsonAsync("/api/v1/working-calendars", new
        { name = $"Employee calendar {Guid.NewGuid():N}", timeZoneId = "Asia/Jerusalem", workdays = new[] { "sunday" }, shiftStartsAtLocal = "06:00", shiftEndsAtLocal = "14:00", usages });
        calendar.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await calendar.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("workingCalendarId").GetString()!;
    }

    private static async Task<string> CreateMachineAsync(HttpClient client, string calendarId, string number)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/machines", new
        {
            number,
            name = $"Machine {number}",
            processType = "milling",
            axisType = "3-axis",
            capabilities = Array.Empty<string>(),
            workingCalendarId = calendarId,
            isActive = true,
            displayEnabled = true
        });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("machineId").GetString()!;
    }

    private static void AddEditHeaders(HttpClient client){client.DefaultRequestHeaders.Add("X-Meimad-Client-Id","admin-client");client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation","1");}
    private static async Task GrantEditAsync(IServiceProvider services){var database=services.GetRequiredService<SqliteDatabase>();await using var connection=await database.OpenConnectionAsync();await using var command=connection.CreateCommand();command.CommandText="UPDATE edit_tokens SET holder_client_id='admin-client',holder_user_id='admin',generation=1,acquired_at='2026-08-12T00:00:00Z',version=version+1 WHERE id=1;";await command.ExecuteNonQueryAsync();}
    private static async Task RunAsync(Func<WebApplication,HttpClient,Task> test,IIsraeliHolidaySource? holidaySource=null)
    {var folder=Path.Combine(Path.GetTempPath(),"MeimadPlanner.Admin.Tests",Guid.NewGuid().ToString("N"));var app=ServerApplication.Build([$"--Database:Path={Path.Combine(folder,"test.db")}"],host=>{host.UseTestServer();if(holidaySource is not null)host.ConfigureServices(services=>{services.RemoveAll<IIsraeliHolidaySource>();services.AddSingleton(holidaySource);});});try{await app.StartAsync();using var client=app.GetTestClient();await test(app,client);await app.StopAsync();}finally{await app.DisposeAsync();SqliteConnection.ClearAllPools();if(Directory.Exists(folder))Directory.Delete(folder,true);}}

    private sealed class TestHolidaySource(IReadOnlyList<IsraeliHolidaySourceItem> items):IIsraeliHolidaySource
    {
        public string ProviderName=>"test";public IReadOnlyList<IsraeliHolidaySourceItem> Items{get;set;}=items;public bool Fail{get;set;}
        public Task<IReadOnlyList<IsraeliHolidaySourceItem>> FetchAsync(int fromYear,int toYear,CancellationToken token)=>Fail
            ? throw new IsraeliHolidaySourceException("Test provider offline.") : Task.FromResult(Items);
    }
}
