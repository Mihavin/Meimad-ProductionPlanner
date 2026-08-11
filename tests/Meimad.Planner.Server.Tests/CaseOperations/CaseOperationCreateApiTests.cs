using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.CaseOperations;

public sealed class CaseOperationCreateApiTests
{
    [Fact]
    public async Task Creates_ordered_route_and_does_not_retrofit_existing_batch_snapshot()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            var caseId = await CreateCaseAsync(client);

            var first = await CreateOperationAsync(client, caseId, new
            {
                operationNumber = 10,
                name = "Saw",
                requiredMachineType = "saw",
                setupTimeSeconds = 60,
                cycleTimePerPartSeconds = 30,
                dependencyType = "INDEPENDENT",
                predecessorCaseOperationId = (string?)null,
                simultaneousGroupKey = (string?)null
            });
            Assert.Equal(0, first.GetProperty("routePosition").GetInt32());
            var firstId = first.GetProperty("caseOperationId").GetString()!;

            using var orderResponse = await client.PostAsJsonAsync("/api/v1/orders", new
            {
                caseId,
                orderNumber = "SO-1",
                quantity = 5,
                workFinishDate = "2026-09-30",
                status = "active",
                notes = (string?)null
            });
            using var orderDocument = JsonDocument.Parse(
                await orderResponse.Content.ReadAsStringAsync());
            var orderId = orderDocument.RootElement.GetProperty("orderId").GetString()!;
            using var batchResponse = await client.PostAsJsonAsync("/api/v1/batches", new
            {
                caseId,
                batchNumber = "B-1",
                status = "planned",
                plannedQuantity = 5,
                allocations = new[]
                {
                    new { allocationType = "order", orderId, quantity = 5 }
                }
            });
            using var batchDocument = JsonDocument.Parse(
                await batchResponse.Content.ReadAsStringAsync());
            var batchId = batchDocument.RootElement.GetProperty("batchId").GetString()!;

            var second = await CreateOperationAsync(client, caseId, new
            {
                operationNumber = 20,
                name = "Finish mill",
                requiredMachineType = "fiveAxisMill",
                setupTimeSeconds = 120,
                cycleTimePerPartSeconds = 45,
                dependencyType = "SEQUENTIAL",
                predecessorCaseOperationId = firstId,
                simultaneousGroupKey = (string?)null
            });
            Assert.Equal(1, second.GetProperty("routePosition").GetInt32());
            Assert.Equal(firstId, second.GetProperty("predecessorCaseOperationId").GetString());

            using var routeResponse = await client.GetAsync($"/api/v1/cases/{caseId}/operations");
            using var routeDocument = JsonDocument.Parse(
                await routeResponse.Content.ReadAsStringAsync());
            Assert.Equal(2, routeDocument.RootElement.GetProperty("items").GetArrayLength());

            using var snapshotResponse = await client.GetAsync($"/api/v1/batches/{batchId}/operations");
            using var snapshotDocument = JsonDocument.Parse(
                await snapshotResponse.Content.ReadAsStringAsync());
            Assert.Single(snapshotDocument.RootElement.GetProperty("items").EnumerateArray());
        });
    }

    [Fact]
    public async Task Rejects_invalid_reference_and_duplicate_number_without_partial_insert()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            var caseId = await CreateCaseAsync(client);
            await CreateOperationAsync(client, caseId, new
            {
                operationNumber = 10,
                name = "Saw",
                dependencyType = "INDEPENDENT"
            });

            using var invalidReference = await client.PostAsJsonAsync(
                $"/api/v1/cases/{caseId}/operations",
                new
                {
                    operationNumber = 20,
                    name = "Invalid predecessor",
                    dependencyType = "SEQUENTIAL",
                    predecessorCaseOperationId = "not-in-this-case"
                });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidReference.StatusCode);
            Assert.Contains("invalid_reference", await invalidReference.Content.ReadAsStringAsync());

            using var duplicate = await client.PostAsJsonAsync(
                $"/api/v1/cases/{caseId}/operations",
                new
                {
                    operationNumber = 10,
                    name = "Duplicate number",
                    dependencyType = "INDEPENDENT"
                });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
            Assert.Contains("duplicate_operation_number", await duplicate.Content.ReadAsStringAsync());

            using var routeResponse = await client.GetAsync($"/api/v1/cases/{caseId}/operations");
            using var routeDocument = JsonDocument.Parse(await routeResponse.Content.ReadAsStringAsync());
            Assert.Single(routeDocument.RootElement.GetProperty("items").EnumerateArray());
        });
    }

    [Fact]
    public async Task Requires_active_edit_generation()
    {
        await RunWithServerAsync(async (_, client) =>
        {
            using var response = await client.PostAsJsonAsync(
                "/api/v1/cases/missing/operations",
                new { operationNumber = 10, name = "Saw", dependencyType = "INDEPENDENT" });
            Assert.Equal((HttpStatusCode)428, response.StatusCode);
        });
    }

    private static async Task<JsonElement> CreateOperationAsync(
        HttpClient client,
        string caseId,
        object body)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/cases/{caseId}/operations",
            body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<string> CreateCaseAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/cases", new
        {
            partNumber = "PN-OP-1",
            name = "Operation API Case",
            workingFolderPath = Path.Combine(Path.GetTempPath(), "PN-OP-1")
        });
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("caseId").GetString()!;
    }

    private static void AddEditHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "operation-api-client");
        client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
    }

    private static async Task GrantEditModeAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE edit_tokens
            SET holder_client_id = 'operation-api-client',
                holder_user_id = 'operation-api-user',
                generation = 1,
                acquired_at = '2026-08-11T00:00:00Z',
                version = version + 1,
                updated_at = '2026-08-11T00:00:00Z'
            WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunWithServerAsync(
        Func<WebApplication, HttpClient, Task> test)
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            "MeimadPlanner.CaseOperation.Api.Tests",
            Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            [
                "--Server:Host=127.0.0.1",
                "--Server:Port=5099",
                $"--Database:Path={Path.Combine(directoryPath, "api-test.db")}"
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
