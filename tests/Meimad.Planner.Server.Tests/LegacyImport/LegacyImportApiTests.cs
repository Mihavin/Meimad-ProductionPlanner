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

    private static async Task<JsonDocument> PreviewAsync(HttpClient client, byte[] workbook)
    {
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent(workbook), "workbook", "working-plan.xlsx");
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
