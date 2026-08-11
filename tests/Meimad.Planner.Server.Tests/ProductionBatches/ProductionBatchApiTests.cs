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
                    status = "planned",
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
        });
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
                    status = "planned",
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

    private static object StockBatchBody(
        string batchNumber,
        int plannedQuantity,
        int stockQuantity) => new
        {
            caseId = "case-1",
            batchNumber,
            status = "planned",
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
