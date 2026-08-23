using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Haas;

public sealed class HaasApiTests
{
    [Fact]
    [Trait("Category", "LiveCommissioning")]
    public async Task Optional_live_MTConnect_API_test_uses_saved_Server_configuration()
    {
        var configured = Environment.GetEnvironmentVariable("MEIMAD_MTCONNECT_LIVE_URL");
        if (string.IsNullOrWhiteSpace(configured)) return;
        var address = new Uri(configured, UriKind.Absolute);
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "haas-client");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
            using var saved = await client.PutAsJsonAsync(
                "/api/v1/machines/machine-haas/haas/connection", new
                {
                    host = address.Host, mdcPort = 5051, mtConnectPort = address.Port,
                    telemetryProvider = "MTCONNECT", localNetShareEnabled = false,
                    productionModeVariable = 10605, legacyVariableAlias = 605,
                    partCounterSource = "M30_COUNTER_1", pollingIntervalMs = 2000,
                    connectionTimeoutMs = 5000, stableProgramPolls = 2,
                    headerLineLimit = 50, headerByteLimit = 32768,
                    headerPartPatterns = new[] { @"PART\s*[:=]\s*([^()]+)" },
                    enabled = false, version = 0
                });
            saved.EnsureSuccessStatusCode();

            using var tested = await client.PostAsync(
                "/api/v1/machines/machine-haas/haas/test-mtconnect", null);
            var body = await tested.Content.ReadAsStringAsync();
            Assert.True(tested.IsSuccessStatusCode, body);
            using var json = JsonDocument.Parse(body);
            Assert.True(json.RootElement.GetProperty("succeeded").GetBoolean());
            Assert.Equal("1500.CNC", json.RootElement.GetProperty("programNumber").GetString());
            Assert.True(json.RootElement.GetProperty("parts").GetInt32() > 0);
        });
    }

    [Fact]
    public async Task Configurable_macro_and_monitoring_contract_are_server_owned()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "haas-client");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");

            using var initial = await client.GetAsync("/api/v1/machines/machine-haas/haas/connection");
            initial.EnsureSuccessStatusCode();
            using (var json = JsonDocument.Parse(await initial.Content.ReadAsStringAsync()))
            {
                Assert.Equal(10605, json.RootElement.GetProperty("productionModeVariable").GetInt32());
                Assert.Equal(605, json.RootElement.GetProperty("legacyVariableAlias").GetInt32());
                Assert.Equal(0, json.RootElement.GetProperty("version").GetInt32());
            }

            using var saved = await client.PutAsJsonAsync(
                "/api/v1/machines/machine-haas/haas/connection", new
                {
                    host = "192.168.1.50", mdcPort = 5051, mtConnectPort = 8082,
                    localNetShareEnabled = true, localNetSharePath = @"\\HAAS-VF3\User Data",
                    credentialsReference = "windows-service-account", productionModeVariable = 10606,
                    legacyVariableAlias = 606, partCounterSource = "M30_COUNTER_1",
                    pollingIntervalMs = 2500, connectionTimeoutMs = 4000, stableProgramPolls = 2,
                    headerLineLimit = 50, headerByteLimit = 32768,
                    headerPartPatterns = new[] { @"PART\s*[:=]\s*([^()]+)" },
                    enabled = false, version = 0, telemetryProvider = "MTCONNECT"
                });
            saved.EnsureSuccessStatusCode();
            using (var json = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()))
            {
                Assert.Equal(10606, json.RootElement.GetProperty("productionModeVariable").GetInt32());
                Assert.Equal("M30_COUNTER_1", json.RootElement.GetProperty("partCounterSource").GetString());
                Assert.Equal("MTCONNECT", json.RootElement.GetProperty("telemetryProvider").GetString());
                Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
            }

            // A pre-provider Windows client must not silently switch an explicitly
            // selected MTConnect connection back to MDC when it saves other fields.
            using var legacySaved = await client.PutAsJsonAsync(
                "/api/v1/machines/machine-haas/haas/connection", new
                {
                    host = "192.168.1.50", mdcPort = 5051, mtConnectPort = 8082,
                    localNetShareEnabled = true, localNetSharePath = @"\\HAAS-VF3\User Data",
                    credentialsReference = "windows-service-account", productionModeVariable = 10606,
                    legacyVariableAlias = 606, partCounterSource = "M30_COUNTER_1",
                    pollingIntervalMs = 2500, connectionTimeoutMs = 4000, stableProgramPolls = 2,
                    headerLineLimit = 50, headerByteLimit = 32768,
                    headerPartPatterns = new[] { @"PART\s*[:=]\s*([^()]+)" },
                    enabled = false, version = 1
                });
            legacySaved.EnsureSuccessStatusCode();
            using (var json = JsonDocument.Parse(await legacySaved.Content.ReadAsStringAsync()))
            {
                Assert.Equal("MTCONNECT", json.RootElement.GetProperty("telemetryProvider").GetString());
                Assert.Equal(2, json.RootElement.GetProperty("version").GetInt32());
            }

            using var monitor = await client.GetAsync("/api/v1/machines/machine-haas/haas/monitor");
            monitor.EnsureSuccessStatusCode();
            using var monitorJson = JsonDocument.Parse(await monitor.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Null, monitorJson.RootElement.GetProperty("snapshot").ValueKind);
            Assert.Equal(JsonValueKind.Null, monitorJson.RootElement.GetProperty("activeBench").ValueKind);

            using var adapters = await client.GetAsync("/api/v1/cnc-adapters");
            adapters.EnsureSuccessStatusCode();
            var adapterText = await adapters.Content.ReadAsStringAsync();
            Assert.Contains("HAAS_NGC", adapterText, StringComparison.Ordinal);
            Assert.Contains("MTCONNECT", adapterText, StringComparison.Ordinal);
            Assert.Contains("Coming later", adapterText, StringComparison.Ordinal);

            using var generic = await client.GetAsync("/api/v1/machines/machine-haas/cnc-connection");
            generic.EnsureSuccessStatusCode();
            var genericText = await generic.Content.ReadAsStringAsync();
            using var genericJson = JsonDocument.Parse(genericText);
            Assert.Equal("HAAS_NGC", genericJson.RootElement.GetProperty("adapterType").GetString());
            Assert.Equal("MTCONNECT", genericJson.RootElement.GetProperty("configuration")
                .GetProperty("telemetryProvider").GetString());
            Assert.Equal(8082, genericJson.RootElement.GetProperty("configuration")
                .GetProperty("mtConnect").GetProperty("port").GetInt32());
            Assert.True(genericJson.RootElement.GetProperty("usernameSecretConfigured").GetBoolean());
            Assert.DoesNotContain("windows-service-account", genericText, StringComparison.Ordinal);
            Assert.DoesNotContain("credentialsReference", genericText, StringComparison.OrdinalIgnoreCase);

            using var genericSaved = await client.PutAsJsonAsync(
                "/api/v1/machines/machine-haas/cnc-connection", new
                {
                    adapterType = "HAAS_NGC", enabled = false,
                    pollingIntervalMs = 3000, connectionTimeoutMs = 4500,
                    maximumReconnectBackoffMs = 30000, allowRead = true, allowWrite = true,
                    rawTelemetryRetentionDays = 14,
                    usernameSecretId = "username-ref", passwordSecretId = "password-ref", version = 2,
                    configuration = new
                    {
                        host = "192.168.1.51", mdc = new { port = 5051, timeoutMs = 4500 },
                        mtConnect = new { port = 8083, timeoutMs = 4500 },
                        telemetryProvider = "MDC",
                        programAccess = new
                        {
                            provider = "HAAS_LOCAL_NET_SHARE", enabled = true,
                            sharePath = @"\\HAAS-VF3\User Data", headerLineLimit = 50,
                            headerByteLimit = 32768,
                            headerPartPatterns = new[] { @"PART\s*[:=]\s*([^()]+)" }
                        },
                        production = new
                        {
                            variableNumber = 10607, legacyVariableAlias = 607,
                            partCounterSource = "Q500"
                        },
                        monitoring = new
                        {
                            pollingIntervalMs = 3000, stableProgramPolls = 2,
                            maximumReconnectBackoffMs = 30000, rawTelemetryRetentionDays = 14
                        }
                    }
                });
            genericSaved.EnsureSuccessStatusCode();
            var savedText = await genericSaved.Content.ReadAsStringAsync();
            Assert.DoesNotContain("username-ref", savedText, StringComparison.Ordinal);
            Assert.DoesNotContain("password-ref", savedText, StringComparison.Ordinal);

            using var projectedHaas = await client.GetAsync("/api/v1/machines/machine-haas/haas/connection");
            projectedHaas.EnsureSuccessStatusCode();
            using var projectedJson = JsonDocument.Parse(await projectedHaas.Content.ReadAsStringAsync());
            Assert.Equal(10607, projectedJson.RootElement.GetProperty("productionModeVariable").GetInt32());
            Assert.Equal(8083, projectedJson.RootElement.GetProperty("mtConnectPort").GetInt32());
            Assert.Equal("MDC", projectedJson.RootElement.GetProperty("telemetryProvider").GetString());
        });
    }

    [Fact]
    public async Task Legacy_Haas_save_preserves_explicitly_disabled_MDC_write_permission()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "haas-client");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");

            using var genericSaved = await client.PutAsJsonAsync(
                "/api/v1/machines/machine-haas/cnc-connection", new
                {
                    adapterType = "HAAS_NGC", enabled = false,
                    pollingIntervalMs = 2000, connectionTimeoutMs = 3000,
                    maximumReconnectBackoffMs = 30000, allowRead = true, allowWrite = false,
                    rawTelemetryRetentionDays = 14, version = 0,
                    configuration = new
                    {
                        host = "192.168.0.56", mdc = new { port = 5051, timeoutMs = 3000 },
                        mtConnect = new { port = 8082, timeoutMs = 3000 },
                        telemetryProvider = "MDC",
                        programAccess = new
                        {
                            provider = "NONE", enabled = false, sharePath = (string?)null,
                            headerLineLimit = 50, headerByteLimit = 32768,
                            headerPartPatterns = new[] { @"PART\s*[:=]\s*([^()]+)" }
                        },
                        production = new
                        {
                            variableNumber = 10605, legacyVariableAlias = 605,
                            partCounterSource = "Q500"
                        },
                        monitoring = new
                        {
                            pollingIntervalMs = 2000, stableProgramPolls = 2,
                            maximumReconnectBackoffMs = 30000, rawTelemetryRetentionDays = 14
                        }
                    }
                });
            genericSaved.EnsureSuccessStatusCode();

            using var legacySaved = await client.PutAsJsonAsync(
                "/api/v1/machines/machine-haas/haas/connection", new
                {
                    host = "192.168.0.56", mdcPort = 5051, mtConnectPort = 8082,
                    localNetShareEnabled = false, productionModeVariable = 10605,
                    legacyVariableAlias = 605, partCounterSource = "Q500",
                    pollingIntervalMs = 2000, connectionTimeoutMs = 3000,
                    stableProgramPolls = 2, headerLineLimit = 50, headerByteLimit = 32768,
                    headerPartPatterns = new[] { @"PART\s*[:=]\s*([^()]+)" },
                    enabled = false, version = 1
                });
            legacySaved.EnsureSuccessStatusCode();

            using var generic = await client.GetAsync(
                "/api/v1/machines/machine-haas/cnc-connection");
            generic.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await generic.Content.ReadAsStringAsync());
            Assert.False(json.RootElement.GetProperty("allowWrite").GetBoolean());
            Assert.Equal("MDC", json.RootElement.GetProperty("configuration")
                .GetProperty("telemetryProvider").GetString());
        });
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id)
            VALUES ('calendar-haas', 'Haas', 'UTC');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES ('machine-haas', 'M-H', 'HAAS VF-3', 'mill', 'calendar-haas', 'active', 1);
            UPDATE edit_tokens SET holder_client_id = 'haas-client', holder_user_id = 'planner',
                generation = 1, acquired_at = $at, version = version + 1 WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.Haas.Api", Guid.NewGuid().ToString("N"));
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
