using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.ProductionBatches;

public sealed class ProductionBatchApiTests
{
    [Fact]
    public async Task Create_is_blocked_until_the_case_has_an_operation_route()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningDataAsync(application.Services);
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var removeRoute = connection.CreateCommand())
            {
                removeRoute.CommandText = "DELETE FROM case_operations WHERE case_id = 'case-1';";
                await removeRoute.ExecuteNonQueryAsync();
            }

            using var response = await client.PostAsJsonAsync(
                "/api/v1/batches",
                StockBatchBody("B-NO-ROUTE", 5, 5));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var error = document.RootElement.GetProperty("error");
            Assert.Equal("case_operations_required", error.GetProperty("code").GetString());
            Assert.Contains("Create operations first", error.GetProperty("message").GetString());
            await using var verify = await database.OpenConnectionAsync();
            await using var count = verify.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM production_batches;";
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        });
    }

    [Fact]
    public async Task Create_and_read_batch_returns_allocations_and_instantiated_operations()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningDataAsync(application.Services);
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);

            using var createResponse = await client.PostAsJsonAsync(
                "/api/v1/batches",
                new
                {
                    caseId = "case-1",
                    batchNumber = "B-API-1",
                    status = "waiting",
                    plannedQuantity = 16,
                    allocations = new object[]
                    {
                        new { allocationType = "order", orderId = "order-1", quantity = 10 },
                        new { allocationType = "stock", orderId = (string?)null, quantity = 4 },
                        new { allocationType = "scrapAllowance", orderId = (string?)null, quantity = 2 }
                    }
                });

            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
            Assert.NotNull(createResponse.Headers.ETag);
            using var createDocument = JsonDocument.Parse(
                await createResponse.Content.ReadAsStringAsync());
            var batchId = createDocument.RootElement.GetProperty("batchId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(batchId));
            Assert.Equal(3, createDocument.RootElement.GetProperty("allocations").GetArrayLength());
            Assert.Equal(2, createDocument.RootElement.GetProperty("batchOperationCount").GetInt32());

            using var getResponse = await client.GetAsync($"/api/v1/batches/{batchId}");
            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

            using var operationsResponse = await client.GetAsync(
                $"/api/v1/batches/{batchId}/operations");
            Assert.Equal(HttpStatusCode.OK, operationsResponse.StatusCode);
            using var operationsDocument = JsonDocument.Parse(
                await operationsResponse.Content.ReadAsStringAsync());
            var operations = operationsDocument.RootElement.GetProperty("items");
            Assert.Equal(2, operations.GetArrayLength());
            Assert.Equal("Saw", operations[0].GetProperty("name").GetString());
            Assert.Equal("not_started", operations[0].GetProperty("status").GetString());
            Assert.Equal(("active", 1), await ReadOrderStatusAndVersionAsync(client, "order-1"));
        });
    }

    [Fact]
    public async Task Cancel_production_resets_done_parts_releases_resources_and_preserves_cycle_evidence()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningDataAsync(application.Services);
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);

            using var created = await client.PostAsJsonAsync("/api/v1/batches", new
            {
                caseId = "case-1",
                batchNumber = "B-CANCEL-PRODUCTION",
                status = "waiting",
                plannedQuantity = 5,
                allocations = new[] { new { allocationType = "order", orderId = "order-1", quantity = 5 } }
            });
            created.EnsureSuccessStatusCode();
            using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var batchId = createdJson.RootElement.GetProperty("batchId").GetString()!;
            var operationId = (await ReadOperationIdsAsync(client, batchId))[0];

            using var remainingCreated = await client.PostAsJsonAsync(
                "/api/v1/batches", StockBatchBody("B-REMAINS", 1, 1));
            remainingCreated.EnsureSuccessStatusCode();
            using var remainingJson = JsonDocument.Parse(await remainingCreated.Content.ReadAsStringAsync());
            var remainingOperationId = (await ReadOperationIdsAsync(
                client, remainingJson.RootElement.GetProperty("batchId").GetString()!))[0];

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO working_calendars(id,name,time_zone_id) VALUES('calendar-cancel','Day','UTC');
                    INSERT INTO machines(id,number,name,machine_type,working_calendar_id,status)
                    VALUES('machine-cancel','10','Mill','mill','calendar-cancel','active');
                    INSERT INTO production_runs(id,status,shared_setup_seconds,setup_snapshot_json,structure_locked_at,created_at,updated_at)
                    VALUES('run-cancel','PLANNED',0,'{}',NULL,$at,$at);
                    INSERT INTO production_run_programs(
                        id,production_run_id,manufacturing_program_id,sequence_position,target_cycle_count,
                        completed_cycle_count,status,legacy_unmanaged,created_at,updated_at)
                    VALUES('run-program-cancel','run-cancel','case-operation:case-op-10',0,5,3,'ACTIVE',1,$at,$at);
                    INSERT INTO production_run_outputs(
                        id,production_run_program_id,batch_operation_id,quantity_per_cycle,target_quantity,
                        produced_quantity,status,created_at,updated_at)
                    VALUES('run-output-cancel','run-program-cancel',$operationId,1,5,3,'IN_PRODUCTION',$at,$at);
                    UPDATE production_runs SET status='IN_PROGRESS',structure_locked_at=$at WHERE id='run-cancel';
                    INSERT INTO production_run_cycle_events(
                        id,production_run_id,production_run_program_id,source,source_event_id,observed_at,
                        completed_cycle_count,created_at,updated_at)
                    VALUES('cycle-evidence-cancel','run-cancel','run-program-cancel','test','cycle-3',$at,3,$at,$at);
                    INSERT INTO machine_assignments(
                        id,batch_operation_id,machine_id,backlog_position,planning_mode,production_run_id)
                    VALUES('assignment-cancel',$operationId,'machine-cancel',4,'manual','run-cancel');
                    INSERT INTO machine_assignments(
                        id,batch_operation_id,machine_id,backlog_position,planning_mode)
                    VALUES('assignment-remains',$remainingOperationId,'machine-cancel',9,'manual');
                    INSERT INTO verified_material_receipts(
                        id,case_id,quantity,received_at,verified_at,verified_by,created_at,updated_at)
                    VALUES('receipt-cancel','case-1',5,$at,$at,'planner',$at,$at);
                    INSERT INTO batch_material_reservations(
                        id,receipt_id,production_batch_id,quantity,reserved_at,reserved_by,created_at,updated_at)
                    VALUES('reservation-cancel','receipt-cancel',$batchId,5,$at,'planner',$at,$at);
                    INSERT INTO haas_bench_sessions(
                        id,batch_operation_id,machine_id,state,auto_start_source,machine_program_number,
                        machine_part_name,setup_started_at,production_started_at,part_counting_enabled,
                        produced_quantity,created_at,updated_at)
                    VALUES('bench-cancel',$operationId,'machine-cancel','PRODUCTION','CNC_HEADER','O1000',
                           'PART',$at,$at,1,3,$at,$at);
                    INSERT INTO haas_bench_state_intervals(id,bench_id,state,started_at,source)
                    VALUES('bench-interval-cancel','bench-cancel','PRODUCTION',$at,'test');
                    UPDATE production_batches SET status='in_production' WHERE id=$batchId;
                    UPDATE batch_operations SET status='in_progress',actual_start=$at WHERE id=$operationId;
                    """;
                seed.Parameters.AddWithValue("$at", "2026-09-02T08:00:00.0000000+00:00");
                seed.Parameters.AddWithValue("$batchId", batchId);
                seed.Parameters.AddWithValue("$operationId", operationId);
                seed.Parameters.AddWithValue("$remainingOperationId", remainingOperationId);
                await seed.ExecuteNonQueryAsync();
            }

            using var cancel = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/batches/{batchId}/cancel-production")
            {
                Content = JsonContent.Create(new { reason = "Test plan cancelled." })
            };
            cancel.Headers.TryAddWithoutValidation("If-Match", created.Headers.ETag!.ToString());
            using var response = await client.SendAsync(cancel);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var responseJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("cancelled", responseJson.RootElement.GetProperty("status").GetString());

            await using var verify = await database.OpenConnectionAsync();
            Assert.Equal("cancelled", await ScalarStringAsync(verify,
                "SELECT status FROM production_batches WHERE id=$id", batchId));
            Assert.Equal("cancelled", await ScalarStringAsync(verify,
                "SELECT status FROM batch_operations WHERE id=$id", operationId));
            Assert.Equal("CANCELLED|0", await ScalarStringAsync(verify,
                "SELECT status||'|'||completed_cycle_count FROM production_run_programs WHERE id=$id", "run-program-cancel"));
            Assert.Equal("ABORTED_REMAINDER_RELEASED|0", await ScalarStringAsync(verify,
                "SELECT status||'|'||produced_quantity FROM production_run_outputs WHERE id=$id", "run-output-cancel"));
            Assert.Equal("COMPLETED|0|0", await ScalarStringAsync(verify,
                "SELECT state||'|'||part_counting_enabled||'|'||produced_quantity FROM haas_bench_sessions WHERE id=$id", "bench-cancel"));
            Assert.Equal(1L, await ScalarInt64Async(verify,
                "SELECT COUNT(*) FROM production_run_cycle_events WHERE id=$id", "cycle-evidence-cancel"));
            Assert.Equal(0L, await ScalarInt64Async(verify,
                "SELECT COUNT(*) FROM machine_assignments WHERE id=$id", "assignment-cancel"));
            Assert.Equal(0L, await ScalarInt64Async(verify,
                "SELECT backlog_position FROM machine_assignments WHERE id=$id", "assignment-remains"));
            Assert.Equal(0L, await ScalarInt64Async(verify,
                "SELECT COUNT(*) FROM batch_material_reservations WHERE production_batch_id=$id", batchId));
            Assert.Equal("active", (await ReadOrderStatusAndVersionAsync(client, "order-1")).Status);
        });
    }

    private static async Task<string> ScalarStringAsync(
        SqliteConnection connection, string sql, string id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> ScalarInt64Async(
        SqliteConnection connection, string sql, string id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Patch_updates_batch_and_allocations_without_recreating_route()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningDataAsync(application.Services);
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            using var created = await client.PostAsJsonAsync("/api/v1/batches", StockBatchBody("B-EDIT", 5, 5));
            created.EnsureSuccessStatusCode();
            using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var batchId = createdJson.RootElement.GetProperty("batchId").GetString()!;
            var operationIdsBefore = await ReadOperationIdsAsync(client, batchId);

            using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/batches/{batchId}")
            {
                Content = JsonContent.Create(new
                {
                    batchNumber = "B-EDITED",
                    plannedQuantity = 8,
                    allocations = new[] { new { allocationType = "stock", orderId = (string?)null, quantity = 8 } }
                })
            };
            request.Headers.TryAddWithoutValidation("If-Match", created.Headers.ETag!.ToString());
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal($"\"batch:{batchId}:v2\"", response.Headers.ETag!.ToString());
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("B-EDITED", document.RootElement.GetProperty("batchNumber").GetString());
            Assert.Equal(8, document.RootElement.GetProperty("plannedQuantity").GetInt32());
            Assert.Equal(2, document.RootElement.GetProperty("batchOperationCount").GetInt32());
            Assert.Equal(operationIdsBefore, await ReadOperationIdsAsync(client, batchId));

            using var stale = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/batches/{batchId}")
            {
                Content = JsonContent.Create(new { batchNumber = "STALE", plannedQuantity = 8, allocations = new[] { new { allocationType = "stock", orderId = (string?)null, quantity = 8 } } })
            };
            stale.Headers.TryAddWithoutValidation("If-Match", created.Headers.ETag!.ToString());
            Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(stale)).StatusCode);
        });
    }

    private static async Task<string[]> ReadOperationIdsAsync(HttpClient client, string batchId)
    {
        using var response = await client.GetAsync($"/api/v1/batches/{batchId}/operations");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items").EnumerateArray().Select(value => value.GetProperty("batchOperationId").GetString()!).ToArray();
    }

    [Fact]
    public async Task Adversarial_requests_reject_mismatch_cross_case_and_missing_edit_mode()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningDataAsync(application.Services);

            using var noEditResponse = await client.PostAsJsonAsync(
                "/api/v1/batches",
                StockBatchBody("B-NO-EDIT", 5, 5));
            Assert.Equal((HttpStatusCode)428, noEditResponse.StatusCode);

            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            using var mismatchResponse = await client.PostAsJsonAsync(
                "/api/v1/batches",
                StockBatchBody("B-MISMATCH", 6, 5));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatchResponse.StatusCode);

            using var crossCaseResponse = await client.PostAsJsonAsync(
                "/api/v1/batches",
                new
                {
                    caseId = "case-1",
                    batchNumber = "B-CROSS-CASE",
                    status = "waiting",
                    plannedQuantity = 5,
                    allocations = new[]
                    {
                        new
                        {
                            allocationType = "order",
                            orderId = "foreign-order",
                            quantity = 5
                        }
                    }
                });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, crossCaseResponse.StatusCode);

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM production_batches;";
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        });
    }

    [Fact]
    public async Task Create_recomputes_preexisting_complete_status()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningDataAsync(application.Services);
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO orders (
                        id, case_id, order_reference, quantity, work_finish_date, status)
                    VALUES ('order-precomplete', 'case-1', 'WO-COMPLETE', 5, '2026-08-20', 'complete');
                    """;
                await seed.ExecuteNonQueryAsync();
            }

            using var response = await client.PostAsJsonAsync(
                "/api/v1/batches",
                new
                {
                    caseId = "case-1",
                    batchNumber = "B-RECOMPUTE",
                    status = "waiting",
                    plannedQuantity = 5,
                    allocations = new object[]
                    {
                        new
                        {
                            allocationType = "order",
                            orderId = "order-precomplete",
                            quantity = 5
                        }
                    }
                });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            Assert.Equal(("active", 2), await ReadOrderStatusAndVersionAsync(
                client,
                "order-precomplete"));
        });
    }

    [Fact]
    public async Task Create_rejects_cancelled_order_allocation_without_writes()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedPlanningDataAsync(application.Services);
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO orders (
                        id, case_id, order_reference, quantity, work_finish_date, status)
                    VALUES ('order-cancelled', 'case-1', 'WO-CANCELLED', 5, '2026-08-20', 'cancelled');
                    """;
                await seed.ExecuteNonQueryAsync();
            }

            using var response = await client.PostAsJsonAsync(
                "/api/v1/batches",
                new
                {
                    caseId = "case-1",
                    batchNumber = "B-CANCELLED",
                    status = "waiting",
                    plannedQuantity = 5,
                    allocations = new[]
                    {
                        new
                        {
                            allocationType = "order",
                            orderId = "order-cancelled",
                            quantity = 5
                        }
                    }
                });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains(
                document.RootElement.GetProperty("error").GetProperty("details").EnumerateArray(),
                detail => detail.GetProperty("code").GetString() == "cancelled_order");

            await using var verify = await database.OpenConnectionAsync();
            await using var count = verify.CreateCommand();
            count.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM production_batches WHERE batch_number = 'B-CANCELLED')
                    + (SELECT COUNT(*) FROM batch_allocations WHERE order_id = 'order-cancelled');
                """;
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
            Assert.Equal(("cancelled", 1), await ReadOrderStatusAndVersionAsync(
                client,
                "order-cancelled"));
        });
    }

    private static object StockBatchBody(
        string batchNumber,
        int plannedQuantity,
        int stockQuantity) => new
        {
            caseId = "case-1",
            batchNumber,
            status = "waiting",
            plannedQuantity,
            allocations = new[]
        {
            new
            {
                allocationType = "stock",
                orderId = (string?)null,
                quantity = stockQuantity
            }
        }
        };

    private static void AddEditHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "batch-api-client");
        client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
    }

    private static async Task<(string Status, int Version)> ReadOrderStatusAndVersionAsync(
        HttpClient client,
        string orderId)
    {
        using var response = await client.GetAsync($"/api/v1/orders/{orderId}");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (
            document.RootElement.GetProperty("status").GetString()!,
            document.RootElement.GetProperty("version").GetInt32());
    }

    private static async Task SeedPlanningDataAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES
                ('case-1', 'PN-1', 'Case One', 'C:\Cases\PN-1'),
                ('case-2', 'PN-2', 'Case Two', 'C:\Cases\PN-2');

            INSERT INTO orders (
                id, case_id, order_reference, quantity, work_finish_date, status)
            VALUES
                ('order-1', 'case-1', 'WO-1', 20, '2026-08-20', 'active'),
                ('foreign-order', 'case-2', 'WO-2', 20, '2026-08-20', 'active');

            INSERT INTO case_operations (
                id, case_id, operation_number, route_position, name,
                required_machine_type, setup_seconds, cycle_seconds)
            VALUES
                ('case-op-10', 'case-1', 10, 0, 'Saw', 'saw', 120, 30),
                ('case-op-20', 'case-1', 20, 1, 'Mill', 'mill', 600, 300);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task GrantEditModeAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE edit_tokens
            SET holder_client_id = 'batch-api-client',
                holder_user_id = 'batch-api-user',
                generation = 1,
                acquired_at = '2026-08-11T00:00:00Z',
                version = version + 1,
                updated_at = '2026-08-11T00:00:00Z'
            WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunWithServerAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            "MeimadPlanner.BatchApi.Tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directoryPath, "api-test.db");
        var application = ServerApplication.Build(
            [
                "--Server:Host=127.0.0.1",
                "--Server:Port=5099",
                $"--Database:Path={databasePath}"
            ],
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
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }
}
