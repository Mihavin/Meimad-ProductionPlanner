using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Cases;

public sealed class CaseApiTests
{
    [Fact]
    public async Task Create_read_and_patch_case_over_http()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);

            using var createResponse = await client.PostAsJsonAsync(
                "/api/v1/cases",
                ValidCreateBody());
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
            Assert.NotNull(createResponse.Headers.ETag);

            using var createDocument = JsonDocument.Parse(
                await createResponse.Content.ReadAsStringAsync());
            var caseId = createDocument.RootElement.GetProperty("caseId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(caseId));
            Assert.Equal("Customer A", createDocument.RootElement.GetProperty("customer").GetString());

            using var getResponse = await client.GetAsync($"/api/v1/cases/{caseId}");
            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
            var originalEntityTag = getResponse.Headers.ETag?.ToString();
            Assert.False(string.IsNullOrWhiteSpace(originalEntityTag));

            using var patchRequest = new HttpRequestMessage(
                HttpMethod.Patch,
                $"/api/v1/cases/{caseId}")
            {
                Content = JsonContent.Create(new
                {
                    customer = "Customer B",
                    notes = "Changed over the API"
                })
            };
            patchRequest.Headers.TryAddWithoutValidation("If-Match", originalEntityTag);
            using var patchResponse = await client.SendAsync(patchRequest);

            Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
            Assert.NotEqual(originalEntityTag, patchResponse.Headers.ETag?.ToString());
            using var patchDocument = JsonDocument.Parse(
                await patchResponse.Content.ReadAsStringAsync());
            Assert.Equal("Customer B", patchDocument.RootElement.GetProperty("customer").GetString());
            Assert.Equal(2, patchDocument.RootElement.GetProperty("version").GetInt32());
        });
    }

    [Fact]
    public async Task Create_rejects_missing_working_folder_path()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);

            using var response = await client.PostAsJsonAsync(
                "/api/v1/cases",
                new
                {
                    partNumber = "PN-MISSING-PATH",
                    name = "Missing path"
                });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(
                "validation_failed",
                document.RootElement.GetProperty("error").GetProperty("code").GetString());
        });
    }

    [Fact]
    public async Task Mutation_rejects_client_without_active_edit_generation()
    {
        await RunWithServerAsync(async (_, client) =>
        {
            using var response = await client.PostAsJsonAsync(
                "/api/v1/cases",
                ValidCreateBody());

            Assert.Equal((HttpStatusCode)428, response.StatusCode);
        });
    }

    [Fact]
    public async Task Case_workspace_queries_filters_tabs_and_serves_preview_over_api()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            var previewPath = Path.Combine(Path.GetTempPath(), $"meimad-preview-{Guid.NewGuid():N}.png");
            var previewBytes = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            await File.WriteAllBytesAsync(previewPath, previewBytes);

            try
            {
                using var createResponse = await client.PostAsJsonAsync(
                    "/api/v1/cases",
                    CreateBody("PN-POOL-1", "Acme Aerospace", previewPath));
                using var createDocument = JsonDocument.Parse(
                    await createResponse.Content.ReadAsStringAsync());
                var caseId = createDocument.RootElement.GetProperty("caseId").GetString()!;
                await SeedWorkspaceRowsAsync(application.Services, caseId);

                using var listResponse = await client.GetAsync(
                    "/api/v1/cases?search=POOL&customer=Aerospace&isActive=true");
                Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
                using var listDocument = JsonDocument.Parse(
                    await listResponse.Content.ReadAsStringAsync());
                Assert.Single(listDocument.RootElement.GetProperty("items").EnumerateArray());
                Assert.True(listDocument.RootElement.GetProperty("items")[0].GetProperty("isActive").GetBoolean());

                using var operationResponse = await client.GetAsync(
                    $"/api/v1/cases/{caseId}/operations");
                using var operationDocument = JsonDocument.Parse(
                    await operationResponse.Content.ReadAsStringAsync());
                Assert.Equal(
                    "SEQUENTIAL",
                    operationDocument.RootElement.GetProperty("items")[0]
                        .GetProperty("dependencyType").GetString());

                using var batchResponse = await client.GetAsync($"/api/v1/batches?caseId={caseId}");
                using var batchDocument = JsonDocument.Parse(
                    await batchResponse.Content.ReadAsStringAsync());
                Assert.Equal(
                    "B-POOL-1",
                    batchDocument.RootElement.GetProperty("items")[0]
                        .GetProperty("batchNumber").GetString());

                using var previewResponse = await client.GetAsync($"/api/v1/cases/{caseId}/preview");
                Assert.Equal("image/png", previewResponse.Content.Headers.ContentType?.MediaType);
                Assert.Equal(previewBytes, await previewResponse.Content.ReadAsByteArrayAsync());
            }
            finally
            {
                File.Delete(previewPath);
            }
        });
    }

    private static object ValidCreateBody() => new
    {
        partNumber = "PN-API-100",
        name = "API bearing housing",
        revision = "A",
        customer = "Customer A",
        customerReference = "PO-API-1",
        previewPath = Path.Combine(Path.GetTempPath(), "meimad-api-preview.png"),
        workingFolderPath = Path.Combine(Path.GetTempPath(), "meimad-api-case"),
        materialType = "Aluminium",
        materialSpecification = "7075-T6",
        rawMaterialForm = "Plate",
        rawMaterialDimensions = "30 x 120 x 180 mm",
        notes = "API test"
    };

    private static object CreateBody(string partNumber, string customer, string previewPath) => new
    {
        partNumber,
        name = "Pool test part",
        customer,
        previewPath,
        workingFolderPath = Path.Combine(Path.GetTempPath(), partNumber)
    };

    private static async Task SeedWorkspaceRowsAsync(IServiceProvider services, string caseId)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO case_operations (
                id, case_id, operation_number, route_position, name,
                dependency_type, version, created_at, updated_at)
            VALUES (
                'operation-pool-1', $caseId, 10, 0, 'Saw',
                'sequential', 1, '2026-08-11T00:00:00Z', '2026-08-11T00:00:00Z');

            INSERT INTO production_batches (
                id, case_id, batch_number, status, planned_quantity,
                version, created_at, updated_at)
            VALUES (
                'batch-pool-1', $caseId, 'B-POOL-1', 'waiting', 1,
                1, '2026-08-11T00:00:00Z', '2026-08-11T00:00:00Z');
            """;
        command.Parameters.AddWithValue("$caseId", caseId);
        await command.ExecuteNonQueryAsync();
    }

    private static void AddEditHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "case-api-test-client");
        client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
    }

    private static async Task GrantEditModeAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE edit_tokens
            SET holder_client_id = 'case-api-test-client',
                holder_user_id = 'case-api-test-user',
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
            "MeimadPlanner.Api.Tests",
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
