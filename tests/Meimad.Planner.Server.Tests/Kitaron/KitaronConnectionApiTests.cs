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
            var pageText = await page.Content.ReadAsStringAsync();
            Assert.Contains(
                "finish the source-to-Meimad mapping draft",
                pageText,
                StringComparison.Ordinal);
            Assert.Contains("id=\"mappingRows\"", pageText, StringComparison.Ordinal);
            Assert.Contains("Domain aligned — recommended", pageText, StringComparison.Ordinal);
            Assert.Contains("One-way Server synchronization", pageText, StringComparison.Ordinal);
            Assert.Contains("Synchronize now", pageText, StringComparison.Ordinal);

            using var script = await client.GetAsync("/kitaron-setup/app.js");
            var scriptText = await script.Content.ReadAsStringAsync();
            Assert.Contains("/api/v1/kitaron/mapping", scriptText, StringComparison.Ordinal);
            Assert.Contains("/api/v1/kitaron/sync", scriptText, StringComparison.Ordinal);

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

            using var mapping = await client.GetAsync("/api/v1/kitaron/mapping");
            Assert.Equal(HttpStatusCode.OK, mapping.StatusCode);
            using var mappingJson = JsonDocument.Parse(await mapping.Content.ReadAsStringAsync());
            Assert.Equal("domain_aligned", mappingJson.RootElement.GetProperty("modelMode").GetString());
            Assert.Equal("draft", mappingJson.RootElement.GetProperty("status").GetString());
            Assert.Equal(22, mappingJson.RootElement.GetProperty("fields").GetArrayLength());
            Assert.Equal(3, mappingJson.RootElement.GetProperty("detectedColumns").GetArrayLength());

            using var after = await client.GetAsync("/api/v1/kitaron/connection");
            var afterText = await after.Content.ReadAsStringAsync();
            Assert.DoesNotContain(secret, afterText, StringComparison.Ordinal);
            using var afterJson = JsonDocument.Parse(afterText);
            Assert.Equal("succeeded", afterJson.RootElement.GetProperty("lastTestStatus").GetString());
            Assert.Equal(3, afterJson.RootElement.GetProperty("lastTestColumnCount").GetInt32());
        });
    }

    [Fact]
    public async Task Mapping_UI_persists_complete_optimistic_draft_but_does_not_enable_import()
    {
        await RunAsync(new CapturingTester(), async (application, client) =>
        {
            using var initial = await client.GetAsync("/api/v1/kitaron/mapping");
            using var initialJson = JsonDocument.Parse(await initial.Content.ReadAsStringAsync());
            var root = initialJson.RootElement;
            var fields = root.GetProperty("fields").EnumerateArray()
                .Select(field => new Dictionary<string, object?>
                {
                    ["targetEntity"] = field.GetProperty("targetEntity").GetString(),
                    ["targetField"] = field.GetProperty("targetField").GetString(),
                    ["enabled"] = field.GetProperty("enabled").GetBoolean(),
                    ["sourceColumn"] = field.GetProperty("sourceColumn").ValueKind == JsonValueKind.Null
                        ? null
                        : field.GetProperty("sourceColumn").GetString(),
                    ["confidence"] = field.GetProperty("confidence").GetString(),
                    ["transform"] = field.GetProperty("transform").GetString(),
                    ["notes"] = "Planner review pending"
                })
                .ToArray();
            var version = root.GetProperty("version").GetInt32();

            using var save = await client.PutAsJsonAsync(
                "/api/v1/kitaron/mapping",
                new
                {
                    modelMode = "domain_aligned",
                    status = "draft",
                    fields,
                    notes = "Initial analyzed mapping; import stays disabled.",
                    version
                });
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
            using var savedJson = JsonDocument.Parse(await save.Content.ReadAsStringAsync());
            Assert.Equal("draft", savedJson.RootElement.GetProperty("status").GetString());
            Assert.Equal(version + 1, savedJson.RootElement.GetProperty("version").GetInt32());

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT mapping_status || '/' || json_array_length(mappings_json)
                    FROM kitaron_mapping_settings WHERE id = 1;
                    """;
                Assert.Equal("draft/22", await command.ExecuteScalarAsync());

                command.CommandText = """
                    SELECT COUNT(*) FROM sqlite_master
                    WHERE type = 'table' AND name LIKE 'kitaron%import%';
                    """;
                Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
            }

            using var blockedReady = await client.PutAsJsonAsync(
                "/api/v1/kitaron/mapping",
                new
                {
                    modelMode = "domain_aligned",
                    status = "ready_for_implementation",
                    fields,
                    notes = "Still contains blocked timing decisions.",
                    version = version + 1
                });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, blockedReady.StatusCode);

            using var stale = await client.PutAsJsonAsync(
                "/api/v1/kitaron/mapping",
                new
                {
                    modelMode = "domain_aligned",
                    status = "draft",
                    fields,
                    notes = "stale",
                    version = 99
                });
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
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

    [Fact]
    public void Source_sync_query_is_read_only_and_quotes_only_selected_columns()
    {
        var query = SqlServerKitaronSourceReader.BuildQuery(
            "dbo", "VQWorkPlanningForStationF4", ["DetailNumber", "OrderNumber"]);
        Assert.Equal("SELECT [DetailNumber], [OrderNumber] FROM [dbo].[VQWorkPlanningForStationF4];", query);
        Assert.DoesNotContain("INSERT", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Canonical_order_query_reads_cancelled_rows_without_mutating_kitaron()
    {
        var query = SqlServerKitaronSourceReader.BuildOrderQuery(
            "dbo", "VQWorkPlanningForStationF4");

        Assert.Contains("so.StopProduction", query, StringComparison.Ordinal);
        Assert.Contains("WHERE StopProduction = 1", query, StringComparison.Ordinal);
        Assert.Contains("so.RecordID", query, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ready_mapping_synchronizes_cases_orders_and_operations_idempotently_without_planning_data()
    {
        var reader = new CapturingSourceReader();
        await RunAsync(new CompleteTester(), async (application, client) =>
        {
            using var saveConnection = await client.PutAsJsonAsync("/api/v1/kitaron/connection", new
            {
                serverHost = "192.168.0.240", serverPort = 1433, databaseName = "KitaronData229",
                viewSchema = "dbo", viewName = "VQWorkPlanningForStationF4", username = "kit",
                password = "sync-secret", clearPassword = false, enabled = true,
                refreshIntervalSeconds = 3600, version = 1
            });
            Assert.Equal(HttpStatusCode.OK, saveConnection.StatusCode);
            using var testConnection = await client.PostAsync("/api/v1/kitaron/connection/test", null);
            Assert.Equal(HttpStatusCode.OK, testConnection.StatusCode);

            using var mappingResponse = await client.GetAsync("/api/v1/kitaron/mapping");
            using var mappingJson = JsonDocument.Parse(await mappingResponse.Content.ReadAsStringAsync());
            var mapping = mappingJson.RootElement;
            var fields = mapping.GetProperty("fields").EnumerateArray().Select(field => new
            {
                targetEntity = field.GetProperty("targetEntity").GetString(),
                targetField = field.GetProperty("targetField").GetString(),
                enabled = field.GetProperty("required").GetBoolean()
                    || field.GetProperty("confidence").GetString() is not ("blocked" or "low"),
                sourceColumn = field.GetProperty("targetField").GetString() == "route_position"
                    ? "ActionNumber"
                    : field.GetProperty("sourceColumn").ValueKind == JsonValueKind.Null
                        ? null : field.GetProperty("sourceColumn").GetString(),
                confidence = field.GetProperty("confidence").GetString() is "blocked" ? "low" : field.GetProperty("confidence").GetString(),
                transform = field.GetProperty("transform").GetString(),
                notes = (string?)null
            }).ToArray();
            using var ready = await client.PutAsJsonAsync("/api/v1/kitaron/mapping", new
            {
                modelMode = "domain_aligned", status = "ready_for_implementation", fields,
                notes = "Automated sync integration test.", version = mapping.GetProperty("version").GetInt32()
            });
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

            using var first = await client.PostAsync("/api/v1/kitaron/sync", null);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            Assert.Equal("succeeded", firstJson.RootElement.GetProperty("status").GetString());
            Assert.Equal(2, firstJson.RootElement.GetProperty("casesCreated").GetInt32());
            Assert.Equal(1, firstJson.RootElement.GetProperty("ordersCreated").GetInt32());
            Assert.Equal(0, firstJson.RootElement.GetProperty("operationsCreated").GetInt32());
            Assert.Equal(1, firstJson.RootElement.GetProperty("componentsCreated").GetInt32());

            using var second = await client.PostAsync("/api/v1/kitaron/sync", null);
            using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
            Assert.Equal(0, secondJson.RootElement.GetProperty("casesCreated").GetInt32());
            Assert.Equal(2, secondJson.RootElement.GetProperty("casesMatched").GetInt32());
            Assert.Equal(1, secondJson.RootElement.GetProperty("ordersMatched").GetInt32());
            Assert.Equal(0, secondJson.RootElement.GetProperty("operationsMatched").GetInt32());
            Assert.Equal(0, secondJson.RootElement.GetProperty("operationsUpdated").GetInt32());
            Assert.Equal(1, secondJson.RootElement.GetProperty("componentsMatched").GetInt32());

            reader.StopProduction = true;
            using var cancelled = await client.PostAsync("/api/v1/kitaron/sync", null);
            using var cancelledJson = JsonDocument.Parse(await cancelled.Content.ReadAsStringAsync());
            Assert.Equal("succeeded", cancelledJson.RootElement.GetProperty("status").GetString());
            Assert.Equal(1, cancelledJson.RootElement.GetProperty("ordersUpdated").GetInt32());

            reader.IncludeComponent = false;
            using var removedComponent = await client.PostAsync("/api/v1/kitaron/sync", null);
            using var removedComponentJson = JsonDocument.Parse(await removedComponent.Content.ReadAsStringAsync());
            Assert.Equal(1, removedComponentJson.RootElement.GetProperty("componentsUpdated").GetInt32());

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT (SELECT COUNT(*) FROM cases), (SELECT COUNT(*) FROM orders),
                    (SELECT COUNT(*) FROM case_operations), (SELECT COUNT(*) FROM production_batches),
                    (SELECT COUNT(*) FROM machine_assignments), (SELECT COUNT(*) FROM kitaron_sync_links),
                    (SELECT COUNT(*) FROM case_components WHERE is_active=1),
                    (SELECT status FROM orders LIMIT 1);
                """;
            await using var counts = await command.ExecuteReaderAsync();
            Assert.True(await counts.ReadAsync());
            Assert.Equal(2, counts.GetInt32(0)); Assert.Equal(1, counts.GetInt32(1));
            Assert.Equal(2, counts.GetInt32(2)); Assert.Equal(0, counts.GetInt32(3));
            Assert.Equal(0, counts.GetInt32(4)); Assert.Equal(6, counts.GetInt32(5));
            Assert.Equal(0, counts.GetInt32(6)); Assert.Equal("cancelled", counts.GetString(7));
        }, reader);
        Assert.Equal(4, reader.ReadCount);
    }

    private static async Task RunAsync(
        IKitaronConnectionTester tester,
        Func<WebApplication, HttpClient, Task> test,
        IKitaronSourceReader? sourceReader = null)
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
                    if (sourceReader is not null)
                    {
                        services.RemoveAll<IKitaronSourceReader>();
                        services.AddSingleton(sourceReader);
                    }
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

    private sealed class CompleteTester : IKitaronConnectionTester
    {
        public Task<IReadOnlyList<KitaronSourceColumn>> TestAsync(
            StoredKitaronConnectionSettings settings, string password, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KitaronSourceColumn>>([
                new("DetailNumber", "nvarchar"), new("DetailName", "nvarchar"), new("REV", "nvarchar"),
                new("CompanyName", "nvarchar"), new("OrderNumber", "nvarchar"), new("OrdAmount", "int"),
                new("SupplyDate", "date"), new("ActionNumber", "int"), new("ActionDescription", "nvarchar"),
                new("Station", "nvarchar"), new("DirectionTimeP", "decimal"), new("TimeProductionP", "decimal"),
                new("RootID", "nvarchar"), new("ProductionAmount", "int"), new("RecordID", "int")]);
    }

    private sealed class CapturingSourceReader : IKitaronSourceReader
    {
        internal int ReadCount { get; private set; }
        internal bool StopProduction { get; set; }
        internal bool IncludeComponent { get; set; } = true;
        public Task<KitaronSourceSnapshot> ReadAsync(
            StoredKitaronConnectionSettings settings, string password, IReadOnlyList<string> columns,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            Assert.Equal("sync-secret", password);
            Assert.Contains("DetailNumber", columns);
            KitaronSourceRow[] rows = [
                Row(10, "Cut", 1), Row(10, "Cut", 1),
                Row(10, "Alternate description", 1), Row(20, "Finish", 2)
            ];
            if (ReadCount % 2 == 0) Array.Reverse(rows);
            IReadOnlyList<KitaronSourceComponent> components = IncludeComponent
                ? [new KitaronSourceComponent(
                    "100:200", "PART-100", "Test Part", "A",
                    "SUB-200", "Sub Case", "B", 2.5, 0)]
                : [];
            return Task.FromResult(new KitaronSourceSnapshot(
                rows,
                [new KitaronSourceOrder(
                    "9001", "PART-100", "Test Part", "A", "SO-100", 12,
                    new DateTime(2026, 9, 1), StopProduction)],
                components));
        }

        private static KitaronSourceRow Row(int operation, string name, int position) => new(
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["DetailNumber"] = "PART-100", ["DetailName"] = "Test Part", ["REV"] = "A",
                ["CompanyName"] = "Customer", ["OrderNumber"] = "SO-100", ["OrdAmount"] = 12,
                ["SupplyDate"] = new DateTime(2026, 9, 1), ["ActionNumber"] = operation,
                ["ActionDescription"] = name, ["Station"] = "MILL", ["RootID"] = "WO-100",
                ["ProductionAmount"] = 12
            });
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
