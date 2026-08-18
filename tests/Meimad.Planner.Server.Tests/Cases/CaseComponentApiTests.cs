using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Cases;

public sealed class CaseComponentApiTests
{
    [Fact]
    public async Task Components_prevent_cycles_explode_demand_and_never_create_child_orders()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "component-test-client");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");

            using var ab = await client.PostAsJsonAsync("/api/v1/cases/case-a/components", new
            {
                childCaseId = "case-b", quantityPerParent = 2, sortOrder = 0, notes = "B sub-case"
            });
            Assert.Equal(HttpStatusCode.Created, ab.StatusCode);
            var abTag = ab.Headers.ETag?.ToString();
            Assert.False(string.IsNullOrWhiteSpace(abTag));

            using var bc = await client.PostAsJsonAsync("/api/v1/cases/case-b/components", new
            {
                childCaseId = "case-c", quantityPerParent = 3, sortOrder = 0
            });
            Assert.Equal(HttpStatusCode.Created, bc.StatusCode);

            using var self = await client.PostAsJsonAsync("/api/v1/cases/case-a/components", new
            {
                childCaseId = "case-a", quantityPerParent = 1
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, self.StatusCode);

            using var cycle = await client.PostAsJsonAsync("/api/v1/cases/case-c/components", new
            {
                childCaseId = "case-a", quantityPerParent = 1
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, cycle.StatusCode);
            using (var document = JsonDocument.Parse(await cycle.Content.ReadAsStringAsync()))
                Assert.Equal("component_cycle", document.RootElement.GetProperty("error").GetProperty("code").GetString());

            using var components = await client.GetAsync("/api/v1/cases/case-a/components");
            using (var document = JsonDocument.Parse(await components.Content.ReadAsStringAsync()))
            {
                var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
                Assert.Equal("PN-B", item.GetProperty("childPartNumber").GetString());
                Assert.Equal(2, item.GetProperty("quantityPerParent").GetDouble());
            }

            using var whereUsed = await client.GetAsync("/api/v1/cases/case-b/where-used");
            using (var document = JsonDocument.Parse(await whereUsed.Content.ReadAsStringAsync()))
                Assert.Equal("PN-A", Assert.Single(document.RootElement.GetProperty("items").EnumerateArray())
                    .GetProperty("parentPartNumber").GetString());

            using var demand = await client.GetAsync("/api/v1/orders/order-a/component-demand");
            using (var document = JsonDocument.Parse(await demand.Content.ReadAsStringAsync()))
            {
                var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
                Assert.Equal(2, items.Length);
                Assert.Equal(20, items.Single(item => item.GetProperty("childPartNumber").GetString() == "PN-B")
                    .GetProperty("totalRequiredQuantity").GetDouble());
                Assert.Equal(60, items.Single(item => item.GetProperty("childPartNumber").GetString() == "PN-C")
                    .GetProperty("totalRequiredQuantity").GetDouble());
            }

            using var abJson = JsonDocument.Parse(await ab.Content.ReadAsStringAsync());
            var componentId = abJson.RootElement.GetProperty("caseComponentId").GetString();
            using var remove = new HttpRequestMessage(HttpMethod.Delete,
                $"/api/v1/cases/case-a/components/{componentId}");
            remove.Headers.TryAddWithoutValidation("If-Match", abTag);
            using var removed = await client.SendAsync(remove);
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT (SELECT COUNT(*) FROM cases),
                       (SELECT COUNT(*) FROM orders WHERE case_id IN ('case-b','case-c')),
                       (SELECT is_active FROM case_components WHERE id=$id);
                """;
            command.Parameters.AddWithValue("$id", componentId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(3, reader.GetInt32(0));
            Assert.Equal(0, reader.GetInt32(1));
            Assert.Equal(0, reader.GetInt32(2));
        });
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cases (id, part_number, name, working_folder_path) VALUES
                ('case-a', 'PN-A', 'Assembly A', 'C:\Cases\A'),
                ('case-b', 'PN-B', 'Sub-case B', 'C:\Cases\B'),
                ('case-c', 'PN-C', 'Sub-case C', 'C:\Cases\C');
            INSERT INTO orders (id, case_id, order_reference, quantity, work_finish_date, status)
            VALUES ('order-a', 'case-a', 'SO-A', 10, '2026-09-01', 'active');
            UPDATE edit_tokens
            SET holder_client_id='component-test-client', holder_user_id='component-test-user',
                generation=1, acquired_at='2026-08-18T00:00:00Z', version=version+1,
                updated_at='2026-08-18T00:00:00Z'
            WHERE id=1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.Component.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directory, "test.db")}"],
            builder => builder.UseTestServer());
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
}
