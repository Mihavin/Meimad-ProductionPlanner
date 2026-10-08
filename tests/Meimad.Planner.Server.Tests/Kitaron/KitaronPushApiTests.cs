using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Application.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Meimad.Planner.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Kitaron;

public sealed class KitaronPushApiTests
{
    [Fact]
    public async Task Reconciliation_is_read_only_for_ERP_and_acknowledgement_requires_fresh_review_version()
    {
        await RunAsync(async client =>
        {
            var intent = await client.GetFromJsonAsync<JsonElement>("/api/v1/kitaron/push/runs/unknown/intent");
            Assert.Equal("OutcomeUnknown", intent.GetProperty("state").GetString());
            using var premature = await client.PostAsJsonAsync("/api/v1/kitaron/push/runs/unknown/acknowledge", new { expectedVersion = 1 });
            Assert.Equal(HttpStatusCode.Conflict, premature.StatusCode);
            using var review = await client.PostAsJsonAsync("/api/v1/kitaron/push/runs/unknown/reconcile", new { expectedVersion = 1 });
            Assert.Equal(HttpStatusCode.OK, review.StatusCode); // Legacy intent: no ERP connection exists or is needed.
            var inspected = await review.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("OutcomeUnknown", inspected.GetProperty("state").GetString());
            using var stale = await client.PostAsJsonAsync("/api/v1/kitaron/push/runs/unknown/acknowledge", new { expectedVersion = 1 });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            using var ack = await client.PostAsJsonAsync("/api/v1/kitaron/push/runs/unknown/acknowledge",
                new { expectedVersion = inspected.GetProperty("version").GetInt32() });
            Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
            var resolved = await ack.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Reconciled", resolved.GetProperty("state").GetString());
            Assert.Equal(2, resolved.GetProperty("reconciliations").GetArrayLength());
        }, async application =>
        {
            await using var sql = await application.Services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
            await using var command = sql.CreateCommand();
            command.CommandText = "INSERT INTO kitaron_push_runs(id,trigger,started_at,status,lifecycle_state) VALUES('unknown','manual','2026-10-08T09:00:00Z','failed','OutcomeUnknown')";
            await command.ExecuteNonQueryAsync();
        });
    }

    [Fact]
    public async Task Everyone_reads_the_push_settings_but_only_Setup_changes_previews_or_pushes()
    {
        await RunAsync(async client =>
        {
            JsonElement settings;
            using (client.SignedInWithOnly(Permissions.DecideQc))
            {
                using var read = await client.GetAsync("/api/v1/kitaron/push");
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                using var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
                settings = json.RootElement.Clone();
                Assert.False(settings.GetProperty("enabled").GetBoolean());
                Assert.Equal(4, settings.GetProperty("kitaronColumns").GetArrayLength());
                Assert.Equal(7, settings.GetProperty("plannerValues").GetArrayLength());

                using var change = await client.PutAsJsonAsync("/api/v1/kitaron/push", new
                {
                    enabled = true, intervalMinutes = 15, mappings = Array.Empty<object>(), expectedVersion = 1
                });
                Assert.Equal(HttpStatusCode.Forbidden, change.StatusCode);
                using var preview = await client.PostAsync("/api/v1/kitaron/push/preview", null);
                Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
                using var run = await client.PostAsync("/api/v1/kitaron/push/run", null);
                Assert.Equal(HttpStatusCode.Forbidden, run.StatusCode);
                using var reconcile = await client.PostAsJsonAsync("/api/v1/kitaron/push/runs/unknown/reconcile", new { expectedVersion = 1 });
                Assert.Equal(HttpStatusCode.Forbidden, reconcile.StatusCode);
                using var acknowledge = await client.PostAsJsonAsync("/api/v1/kitaron/push/runs/unknown/acknowledge", new { expectedVersion = 1 });
                Assert.Equal(HttpStatusCode.Forbidden, acknowledge.StatusCode);
            }

            // The connector is off in a new installation: the push explains why it cannot run.
            using (var blocked = await client.PostAsync("/api/v1/kitaron/push/run", null))
            {
                Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
                var body = await blocked.Content.ReadAsStringAsync();
                Assert.Contains("kitaron_push_blocked", body, StringComparison.Ordinal);
                Assert.Contains("switched off", body, StringComparison.Ordinal);
            }

            using (var invalid = await client.PutAsJsonAsync("/api/v1/kitaron/push", new
                   {
                       enabled = true, intervalMinutes = 15,
                       mappings = new[] { new { kitaronColumn = "StartDate", plannerValue = "actual_start", enabled = true } },
                       expectedVersion = settings.GetProperty("version").GetInt32()
                   }))
                Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);

            var version = settings.GetProperty("version").GetInt32();
            using (var saved = await client.PutAsJsonAsync("/api/v1/kitaron/push", new
                   {
                       enabled = true, intervalMinutes = 20,
                       mappings = new[] { new { kitaronColumn = "FinishDateCalc", plannerValue = "forecast_finish", enabled = true } },
                       expectedVersion = version
                   }))
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            using var stale = await client.PutAsJsonAsync("/api/v1/kitaron/push", new
            {
                enabled = false, intervalMinutes = 20, mappings = Array.Empty<object>(), expectedVersion = version
            });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Contains("edit_conflict", await stale.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        });
    }

    private static async Task RunAsync(Func<HttpClient, Task> test, Func<WebApplication, Task>? arrange = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.KitaronPush.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directory, "test.db")}"],
            builder => builder.UseSignedInTestServer());
        try
        {
            await application.StartAsync();
            if (arrange is not null) await arrange(application);
            using var client = application.GetTestClient();
            await test(client);
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
