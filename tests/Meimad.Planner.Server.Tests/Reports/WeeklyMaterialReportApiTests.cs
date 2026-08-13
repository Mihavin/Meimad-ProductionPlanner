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

public sealed class WeeklyMaterialReportApiTests
{
    [Fact]
    public async Task Minimal_report_includes_scrap_and_manual_and_automatic_email_use_configured_recipients()
    {
        var now = new DateTimeOffset(2026, 8, 13, 9, 0, 0, TimeSpan.Zero); // Thursday
        var fakeTime = new FixedTimeProvider(now);
        var sender = new CapturingSender();
        await RunAsync(fakeTime, sender, async (application, client) =>
        {
            await SeedAsync(application.Services);

            using var generated = await client.GetAsync("/api/v1/reports/weekly-material-order");
            Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
            using var document = JsonDocument.Parse(await generated.Content.ReadAsStringAsync());
            var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(2, item.EnumerateObject().Count());
            Assert.Equal("PN-MAT-A", item.GetProperty("casePartNumber").GetString());
            Assert.Equal(17, item.GetProperty("requiredMaterialPieceQuantity").GetInt64());
            var json = await generated.Content.ReadAsStringAsync();
            Assert.DoesNotContain("operation", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("supplier", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("status", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("date", json, StringComparison.OrdinalIgnoreCase);

            using var sendRequest = new HttpRequestMessage(HttpMethod.Post,
                "/api/v1/reports/weekly-material-order/send");
            sendRequest.Headers.Add("X-Meimad-Client-Id", "report-client");
            sendRequest.Headers.Add("X-Meimad-Edit-Generation", "1");
            using var sent = await client.SendAsync(sendRequest);
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
            Assert.Single(sender.Messages);
            Assert.Equal(["buyer@example.test"], sender.Messages[0].Recipients);
            Assert.Equal(
                "Case / Part Number\tRequired Material Piece Quantity\r\nPN-MAT-A\t17\r\n",
                sender.Messages[0].Body);

            var service = application.Services.GetRequiredService<WeeklyMaterialReportService>();
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
            INSERT INTO cases (id,part_number,name,working_folder_path) VALUES
                ('case-mat-a','PN-MAT-A','Material A','C:\Cases\A'),
                ('case-mat-b','PN-MAT-B','Material B','C:\Cases\B');
            INSERT INTO orders (id,case_id,order_reference,quantity,work_finish_date,status) VALUES
                ('order-mat-a1','case-mat-a','SO-1',10,'2026-08-16','active'),
                ('order-mat-a2','case-mat-a','SO-2',4,'2026-08-18','active'),
                ('order-mat-b','case-mat-b','SO-3',9,'2026-08-30','active');
            INSERT INTO production_batches (id,case_id,batch_number,status,planned_quantity) VALUES
                ('batch-mat-a1','case-mat-a','B-1','waiting',12),
                ('batch-mat-a2','case-mat-a','B-2','in_production',5),
                ('batch-mat-b','case-mat-b','B-3','waiting',10);
            INSERT INTO batch_allocations (id,production_batch_id,allocation_type,order_id,quantity) VALUES
                ('alloc-a1-order','batch-mat-a1','order','order-mat-a1',10),
                ('alloc-a1-scrap','batch-mat-a1','scrap_allowance',NULL,2),
                ('alloc-a2-order','batch-mat-a2','order','order-mat-a2',4),
                ('alloc-a2-scrap','batch-mat-a2','scrap_allowance',NULL,1),
                ('alloc-b-order','batch-mat-b','order','order-mat-b',9),
                ('alloc-b-scrap','batch-mat-b','scrap_allowance',NULL,1);
            UPDATE report_email_settings SET
                sender_address='planner@example.test',
                recipients_json='["buyer@example.test"]',
                smtp_host='smtp.example.test',smtp_port=25,use_ssl=0,
                weekly_material_report_enabled=1,
                weekly_material_report_send_day='thursday',
                weekly_material_report_time_local='08:00',
                time_zone_id='UTC';
            UPDATE edit_tokens SET holder_client_id='report-client',holder_user_id='planner',generation=1,
                acquired_at='2026-08-13T08:00:00Z',updated_at='2026-08-13T08:00:00Z' WHERE id=1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunAsync(
        TimeProvider timeProvider,
        CapturingSender sender,
        Func<WebApplication, HttpClient, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "MeimadPlanner.MaterialReport.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5097", $"--Database:Path={Path.Combine(root, "test.db")}"],
            webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(timeProvider);
                    services.RemoveAll<IMaterialReportEmailSender>();
                    services.AddSingleton<IMaterialReportEmailSender>(sender);
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
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CapturingSender : IMaterialReportEmailSender
    {
        internal List<Message> Messages { get; } = [];
        public Task SendAsync(
            ReportEmailSettings settings,
            WeeklyMaterialOrderReport report,
            CancellationToken token)
        {
            Messages.Add(new(settings.Recipients, SmtpMaterialReportEmailSender.Body(report)));
            return Task.CompletedTask;
        }
    }

    private sealed record Message(IReadOnlyList<string> Recipients, string Body);
}
