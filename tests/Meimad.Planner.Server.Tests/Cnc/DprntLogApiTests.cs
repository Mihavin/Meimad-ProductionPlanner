using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Cnc;

/// <summary>The permanent DPRNT log (schema v96): every line a Machine sent, read in arrival order.</summary>
public sealed class DprntLogApiTests
{
    private const string Log = "/api/v1/machines/machine-15/dprnt-log";

    [Fact]
    public async Task The_log_returns_every_line_in_arrival_order_with_period_search_and_paging()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);

            var all = await ReadAsync(client, Log);
            Assert.Equal(new[] { "pingret", "30P647004101-001", "MEIMAD/V/1/EVENT/CST/ID/X/SEQ/1", "T12 D=10.000", "100% done_1" },
                Lines(all));
            Assert.False(all.GetProperty("hasMore").GetBoolean());
            Assert.Equal("2026-10-04T10:00:00+00:00", all.GetProperty("lines")[0].GetProperty("receivedAt").GetString());

            // The period is [from, to); search is case-insensitive and takes % and _ literally.
            var period = await ReadAsync(client, $"{Log}?from=2026-10-04T10:00:01Z&to=2026-10-05T00:00:00Z");
            Assert.Equal(new[] { "MEIMAD/V/1/EVENT/CST/ID/X/SEQ/1", "T12 D=10.000" }, Lines(period));
            Assert.Equal(new[] { "MEIMAD/V/1/EVENT/CST/ID/X/SEQ/1" }, Lines(await ReadAsync(client, $"{Log}?search=meimad")));
            Assert.Equal(new[] { "100% done_1" }, Lines(await ReadAsync(client, $"{Log}?search=%25%20done_")));
            Assert.Equal(new[] { "100% done_1" }, Lines(await ReadAsync(client, $"{Log}?search=_")));

            // Paging: the last id of a page continues the log.
            var first = await ReadAsync(client, $"{Log}?limit=2");
            Assert.True(first.GetProperty("hasMore").GetBoolean());
            var lastId = first.GetProperty("lines")[1].GetProperty("id").GetInt64();
            var next = await ReadAsync(client, $"{Log}?limit=2&afterId={lastId}");
            Assert.Equal(new[] { "MEIMAD/V/1/EVENT/CST/ID/X/SEQ/1", "T12 D=10.000" }, Lines(next));

            // Another Machine's lines are not mixed in.
            Assert.Equal(new[] { "other machine" }, Lines(await ReadAsync(client, "/api/v1/machines/machine-14/dprnt-log")));

            using var unknown = await client.GetAsync("/api/v1/machines/no-such-machine/dprnt-log");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            foreach (var invalid in new[] { "from=yesterday", "from=2026-10-05T00:00:00Z&to=2026-10-04T00:00:00Z", "limit=0", "limit=50001", "afterId=-1" })
            {
                using var response = await client.GetAsync($"{Log}?{invalid}");
                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            }
        });
    }

    [Fact]
    public async Task Server_maintenance_counts_and_clears_the_DPRNT_log_like_other_collected_data()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            var catalog = await ReadAsync(client, "/api/v1/server-maintenance/database");
            var dprnt = catalog.GetProperty("deletableTypes").EnumerateArray()
                .Single(value => value.GetProperty("type").GetString() == "cnc_dprnt_log");
            Assert.Equal("DPRNT log", dprnt.GetProperty("displayName").GetString());

            using var preview = await client.PostAsJsonAsync("/api/v1/server-maintenance/collected-data/preview", new
            {
                fromInclusive = "2026-10-04T00:00:00Z",
                toExclusive = "2026-10-05T00:00:00Z",
                types = new[] { "cnc_dprnt_log" },
                machineId = "machine-15"
            });
            var body = await preview.Content.ReadAsStringAsync();
            Assert.True(preview.StatusCode == HttpStatusCode.OK, body);
            using var document = JsonDocument.Parse(body);
            Assert.Equal(4, document.RootElement.GetProperty("totalRows").GetInt64());
        });
    }

    private static IReadOnlyList<string> Lines(JsonElement page) =>
        page.GetProperty("lines").EnumerateArray().Select(line => line.GetProperty("line").GetString()!).ToArray();

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id) VALUES ('calendar-1', 'Day', 'UTC');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES ('machine-15', '15', 'Haas UMC-500ss', 'mill', 'calendar-1', 'active', 1),
                   ('machine-14', '14', 'Haas UMC-500ss', 'mill', 'calendar-1', 'active', 1);
            INSERT INTO machine_dprnt_lines (machine_id, connection_id, received_at, line)
            VALUES ('machine-15', 'cnc-15', '2026-10-04T10:00:00.0000000+00:00', 'pingret'),
                   ('machine-15', 'cnc-15', '2026-10-04T10:00:00.0000000+00:00', '30P647004101-001'),
                   ('machine-14', 'cnc-14', '2026-10-04T10:00:00.5000000+00:00', 'other machine'),
                   ('machine-15', 'cnc-15', '2026-10-04T10:54:48.0000000+00:00', 'MEIMAD/V/1/EVENT/CST/ID/X/SEQ/1'),
                   ('machine-15', 'cnc-15', '2026-10-04T23:59:59.0000000+00:00', 'T12 D=10.000'),
                   ('machine-15', 'cnc-15', '2026-10-05T00:00:00.0000000+00:00', '100% done_1');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.DprntLog.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directory, "test.db")}"],
            webHost => webHost.UseSignedInTestServer());
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
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (IOException) { } // A pooled handle may still hold the file; the temp folder is disposable.
        }
    }
}
