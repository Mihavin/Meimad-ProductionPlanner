using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Application.Reports;
using Meimad.Planner.Server.Domain.AdministrativeSetup;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meimad.Planner.Server.Tests.Reports;

public sealed class WeeklyEmployeeEfficiencyReportApiTests
{
    [Fact]
    public async Task Report_groups_employees_compares_plan_actual_and_calendar_capacity_and_sends_once()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026,8,13,9,0,0,TimeSpan.Zero));
        var sender = new CapturingSender();
        await RunAsync(time, sender, async (app, client) =>
        {
            await SeedAsync(app.Services);
            using var measurement = new HttpRequestMessage(HttpMethod.Post, "/api/v1/employee-work-measurements")
            { Content = JsonContent.Create(new { employeeResourceId="setup-1", workDate="2026-08-03", plannedSeconds=3600, actualSeconds=4500, sourceReference="B-10 / Op10" }) };
            measurement.Headers.Add("X-Meimad-Client-Id","efficiency-client");
            measurement.Headers.Add("X-Meimad-User-Id","planner-user");
            measurement.Headers.Add("X-Meimad-Edit-Generation","1");
            using var recorded = await client.SendAsync(measurement);
            Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);

            using var generated = await client.GetAsync("/api/v1/reports/weekly-employee-efficiency");
            Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
            using var document = JsonDocument.Parse(await generated.Content.ReadAsStringAsync());
            Assert.Equal("2026-08-02", document.RootElement.GetProperty("weekStart").GetString());
            Assert.Equal("2026-08-08", document.RootElement.GetProperty("weekEnd").GetString());
            var employees = document.RootElement.GetProperty("employees").EnumerateArray().ToArray();
            Assert.Equal(2, employees.Length);
            var setup = employees[0];
            Assert.Equal("setup_worker", setup.GetProperty("role").GetString());
            Assert.Equal(3600, setup.GetProperty("plannedSeconds").GetInt64());
            Assert.Equal(4500, setup.GetProperty("actualSeconds").GetInt64());
            Assert.Equal(900, setup.GetProperty("differenceSeconds").GetInt64());
            Assert.Equal(25m, setup.GetProperty("percentageDifference").GetDecimal());
            Assert.Equal(144000, setup.GetProperty("availableCapacitySeconds").GetInt64());
            Assert.Equal(2.5m, setup.GetProperty("plannedCapacityPercent").GetDecimal());
            Assert.Equal(3.13m, setup.GetProperty("actualCapacityPercent").GetDecimal());
            var qa = employees[1];
            Assert.Equal("qa_worker", qa.GetProperty("role").GetString());
            Assert.Equal(-50m, qa.GetProperty("percentageDifference").GetDecimal());
            Assert.DoesNotContain("regular-1", await generated.Content.ReadAsStringAsync());
            var json = await generated.Content.ReadAsStringAsync();
            Assert.DoesNotContain("rank", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("payroll", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("maintenance", json, StringComparison.OrdinalIgnoreCase);

            using var send = new HttpRequestMessage(HttpMethod.Post,"/api/v1/reports/weekly-employee-efficiency/send");
            send.Headers.Add("X-Meimad-Client-Id","efficiency-client");
            send.Headers.Add("X-Meimad-Edit-Generation","1");
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(send)).StatusCode);
            Assert.Single(sender.Messages);
            Assert.Equal(["production@example.test"], sender.Messages[0].Recipients);
            Assert.Contains("Setup worker", sender.Messages[0].Body);
            Assert.Contains("25.00%", sender.Messages[0].Body);

            var service = app.Services.GetRequiredService<WeeklyEmployeeEfficiencyReportService>();
            Assert.True(await service.SendIfDueAsync());
            Assert.False(await service.SendIfDueAsync());
            Assert.Equal(2, sender.Messages.Count);
        });
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars(id,name,time_zone_id,calendar_json) VALUES
              ('workers','Workers','UTC','{"weeklySchedule":{"workdays":["monday","tuesday","wednesday","thursday","friday"],"windows":[{"startsAtLocal":"08:00","endsAtLocal":"16:00"}]},"usages":["setup_worker","qa_worker","regular_worker"]}');
            INSERT INTO employee_resources(id,employee_number,name,resource_type,first_name,last_name,skills_json,assigned_calendar_id,is_active) VALUES
              ('setup-1','E-01','Avi Cohen','setup_worker','Avi','Cohen','[]','workers',1),
              ('qa-1','E-02','Dana Levi','qa_worker','Dana','Levi','[]','workers',1),
              ('regular-1','E-03','Noa Bar','regular_worker','Noa','Bar','[]','workers',1);
            INSERT INTO employee_work_measurements(id,employee_resource_id,work_date,planned_seconds,actual_seconds,recorded_by,recorded_at) VALUES
              ('qa-work','qa-1','2026-08-04',7200,3600,'planner-user','2026-08-05T00:00:00Z');
            UPDATE report_email_settings SET sender_address='planner@example.test',recipients_json='["production@example.test"]',
              smtp_host='smtp.example.test',smtp_port=25,use_ssl=0,time_zone_id='UTC',
              weekly_employee_efficiency_enabled=1,weekly_employee_efficiency_send_day='thursday',weekly_employee_efficiency_time_local='08:00';
            UPDATE edit_tokens SET holder_client_id='efficiency-client',holder_user_id='planner-user',generation=1,
              acquired_at='2026-08-13T08:00:00Z',updated_at='2026-08-13T08:00:00Z' WHERE id=1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunAsync(TimeProvider timeProvider, CapturingSender sender, Func<WebApplication,HttpClient,Task> test)
    {
        var root=Path.Combine(Path.GetTempPath(),"MeimadPlanner.Efficiency.Tests",Guid.NewGuid().ToString("N"));
        var app=ServerApplication.Build(["--Server:Host=127.0.0.1","--Server:Port=5098",$"--Database:Path={Path.Combine(root,"test.db")}"], webHost =>
        { webHost.UseTestServer(); webHost.ConfigureServices(services => { services.RemoveAll<TimeProvider>();services.AddSingleton(timeProvider);services.RemoveAll<IEmployeeEfficiencyEmailSender>();services.AddSingleton<IEmployeeEfficiencyEmailSender>(sender); }); });
        try { await app.StartAsync();using var client=app.GetTestClient();await test(app,client);await app.StopAsync(); }
        finally { await app.DisposeAsync();SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true); }
    }
    private sealed class FixedTimeProvider(DateTimeOffset now):TimeProvider { public override DateTimeOffset GetUtcNow()=>now; }
    private sealed class CapturingSender:IEmployeeEfficiencyEmailSender
    {
        internal List<Message> Messages { get; }=[];
        public Task SendAsync(ReportEmailSettings settings,WeeklyEmployeeEfficiencyReport report,CancellationToken token)
        { Messages.Add(new(settings.Recipients,SmtpEmployeeEfficiencyEmailSender.Body(report)));return Task.CompletedTask; }
    }
    private sealed record Message(IReadOnlyList<string> Recipients,string Body);
}
