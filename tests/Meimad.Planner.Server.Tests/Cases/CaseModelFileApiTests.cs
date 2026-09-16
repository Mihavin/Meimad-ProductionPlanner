using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Cases;

public sealed class CaseModelFileApiTests
{
    [Fact]
    public async Task Model_files_are_created_listed_updated_and_deleted_with_primary_bookkeeping()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            AddHeaders(client);

            using var partResponse = await client.PostAsJsonAsync(
                "/api/v1/cases/case-1/model-files",
                new { filePath = @"\\factory\cad\PN-1\part.stp" });
            Assert.Equal(HttpStatusCode.Created, partResponse.StatusCode);
            using var partJson = JsonDocument.Parse(await partResponse.Content.ReadAsStringAsync());
            var partId = partJson.RootElement.GetProperty("caseModelFileId").GetString()!;
            Assert.Equal("part", partJson.RootElement.GetProperty("kind").GetString());
            Assert.Equal("step", partJson.RootElement.GetProperty("format").GetString());
            Assert.Equal("part.stp", partJson.RootElement.GetProperty("label").GetString());
            Assert.True(partJson.RootElement.GetProperty("isPrimary").GetBoolean());
            Assert.Equal($"\"case-model-file:{partId}:v1\"", partResponse.Headers.ETag?.Tag);

            using var stockResponse = await client.PostAsJsonAsync(
                "/api/v1/cases/case-1/model-files",
                new { filePath = @"C:\cad\PN-1\rest-stock.STL", kind = "Stock", label = "  Rest material  ", caseOperationId = "case-op-1" });
            Assert.Equal(HttpStatusCode.Created, stockResponse.StatusCode);
            using var stockJson = JsonDocument.Parse(await stockResponse.Content.ReadAsStringAsync());
            var stockId = stockJson.RootElement.GetProperty("caseModelFileId").GetString()!;
            Assert.Equal("stock", stockJson.RootElement.GetProperty("kind").GetString());
            Assert.Equal("stl", stockJson.RootElement.GetProperty("format").GetString());
            Assert.Equal("Rest material", stockJson.RootElement.GetProperty("label").GetString());
            Assert.Equal("case-op-1", stockJson.RootElement.GetProperty("caseOperationId").GetString());
            Assert.False(stockJson.RootElement.GetProperty("isPrimary").GetBoolean());
            Assert.Equal(1, stockJson.RootElement.GetProperty("sortOrder").GetInt32());

            using var list = await client.GetAsync("/api/v1/cases/case-1/model-files");
            list.EnsureSuccessStatusCode();
            using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            var items = listJson.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(2, items.Length);
            Assert.Equal(partId, items[0].GetProperty("caseModelFileId").GetString());

            // Promoting the stock file demotes the previous primary.
            using (var promote = Patch(stockId, 1, new { isPrimary = true, label = "Rest stock OP10" }))
            using (var promoted = await client.SendAsync(promote))
            {
                Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
                using var promotedJson = JsonDocument.Parse(await promoted.Content.ReadAsStringAsync());
                Assert.True(promotedJson.RootElement.GetProperty("isPrimary").GetBoolean());
                Assert.Equal(2, promotedJson.RootElement.GetProperty("version").GetInt32());
                Assert.Equal("Rest stock OP10", promotedJson.RootElement.GetProperty("label").GetString());
                Assert.Equal($"\"case-model-file:{stockId}:v2\"", promoted.Headers.ETag?.Tag);
            }

            using (var afterPromotion = await client.GetAsync("/api/v1/cases/case-1/model-files"))
            {
                using var afterJson = JsonDocument.Parse(await afterPromotion.Content.ReadAsStringAsync());
                var part = afterJson.RootElement.GetProperty("items").EnumerateArray()
                    .Single(item => item.GetProperty("caseModelFileId").GetString() == partId);
                Assert.False(part.GetProperty("isPrimary").GetBoolean());
                Assert.Equal(2, part.GetProperty("version").GetInt32());
            }

            using (var stale = Patch(stockId, 1, new { label = "stale" }))
            using (var staleResponse = await client.SendAsync(stale))
            {
                Assert.Equal(HttpStatusCode.PreconditionFailed, staleResponse.StatusCode);
            }

            using (var missingTag = await client.PatchAsJsonAsync(
                       $"/api/v1/cases/case-1/model-files/{stockId}", new { label = "no tag" }))
            {
                Assert.Equal((HttpStatusCode)428, missingTag.StatusCode);
            }

            using (var detach = Patch(stockId, 2, new { clearCaseOperation = true }))
            using (var detached = await client.SendAsync(detach))
            {
                Assert.Equal(HttpStatusCode.OK, detached.StatusCode);
                using var detachedJson = JsonDocument.Parse(await detached.Content.ReadAsStringAsync());
                Assert.Equal(JsonValueKind.Null, detachedJson.RootElement.GetProperty("caseOperationId").ValueKind);
            }

