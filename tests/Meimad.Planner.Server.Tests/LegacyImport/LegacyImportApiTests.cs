using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.LegacyImport;

public sealed class LegacyImportApiTests
{
    [Fact]
    public async Task All_skip_commit_is_rejected_without_receipt_or_event()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningAsync(application.Services);
            AddEditHeaders(client);
            using var preview = await PreviewAsync(client, LegacyWorkbookFixture.Create(marker: "all-skip"));
            var body = CommitBody(
                preview.RootElement.GetProperty("importToken").GetString()!,
                preview.RootElement.GetProperty("workbookSha256").GetString()!,
                [new { rowKey = "תכנית ייצור!3", action = "skip" }]);

            using var response = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains(error.RootElement.GetProperty("error").GetProperty("details").EnumerateArray(), issue =>
                issue.GetProperty("code").GetString() == "no_import_actions_selected");
            Assert.Equal(0, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM legacy_working_plan_imports;"));
            Assert.Equal(0, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM structured_event_log WHERE event_type = 'legacy_working_plan_import_committed';"));
        });
    }

    [Fact]
    public async Task Generic_flat_order_headers_are_detected_and_mapped_from_actual_columns()
    {
        await RunWithServerAsync(async (_, client) =>
        {
            using var preview = await PreviewAsync(client, LegacyWorkbookFixture.CreateFlatOpenOrders());

            Assert.Null(preview.RootElement.GetProperty("suggestions").GetProperty("planningSheet").GetString());
            Assert.Equal("Orders", preview.RootElement.GetProperty("suggestions").GetProperty("openOrdersSheet").GetString());
            var suggestions = preview.RootElement.GetProperty("suggestions").GetProperty("openOrderColumns")
                .EnumerateArray().ToDictionary(value => value.GetProperty("field").GetString()!);
            Assert.Equal("A", suggestions["partNumber"].GetProperty("column").GetString());
            Assert.Equal("Part Number", suggestions["partNumber"].GetProperty("header").GetString());
            Assert.Equal(0.98m, suggestions["partNumber"].GetProperty("confidence").GetDecimal());
            Assert.True(suggestions["partNumber"].GetProperty("required").GetBoolean());
            Assert.Equal("B", suggestions["orderNumber"].GetProperty("column").GetString());
            Assert.Equal("C", suggestions["outstandingQuantity"].GetProperty("column").GetString());
            Assert.Equal("D", suggestions["deliveryDate"].GetProperty("column").GetString());
            Assert.DoesNotContain(suggestions.Values, value =>
                value.GetProperty("confidence").GetDecimal() == 1.0m);

            var row = Assert.Single(preview.RootElement.GetProperty("openOrderRows").EnumerateArray());
            Assert.Equal("PN-1001", row.GetProperty("values").GetProperty("partNumber").GetString());
            Assert.Equal("ORD-001", row.GetProperty("values").GetProperty("orderNumber").GetString());
            Assert.Equal(50, row.GetProperty("values").GetProperty("outstandingQuantity").GetInt32());
            Assert.Equal("2026-09-10", row.GetProperty("values").GetProperty("deliveryDate").GetString());
            Assert.Contains(preview.RootElement.GetProperty("issues").EnumerateArray(), issue =>
                issue.GetProperty("code").GetString() == "planning_sheet_not_found"
                && issue.GetProperty("severity").GetString() == "warning");
        });
    }

    [Fact]
    public async Task Supplied_legacy_layout_keeps_known_part_column_when_two_headers_are_ambiguous()
    {
        await RunWithServerAsync(async (_, client) =>
        {
            using var preview = await PreviewAsync(
                client,
                LegacyWorkbookFixture.Create(planningCustomerHeader: "מקט"));

            var suggestions = preview.RootElement.GetProperty("suggestions").GetProperty("planningColumns")
                .EnumerateArray().ToDictionary(value => value.GetProperty("field").GetString()!);
            Assert.Equal("A", suggestions["customer"].GetProperty("column").GetString());
            Assert.Equal("B", suggestions["partNumber"].GetProperty("column").GetString());
            Assert.Equal("PN-1", preview.RootElement.GetProperty("rows")[0]
                .GetProperty("values").GetProperty("partNumber").GetString());
        });
    }

    [Fact]
    public async Task Flat_orders_title_row_is_not_misclassified_as_a_planning_machine_section()
    {
        await RunWithServerAsync(async (_, client) =>
        {
            using var preview = await PreviewAsync(
                client,
                LegacyWorkbookFixture.CreateFlatOpenOrders(title: "Open Orders Report"));

            Assert.Null(preview.RootElement.GetProperty("suggestions").GetProperty("planningSheet").GetString());
            Assert.Equal("Orders", preview.RootElement.GetProperty("suggestions").GetProperty("openOrdersSheet").GetString());
            Assert.Empty(preview.RootElement.GetProperty("machineSections").EnumerateArray());
            var row = Assert.Single(preview.RootElement.GetProperty("openOrderRows").EnumerateArray());
            Assert.Equal("Orders!3", row.GetProperty("rowKey").GetString());
        });
    }

    [Fact]
    public async Task English_planning_header_is_detected_but_not_emitted_as_a_data_row()
    {
        await RunWithServerAsync(async (_, client) =>
        {
            using var preview = await PreviewAsync(client, LegacyWorkbookFixture.Create(
                machineLabel: "Machine M01",
                planningPartHeader: "Part Number",
                planningQuantityHeader: "Quantity"));

            Assert.Equal(LegacyWorkbookFixture.PlanningSheet,
                preview.RootElement.GetProperty("suggestions").GetProperty("planningSheet").GetString());
            var rows = preview.RootElement.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Equal(3, rows.Length);
            Assert.Equal($"{LegacyWorkbookFixture.PlanningSheet}!3", rows[0].GetProperty("rowKey").GetString());
            Assert.DoesNotContain(rows, row =>
                row.GetProperty("values").GetProperty("partNumber").GetString() == "Part Number");
        });
    }

    [Fact]
    public async Task Planning_quantity_without_part_number_is_a_blocking_row_issue()
    {
        await RunWithServerAsync(async (_, client) =>
        {
            using var preview = await PreviewAsync(client, LegacyWorkbookFixture.Create(firstPlanningPartNumber: ""));
            Assert.Contains(preview.RootElement.GetProperty("issues").EnumerateArray(), issue =>
                issue.GetProperty("code").GetString() == "part_number_required"
                && issue.GetProperty("severity").GetString() == "blocking"
                && issue.GetProperty("rowNumber").GetInt32() == 3);
        });
    }

    [Fact]
    public async Task Generic_orders_only_preview_can_create_case_and_order()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningAsync(application.Services);
            AddEditHeaders(client);
            using var preview = await PreviewAsync(client, LegacyWorkbookFixture.CreateFlatOpenOrders(
                partNumber: "PN-GENERIC",
                orderNumber: "ORD-GENERIC"));
            var body = OpenOrderCreateBody(preview, "PN-GENERIC", "ORD-GENERIC", 50, "2026-09-10");

            using var response = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", body);
            var json = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, json);
            Assert.Equal(1, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM cases WHERE part_number = 'PN-GENERIC';"));
            Assert.Equal(1, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM orders WHERE order_reference = 'ORD-GENERIC' AND quantity = 50;"));

            using var replayResponse = await client.PostAsJsonAsync(
                "/api/v1/imports/legacy-working-plan/commit",
                body);
            Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
            using var replay = JsonDocument.Parse(await replayResponse.Content.ReadAsStringAsync());
            Assert.True(replay.RootElement.GetProperty("replayed").GetBoolean());
            Assert.Equal(1, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM cases WHERE part_number = 'PN-GENERIC';"));
            Assert.Equal(1, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM orders WHERE order_reference = 'ORD-GENERIC';"));
        });
    }

    [Fact]
    public async Task Orders_only_commit_ignores_explicit_skip_from_excluded_planning_sheet()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningAsync(application.Services);
            AddEditHeaders(client);
            using var preview = await PreviewAsync(client, LegacyWorkbookFixture.Create(marker: "orders-only-skip"));
            var body = new
            {
                schemaVersion = 1,
                importToken = preview.RootElement.GetProperty("importToken").GetString(),
                workbookSha256 = preview.RootElement.GetProperty("workbookSha256").GetString(),
                planningSheet = (string?)null,
                openOrdersSheet = LegacyWorkbookFixture.OpenOrdersSheet,
                columnMappings = Array.Empty<object>(),
                machineMappings = Array.Empty<object>(),
                openOrderSelections = new object[]
                {
                    new
                    {
                        rowKey = $"{LegacyWorkbookFixture.OpenOrdersSheet}!2",
                        action = "create_case",
                        newCase = new
                        {
                            partNumber = "PN-ORDERS-ONLY",
                            name = "Orders only",
                            workingFolderPath = Path.Combine(Path.GetTempPath(), "PN-ORDERS-ONLY")
                        },
                        order = new { orderNumber = "ORD-ORDERS-ONLY", quantity = 5, workFinishDate = "2026-09-10" }
                    }
                },
                planningSelections = new object[]
                {
                    new { rowKey = $"{LegacyWorkbookFixture.PlanningSheet}!3", action = "skip" }
                }
            };

            using var response = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", body);
            var json = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, json);
            Assert.Equal(1, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM orders WHERE order_reference = 'ORD-ORDERS-ONLY';"));
        });
    }

    [Fact]
    public async Task Planning_only_commit_ignores_explicit_skip_from_excluded_open_orders_sheet()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningAsync(application.Services);
            AddEditHeaders(client);
            using var preview = await PreviewAsync(client, LegacyWorkbookFixture.Create(marker: "planning-only-skip"));
            var body = new
            {
                schemaVersion = 1,
                importToken = preview.RootElement.GetProperty("importToken").GetString(),
                workbookSha256 = preview.RootElement.GetProperty("workbookSha256").GetString(),
                planningSheet = LegacyWorkbookFixture.PlanningSheet,
                openOrdersSheet = (string?)null,
                columnMappings = Array.Empty<object>(),
                machineMappings = new[]
                {
                    new { sectionKey = $"{LegacyWorkbookFixture.PlanningSheet}!1", machineId = "machine-01" }
                },
                openOrderSelections = new object[]
                {
                    new { rowKey = $"{LegacyWorkbookFixture.OpenOrdersSheet}!2", action = "skip" }
                },
                planningSelections = new object[]
                {
                    Planning($"{LegacyWorkbookFixture.PlanningSheet}!4", "batch-operation-2", "machine-01", null)
                }
            };

            using var response = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", body);
            var json = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, json);
            Assert.Equal(1, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM machine_assignments WHERE batch_operation_id = 'batch-operation-2';"));
        });
    }

    [Fact]
    public async Task Duplicate_machine_mapping_returns_structured_validation_without_mutation()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningAsync(application.Services);
            AddEditHeaders(client);
            using var preview = await PreviewAsync(client, LegacyWorkbookFixture.Create(marker: "duplicate-machine-map"));
            var sectionKey = $"{LegacyWorkbookFixture.PlanningSheet}!1";
            var body = new
            {
                schemaVersion = 1,
                importToken = preview.RootElement.GetProperty("importToken").GetString(),
                workbookSha256 = preview.RootElement.GetProperty("workbookSha256").GetString(),
                planningSheet = LegacyWorkbookFixture.PlanningSheet,
                openOrdersSheet = LegacyWorkbookFixture.OpenOrdersSheet,
                columnMappings = Array.Empty<object>(),
                machineMappings = new[]
                {
                    new { sectionKey, machineId = "machine-01" },
                    new { sectionKey, machineId = "machine-manual" }
                },
                openOrderSelections = Array.Empty<object>(),
                planningSelections = new object[]
                {
                    Planning($"{LegacyWorkbookFixture.PlanningSheet}!4", "batch-operation-2", "machine-01", null)
                }
            };

            using var response = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains(error.RootElement.GetProperty("error").GetProperty("details").EnumerateArray(), issue =>
                issue.GetProperty("code").GetString() == "duplicate_selection"
                && issue.GetProperty("field").GetString() == "machineMappings");
            Assert.Equal(0, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM machine_assignments WHERE batch_operation_id = 'batch-operation-2';"));
        });
    }

    [Fact]
    public async Task Invalid_mapped_order_date_is_blocking_and_creates_nothing()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningAsync(application.Services);
            AddEditHeaders(client);
            using var preview = await PreviewAsync(client, LegacyWorkbookFixture.CreateFlatOpenOrders(
                partNumber: "PN-BAD-DATE",
                orderNumber: "ORD-BAD-DATE",
                finishDate: "not-a-date"));
            Assert.Contains(preview.RootElement.GetProperty("issues").EnumerateArray(), issue =>
                issue.GetProperty("code").GetString() == "invalid_date"
                && issue.GetProperty("severity").GetString() == "blocking");

            var body = OpenOrderCreateBody(preview, "PN-BAD-DATE", "ORD-BAD-DATE", 50, "2026-09-10");
            using var response = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(0, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM cases WHERE part_number = 'PN-BAD-DATE';"));
            Assert.Equal(0, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM legacy_working_plan_imports;"));
        });
    }

    [Fact]
    public async Task Negative_mapped_order_quantity_is_blocking_and_creates_nothing()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningAsync(application.Services);
            AddEditHeaders(client);
            using var preview = await PreviewAsync(client, LegacyWorkbookFixture.CreateFlatOpenOrders(
                partNumber: "PN-NEGATIVE",
                orderNumber: "ORD-NEGATIVE",
                quantity: "-5"));
            Assert.Contains(preview.RootElement.GetProperty("issues").EnumerateArray(), issue =>
                issue.GetProperty("code").GetString() == "invalid_quantity"
                && issue.GetProperty("severity").GetString() == "blocking");

            var body = OpenOrderCreateBody(preview, "PN-NEGATIVE", "ORD-NEGATIVE", 5, "2026-09-10");
            using var response = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(0, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM cases WHERE part_number = 'PN-NEGATIVE';"));
            Assert.Equal(0, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM legacy_working_plan_imports;"));
        });
    }

    [Fact]
    public async Task Explicit_sheet_and_column_mapping_are_applied_during_read_only_repreview()
    {
        await RunWithServerAsync(async (_, client) =>
        {
            var workbook = LegacyWorkbookFixture.CreateFlatOpenOrders(
                partHeader: "Legacy Item",
                orderHeader: "Legacy PO",
                quantityHeader: "Legacy Count",
                finishHeader: "Legacy Deadline");
            using var initial = await PreviewAsync(client, workbook);
            Assert.Empty(initial.RootElement.GetProperty("openOrderRows").EnumerateArray());
            var sourceColumns = initial.RootElement.GetProperty("workbook").GetProperty("sheets")[0]
                .GetProperty("columns").EnumerateArray()
                .ToDictionary(column => column.GetProperty("column").GetString()!);
            Assert.Equal("Legacy Item", sourceColumns["A"].GetProperty("header").GetString());
            Assert.Equal("PN-1001", sourceColumns["A"].GetProperty("sample").GetString());
            Assert.Equal("Legacy Deadline", sourceColumns["D"].GetProperty("header").GetString());
            Assert.Equal("2026-09-10", sourceColumns["D"].GetProperty("sample").GetString());
            var mappings = new object[]
            {
                new { scope = "open_orders", field = "partNumber", column = "A" },
                new { scope = "open_orders", field = "orderNumber", column = "B" },
                new { scope = "open_orders", field = "outstandingQuantity", column = "C" },
                new { scope = "open_orders", field = "deliveryDate", column = "D" }
            };
            using var remapped = await PreviewAsync(
                client,
                workbook,
                openOrdersSheet: "Orders",
                columnMappings: mappings);

            Assert.NotEqual(
                initial.RootElement.GetProperty("importToken").GetString(),
                remapped.RootElement.GetProperty("importToken").GetString());
            var row = Assert.Single(remapped.RootElement.GetProperty("openOrderRows").EnumerateArray());
            Assert.Equal("PN-1001", row.GetProperty("values").GetProperty("partNumber").GetString());
            Assert.Equal("ORD-001", row.GetProperty("values").GetProperty("orderNumber").GetString());
            Assert.Equal(50, row.GetProperty("values").GetProperty("outstandingQuantity").GetInt32());
            Assert.Equal("2026-09-10", row.GetProperty("values").GetProperty("deliveryDate").GetString());
            Assert.All(remapped.RootElement.GetProperty("suggestions").GetProperty("openOrderColumns").EnumerateArray(),
                suggestion => Assert.Equal(1.0m, suggestion.GetProperty("confidence").GetDecimal()));
        });
    }

    [Fact]
    public async Task Commit_preserves_a_reviewed_optional_column_omission()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningAsync(application.Services);
            AddEditHeaders(client);
            var workbook = LegacyWorkbookFixture.CreateFlatOpenOrders(
                partNumber: "PN-CLEARED-DATE",
                orderNumber: "ORD-CLEARED-DATE",
                finishDate: "not-a-date");
            var mappings = new object[]
            {
                new { scope = "open_orders", field = "partNumber", column = "A" },
                new { scope = "open_orders", field = "orderNumber", column = "B" },
                new { scope = "open_orders", field = "outstandingQuantity", column = "C" }
            };
            using var remapped = await PreviewAsync(
                client,
                workbook,
                openOrdersSheet: "Orders",
                columnMappings: mappings);
            Assert.DoesNotContain(remapped.RootElement.GetProperty("issues").EnumerateArray(), issue =>
                issue.GetProperty("code").GetString() == "invalid_date");

            var body = OpenOrderCreateBody(
                remapped,
                "PN-CLEARED-DATE",
                "ORD-CLEARED-DATE",
                50,
                "2026-09-10",
                mappings);
            using var response = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", body);
            var json = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, json);
            Assert.Equal(1, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM orders WHERE order_reference = 'ORD-CLEARED-DATE';"));
        });
    }

    [Fact]
    public async Task Missing_order_number_with_mapped_order_facts_is_blocking()
    {
        await RunWithServerAsync(async (_, client) =>
        {
            using var preview = await PreviewAsync(client, LegacyWorkbookFixture.CreateFlatOpenOrders(orderNumber: ""));
            Assert.Contains(preview.RootElement.GetProperty("issues").EnumerateArray(), issue =>
                issue.GetProperty("code").GetString() == "order_number_required"
                && issue.GetProperty("severity").GetString() == "blocking");
        });
    }

    [Fact]
    public async Task Exact_replay_survives_server_restart_without_staged_token()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.LegacyImport.RestartTests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "test.db");
        object? committedBody = null;
        var first = BuildServer(databasePath);
        var firstDisposed = false;
        try
        {
            await first.StartAsync();
            using (var client = first.GetTestClient())
            {
                await SeedPlanningAsync(first.Services);
                AddEditHeaders(client);
                using var preview = await PreviewAsync(client, LegacyWorkbookFixture.Create(marker: "restart-replay"));
                committedBody = CommitBody(
                    preview.RootElement.GetProperty("importToken").GetString()!,
                    preview.RootElement.GetProperty("workbookSha256").GetString()!,
                    [Planning("תכנית ייצור!4", "batch-operation-2", "machine-01", null)]);
                using var commit = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", committedBody);
                Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
            }
            await first.StopAsync();
            await first.DisposeAsync();
            firstDisposed = true;

            var second = BuildServer(databasePath);
            try
            {
                await second.StartAsync();
                using var client = second.GetTestClient();
                AddEditHeaders(client);
                using var replay = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", committedBody);
                Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
                using var document = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
                Assert.True(document.RootElement.GetProperty("replayed").GetBoolean());
                Assert.Equal(1, await ScalarAsync(second.Services,
                    "SELECT COUNT(*) FROM structured_event_log WHERE event_type = 'legacy_working_plan_import_committed';"));
                await second.StopAsync();
            }
            finally
            {
                await second.DisposeAsync();
            }
        }
        finally
        {
            if (!firstDisposed)
            {
                await first.DisposeAsync();
            }
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Preview_staging_is_bounded_and_oldest_token_is_evicted()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningAsync(application.Services);
            var previews = new List<JsonDocument>();
            try
            {
                for (var index = 0; index < 5; index++)
                {
                    previews.Add(await PreviewAsync(client, LegacyWorkbookFixture.Create(marker: $"stage-{index}")));
                }
                AddEditHeaders(client);
                var first = previews[0].RootElement;
                var body = CommitBody(
                    first.GetProperty("importToken").GetString()!,
                    first.GetProperty("workbookSha256").GetString()!,
                    [Planning("תכנית ייצור!3", "batch-operation-1", "machine-01", new { confirmed = true, reason = "eviction test" })]);
                using var response = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", body);
                Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
            }
            finally
            {
                foreach (var preview in previews) preview.Dispose();
            }
        });
    }

    [Fact]
    public async Task Preview_and_commit_are_explicit_atomic_ordered_and_idempotent()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningAsync(application.Services);
            var preview = await PreviewAsync(client, LegacyWorkbookFixture.Create());
            Assert.Equal(3, preview.RootElement.GetProperty("rows").GetArrayLength());
            Assert.Equal("2026-08-03", preview.RootElement.GetProperty("rows")[0]
                .GetProperty("values").GetProperty("startDate").GetString());
            Assert.Equal("formula_cached", preview.RootElement.GetProperty("rows")[0]
                .GetProperty("provenance").EnumerateArray().Single(value => value.GetProperty("field").GetString() == "caseReference")
                .GetProperty("kind").GetString());
            var machineCandidates = preview.RootElement.GetProperty("machineSections")[0].GetProperty("candidates");
            Assert.Equal("machine-01", machineCandidates[0].GetProperty("machineId").GetString());
            Assert.Equal(1m, machineCandidates[0].GetProperty("score").GetDecimal());
            Assert.Contains(machineCandidates.EnumerateArray(), value =>
                value.GetProperty("machineId").GetString() == "machine-manual"
                && value.GetProperty("reason").GetString() == "manual_choice");
            Assert.Contains("probing", machineCandidates[0].GetProperty("capabilities").EnumerateArray()
                .Select(value => value.GetString()));
            var batchOperationCandidate = preview.RootElement.GetProperty("rows")[0]
                .GetProperty("candidates").GetProperty("batchOperations")[0];
            Assert.Equal("B1", batchOperationCandidate.GetProperty("batchNumber").GetString());
            Assert.Equal("case-1", batchOperationCandidate.GetProperty("caseId").GetString());
            Assert.Equal("PN-1", batchOperationCandidate.GetProperty("partNumber").GetString());
            Assert.Contains(preview.RootElement.GetProperty("issues").EnumerateArray(), issue =>
                issue.GetProperty("code").GetString() == "source_cell_error"
                && issue.GetProperty("severity").GetString() == "warning");

            AddEditHeaders(client);
            var token = preview.RootElement.GetProperty("importToken").GetString()!;
            var hash = preview.RootElement.GetProperty("workbookSha256").GetString()!;
            var failing = CommitBody(token, hash, [
                Planning("תכנית ייצור!3", "batch-operation-1", "machine-01", new { confirmed = true, reason = "Approved legacy placement" }),
                Planning("תכנית ייצור!5", "batch-operation-3", "machine-01", null)
            ]);
            using (var response = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", failing))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
                using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Contains(error.RootElement.GetProperty("error").GetProperty("details").EnumerateArray(), issue =>
                    issue.GetProperty("code").GetString() == "operation_already_assigned");
            }
            Assert.Equal(0, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM machine_assignments WHERE batch_operation_id = 'batch-operation-1';"));
            Assert.Equal(0, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM machine_assignment_overrides WHERE batch_operation_id = 'batch-operation-1';"));

            var success = CommitBody(token, hash, [
                Planning("תכנית ייצור!4", "batch-operation-2", "machine-01", null),
                Planning("תכנית ייצור!3", "batch-operation-1", "machine-01", new { confirmed = true, reason = "Approved legacy placement" })
            ]);
            using var successResponse = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", success);
            var successJson = await successResponse.Content.ReadAsStringAsync();
            Assert.True(successResponse.StatusCode == HttpStatusCode.OK, successJson);
            using var successDocument = JsonDocument.Parse(successJson);
            Assert.False(successDocument.RootElement.GetProperty("replayed").GetBoolean());
            Assert.Equal(2, successDocument.RootElement.GetProperty("created").GetProperty("assignmentIds").GetArrayLength());

            Assert.Equal(1, await ScalarAsync(application.Services,
                "SELECT backlog_position FROM machine_assignments WHERE batch_operation_id = 'batch-operation-1';"));
            Assert.Equal(2, await ScalarAsync(application.Services,
                "SELECT backlog_position FROM machine_assignments WHERE batch_operation_id = 'batch-operation-2';"));
            Assert.Equal(1, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM machine_assignment_overrides WHERE batch_operation_id = 'batch-operation-1';"));
            Assert.Equal(1, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM structured_event_log WHERE event_type = 'legacy_working_plan_import_committed';"));
            Assert.Equal(1, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM structured_event_log WHERE event_type = 'cross_machine_type_override' AND user_id = 'planner-user';"));

            var evictionPreviews = new List<JsonDocument>();
            for (var index = 0; index < 5; index++)
            {
                evictionPreviews.Add(await PreviewAsync(client, LegacyWorkbookFixture.Create(marker: $"post-commit-{index}")));
            }
            foreach (var evictionPreview in evictionPreviews) evictionPreview.Dispose();

            using var replayResponse = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", success);
            Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
            using var replayDocument = JsonDocument.Parse(await replayResponse.Content.ReadAsStringAsync());
            Assert.True(replayDocument.RootElement.GetProperty("replayed").GetBoolean());
            Assert.Empty(replayDocument.RootElement.GetProperty("created").GetProperty("assignmentIds").EnumerateArray());
            Assert.Equal(1, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM structured_event_log WHERE event_type = 'legacy_working_plan_import_committed';"));

            var changed = CommitBody(token, hash, [Planning("תכנית ייצור!3", "batch-operation-1", "machine-01", null)]);
            using var changedResponse = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", changed);
            Assert.Equal(HttpStatusCode.Conflict, changedResponse.StatusCode);
        });
    }

    [Fact]
    public async Task Create_case_may_atomically_include_or_omit_new_order()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningAsync(application.Services);
            AddEditHeaders(client);
            var preview = await PreviewAsync(client, LegacyWorkbookFixture.Create(marker: "case-order"));
            var body = new
            {
                schemaVersion = 1,
                importToken = preview.RootElement.GetProperty("importToken").GetString(),
                workbookSha256 = preview.RootElement.GetProperty("workbookSha256").GetString(),
                planningSheet = LegacyWorkbookFixture.PlanningSheet,
                openOrdersSheet = LegacyWorkbookFixture.OpenOrdersSheet,
                columnMappings = Array.Empty<object>(),
                machineMappings = Array.Empty<object>(),
                openOrderSelections = new object[]
                {
                    new
                    {
                        rowKey = "גיליון1!2",
                        action = "create_case",
                        newCase = new
                        {
                            partNumber = "PN-NEW",
                            name = "New Part",
                            customer = "New Customer",
                            workingFolderPath = Path.Combine(Path.GetTempPath(), "PN-NEW")
                        },
                        order = new
                        {
                            orderNumber = "O-NEW",
                            quantity = 5,
                            workFinishDate = "2026-08-03"
                        }
                    }
                },
                planningSelections = Array.Empty<object>()
            };
            using var response = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM cases WHERE part_number = 'PN-NEW';"));
            Assert.Equal(1, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM orders WHERE order_reference = 'O-NEW';"));

            var caseOnlyPreview = await PreviewAsync(client, LegacyWorkbookFixture.Create(marker: "case-only"));
            var caseOnly = new
            {
                schemaVersion = 1,
                importToken = caseOnlyPreview.RootElement.GetProperty("importToken").GetString(),
                workbookSha256 = caseOnlyPreview.RootElement.GetProperty("workbookSha256").GetString(),
                planningSheet = LegacyWorkbookFixture.PlanningSheet,
                openOrdersSheet = LegacyWorkbookFixture.OpenOrdersSheet,
                columnMappings = Array.Empty<object>(),
                machineMappings = Array.Empty<object>(),
                openOrderSelections = new object[]
                {
                    new
                    {
                        rowKey = "גיליון1!2",
                        action = "create_case",
                        newCase = new
                        {
                            partNumber = "PN-ONLY",
                            name = "Case only",
                            workingFolderPath = Path.Combine(Path.GetTempPath(), "PN-ONLY")
                        }
                    }
                },
                planningSelections = Array.Empty<object>()
            };
            using var caseOnlyResponse = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", caseOnly);
            Assert.Equal(HttpStatusCode.OK, caseOnlyResponse.StatusCode);
            Assert.Equal(1, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM cases WHERE part_number = 'PN-ONLY';"));
        });
    }

    [Fact]
    public async Task Create_batch_reuses_allocation_rules_and_snapshots_existing_route()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningAsync(application.Services);
            await ExecuteAsync(application.Services, """
                INSERT INTO orders (
                    id, case_id, order_reference, quantity, work_finish_date, status)
                VALUES ('order-1', 'case-1', 'O-1', 1, '2026-08-03', 'active');
                INSERT INTO case_operations (
                    id, case_id, operation_number, route_position, name,
                    required_machine_type, dependency_type,
                    predecessor_case_operation_id)
                VALUES (
                    'case-operation-1b', 'case-1', 20, 1, 'Op 1B',
                    'mill', 'sequential', 'case-operation-1');
                """);
            AddEditHeaders(client);
            var preview = await PreviewAsync(client, LegacyWorkbookFixture.Create(marker: "batch-create"));
            var token = preview.RootElement.GetProperty("importToken").GetString()!;
            var hash = preview.RootElement.GetProperty("workbookSha256").GetString()!;
            object Selection(object[] allocations) => new
            {
                rowKey = "תכנית ייצור!3",
                action = "create_batch_and_assign",
                caseId = "case-1",
                caseOperationId = "case-operation-1",
                batchNumber = "B-IMPORTED",
                machineId = "machine-01",
                compatibilityOverride = new { confirmed = true, reason = "Approved import" },
                expectedCaseRoute = ExpectedRoute("case-operation-1", "case-operation-1b"),
                allocations
            };

            var invalid = CommitBody(token, hash, [Selection([new { type = "scrapAllowance", quantity = 2 }])]);
            using (var response = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", invalid))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            }
            Assert.Equal(0, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM production_batches WHERE batch_number = 'B-IMPORTED';"));

            var valid = CommitBody(token, hash, [Selection([
                new { type = "order", orderId = "order-1", quantity = 1 },
                new { type = "scrapAllowance", orderId = (string?)null, quantity = 1 }
            ])]);
            using var success = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", valid);
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
            using var successDocument = JsonDocument.Parse(await success.Content.ReadAsStringAsync());
            Assert.Equal(2, successDocument.RootElement.GetProperty("created").GetProperty("batchOperationIds").GetArrayLength());
            Assert.Single(successDocument.RootElement.GetProperty("poolBatchOperationIds").EnumerateArray());
            Assert.Single(successDocument.RootElement.GetProperty("created").GetProperty("assignmentIds").EnumerateArray());
            Assert.Equal(1, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM production_batches WHERE batch_number = 'B-IMPORTED';"));
            Assert.Equal(2, await ScalarAsync(application.Services, """
                SELECT COUNT(*) FROM batch_allocations
                WHERE production_batch_id = (
                    SELECT id FROM production_batches WHERE batch_number = 'B-IMPORTED');
                """));
            Assert.Equal(1, await ScalarAsync(application.Services, "SELECT status = 'active' FROM orders WHERE id = 'order-1';"));
        });
    }

    [Fact]
    public async Task Create_batch_to_pool_snapshots_the_full_route_without_assigning_or_reordering()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningAsync(application.Services);
            await ExecuteAsync(application.Services, """
                INSERT INTO case_operations (
                    id, case_id, operation_number, route_position, name,
                    required_machine_type, dependency_type,
                    predecessor_case_operation_id)
                VALUES (
                    'case-operation-1b', 'case-1', 20, 1, 'Op 1B',
                    'mill', 'sequential', 'case-operation-1');
                """);
            AddEditHeaders(client);
            using var preview = await PreviewAsync(client, LegacyWorkbookFixture.Create(marker: "batch-to-pool"));
            var body = CommitBody(
                preview.RootElement.GetProperty("importToken").GetString()!,
                preview.RootElement.GetProperty("workbookSha256").GetString()!,
                [new
                {
                    rowKey = $"{LegacyWorkbookFixture.PlanningSheet}!3",
                    action = "create_batch_to_pool",
                    caseId = "case-1",
                    batchNumber = "B-POOL",
                    expectedCaseRoute = ExpectedRoute("case-operation-1", "case-operation-1b"),
                    allocations = new[] { new { type = "stock", quantity = 2 } }
                }]);

            using var response = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", body);
            var json = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, json);
            using var document = JsonDocument.Parse(json);
            Assert.Single(document.RootElement.GetProperty("created").GetProperty("batchIds").EnumerateArray());
            Assert.Equal(2, document.RootElement.GetProperty("created").GetProperty("batchOperationIds").GetArrayLength());
            Assert.Empty(document.RootElement.GetProperty("created").GetProperty("assignmentIds").EnumerateArray());
            Assert.Equal(2, document.RootElement.GetProperty("poolBatchOperationIds").GetArrayLength());
            Assert.Empty(document.RootElement.GetProperty("machineBacklogs").EnumerateArray());

            Assert.Equal(2, await ScalarAsync(application.Services, """
                SELECT COUNT(*) FROM batch_operations
                WHERE production_batch_id = (
                    SELECT id FROM production_batches WHERE batch_number = 'B-POOL');
                """));
            Assert.Equal(0, await ScalarAsync(application.Services, """
                SELECT COUNT(*) FROM machine_assignments
                WHERE batch_operation_id IN (
                    SELECT id FROM batch_operations
                    WHERE production_batch_id = (
                        SELECT id FROM production_batches WHERE batch_number = 'B-POOL'));
                """));

            using var boardResponse = await client.GetAsync("/api/v1/planning-board");
            Assert.Equal(HttpStatusCode.OK, boardResponse.StatusCode);
            using var board = JsonDocument.Parse(await boardResponse.Content.ReadAsStringAsync());
            Assert.Equal(2, board.RootElement.GetProperty("pool").EnumerateArray()
                .Count(operation => operation.GetProperty("batchNumber").GetString() == "B-POOL"));
            Assert.Equal(0, await ScalarAsync(application.Services,
                "SELECT backlog_position FROM machine_assignments WHERE id = 'existing-assignment';"));

            using var replayResponse = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", body);
            Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
            using var replay = JsonDocument.Parse(await replayResponse.Content.ReadAsStringAsync());
            Assert.True(replay.RootElement.GetProperty("replayed").GetBoolean());
            Assert.Empty(replay.RootElement.GetProperty("created").GetProperty("batchOperationIds").EnumerateArray());
            Assert.Equal(2, replay.RootElement.GetProperty("unchanged").GetProperty("batchOperationIds").GetArrayLength());
            Assert.Equal(2, replay.RootElement.GetProperty("poolBatchOperationIds").GetArrayLength());
            Assert.Equal(1, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM production_batches WHERE batch_number = 'B-POOL';"));
        });
    }

    [Fact]
    public async Task Batch_create_rejects_missing_or_stale_reviewed_case_route()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningAsync(application.Services);
            AddEditHeaders(client);
            using var preview = await PreviewAsync(client, LegacyWorkbookFixture.Create(marker: "route-review"));
            object Selection(object? expectedCaseRoute) => new
            {
                rowKey = $"{LegacyWorkbookFixture.PlanningSheet}!3",
                action = "create_batch_to_pool",
                caseId = "case-1",
                batchNumber = "B-ROUTE-REVIEW",
                expectedCaseRoute,
                allocations = new[] { new { type = "stock", quantity = 2 } }
            };

            var missing = CommitBody(
                preview.RootElement.GetProperty("importToken").GetString()!,
                preview.RootElement.GetProperty("workbookSha256").GetString()!,
                [Selection(null)]);
            using (var response = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", missing))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
                using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Contains(error.RootElement.GetProperty("error").GetProperty("details").EnumerateArray(), issue =>
                    issue.GetProperty("code").GetString() == "case_route_review_required");
            }

            await ExecuteAsync(application.Services,
                "UPDATE case_operations SET version = version + 1 WHERE id = 'case-operation-1';");
            var stale = CommitBody(
                preview.RootElement.GetProperty("importToken").GetString()!,
                preview.RootElement.GetProperty("workbookSha256").GetString()!,
                [Selection(ExpectedRoute("case-operation-1"))]);
            using (var response = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", stale))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
                using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Contains(error.RootElement.GetProperty("error").GetProperty("details").EnumerateArray(), issue =>
                    issue.GetProperty("code").GetString() == "case_route_changed");
            }
            Assert.Equal(0, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM production_batches WHERE batch_number = 'B-ROUTE-REVIEW';"));
        });
    }

    [Fact]
    public async Task Unknown_machine_section_can_be_approved_as_unassigned_pool_without_reordering()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningAsync(application.Services);
            AddEditHeaders(client);
            using var preview = await PreviewAsync(client, LegacyWorkbookFixture.Create(
                marker: "unknown-machine-pool",
                machineLabel: "Legacy workstation without a registered Machine"));
            Assert.Contains(preview.RootElement.GetProperty("issues").EnumerateArray(), issue =>
                issue.GetProperty("code").GetString() == "machine_mapping_required"
                && issue.GetProperty("severity").GetString() == "warning");
            var body = new
            {
                schemaVersion = 1,
                importToken = preview.RootElement.GetProperty("importToken").GetString(),
                workbookSha256 = preview.RootElement.GetProperty("workbookSha256").GetString(),
                planningSheet = LegacyWorkbookFixture.PlanningSheet,
                openOrdersSheet = LegacyWorkbookFixture.OpenOrdersSheet,
                columnMappings = Array.Empty<object>(),
                machineMappings = Array.Empty<object>(),
                openOrderSelections = Array.Empty<object>(),
                planningSelections = new object[]
                {
                    new
                    {
                        rowKey = $"{LegacyWorkbookFixture.PlanningSheet}!3",
                        action = "create_batch_to_pool",
                        caseId = "case-1",
                        batchNumber = "B-UNKNOWN-MACHINE",
                        expectedCaseRoute = ExpectedRoute("case-operation-1"),
                        allocations = new[] { new { type = "stock", quantity = 2 } }
                    }
                }
            };

            using var response = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", body);
            var json = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, json);
            Assert.Equal(0, await ScalarAsync(application.Services, """
                SELECT COUNT(*) FROM machine_assignments
                WHERE batch_operation_id IN (
                    SELECT id FROM batch_operations
                    WHERE production_batch_id = (
                        SELECT id FROM production_batches WHERE batch_number = 'B-UNKNOWN-MACHINE'));
                """));
            Assert.Equal(0, await ScalarAsync(application.Services,
                "SELECT backlog_position FROM machine_assignments WHERE id = 'existing-assignment';"));
        });
    }

    [Fact]
    public async Task Pool_action_rejects_stale_machine_values_without_mutating()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningAsync(application.Services);
            AddEditHeaders(client);
            using var preview = await PreviewAsync(client, LegacyWorkbookFixture.Create(marker: "pool-stale-machine"));
            var body = CommitBody(
                preview.RootElement.GetProperty("importToken").GetString()!,
                preview.RootElement.GetProperty("workbookSha256").GetString()!,
                [new
                {
                    rowKey = $"{LegacyWorkbookFixture.PlanningSheet}!3",
                    action = "create_batch_to_pool",
                    caseId = "case-1",
                    batchNumber = "B-SHOULD-NOT-EXIST",
                    expectedCaseRoute = ExpectedRoute("case-operation-1"),
                    allocations = new[] { new { type = "stock", quantity = 2 } },
                    machineId = "machine-01"
                }]);

            using var response = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains(error.RootElement.GetProperty("error").GetProperty("details").EnumerateArray(), issue =>
                issue.GetProperty("code").GetString() == "pool_assignment_forbidden");
            Assert.Equal(0, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM production_batches WHERE batch_number = 'B-SHOULD-NOT-EXIST';"));
        });
    }

    [Fact]
    public async Task Expanded_pool_rows_are_revalidated_and_rolled_back_atomically()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningAsync(application.Services);
            AddEditHeaders(client);
            using var preview = await PreviewAsync(client, LegacyWorkbookFixture.Create(marker: "pool-atomic"));
            var body = CommitBody(
                preview.RootElement.GetProperty("importToken").GetString()!,
                preview.RootElement.GetProperty("workbookSha256").GetString()!,
                [
                    new
                    {
                        rowKey = $"{LegacyWorkbookFixture.PlanningSheet}!3",
                        action = "create_batch_to_pool",
                        caseId = "case-1",
                        batchNumber = "B-ATOMIC-FIRST",
                        expectedCaseRoute = ExpectedRoute("case-operation-1"),
                        allocations = new[] { new { type = "stock", quantity = 2 } }
                    },
                    new
                    {
                        rowKey = $"{LegacyWorkbookFixture.PlanningSheet}!4",
                        action = "create_batch_to_pool",
                        caseId = "missing-case",
                        batchNumber = "B-ATOMIC-INVALID",
                        expectedCaseRoute = ExpectedRoute("case-operation-1"),
                        allocations = new[] { new { type = "stock", quantity = 3 } }
                    }
                ]);

            using var response = await client.PostAsJsonAsync("/api/v1/imports/legacy-working-plan/commit", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains(error.RootElement.GetProperty("error").GetProperty("details").EnumerateArray(), issue =>
                issue.GetProperty("code").GetString() is "case_route_required" or "case_not_found");
            Assert.Equal(0, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM production_batches WHERE batch_number LIKE 'B-ATOMIC-%';"));
            Assert.Equal(0, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM legacy_working_plan_imports WHERE workbook_sha256 = $hash;"
                    .Replace("$hash", $"'{preview.RootElement.GetProperty("workbookSha256").GetString()}'", StringComparison.Ordinal)));
        });
    }

    private static object[] ExpectedRoute(params string[] operationIds) => operationIds
        .Select(operationId => (object)new { caseOperationId = operationId, version = 1 })
        .ToArray();

    private static object CommitBody(string token, string hash, object[] planningSelections) => new
    {
        schemaVersion = 1,
        importToken = token,
        workbookSha256 = hash,
        planningSheet = LegacyWorkbookFixture.PlanningSheet,
        openOrdersSheet = LegacyWorkbookFixture.OpenOrdersSheet,
        columnMappings = Array.Empty<object>(),
        machineMappings = new[] { new { sectionKey = "תכנית ייצור!1", machineId = "machine-01" } },
        openOrderSelections = Array.Empty<object>(),
        planningSelections
    };

    private static object Planning(string rowKey, string operationId, string machineId, object? compatibilityOverride) => new
    {
        rowKey,
        action = "assign_existing_operation",
        batchOperationId = operationId,
        machineId,
        compatibilityOverride
    };

    private static object OpenOrderCreateBody(
        JsonDocument preview,
        string partNumber,
        string orderNumber,
        int quantity,
        string workFinishDate,
        object[]? columnMappings = null) => new
        {
            schemaVersion = 1,
            importToken = preview.RootElement.GetProperty("importToken").GetString(),
            workbookSha256 = preview.RootElement.GetProperty("workbookSha256").GetString(),
            planningSheet = (string?)null,
            openOrdersSheet = "Orders",
            columnMappings = columnMappings ?? Array.Empty<object>(),
            machineMappings = Array.Empty<object>(),
            openOrderSelections = new object[]
            {
                new
                {
                    rowKey = "Orders!2",
                    action = "create_case",
                    newCase = new
                    {
                        partNumber,
                        name = $"Imported {partNumber}",
                        workingFolderPath = Path.Combine(Path.GetTempPath(), partNumber)
                    },
                    order = new { orderNumber, quantity, workFinishDate }
                }
            },
            planningSelections = Array.Empty<object>()
        };

    private static async Task<JsonDocument> PreviewAsync(
        HttpClient client,
        byte[] workbook,
        string? planningSheet = null,
        string? openOrdersSheet = null,
        object? columnMappings = null)
    {
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent(workbook), "workbook", "working-plan.xlsx");
        if (!string.IsNullOrWhiteSpace(planningSheet))
        {
            multipart.Add(new StringContent(planningSheet), "planningSheet");
        }
        if (!string.IsNullOrWhiteSpace(openOrdersSheet))
        {
            multipart.Add(new StringContent(openOrdersSheet), "openOrdersSheet");
        }
        if (columnMappings is not null)
        {
            multipart.Add(new StringContent(JsonSerializer.Serialize(columnMappings)), "columnMappings");
        }
        using var response = await client.PostAsync("/api/v1/imports/legacy-working-plan/preview", multipart);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static void AddEditHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "legacy-import-client");
        client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "7");
    }

    private static async Task SeedPlanningAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id, calendar_json)
            VALUES ('calendar', 'Calendar', 'Asia/Jerusalem', '{}');
            INSERT INTO machines (
                id, number, name, machine_type, axis_type, capabilities_json,
                working_calendar_id, display_configuration_json, status, is_active, display_enabled)
            VALUES
                ('machine-01', '01', 'Machine 01', 'mill', '3-axis', '["probing"]', 'calendar', '{}', 'available', 1, 1),
                ('machine-manual', '99', 'Manual choice', 'mill', '3-axis', '[]', 'calendar', '{}', 'available', 1, 1);
            INSERT INTO cases (id, part_number, name, working_folder_path) VALUES
                ('case-1', 'PN-1', 'Part 1', 'C:\cases\1'),
                ('case-2', 'PN-2', 'Part 2', 'C:\cases\2'),
                ('case-3', 'PN-3', 'Part 3', 'C:\cases\3');
            INSERT INTO case_operations (
                id, case_id, operation_number, route_position, name, required_machine_type)
            VALUES
                ('case-operation-1', 'case-1', 10, 0, 'Op 1', 'lathe'),
                ('case-operation-2', 'case-2', 10, 0, 'Op 2', 'mill'),
                ('case-operation-3', 'case-3', 10, 0, 'Op 3', 'mill');
            INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity) VALUES
                ('batch-1', 'case-1', 'B1', 'waiting', 2),
                ('batch-2', 'case-2', 'B2', 'waiting', 3),
                ('batch-3', 'case-3', 'B3', 'waiting', 4);
            INSERT INTO batch_operations (
                id, production_batch_id, source_case_operation_id,
                operation_number, route_position, name, required_machine_type, status)
            VALUES
                ('batch-operation-1', 'batch-1', 'case-operation-1', 10, 0, 'Op 1', 'lathe', 'not_started'),
                ('batch-operation-2', 'batch-2', 'case-operation-2', 10, 0, 'Op 2', 'mill', 'not_started'),
                ('batch-operation-3', 'batch-3', 'case-operation-3', 10, 0, 'Op 3', 'mill', 'not_started');
            INSERT INTO machine_assignments (
                id, batch_operation_id, machine_id, backlog_position, planning_mode)
            VALUES ('existing-assignment', 'batch-operation-3', 'machine-01', 0, 'manual');
            UPDATE edit_tokens
            SET holder_client_id = 'legacy-import-client', holder_user_id = 'planner-user',
                generation = 7, acquired_at = '2026-08-15T00:00:00Z'
            WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(IServiceProvider services, string sql)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(IServiceProvider services, string sql)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunWithServerAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.LegacyImport.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directory, "test.db")}"],
            webHost => webHost.UseTestServer());
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

    private static WebApplication BuildServer(string databasePath) => ServerApplication.Build(
        ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={databasePath}"],
        webHost => webHost.UseTestServer());
}
