using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Orders;

public sealed class OrderApiTests
{
    [Fact]
    public async Task Create_read_list_and_patch_order_updates_derived_case_activity()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            var caseId = await CreateCaseAsync(client);

            using var createResponse = await client.PostAsJsonAsync(
                "/api/v1/orders",
                ValidCreateBody(caseId));
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
            var firstEntityTag = createResponse.Headers.ETag?.ToString();
            Assert.False(string.IsNullOrWhiteSpace(firstEntityTag));

            using var createdDocument = JsonDocument.Parse(
                await createResponse.Content.ReadAsStringAsync());
            var orderId = createdDocument.RootElement.GetProperty("orderId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(orderId));
            Assert.Equal("WO-API-1042", createdDocument.RootElement.GetProperty("orderNumber").GetString());
            Assert.False(createdDocument.RootElement.TryGetProperty("machineId", out _));
            Assert.False(createdDocument.RootElement.TryGetProperty("machineAssignment", out _));

            using var caseWithDemand = await client.GetAsync($"/api/v1/cases/{caseId}");
            using var caseWithDemandDocument = JsonDocument.Parse(
                await caseWithDemand.Content.ReadAsStringAsync());
            Assert.True(caseWithDemandDocument.RootElement.GetProperty("isActive").GetBoolean());

            using var listResponse = await client.GetAsync($"/api/v1/orders?caseId={caseId}");
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
            using var listDocument = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
            Assert.Single(listDocument.RootElement.GetProperty("items").EnumerateArray());

            using var patchRequest = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/orders/{orderId}")
            {
                Content = JsonContent.Create(new
                {
                    quantity = 60,
                    status = "cancelled",
                    notes = "Demand cancelled"
                })
            };
            patchRequest.Headers.TryAddWithoutValidation("If-Match", firstEntityTag);
            using var patchResponse = await client.SendAsync(patchRequest);
            Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

            using var getResponse = await client.GetAsync($"/api/v1/orders/{orderId}");
            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
            using var orderDocument = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
            Assert.Equal(60, orderDocument.RootElement.GetProperty("quantity").GetInt32());
            Assert.Equal("cancelled", orderDocument.RootElement.GetProperty("status").GetString());
            Assert.Equal(2, orderDocument.RootElement.GetProperty("version").GetInt32());

            using var caseWithoutDemand = await client.GetAsync($"/api/v1/cases/{caseId}");
            using var caseWithoutDemandDocument = JsonDocument.Parse(
                await caseWithoutDemand.Content.ReadAsStringAsync());
            Assert.False(caseWithoutDemandDocument.RootElement.GetProperty("isActive").GetBoolean());
        });
    }

    [Fact]
    public async Task Create_rejects_invalid_demand_and_missing_parent()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);

            using var invalidResponse = await client.PostAsJsonAsync(
                "/api/v1/orders",
                new
                {
                    caseId = "missing-case",
                    orderNumber = "WO-BAD",
                    quantity = 0,
                    workFinishDate = "08/20/2026",
                    status = "ACTIVE"
                });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidResponse.StatusCode);

            using var orphanResponse = await client.PostAsJsonAsync(
                "/api/v1/orders",
                ValidCreateBody("missing-case"));
            Assert.Equal(HttpStatusCode.NotFound, orphanResponse.StatusCode);
        });
    }

    [Fact]
    public async Task Mutation_requires_active_edit_mode()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedCaseAsync(application.Services, "case-no-edit");
            using var createResponse = await client.PostAsJsonAsync(
                "/api/v1/orders",
                ValidCreateBody("case-no-edit"));
            Assert.Equal((HttpStatusCode)428, createResponse.StatusCode);
        });
    }

    private static object ValidCreateBody(string caseId) => new
    {
        caseId,
        orderNumber = "WO-API-1042",
        quantity = 50,
        workFinishDate = "2026-08-20",
        status = "active",
        notes = "API demand"
    };

    private static async Task<string> CreateCaseAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/cases",
            new
            {
                partNumber = "PN-ORDER-API",
                name = "Order API parent",
                workingFolderPath = Path.Combine(Path.GetTempPath(), "meimad-order-api-case")
            });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("caseId").GetString()!;
    }

    private static void AddEditHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "order-api-test-client");
        client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
    }

    private static async Task GrantEditModeAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE edit_tokens
            SET holder_client_id = 'order-api-test-client',
                holder_user_id = 'order-api-test-user',
                generation = 1,
                acquired_at = '2026-08-11T00:00:00Z',
                version = version + 1,
                updated_at = '2026-08-11T00:00:00Z'
            WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedCaseAsync(IServiceProvider services, string caseId)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ($caseId, 'PN-NO-EDIT', 'No edit parent', 'C:\Cases\PN-NO-EDIT');
            """;
        command.Parameters.AddWithValue("$caseId", caseId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunWithServerAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            "MeimadPlanner.OrderApi.Tests",
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