            Assert.Equal(HttpStatusCode.NoContent,
                (await client.DeleteAsync($"/api/v1/cases/case-1/model-files/{stockId}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.DeleteAsync($"/api/v1/cases/case-1/model-files/{stockId}")).StatusCode);

            using var events = await client.GetAsync("/api/v1/event-log?eventType=case_model_file_added");
            events.EnsureSuccessStatusCode();
            using var eventsJson = JsonDocument.Parse(await events.Content.ReadAsStringAsync());
            Assert.Equal(2, eventsJson.RootElement.GetProperty("items").EnumerateArray().Count());
        });
    }

    [Fact]
    public async Task Model_file_validation_rejects_bad_input()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);

            using (var noHeaders = await client.PostAsJsonAsync(
                       "/api/v1/cases/case-1/model-files", new { filePath = @"C:\cad\part.stp" }))
            {
                Assert.Equal((HttpStatusCode)428, noHeaders.StatusCode);
            }

            AddHeaders(client);
            await AssertValidationAsync(client, "/api/v1/cases/case-1/model-files",
                new { filePath = @"C:\cad\drawing.pdf" }, "filePath", "unsupported_format");
            await AssertValidationAsync(client, "/api/v1/cases/case-1/model-files",
                new { filePath = @"C:\cad\part.stp", kind = "tooling" }, "kind", "invalid_value");
            await AssertValidationAsync(client, "/api/v1/cases/case-1/model-files",
                new { filePath = @"C:\cad\part.stp", caseOperationId = "case-op-other" }, "caseOperationId", "operation_not_in_case");
            await AssertValidationAsync(client, "/api/v1/cases/case-1/model-files",
                new { filePath = "   " }, "filePath", "required");

            using var unknownCase = await client.PostAsJsonAsync(
                "/api/v1/cases/no-such-case/model-files", new { filePath = @"C:\cad\part.stp" });
            Assert.Equal(HttpStatusCode.NotFound, unknownCase.StatusCode);

            using var unknownList = await client.GetAsync("/api/v1/cases/no-such-case/model-files");
            Assert.Equal(HttpStatusCode.NotFound, unknownList.StatusCode);
        });
    }

    [Fact]
    public async Task Deleting_the_operation_detaches_and_deleting_the_case_removes_model_files()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            AddHeaders(client);

            using var created = await client.PostAsJsonAsync(
                "/api/v1/cases/case-1/model-files",
                new { filePath = @"C:\cad\fixture.step", kind = "fixture", caseOperationId = "case-op-1" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            Assert.Equal(HttpStatusCode.NoContent,
                (await client.DeleteAsync("/api/v1/cases/case-1/operations/case-op-1")).StatusCode);
            using (var list = await client.GetAsync("/api/v1/cases/case-1/model-files"))
            {
                using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
                var item = Assert.Single(listJson.RootElement.GetProperty("items").EnumerateArray());
                Assert.Equal(JsonValueKind.Null, item.GetProperty("caseOperationId").ValueKind);
                Assert.Equal("fixture", item.GetProperty("kind").GetString());
            }

            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/cases/case-1")).StatusCode);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM case_model_files;";
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        });
    }

    private static HttpRequestMessage Patch(string fileId, int version, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/cases/case-1/model-files/{fileId}")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"case-model-file:{fileId}:v{version}\"");
        return request;
    }

    private static async Task AssertValidationAsync(HttpClient client, string path, object body, string field, string code)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var detail = Assert.Single(json.RootElement.GetProperty("error").GetProperty("details").EnumerateArray());
        Assert.Equal(field, detail.GetProperty("field").GetString());
        Assert.Equal(code, detail.GetProperty("code").GetString());
    }

    private static void AddHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "model-client");
        client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ('case-1', 'PN-1', 'Part', 'C:\Cases\PN-1'),
                   ('case-other', 'PN-2', 'Other', 'C:\Cases\PN-2');
            INSERT INTO case_operations (id, case_id, operation_number, route_position, name)
            VALUES ('case-op-1', 'case-1', 10, 0, 'Mill'),
                   ('case-op-other', 'case-other', 10, 0, 'Mill');
            UPDATE edit_tokens SET holder_client_id = 'model-client', holder_user_id = 'planner',
                generation = 1, acquired_at = '2026-09-16T00:00:00Z' WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var path = Path.Combine(Path.GetTempPath(), "MeimadPlanner.ModelFiles.Tests", Guid.NewGuid().ToString("N"));
        var app = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5097", $"--Database:Path={Path.Combine(path, "test.db")}"],
            host => host.UseTestServer());
        try
        {
            await app.StartAsync();
            using var client = app.GetTestClient();
            await test(app, client);
            await app.StopAsync();
        }
        finally
        {
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
