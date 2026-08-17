using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Api.Kitaron;
using Meimad.Planner.Server.Application.Kitaron;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meimad.Planner.Server.Tests.Kitaron;

public sealed class KitaronConnectionApiTests
{
    [Fact]
    public async Task Local_server_page_saves_encrypted_secret_and_tests_read_only_view_metadata()
    {
        var tester = new CapturingTester();
        await RunAsync(tester, async (application, client) =>
        {
            using var page = await client.GetAsync("/kitaron-setup/");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains(
                "one-way read-only SQL Server connection",
                await page.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            using var initial = await client.GetAsync("/api/v1/kitaron/connection");
            Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
            using var initialJson = JsonDocument.Parse(await initial.Content.ReadAsStringAsync());
            Assert.Equal("192.168.0.240", initialJson.RootElement.GetProperty("serverHost").GetString());
            Assert.False(initialJson.RootElement.GetProperty("passwordConfigured").GetBoolean());

            const string secret = "server-test-secret";
            using var save = await client.PutAsJsonAsync(
                "/api/v1/kitaron/connection",
                new
                {
                    serverHost = "192.168.0.240",
                    serverPort = 1433,
                    databaseName = "KitaronData2550OLAP",
                    viewSchema = "dbo",
                    viewName = "VProductionPlanning",
                    username = "kit",
                    password = secret,
                    clearPassword = false,
                    enabled = false,
                    refreshIntervalSeconds = 300,
                    version = 1
                });
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
            var saveText = await save.Content.ReadAsStringAsync();
            Assert.DoesNotContain(secret, saveText, StringComparison.Ordinal);
            using var saveJson = JsonDocument.Parse(saveText);
            Assert.True(saveJson.RootElement.GetProperty("passwordConfigured").GetBoolean());

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT protected_password FROM kitaron_connection_settings WHERE id = 1;";
                var encrypted = Assert.IsType<string>(await command.ExecuteScalarAsync());
                Assert.DoesNotContain(secret, encrypted, StringComparison.Ordinal);
            }

            using var test = await client.PostAsync("/api/v1/kitaron/connection/test", null);
            Assert.Equal(HttpStatusCode.OK, test.StatusCode);
            using var testJson = JsonDocument.Parse(await test.Content.ReadAsStringAsync());
            Assert.True(testJson.RootElement.GetProperty("succeeded").GetBoolean());
            Assert.Equal(3, testJson.RootElement.GetProperty("columns").GetArrayLength());
            Assert.Equal(secret, tester.Password);
            Assert.Equal("VProductionPlanning", tester.Settings?.ViewName);

            using var after = await client.GetAsync("/api/v1/kitaron/connection");
            var afterText = await after.Content.ReadAsStringAsync();
            Assert.DoesNotContain(secret, afterText, StringComparison.Ordinal);
            using var afterJson = JsonDocument.Parse(afterText);
            Assert.Equal("succeeded", afterJson.RootElement.GetProperty("lastTestStatus").GetString());
            Assert.Equal(3, afterJson.RootElement.GetProperty("lastTestColumnCount").GetInt32());
        });
    }

    [Fact]
    public async Task Save_validates_identifiers_and_stale_versions_without_revealing_password()
    {
        await RunAsync(new CapturingTester(), async (_, client) =>
        {
            using var invalid = await client.PutAsJsonAsync(
                "/api/v1/kitaron/connection",
                new
                {
                    serverHost = "192.168.0.240",
                    serverPort = 1433,
                    databaseName = "KitaronData2550OLAP;DROP",
                    viewSchema = "dbo",
                    viewName = "VProductionPlanning",
                    username = "kit",
                    password = "not-returned",
                    clearPassword = false,
                    enabled = false,
                    refreshIntervalSeconds = 300,
                    version = 1
                });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
            Assert.DoesNotContain(
                "not-returned",
                await invalid.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            using var stale = await client.PutAsJsonAsync(
                "/api/v1/kitaron/connection",
                new
                {
                    serverHost = "192.168.0.240",
                    serverPort = 1433,
                    databaseName = "KitaronData2550OLAP",
                    viewSchema = "dbo",
                    viewName = "VProductionPlanning",
                    username = "kit",
                    password = "secret",
                    clearPassword = false,
                    enabled = false,
                    refreshIntervalSeconds = 300,
                    version = 99
                });
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        });
    }

    [Fact]
    public void Kitaron_setup_rejects_non_local_addresses()
    {
        var local = new DefaultHttpContext();
        local.Connection.RemoteIpAddress = IPAddress.Loopback;
        Assert.True(KitaronConnectionEndpoints.IsLocalRequest(local));

        var remote = new DefaultHttpContext();
        remote.Connection.RemoteIpAddress = IPAddress.Parse("192.168.0.50");
        Assert.False(KitaronConnectionEndpoints.IsLocalRequest(remote));
    }

    [Fact]
    public void Source_probe_is_schema_only_select_and_contains_no_mutation_statement()
    {
        var query = SqlServerKitaronConnectionTester.SchemaQuery(
            "dbo",
            "VProductionPlanning");

        Assert.Equal("SELECT TOP (0) * FROM [dbo].[VProductionPlanning];", query);
        Assert.DoesNotContain("INSERT", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", query, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task RunAsync(
        IKitaronConnectionTester tester,
        Func<WebApplication, HttpClient, Task> test)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "MeimadPlanner.Kitaron.Tests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "kitaron-test.db");
        var application = ServerApplication.Build(
            [
                "--Server:Host=127.0.0.1",
                "--Server:Port=5099",
                $"--Database:Path={databasePath}"
            ],
            webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.RemoveAll<IKitaronConnectionTester>();
                    services.AddSingleton(tester);
                    services.RemoveAll<IDataProtectionProvider>();
                    services.AddSingleton<IDataProtectionProvider>(
                        new EphemeralDataProtectionProvider());
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
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CapturingTester : IKitaronConnectionTester
    {
        internal StoredKitaronConnectionSettings? Settings { get; private set; }
        internal string? Password { get; private set; }

        public Task<IReadOnlyList<KitaronSourceColumn>> TestAsync(
            StoredKitaronConnectionSettings settings,
            string password,
            CancellationToken cancellationToken)
        {
            Settings = settings;
            Password = password;
            return Task.FromResult<IReadOnlyList<KitaronSourceColumn>>(
            [
                new("ITEM_NUMBER", "nvarchar"),
                new("WORKORDER_NUMBER", "nvarchar"),
                new("OPER_NUMBER", "int")
            ]);
        }
    }
}
