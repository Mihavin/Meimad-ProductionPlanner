using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Cnc;

public sealed class FanucFocasApiTests
{
    [Fact]
    public async Task FANUC_FOCAS_connection_is_registered_saved_sanitized_and_read_only()
    {
        var printFile = Path.Combine(Path.GetTempPath(), "MeimadPlanner.FocasApi", Guid.NewGuid().ToString("N"), "print.txt");
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "focas-client");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");

            using var adapters = await client.GetAsync("/api/v1/cnc-adapters");
            adapters.EnsureSuccessStatusCode();
            using (var json = JsonDocument.Parse(await adapters.Content.ReadAsStringAsync()))
            {
                var focas = json.RootElement.EnumerateArray().Single(value => value.GetProperty("id").GetString() == "FANUC_FOCAS");
                Assert.True(focas.GetProperty("implemented").GetBoolean());
                Assert.False(focas.GetProperty("capabilities").GetProperty("canWriteVariables").GetBoolean());
                Assert.True(focas.GetProperty("capabilities").GetProperty("canReadPartCounter").GetBoolean());
            }

            using var writeRejected = await client.PutAsJsonAsync(
                "/api/v1/machines/machine-fanuc/cnc-connection", Body(printFile, allowWrite: true));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, writeRejected.StatusCode);
            Assert.Contains("allowWrite", await writeRejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var pathRejected = await client.PutAsJsonAsync(
                "/api/v1/machines/machine-fanuc/cnc-connection", Body(null, allowWrite: false));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, pathRejected.StatusCode);
            Assert.Contains("dprnt.filePath", await pathRejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var hostRejected = await client.PutAsJsonAsync(
                "/api/v1/machines/machine-fanuc/cnc-connection", Body(printFile, allowWrite: false, dprntHost: "not a host"));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, hostRejected.StatusCode);
            Assert.Contains("dprnt.host", await hostRejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var saved = await client.PutAsJsonAsync(
                "/api/v1/machines/machine-fanuc/cnc-connection", Body(printFile, allowWrite: false));
            saved.EnsureSuccessStatusCode();
            using (var json = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()))
            {
                Assert.Equal("FANUC_FOCAS", json.RootElement.GetProperty("adapterType").GetString());
                Assert.False(json.RootElement.GetProperty("allowWrite").GetBoolean());
                var configuration = json.RootElement.GetProperty("configuration");
                Assert.Equal("192.168.0.77", configuration.GetProperty("host").GetString());
                Assert.Equal(8193, configuration.GetProperty("port").GetInt32());
                Assert.Equal(3000, configuration.GetProperty("timeoutMs").GetInt32());
                Assert.Equal("PARTS_TOTAL_6712", configuration.GetProperty("partCounterSource").GetString());
                var dprnt = configuration.GetProperty("dprnt");
                Assert.Equal("FILE", dprnt.GetProperty("source").GetString());
                Assert.Equal(printFile, dprnt.GetProperty("filePath").GetString());
                Assert.Equal("ON_OFFSET_LOADER", dprnt.GetProperty("clearPolicy").GetString());
                Assert.Equal("192.168.0.90", dprnt.GetProperty("host").GetString());
                Assert.Equal(4001, dprnt.GetProperty("port").GetInt32());
                var access = configuration.GetProperty("programAccess");
                Assert.Equal("FOCAS_PROGRAM_UPLOAD", access.GetProperty("provider").GetString());
                Assert.Equal("//CNC_MEM/USER/PATH1/", access.GetProperty("programFolder").GetString());
                Assert.True(access.GetProperty("headerPartPatterns").GetArrayLength() > 0);
                Assert.Equal(2000, configuration.GetProperty("monitoring").GetProperty("pollingIntervalMs").GetInt32());
            }

            using var read = await client.GetAsync("/api/v1/machines/machine-fanuc/cnc-connection");
            read.EnsureSuccessStatusCode();
            using (var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync()))
            {
                Assert.Equal("FANUC_FOCAS", json.RootElement.GetProperty("adapterType").GetString());
                Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
            }

            // No FOCAS library is installed on the build machine, so the test reports the adapter fault as data.
            using var test = await client.PostAsync("/api/v1/machines/machine-fanuc/cnc-connection/test", null);
            Assert.Equal(HttpStatusCode.BadGateway, test.StatusCode);
            using var testJson = JsonDocument.Parse(await test.Content.ReadAsStringAsync());
            Assert.False(testJson.RootElement.GetProperty("overallSuccess").GetBoolean());
            Assert.Equal("OFFLINE", testJson.RootElement.GetProperty("connectionStatus").GetString());
            var focasCheck = testJson.RootElement.GetProperty("checks").EnumerateArray()
                .Single(value => value.GetProperty("id").GetString() == "focas");
            Assert.False(focasCheck.GetProperty("succeeded").GetBoolean());
        });
    }

    private static object Body(string? printFile, bool allowWrite, string dprntHost = "192.168.0.90") => new
    {
        adapterType = "FANUC_FOCAS", enabled = false,
        pollingIntervalMs = 2000, connectionTimeoutMs = 3000,
        maximumReconnectBackoffMs = 30000, allowRead = true, allowWrite,
        rawTelemetryRetentionDays = 14, version = 0,
        configuration = new
        {
            host = "192.168.0.77", macAddress = "00:E0:E4:12:34:56", port = 8193, timeoutMs = 9999,
            partCounterSource = "parts_total_6712",
            dprnt = new { source = "FILE", filePath = printFile, clearPolicy = "on_offset_loader", port = 4001, host = dprntHost },
            programAccess = new { provider = "FOCAS_PROGRAM_UPLOAD", enabled = true }
        }
    };

    private static async Task SeedAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id)
            VALUES ('calendar-fanuc', 'Fanuc', 'UTC');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES ('machine-fanuc', 'M-F', 'FANUC 0i-F', 'mill', 'calendar-fanuc', 'active', 1);
            UPDATE edit_tokens SET holder_client_id = 'focas-client', holder_user_id = 'planner',
                generation = 1, acquired_at = $at, version = version + 1 WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.Focas.Api", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build([
            "--Server:Host=127.0.0.1", "--Server:Port=5099",
            $"--Database:Path={Path.Combine(directory, "test.db")}"], builder => builder.UseTestServer());
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
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
