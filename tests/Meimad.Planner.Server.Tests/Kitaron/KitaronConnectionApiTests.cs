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
            Assert.Contains("CONNECTOR MANAGED", pageText, StringComparison.Ordinal);
            Assert.Contains("PriceInCurr", pageText, StringComparison.Ordinal);

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
            Assert.Equal(39, mappingJson.RootElement.GetProperty("fields").GetArrayLength());
            var orderFields = mappingJson.RootElement.GetProperty("fields").EnumerateArray()
                .Where(field => field.GetProperty("targetEntity").GetString() == "orders")
                .ToArray();
            var statusField = orderFields.Single(field => field.GetProperty("targetField").GetString() == "status");
            var priceField = orderFields.Single(field => field.GetProperty("targetField").GetString() == "price");
            Assert.True(statusField.GetProperty("connectorManaged").GetBoolean());
            Assert.Equal("canonical_order_status", statusField.GetProperty("transform").GetString());
            Assert.True(priceField.GetProperty("connectorManaged").GetBoolean());
            Assert.Equal("PriceInCurr", priceField.GetProperty("sourceColumn").GetString());
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
                Assert.Equal("draft/39", await command.ExecuteScalarAsync());

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

            var price = fields.Single(field =>
                Equals(field["targetEntity"], "orders") && Equals(field["targetField"], "price"));
            price["enabled"] = false;
            using var remapManaged = await client.PutAsJsonAsync(
                "/api/v1/kitaron/mapping",
                new
                {
                    modelMode = "domain_aligned",
                    status = "draft",
                    fields,
                    notes = "Attempt to disable canonical price.",
                    version = version + 1
                });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, remapManaged.StatusCode);
            Assert.Contains("managed by the canonical Kitaron connector",
                await remapManaged.Content.ReadAsStringAsync(), StringComparison.Ordinal);
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
    public void Canonical_order_query_reads_all_delivery_rows_without_mutating_kitaron()
    {
        var query = SqlServerKitaronSourceReader.BuildOrderQuery(
            "dbo", "VQWorkPlanningForStationF4", "PriceInCurr");

        Assert.Contains("so.StopProduction", query, StringComparison.Ordinal);
        Assert.Contains("so.RecordID", query, StringComparison.Ordinal);
        Assert.Contains("source_details", query, StringComparison.Ordinal);
        Assert.Contains("work.[DetailNumber]", query, StringComparison.Ordinal);
        Assert.DoesNotContain("work.[OrderNumber]", query, StringComparison.Ordinal);
        Assert.Contains("SELECT DISTINCT node.TreeHead", query, StringComparison.Ordinal);
        Assert.Contains("SELECT DISTINCT node.IDNodeContens", query, StringComparison.Ordinal);
        Assert.Contains("so.DetailID = source.DetailID", query, StringComparison.Ordinal);
        Assert.Contains("so.[PriceInCurr] AS Price", query, StringComparison.Ordinal);
        Assert.Contains("o.OrderNumber)) <> N'הזמנה לדוגמא 1'", query, StringComparison.Ordinal);
        Assert.Contains("WHERE StopProduction = 1", query, StringComparison.Ordinal);
        Assert.DoesNotContain("TRY_CONVERT", query, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Canonical_order_price_prefers_Kitaron_unit_price_in_order_currency()
    {
        var selected = SqlServerKitaronSourceReader.SelectOrderPriceColumn(
            ["FullCost", "PriceRow", "PriceInCurr", "COOrderUnitPriceBOM"]);

        Assert.Equal("PriceInCurr", selected);
    }

    [Fact]
    public void Canonical_order_query_combines_row_and_header_closure_facts()
    {
        var query = SqlServerKitaronSourceReader.BuildOrderQuery(
            "dbo",
            "VQWorkPlanningForStationF4",
            "PriceInCurr",
            ["OrderClosed", "RecordClosed", "RowClosed"],
            ["OrderClosed", "Closed"]);

        Assert.Contains("COALESCE(TRY_CONVERT(int, so.[OrderClosed]), 0) = 2", query, StringComparison.Ordinal);
        Assert.Contains("COALESCE(TRY_CONVERT(int, so.[RecordClosed]), 0) <> 0", query, StringComparison.Ordinal);
        Assert.Contains("COALESCE(TRY_CONVERT(int, so.[RowClosed]), 0) <> 0", query, StringComparison.Ordinal);
        Assert.Contains("COALESCE(TRY_CONVERT(int, o.[OrderClosed]), 0) = 2", query, StringComparison.Ordinal);
        Assert.Contains("COALESCE(TRY_CONVERT(int, o.[Closed]), 0) <> 0", query, StringComparison.Ordinal);
        Assert.Contains("AS IsClosed", query, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_order_row_identity_suppresses_stale_work_view_status_for_same_record()
    {
        Assert.Equal(
            KitaronSyncService.OrderRowIdentity("30P410178702-501", "33269"),
            KitaronSyncService.OrderRowIdentity(" 30P410178702-501 ", " 33269 "));
    }

    [Theory]
    [InlineData("הזמנה לדוגמא 1")]
    [InlineData("  הזמנה לדוגמא 1  ")]
    public void Known_Kitaron_test_order_is_excluded(string orderNumber)
    {
        Assert.True(KitaronSyncService.IsIgnoredOrderNumber(orderNumber));
        var orders = KitaronSyncService.BuildOrders(
            [new KitaronSourceOrder("1", "TEST", "Test", null, orderNumber, 1,
                new DateTime(2027, 1, 1), false)],
            []);
        Assert.Empty(orders);
        Assert.False(KitaronSyncService.IsIgnoredOrderNumber("3000030623"));
    }

    [Fact]
    public void Canonical_order_rows_remain_separate_with_order_row_reference_quantity_and_date()
    {
        KitaronSourceOrder[] source =
        [
            new("501", "30P782531500-001", "ATS DUCT SUPPORT", "NEW", "3000030679", 24, new DateTime(2027,3,2), false),
            new("502", "30P782531500-001", "ATS DUCT SUPPORT", "NEW", "3000030679", 12, new DateTime(2027,3,9), false),
            new("503", "30P782531500-001", "ATS DUCT SUPPORT", "NEW", "3000030679", 12, new DateTime(2027,3,16), false),
            new("504", "30P782531500-001", "ATS DUCT SUPPORT", "NEW", "3000030679", 12, new DateTime(2027,3,23), false),
            new("505", "30P782531500-001", "ATS DUCT SUPPORT", "NEW", "3000030679", 12, new DateTime(2027,3,30), false)
        ];
        var warnings = new List<string>();

        var orders = KitaronSyncService.BuildOrders(source, warnings);

        Assert.Equal(5, orders.Count);
        Assert.Equal(72, orders.Sum(order => order.Quantity));
        Assert.Equal("3000030679/501", orders[0].OrderNumber);
        Assert.Equal("3000030679", orders[0].CanonicalOrderNumber);
        Assert.Equal(new DateOnly(2027,3,30), orders[4].WorkFinishDate);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Canonical_order_status_and_price_follow_Kitaron_facts()
    {
        KitaronSourceOrder[] source =
        [
            new("open", "PART", "Part", null, "100", 2, new DateTime(2027,1,1), false, false, 12.50m),
            new("closed", "PART", "Part", null, "101", 3, new DateTime(2027,1,2), false, true, 20m),
            new("cancelled", "PART", "Part", null, "102", 4, new DateTime(2027,1,3), true, true, 30m)
        ];

        var result = KitaronSyncService.BuildOrders(source, []);

        Assert.Equal("active", result.Single(item => item.SourceKey == "open").Status);
        Assert.Equal(12.50m, result.Single(item => item.SourceKey == "open").Price);
        Assert.Equal("complete", result.Single(item => item.SourceKey == "closed").Status);
        Assert.Equal("cancelled", result.Single(item => item.SourceKey == "cancelled").Status);
    }

    [Fact]
    public async Task Kitaron_managed_case_and_orders_reject_planner_mutations()
    {
        await RunAsync(new CapturingTester(), async (application, client) =>
        {
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO cases (id, part_number, name, working_folder_path)
                    VALUES ('kit-case', 'KIT-001', 'Kitaron Case', 'C:\Cases\KIT-001');
                    INSERT INTO orders (id, case_id, order_reference, quantity, work_finish_date, status,
                        kitaron_status, price, kitaron_history_only)
                    VALUES
                        ('kit-order', 'kit-case', '100/1', 4, '2027-01-01', 'complete', 'inactive', 12.5, 0),
                        ('kit-history', 'kit-case', '100', 4, '2026-01-01', 'active', 'inactive', 12.5, 1),
                        ('kit-unlinked', 'kit-case', '100-OLD', 4, '2026-02-01', 'active', 'active', 12.5, 0);
                    INSERT INTO kitaron_sync_links
                        (source_entity, source_key, target_id, owns_target, source_hash, first_seen_at, last_seen_at)
                    VALUES
                        ('case', 'KIT-001', 'kit-case', 1, 'case-hash', '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z'),
                        ('order', '1', 'kit-order', 1, 'order-hash', '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z');
                    UPDATE edit_tokens
                    SET holder_client_id='kitaron-guard-test', holder_user_id='tester', generation=1,
                        acquired_at='2026-09-01T00:00:00Z', version=version+1,
                        updated_at='2026-09-01T00:00:00Z'
                    WHERE id=1;
                    """;
                await seed.ExecuteNonQueryAsync();
            }

            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "kitaron-guard-test");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");

            using var casePatch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/cases/kit-case")
            {
                Content = JsonContent.Create(new { name = "Changed" })
            };
            casePatch.Headers.TryAddWithoutValidation("If-Match", "\"case:kit-case:v1\"");
            using var casePatchResponse = await client.SendAsync(casePatch);
            Assert.Equal(HttpStatusCode.Conflict, casePatchResponse.StatusCode);
            Assert.Contains("kitaron_managed_read_only", await casePatchResponse.Content.ReadAsStringAsync());

            using var createOrder = await client.PostAsJsonAsync("/api/v1/orders", new
            {
                caseId = "kit-case", orderNumber = "manual", quantity = 1,
                workFinishDate = "2027-02-01", status = "active"
            });
            Assert.Equal(HttpStatusCode.Conflict, createOrder.StatusCode);

            using var orderDelete = await client.DeleteAsync("/api/v1/orders/kit-order");
            Assert.Equal(HttpStatusCode.Conflict, orderDelete.StatusCode);

            using var historyPatch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/orders/kit-history")
            {
                Content = JsonContent.Create(new { notes = "Changed" })
            };
            historyPatch.Headers.TryAddWithoutValidation("If-Match", "\"order:kit-history:v1\"");
            using var historyPatchResponse = await client.SendAsync(historyPatch);
            Assert.Equal(HttpStatusCode.Conflict, historyPatchResponse.StatusCode);
            Assert.Contains(
                "kitaron_managed_read_only",
                await historyPatchResponse.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            using var historyDelete = await client.DeleteAsync("/api/v1/orders/kit-history");
            Assert.Equal(HttpStatusCode.Conflict, historyDelete.StatusCode);

            using var unlinkedPatch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/orders/kit-unlinked")
            {
                Content = JsonContent.Create(new { notes = "Changed" })
            };
            unlinkedPatch.Headers.TryAddWithoutValidation("If-Match", "\"order:kit-unlinked:v1\"");
            using var unlinkedPatchResponse = await client.SendAsync(unlinkedPatch);
            Assert.Equal(HttpStatusCode.Conflict, unlinkedPatchResponse.StatusCode);

            using var unlinkedDelete = await client.DeleteAsync("/api/v1/orders/kit-unlinked");
            Assert.Equal(HttpStatusCode.Conflict, unlinkedDelete.StatusCode);

            using var caseDelete = await client.DeleteAsync("/api/v1/cases/kit-case");
            Assert.Equal(HttpStatusCode.Conflict, caseDelete.StatusCode);

            using var read = await client.GetAsync("/api/v1/orders?caseId=kit-case");
            using var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
            var item = Assert.Single(items);
            Assert.Equal("kit-order", item.GetProperty("orderId").GetString());
            Assert.True(item.GetProperty("isKitaronManaged").GetBoolean());
            Assert.Equal("inactive", item.GetProperty("status").GetString());
            Assert.Equal(12.5m, item.GetProperty("price").GetDecimal());

            using var historyRead = await client.GetAsync("/api/v1/orders/kit-history");
            historyRead.EnsureSuccessStatusCode();
            using var historyJson = JsonDocument.Parse(await historyRead.Content.ReadAsStringAsync());
            Assert.True(historyJson.RootElement.GetProperty("isHistorical").GetBoolean());
            Assert.True(historyJson.RootElement.GetProperty("isKitaronManaged").GetBoolean());

            using var unlinkedRead = await client.GetAsync("/api/v1/orders/kit-unlinked");
            unlinkedRead.EnsureSuccessStatusCode();
            using var unlinkedJson = JsonDocument.Parse(await unlinkedRead.Content.ReadAsStringAsync());
            Assert.True(unlinkedJson.RootElement.GetProperty("isKitaronManaged").GetBoolean());
            Assert.False(unlinkedJson.RootElement.GetProperty("isHistorical").GetBoolean());

            var activeCases = await client.GetFromJsonAsync<JsonElement>(
                "/api/v1/cases?search=KIT-001&isActive=true");
            Assert.Empty(activeCases.GetProperty("items").EnumerateArray().ToArray());
        });
    }

    [Fact]
    public async Task Exact_duplicate_source_order_rows_do_not_break_snapshot_reconciliation()
    {
        await RunAsync(new CapturingTester(), async (application, _) =>
        {
            var repository = application.Services.GetRequiredService<IKitaronSyncRepository>();
            var item = new KitaronSyncCase(
                "PART-DUP", "PART-DUP", "Duplicate source row", null, null, "dup", "case-hash");
            var order = new KitaronSyncOrder(
                "same-source", "PART-DUP", "100/same-source", 5,
                new DateOnly(2027, 1, 1), "active", "order-hash");
            var plan = new KitaronSyncPlan(
                2, [item], [order, order], [], [], new HashSet<string>(), [], 1);

            var result = await repository.ApplyAsync(
                plan, new DateTimeOffset(2026, 9, 1, 18, 0, 0, TimeSpan.Zero), CancellationToken.None);

            Assert.Equal("succeeded", result.Status);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM orders WHERE order_reference='100/same-source';";
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
        });
    }

    [Fact]
    public async Task Stale_order_source_link_to_another_case_is_repaired_without_losing_authoritative_row()
    {
        await RunAsync(new CapturingTester(), async (application, _) =>
        {
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO cases (id, part_number, name, working_folder_path)
                    VALUES ('old-case', 'OLD-PART', 'Old', 'old'),
                           ('new-case', 'NEW-PART', 'New', 'new');
                    INSERT INTO orders (id, case_id, order_reference, quantity, work_finish_date, status)
                    VALUES ('wrong-target', 'old-case', 'OLD/41093', 1, '2026-01-01', 'active');
                    INSERT INTO kitaron_sync_links
                        (source_entity, source_key, target_id, owns_target, source_hash, first_seen_at, last_seen_at)
                    VALUES ('order', '41093', 'wrong-target', 1, 'old-hash',
                            '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                    """;
                await seed.ExecuteNonQueryAsync();
            }

            var cases = new[]
            {
                new KitaronSyncCase("OLD-PART", "OLD-PART", "Old", null, null, "old", "old-case-hash"),
                new KitaronSyncCase("NEW-PART", "NEW-PART", "New", null, null, "new", "new-case-hash")
            };
            var order = new KitaronSyncOrder(
                "41093", "NEW-PART", "3000030627/41093", 8,
                new DateOnly(2027, 2, 1), "active", "new-order-hash");
            var repository = application.Services.GetRequiredService<IKitaronSyncRepository>();

            var result = await repository.ApplyAsync(
                new KitaronSyncPlan(1, cases, [order], [], [], new HashSet<string>(), [], 1),
                new DateTimeOffset(2026, 9, 2, 6, 0, 0, TimeSpan.Zero), CancellationToken.None);

            Assert.Equal("succeeded", result.Status);
            await using var verifyConnection = await database.OpenConnectionAsync();
            await using var verify = verifyConnection.CreateCommand();
            verify.CommandText = """
                SELECT c.part_number, o.order_reference, o.quantity
                FROM orders o JOIN cases c ON c.id=o.case_id
                WHERE o.order_reference='3000030627/41093';
                """;
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("NEW-PART", reader.GetString(0));
            Assert.Equal("3000030627/41093", reader.GetString(1));
            Assert.Equal(8, reader.GetInt32(2));
        });
    }

    [Fact]
    public async Task Canonical_rows_normalize_one_exact_linked_legacy_plain_order_without_double_counting()
    {
        await RunAsync(new CapturingTester(), async (application, client) =>
        {
            const string part = "30P782531500-001";
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO cases (
                        id, part_number, name, working_folder_path,
                        version, created_at, updated_at)
                    VALUES (
                        'legacy-case', '30P782531500-001', 'ATS DUCT SUPPORT', 'legacy',
                        1, '2026-08-17T00:00:00Z', '2026-08-17T00:00:00Z');
                    INSERT INTO orders (
                        id, case_id, order_reference, quantity, work_finish_date, status,
                        version, created_at, updated_at)
                    VALUES (
                        'legacy-order', 'legacy-case', '3000030679 ', 24, '2027-03-02', 'active',
                        1, '2026-08-17T00:00:00Z', '2026-08-17T00:00:00Z');
                    INSERT INTO orders (
                        id, case_id, order_reference, quantity, work_finish_date, status,
                        version, created_at, updated_at)
                    VALUES (
                        'stale-order', 'legacy-case', '7000140610/42015', 24, '2028-03-09', 'active',
                        1, '2026-09-01T15:02:17Z', '2026-09-01T15:02:17Z');
                    INSERT INTO kitaron_sync_links (
                        source_entity, source_key, target_id, owns_target, source_hash,
                        first_seen_at, last_seen_at)
                    VALUES (
                        'order', '501', 'legacy-order', 0, 'legacy-hash',
                        '2026-08-17T00:00:00Z', '2026-08-17T00:00:00Z');
                    INSERT INTO kitaron_sync_links (
                        source_entity, source_key, target_id, owns_target, source_hash,
                        first_seen_at, last_seen_at)
                    VALUES (
                        'order', '42015', 'stale-order', 1, 'stale-hash',
                        '2026-09-01T15:02:17Z', '2026-09-01T15:00:00.0000000+00:00');
                    """;
                await seed.ExecuteNonQueryAsync();
            }

            KitaronSourceOrder[] source =
            [
                new("501", part, "ATS DUCT SUPPORT", "NEW", "3000030679", 24, new DateTime(2027,3,2), false),
                new("502", part, "ATS DUCT SUPPORT", "NEW", "3000030679", 12, new DateTime(2027,3,9), false),
                new("503", part, "ATS DUCT SUPPORT", "NEW", "3000030679", 12, new DateTime(2027,3,16), false),
                new("504", part, "ATS DUCT SUPPORT", "NEW", "3000030679", 12, new DateTime(2027,3,23), false),
                new("505", part, "ATS DUCT SUPPORT", "NEW", "3000030679", 12, new DateTime(2027,3,30), false)
            ];
            var warnings = new List<string>();
            var orders = KitaronSyncService.BuildOrders(source, warnings);
            var item = new KitaronSyncCase(part, part, "ATS DUCT SUPPORT", "NEW", null, "legacy", "case-hash");
            var plan = new KitaronSyncPlan(5, [item], orders, [], [], new HashSet<string>(), warnings, 1);

            // Reproduce an older owned link whose saved hash is already current while its
            // Planner target still has the plain header OrderNumber. Sync must repair the
            // displayed row reference instead of treating the target as an unchanged match.
            await using (var connection = await database.OpenConnectionAsync())
            await using (var seedCurrentHash = connection.CreateCommand())
            {
                seedCurrentHash.CommandText = """
                    UPDATE kitaron_sync_links
                    SET owns_target=1, source_hash=$hash
                    WHERE source_entity='order' AND source_key='501';
                    """;
                seedCurrentHash.Parameters.AddWithValue("$hash", orders[0].SourceHash);
                await seedCurrentHash.ExecuteNonQueryAsync();
            }

            var repository = application.Services.GetRequiredService<IKitaronSyncRepository>();
            var result = await repository.ApplyAsync(
                plan, new DateTimeOffset(2026, 9, 1, 15, 0, 0, TimeSpan.Zero), CancellationToken.None);

            Assert.Equal(4, result.OrdersCreated);
            Assert.Equal(1, result.OrdersUpdated);
            Assert.Contains("1 non-Kitaron Order(s) removed", result.Message, StringComparison.Ordinal);
            using var response = await client.GetAsync("/api/v1/orders?caseId=legacy-case");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var imported = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(5, imported.Length);
            Assert.Equal(72, imported.Sum(order => order.GetProperty("quantity").GetInt32()));
            Assert.All(imported, order => Assert.StartsWith(
                "3000030679/", order.GetProperty("orderNumber").GetString(), StringComparison.Ordinal));
            Assert.Contains(imported, order =>
                order.GetProperty("orderId").GetString() == "legacy-order"
                && order.GetProperty("orderNumber").GetString() == "3000030679/501");

            await using (var connection = await database.OpenConnectionAsync())
            await using (var seedBlocked = connection.CreateCommand())
            {
                seedBlocked.CommandText = """
                    INSERT INTO orders (
                        id, case_id, order_reference, quantity, work_finish_date, status)
                    VALUES ('allocated-stale', 'legacy-case', 'NON-KITARON', 1, '2028-01-01', 'active');
                    INSERT INTO production_batches (
                        id, case_id, batch_number, status, planned_quantity)
                    VALUES ('allocated-batch', 'legacy-case', 'B-1', 'waiting', 1);
                    INSERT INTO batch_allocations (
                        id, production_batch_id, allocation_type, order_id, quantity)
                    VALUES ('allocated-row', 'allocated-batch', 'order', 'allocated-stale', 1);
                    """;
                await seedBlocked.ExecuteNonQueryAsync();
            }
            var cleaned = await repository.ApplyAsync(
                plan, new DateTimeOffset(2026, 9, 1, 15, 1, 0, TimeSpan.Zero), CancellationToken.None);
            Assert.Contains("1 dependent Production Batch(es)", cleaned.Message, StringComparison.Ordinal);
            Assert.Contains("1 non-Kitaron Order(s) removed", cleaned.Message, StringComparison.Ordinal);
            await using (var verifyConnection = await database.OpenConnectionAsync())
            await using (var verify = verifyConnection.CreateCommand())
            {
                verify.CommandText = """
                    SELECT
                        (SELECT COUNT(*) FROM production_batches WHERE id='allocated-batch'),
                        (SELECT COUNT(*) FROM orders WHERE id='allocated-stale'),
                        (SELECT COUNT(*) FROM batch_allocations WHERE id='allocated-row');
                    """;
                await using var remaining = await verify.ExecuteReaderAsync();
                Assert.True(await remaining.ReadAsync());
                Assert.Equal(0, remaining.GetInt32(0));
                Assert.Equal(0, remaining.GetInt32(1));
                Assert.Equal(0, remaining.GetInt32(2));
            }
        });
    }

    [Fact]
    public async Task Planning_view_order_row_completes_canonical_delivery_rows_for_exact_kitaron_total()
    {
        await RunAsync(new CompleteTester(), async (_, client) =>
        {
            await ConfigureReadySyncAsync(client);
            using var sync = await client.PostAsync("/api/v1/kitaron/sync", null);
            Assert.Equal(HttpStatusCode.OK, sync.StatusCode);

            var cases = await client.GetFromJsonAsync<JsonElement>(
                "/api/v1/cases?search=30P782531500-001");
            var caseId = cases.GetProperty("items")[0].GetProperty("caseId").GetString();
            var response = await client.GetFromJsonAsync<JsonElement>(
                $"/api/v1/orders?caseId={caseId}");
            var orders = response.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(5, orders.Length);
            Assert.Equal(72, orders.Sum(order => order.GetProperty("quantity").GetInt32()));
            Assert.All(orders, order => Assert.StartsWith(
                "3000030679/", order.GetProperty("orderNumber").GetString(), StringComparison.Ordinal));
        }, new WorkFallbackSourceReader());
    }

    [Fact]
    public async Task Superseded_order_with_locked_run_is_retained_as_history_without_blocking_sync()
    {
        await RunAsync(new CapturingTester(), async (application, client) =>
        {
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO cases (id,part_number,name,working_folder_path)
                    VALUES ('history-case','30P647004101-001','Historical part','history');
                    INSERT INTO orders (
                        id,case_id,order_reference,quantity,work_finish_date,status,kitaron_status)
                    VALUES (
                        'history-order','history-case','3000030662',80,'2027-02-02',
                        'complete','inactive');
                    INSERT INTO case_operations
                        (id,case_id,operation_number,route_position,name)
                    VALUES ('history-case-operation','history-case',30,0,'Mill');
                    INSERT INTO production_batches
                        (id,case_id,batch_number,status,planned_quantity)
                    VALUES ('history-batch','history-case','1','complete',80);
                    INSERT INTO batch_allocations
                        (id,production_batch_id,allocation_type,order_id,quantity)
                    VALUES ('history-allocation','history-batch','order','history-order',80);
                    INSERT INTO batch_operations
                        (id,production_batch_id,source_case_operation_id,operation_number,route_position,name,status)
                    VALUES ('history-operation','history-batch','history-case-operation',30,0,'Mill','complete');
                    INSERT INTO production_runs
                        (id,status,shared_setup_seconds,setup_snapshot_json,structure_locked_at,
                         legacy_batch_operation_id,version,created_at,updated_at)
                    VALUES ('history-run','COMPLETED',0,'{}','2026-08-27T13:00:00Z',
                            'history-operation',1,'2026-08-27T12:00:00Z','2026-08-27T13:00:00Z');
                    INSERT INTO kitaron_sync_links (
                        source_entity,source_key,target_id,owns_target,source_hash,
                        first_seen_at,last_seen_at)
                    VALUES (
                        'order','obsolete-history-source','history-order',1,'old-hash',
                        '2026-08-27T12:00:00Z','2026-08-27T13:00:00Z');
                    """;
                await seed.ExecuteNonQueryAsync();
            }

            var item = new KitaronSyncCase(
                "30P647004101-001", "30P647004101-001", "Historical part", null, null,
                "history", "case-hash");
            var canonical = new KitaronSyncOrder(
                "40261", item.SourceKey, "3000030662/40261", 16,
                new DateOnly(2026, 12, 21), "active", "order-hash")
            { CanonicalOrderNumber = "3000030662" };
            var repository = application.Services.GetRequiredService<IKitaronSyncRepository>();

            var result = await repository.ApplyAsync(
                new KitaronSyncPlan(1, [item], [canonical], [], [], new HashSet<string>(), [], 1),
                new DateTimeOffset(2026, 9, 2, 9, 0, 0, TimeSpan.Zero), CancellationToken.None);

            Assert.Equal("succeeded", result.Status);
            Assert.Contains("1 superseded Order(s) retained", result.Message, StringComparison.Ordinal);

            await using (var duplicateConnection = await database.OpenConnectionAsync())
            await using (var duplicate = duplicateConnection.CreateCommand())
            {
                duplicate.CommandText = """
                    INSERT INTO orders (
                        id,case_id,order_reference,quantity,work_finish_date,status,kitaron_status)
                    VALUES (
                        'duplicate-current-reference','history-case','3000030662/40261',16,
                        '2026-12-21','active','active');
                    """;
                await duplicate.ExecuteNonQueryAsync();
            }

            var reconciled = await repository.ApplyAsync(
                new KitaronSyncPlan(1, [item], [canonical], [], [], new HashSet<string>(), [], 1),
                new DateTimeOffset(2026, 9, 2, 9, 1, 0, TimeSpan.Zero), CancellationToken.None);
            Assert.Equal("succeeded", reconciled.Status);
            Assert.Contains("1 non-Kitaron Order(s) removed", reconciled.Message, StringComparison.Ordinal);

            using var currentResponse = await client.GetAsync("/api/v1/orders?caseId=history-case");
            Assert.Equal(HttpStatusCode.OK, currentResponse.StatusCode);
            using var currentJson = JsonDocument.Parse(await currentResponse.Content.ReadAsStringAsync());
            var currentOrders = currentJson.RootElement.GetProperty("items").EnumerateArray().ToArray();
            var currentOrder = Assert.Single(currentOrders);
            Assert.Equal("3000030662/40261", currentOrder.GetProperty("orderNumber").GetString());
            Assert.False(currentOrder.GetProperty("isHistorical").GetBoolean());

            using var historicalResponse = await client.GetAsync("/api/v1/orders/history-order");
            Assert.Equal(HttpStatusCode.OK, historicalResponse.StatusCode);
            using var historicalJson = JsonDocument.Parse(await historicalResponse.Content.ReadAsStringAsync());
            Assert.True(historicalJson.RootElement.GetProperty("isHistorical").GetBoolean());
            Assert.True(historicalJson.RootElement.GetProperty("isKitaronManaged").GetBoolean());
            Assert.Equal("inactive", historicalJson.RootElement.GetProperty("status").GetString());

            await using var verifyConnection = await database.OpenConnectionAsync();
            await using var verify = verifyConnection.CreateCommand();
            verify.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM orders WHERE id='history-order'),
                    (SELECT COUNT(*) FROM production_batches WHERE id='history-batch'),
                    (SELECT COUNT(*) FROM orders WHERE order_reference='3000030662/40261'),
                    (SELECT kitaron_history_only FROM orders WHERE id='history-order'),
                    (SELECT COUNT(*) FROM orders WHERE id='duplicate-current-reference');
                """;
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(1, reader.GetInt32(1));
            Assert.Equal(1, reader.GetInt32(2));
            Assert.Equal(1, reader.GetInt32(3));
            Assert.Equal(0, reader.GetInt32(4));
        });
    }

    [Fact]
    public async Task Superseded_derived_order_with_locked_multi_output_run_is_retained_as_history()
    {
        await RunAsync(new CapturingTester(), async (application, client) =>
        {
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO cases (id,part_number,name,working_folder_path)
                    VALUES ('output-history-case','30P647004102-001','Output history part','history');
                    INSERT INTO orders (id,case_id,order_reference,quantity,work_finish_date,status)
                    VALUES ('output-history-order','output-history-case','3000030777',8,'2027-02-02','complete');
                    INSERT INTO case_operations
                        (id,case_id,operation_number,route_position,name)
                    VALUES ('output-history-case-operation','output-history-case',30,0,'Mill');
                    INSERT INTO production_batches
                        (id,case_id,batch_number,status,planned_quantity)
                    VALUES ('output-history-batch','output-history-case','1','in_production',8);
                    INSERT INTO batch_allocations
                        (id,production_batch_id,allocation_type,derived_order_key,quantity)
                    VALUES (
                        'output-history-allocation','output-history-batch','derived_order',
                        'derived:output-history-order:path',8);
                    INSERT INTO batch_operations
                        (id,production_batch_id,source_case_operation_id,operation_number,route_position,name,status)
                    VALUES (
                        'output-history-operation','output-history-batch',
                        'output-history-case-operation',30,0,'Mill','started');
                    INSERT INTO production_runs
                        (id,status,shared_setup_seconds,setup_snapshot_json,structure_locked_at,
                         version,created_at,updated_at)
                    VALUES (
                        'output-history-run','PLANNED',0,'{}',NULL,1,
                        '2026-08-27T12:00:00Z','2026-08-27T12:00:00Z');
                    INSERT INTO production_run_programs (
                        id,production_run_id,manufacturing_program_id,sequence_position,
                        target_cycle_count,completed_cycle_count,status,legacy_unmanaged,
                        version,created_at,updated_at)
                    VALUES (
                        'output-history-program','output-history-run',
                        'case-operation:output-history-case-operation',0,8,0,'ACTIVE',1,1,
                        '2026-08-27T12:00:00Z','2026-08-27T12:00:00Z');
                    INSERT INTO production_run_outputs (
                        id,production_run_program_id,batch_operation_id,quantity_per_cycle,
                        target_quantity,produced_quantity,status,version,created_at,updated_at)
                    VALUES (
                        'output-history-output','output-history-program','output-history-operation',
                        1,8,0,'ALLOCATED',1,'2026-08-27T12:00:00Z','2026-08-27T12:00:00Z');
                    UPDATE production_runs
                    SET status='IN_PROGRESS', structure_locked_at='2026-08-27T13:00:00Z'
                    WHERE id='output-history-run';
                    """;
                await seed.ExecuteNonQueryAsync();
            }

            var item = new KitaronSyncCase(
                "30P647004102-001", "30P647004102-001", "Output history part", null, null,
                "history", "case-hash");
            var canonical = new KitaronSyncOrder(
                "50101", item.SourceKey, "3000030777/50101", 8,
                new DateOnly(2027, 3, 2), "active", "order-hash")
            { CanonicalOrderNumber = "3000030777" };
            var repository = application.Services.GetRequiredService<IKitaronSyncRepository>();

            var result = await repository.ApplyAsync(
                new KitaronSyncPlan(1, [item], [canonical], [], [], new HashSet<string>(), [], 1),
                new DateTimeOffset(2026, 9, 2, 9, 0, 0, TimeSpan.Zero), CancellationToken.None);

            Assert.Equal("succeeded", result.Status);
            Assert.Contains("1 superseded Order(s) retained", result.Message, StringComparison.Ordinal);
            var current = await client.GetFromJsonAsync<JsonElement>(
                "/api/v1/orders?caseId=output-history-case");
            Assert.Equal(
                "3000030777/50101",
                Assert.Single(current.GetProperty("items").EnumerateArray().ToArray())
                    .GetProperty("orderNumber").GetString());

            await using var verifyConnection = await database.OpenConnectionAsync();
            await using var verify = verifyConnection.CreateCommand();
            verify.CommandText = """
                SELECT
                    (SELECT kitaron_history_only FROM orders WHERE id='output-history-order'),
                    (SELECT COUNT(*) FROM production_batches WHERE id='output-history-batch'),
                    (SELECT COUNT(*) FROM production_runs WHERE id='output-history-run');
                """;
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(1, reader.GetInt32(1));
            Assert.Equal(1, reader.GetInt32(2));
        });
    }

    [Fact]
    public void Canonical_material_order_query_reads_latest_delivery_approval_without_mutating_kitaron()
    {
        var query = SqlServerKitaronSourceReader.BuildMaterialQuery(
            ["BuyRowID", "BuyMainID", "RowMaterialID", "SupplierDate", "SupplierAmount"]);

        Assert.Contains("dbo.TBuyRow", query, StringComparison.Ordinal);
        Assert.Contains("dbo.TAppCostOfferBySupplier", query, StringComparison.Ordinal);
        Assert.Contains("TOP (1)", query, StringComparison.Ordinal);
        Assert.Contains("candidate.PresentDate DESC", query, StringComparison.Ordinal);
        Assert.Contains("[SupplierDate]", query, StringComparison.Ordinal);
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
                    (SELECT status FROM orders LIMIT 1),
                    (SELECT COUNT(*) FROM kitaron_material_orders WHERE active=1),
                    (SELECT approved_delivery_date FROM kitaron_material_orders LIMIT 1);
                """;
            await using var counts = await command.ExecuteReaderAsync();
            Assert.True(await counts.ReadAsync());
            Assert.Equal(2, counts.GetInt32(0)); Assert.Equal(1, counts.GetInt32(1));
            Assert.Equal(2, counts.GetInt32(2)); Assert.Equal(0, counts.GetInt32(3));
            Assert.Equal(0, counts.GetInt32(4)); Assert.Equal(6, counts.GetInt32(5));
            Assert.Equal(0, counts.GetInt32(6)); Assert.Equal("cancelled", counts.GetString(7));
            Assert.Equal(1, counts.GetInt32(8)); Assert.Equal("2026-08-28", counts.GetString(9));
        }, reader);
        Assert.Equal(4, reader.ReadCount);
    }

    [Fact]
    public async Task Existing_planner_case_seeds_component_import_when_parent_is_absent_from_planning_view()
    {
        var reader = new ExistingCaseComponentReader();
        await RunAsync(new CompleteTester(), async (application, client) =>
        {
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO cases (
                        id, part_number, name, working_folder_path,
                        version, created_at, updated_at)
                    VALUES (
                        'existing-parent', '30P410136000-501', 'INTERCOSTAL 1 ASSEMBLY', 'existing',
                        1, '2026-08-19T00:00:00Z', '2026-08-19T00:00:00Z');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await ConfigureReadySyncAsync(client);
            using var sync = await client.PostAsync("/api/v1/kitaron/sync", null);
            Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
            using var syncJson = JsonDocument.Parse(await sync.Content.ReadAsStringAsync());
            Assert.Equal("succeeded", syncJson.RootElement.GetProperty("status").GetString());
            Assert.Equal(1, syncJson.RootElement.GetProperty("casesCreated").GetInt32());
            Assert.Equal(0, syncJson.RootElement.GetProperty("casesMatched").GetInt32());
            Assert.Equal(1, syncJson.RootElement.GetProperty("casesUpdated").GetInt32());
            Assert.Equal(1, syncJson.RootElement.GetProperty("componentsCreated").GetInt32());

            await using var verifyConnection = await database.OpenConnectionAsync();
            await using var verify = verifyConnection.CreateCommand();
            verify.CommandText = """
                SELECT parent.part_number, child.part_number, component.quantity_per_parent
                FROM case_components component
                JOIN cases parent ON parent.id=component.parent_case_id
                JOIN cases child ON child.id=component.child_case_id
                WHERE component.is_active=1;
                """;
            await using var relationship = await verify.ExecuteReaderAsync();
            Assert.True(await relationship.ReadAsync());
            Assert.Equal("30P410136000-501", relationship.GetString(0));
            Assert.Equal("30P410136100-001", relationship.GetString(1));
            Assert.Equal(1d, relationship.GetDouble(2));
            Assert.False(await relationship.ReadAsync());
        }, reader);
    }

    [Fact]
    public async Task Kitaron_bom_root_imports_when_absent_from_both_planning_view_and_planner()
    {
        var reader = new ExistingCaseComponentReader();
        await RunAsync(new CompleteTester(), async (application, client) =>
        {
            await ConfigureReadySyncAsync(client);
            using var sync = await client.PostAsync("/api/v1/kitaron/sync", null);
            Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
            using var syncJson = JsonDocument.Parse(await sync.Content.ReadAsStringAsync());
            Assert.Equal("succeeded", syncJson.RootElement.GetProperty("status").GetString());
            Assert.Equal(2, syncJson.RootElement.GetProperty("casesCreated").GetInt32());
            Assert.Equal(1, syncJson.RootElement.GetProperty("componentsCreated").GetInt32());

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            await using var verify = connection.CreateCommand();
            verify.CommandText = """
                SELECT COUNT(*)
                FROM case_components component
                JOIN cases parent ON parent.id=component.parent_case_id
                JOIN cases child ON child.id=component.child_case_id
                WHERE parent.part_number='30P410136000-501'
                  AND child.part_number='30P410136100-001'
                  AND component.quantity_per_parent=1
                  AND component.is_active=1;
                """;
            Assert.Equal(1L, (long)(await verify.ExecuteScalarAsync())!);
        }, reader);
    }

    [Fact]
    public async Task Sync_repairs_link_whose_case_operation_target_was_deleted()
    {
        await RunAsync(new CapturingTester(), async (application, _) =>
        {
            var repository = application.Services.GetRequiredService<IKitaronSyncRepository>();
            var now = new DateTimeOffset(2026, 8, 19, 6, 0, 0, TimeSpan.Zero);
            var item = new KitaronSyncCase(
                "STALE-PART", "STALE-PART", "Stale link part", null, null,
                "stale", "case-hash");
            var operation = new KitaronSyncOperation(
                "STALE-PART\u001f10", "STALE-PART", 10, 0, "Cut", null, null, null, "operation-hash");
            var plan = new KitaronSyncPlan(
                1, [item], [], [operation], [], new HashSet<string>(), [], 1);

            var first = await repository.ApplyAsync(plan, now, CancellationToken.None);
            Assert.Equal(1, first.OperationsCreated);

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM case_operations;";
                await delete.ExecuteNonQueryAsync();
            }

            var repaired = await repository.ApplyAsync(plan, now.AddMinutes(1), CancellationToken.None);
            Assert.Equal("succeeded", repaired.Status);
            Assert.Equal(1, repaired.OperationsCreated);
            Assert.Equal(1, repaired.WarningCount);

            await using var verifyConnection = await database.OpenConnectionAsync();
            await using var verify = verifyConnection.CreateCommand();
            verify.CommandText = """
                SELECT COUNT(*)
                FROM kitaron_sync_links link
                JOIN case_operations operation ON operation.id=link.target_id
                WHERE link.source_entity='case_operation' AND link.source_key='STALE-PART' || char(31) || '10';
                """;
            Assert.Equal(1L, (long)(await verify.ExecuteScalarAsync())!);
        });
    }

    [Fact]
    public async Task Legacy_parent_operations_skip_conflicting_component_without_failing_sync()
    {
        await RunAsync(new CapturingTester(), async (application, _) =>
        {
            var repository = application.Services.GetRequiredService<IKitaronSyncRepository>();
            var now = new DateTimeOffset(2026, 8, 19, 6, 0, 0, TimeSpan.Zero);
            var parent = new KitaronSyncCase("PARENT", "PARENT", "Parent", null, null, "parent", "parent-hash");
            var child = new KitaronSyncCase("CHILD", "CHILD", "Child", null, null, "child", "child-hash");
            var operation = new KitaronSyncOperation(
                "PARENT\u001f10", "PARENT", 10, 0, "Legacy route", null, null, null, "operation-hash");
            await repository.ApplyAsync(
                new KitaronSyncPlan(1, [parent], [], [operation], [], new HashSet<string>(), [], 1),
                now, CancellationToken.None);

            var component = new KitaronSyncComponent(
                "1:2", "PARENT", "CHILD", 1, 0, "component-hash");
            var result = await repository.ApplyAsync(
                new KitaronSyncPlan(1, [parent, child], [], [], [component], new HashSet<string> { "1:2" }, [], 1),
                now.AddMinutes(1), CancellationToken.None);

            Assert.Equal("succeeded", result.Status);
            Assert.Equal(0, result.ComponentsCreated);
            Assert.Equal(1, result.WarningCount);

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            await using var verify = connection.CreateCommand();
            verify.CommandText = "SELECT COUNT(*) FROM case_components;";
            Assert.Equal(0L, (long)(await verify.ExecuteScalarAsync())!);
        });
    }

    [Fact]
    public async Task Duplicate_kitaron_edges_reuse_one_parent_child_relationship()
    {
        await RunAsync(new CapturingTester(), async (application, _) =>
        {
            var repository = application.Services.GetRequiredService<IKitaronSyncRepository>();
            var now = new DateTimeOffset(2026, 8, 19, 6, 0, 0, TimeSpan.Zero);
            var parent = new KitaronSyncCase("PARENT", "PARENT", "Parent", null, null, "parent", "parent-hash");
            var child = new KitaronSyncCase("CHILD", "CHILD", "Child", null, null, "child", "child-hash");
            var first = new KitaronSyncComponent("1:2", "PARENT", "CHILD", 1, 0, "component-hash-1");
            var duplicate = new KitaronSyncComponent("3:4", "PARENT", "CHILD", 1, 1, "component-hash-2");
            var result = await repository.ApplyAsync(
                new KitaronSyncPlan(
                    2, [parent, child], [], [], [first, duplicate],
                    new HashSet<string> { "1:2", "3:4" }, [], 1),
                now, CancellationToken.None);

            Assert.Equal("succeeded", result.Status);
            Assert.Equal(1, result.ComponentsCreated);
            Assert.Equal(1, result.ComponentsMatched);
            Assert.Equal(1, result.WarningCount);

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            await using var verify = connection.CreateCommand();
            verify.CommandText = """
                SELECT (SELECT COUNT(*) FROM case_components),
                       (SELECT COUNT(*) FROM kitaron_sync_links WHERE source_entity='case_component');
                """;
            await using var counts = await verify.ExecuteReaderAsync();
            Assert.True(await counts.ReadAsync());
            Assert.Equal(1, counts.GetInt32(0));
            Assert.Equal(1, counts.GetInt32(1));
        });
    }

    private static async Task ConfigureReadySyncAsync(HttpClient client)
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
            confidence = field.GetProperty("confidence").GetString() is "blocked"
                ? "low" : field.GetProperty("confidence").GetString(),
            transform = field.GetProperty("transform").GetString(),
            notes = (string?)null
        }).ToArray();
        using var ready = await client.PutAsJsonAsync("/api/v1/kitaron/mapping", new
        {
            modelMode = "domain_aligned", status = "ready_for_implementation", fields,
            notes = "Existing-case component synchronization test.",
            version = mapping.GetProperty("version").GetInt32()
        });
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
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
            for (var attempt = 0; Directory.Exists(directory); attempt++)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(100 * (attempt + 1));
                    SqliteConnection.ClearAllPools();
                }
            }
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
                new("RootID", "nvarchar"), new("ProductionAmount", "int"), new("RecordID", "int"),
                new("BuyRowID", "int"), new("BuyMainID", "int"), new("NumberOfString", "float"),
                new("RowMaterialID", "int"), new("Information", "nvarchar"), new("SupplyerName", "nvarchar"),
                new("Amount", "float"), new("ReceivedAmount", "float"), new("MeasureUnit", "nvarchar"),
                new("DateToRecept", "datetime"), new("SupplierDate", "datetime"),
                new("SupplierAmount", "float"), new("SupplierRemark", "nvarchar"),
                new("Status", "nvarchar"), new("Closed", "bit")]);
    }

    private sealed class CapturingSourceReader : IKitaronSourceReader
    {
        internal int ReadCount { get; private set; }
        internal bool StopProduction { get; set; }
        internal bool IncludeComponent { get; set; } = true;
        public Task<KitaronSourceSnapshot> ReadAsync(
            StoredKitaronConnectionSettings settings, string password, IReadOnlyList<string> columns,
            IReadOnlyList<string> materialColumns,
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
                components,
                [new KitaronSourceRow(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["BuyRowID"] = 40805, ["BuyMainID"] = 76493, ["NumberOfString"] = 1d,
                    ["RowMaterialID"] = 748, ["Information"] = "AL 7050 plate",
                    ["SupplyerName"] = "Material supplier", ["Amount"] = 22d,
                    ["ReceivedAmount"] = 5d, ["MeasureUnit"] = "piece",
                    ["DateToRecept"] = new DateTime(2026, 8, 20),
                    ["SupplierDate"] = new DateTime(2026, 8, 28),
                    ["SupplierAmount"] = 22d, ["SupplierRemark"] = "Confirmed",
                    ["Status"] = "approved", ["Closed"] = false
                })]));
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

    private sealed class ExistingCaseComponentReader : IKitaronSourceReader
    {
        public Task<KitaronSourceSnapshot> ReadAsync(
            StoredKitaronConnectionSettings settings, string password, IReadOnlyList<string> columns,
            IReadOnlyList<string> materialColumns,
            CancellationToken cancellationToken)
        {
            Assert.Equal("sync-secret", password);
            return Task.FromResult(new KitaronSourceSnapshot(
                [],
                [],
                [new KitaronSourceComponent(
                    "10251:10254",
                    "30P410136000-501", "INTERCOSTAL 1 ASSEMBLY", "NEW",
                    "30P410136100-001", "INTERCOSTAL 1", "NEW", 1, 0)]));
        }
    }

    private sealed class WorkFallbackSourceReader : IKitaronSourceReader
    {
        public Task<KitaronSourceSnapshot> ReadAsync(
            StoredKitaronConnectionSettings settings, string password, IReadOnlyList<string> columns,
            IReadOnlyList<string> materialColumns, CancellationToken cancellationToken)
        {
            Assert.Contains("RecordID", columns);
            var work = new KitaronSourceRow(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["RecordID"] = 41399, ["DetailNumber"] = "30P782531500-001",
                ["DetailName"] = "ATS DUCT SUPPORT", ["REV"] = "NEW", ["CompanyName"] = "Customer",
                ["OrderNumber"] = "3000030679", ["OrdAmount"] = 24,
                ["SupplyDate"] = new DateTime(2027, 3, 2), ["ActionNumber"] = 10,
                ["ActionDescription"] = "Test", ["Station"] = "MILL",
                ["RootID"] = "WO-1", ["ProductionAmount"] = 24
            });
            KitaronSourceOrder[] canonical =
            [
                new("41400", "30P782531500-001", "ATS DUCT SUPPORT", "NEW", "3000030679", 12, new DateTime(2026,10,27), false),
                new("41401", "30P782531500-001", "ATS DUCT SUPPORT", "NEW", "3000030679", 12, new DateTime(2027,4,14), false),
                new("41402", "30P782531500-001", "ATS DUCT SUPPORT", "NEW", "3000030679", 12, new DateTime(2027,7,21), false),
                new("41403", "30P782531500-001", "ATS DUCT SUPPORT", "NEW", "3000030679", 12, new DateTime(2027,11,2), false)
            ];
            return Task.FromResult(new KitaronSourceSnapshot([work], canonical, [], []));
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
