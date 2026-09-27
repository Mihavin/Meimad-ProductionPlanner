using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.ProductionBatches;

/// <summary>
/// A pending Work Order takes its operation list from its Case; releasing it freezes the list, and
/// setting it back to pending refreshes it (owner decision 2026-09-27).
/// </summary>
public sealed class WorkOrderRouteReleaseTests
{
    [Fact]
    public async Task A_pending_work_order_follows_every_case_operation_change_and_keeps_machine_placement()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            var caseId = await CreateCaseAsync(client);
            var mill = await CreateOperationAsync(client, caseId, 10, "Mill", "Mill 3x");
            var batchId = await CreateWorkOrderAsync(client, caseId, "WO-PENDING");
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await PlaceOnMachineAsync(database, batchId, 10);

            var deburr = await CreateOperationAsync(client, caseId, 20, "Deburr", "Mill 3x");
            await PatchOperationAsync(client, caseId, mill, 1, new { name = "Mill both sides", setupTimeSeconds = 900, requiredMachineType = "Mill 5x" });

            var route = await RouteAsync(database, batchId);
            Assert.Equal(["10:Mill both sides:Mill 5x:900", "20:Deburr:Mill 3x:60"], route);
            // The placed operation kept its Machine: the same row was updated in place.
            Assert.Equal(1L, await ScalarAsync(database,
                $"SELECT COUNT(*) FROM machine_assignments a JOIN batch_operations o ON o.id = a.batch_operation_id WHERE o.production_batch_id = '{batchId}' AND o.operation_number = 10 AND a.released_at IS NULL;"));

            using (var delete = await client.DeleteAsync($"/api/v1/cases/{caseId}/operations/{deburr}"))
            {
                Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            }
            Assert.Equal(["10:Mill both sides:Mill 5x:900"], await RouteAsync(database, batchId));
        });
    }

    [Fact]
    public async Task Releasing_freezes_the_operation_list_and_going_back_to_pending_refreshes_it()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            var caseId = await CreateCaseAsync(client);
            var mill = await CreateOperationAsync(client, caseId, 10, "Mill", "Mill 3x");
            var batchId = await CreateWorkOrderAsync(client, caseId, "WO-RELEASED");
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await PlaceOnMachineAsync(database, batchId, 10);
            await SetReleaseAsync(client, batchId, released: true);

            await CreateOperationAsync(client, caseId, 20, "Deburr", "Mill 3x");
            await PatchOperationAsync(client, caseId, mill, 1, new { setupTimeSeconds = 900 });
            Assert.Equal(["10:Mill:Mill 3x:60"], await RouteAsync(database, batchId));
            using (var delete = await client.DeleteAsync($"/api/v1/cases/{caseId}/operations/{mill}"))
            {
                Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
                Assert.Contains("released", await delete.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            await SetReleaseAsync(client, batchId, released: false);
            Assert.Equal(["10:Mill:Mill 3x:900", "20:Deburr:Mill 3x:60"], await RouteAsync(database, batchId));
            Assert.Equal(1L, await ScalarAsync(database,
                $"SELECT COUNT(*) FROM machine_assignments a JOIN batch_operations o ON o.id = a.batch_operation_id WHERE o.production_batch_id = '{batchId}' AND a.released_at IS NULL;"));
            Assert.Equal(1L, await ScalarAsync(database,
                $"SELECT COUNT(*) FROM structured_event_log WHERE event_type = 'production_batch_route_refreshed' AND related_entity_ids_json LIKE '%{batchId}%';"));
        });
    }

    [Fact]
    public async Task A_work_order_whose_production_started_cannot_go_back_to_pending()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            var caseId = await CreateCaseAsync(client);
            await CreateOperationAsync(client, caseId, 10, "Mill", "Mill 3x");
            var batchId = await CreateWorkOrderAsync(client, caseId, "WO-STARTED");
            await SetReleaseAsync(client, batchId, released: true);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await ExecuteAsync(database, $"UPDATE batch_operations SET status = 'in_progress' WHERE production_batch_id = '{batchId}';");

            using var unrelease = await client.PostAsync($"/api/v1/batches/{batchId}/unrelease", null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unrelease.StatusCode);
            Assert.Contains("work_order_started", await unrelease.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal("released", await ScalarAsync(database,
                $"SELECT release_state FROM production_batches WHERE id = '{batchId}';"));
        });
    }

    private static async Task<string[]> RouteAsync(SqliteDatabase database, string batchId)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_number || ':' || name || ':' || COALESCE(required_machine_type, '') || ':' || COALESCE(setup_seconds, '')
            FROM batch_operations WHERE production_batch_id = $id ORDER BY route_position;
            """;
        command.Parameters.AddWithValue("$id", batchId);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static async Task PlaceOnMachineAsync(SqliteDatabase database, string batchId, int operationNumber) =>
        await ExecuteAsync(database, $"""
            INSERT INTO working_calendars (id, name, time_zone_id) VALUES ('calendar-1', 'Day', 'UTC');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES ('machine-1', 'M-1', 'Mill 1', 'Mill 3x', 'calendar-1', 'active', 1);
            INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position)
            SELECT 'assignment-1', id, 'machine-1', 0 FROM batch_operations
            WHERE production_batch_id = '{batchId}' AND operation_number = {operationNumber};
            """);

    private static async Task<string> CreateCaseAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/cases", new
        {
            partNumber = "PN-WO-ROUTE",
            name = "Work Order route",
            workingFolderPath = Path.Combine(Path.GetTempPath(), "PN-WO-ROUTE")
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("caseId").GetString()!;
    }

    private static async Task<string> CreateOperationAsync(
        HttpClient client, string caseId, int number, string name, string machineType)
    {
        using var response = await client.PostAsJsonAsync($"/api/v1/cases/{caseId}/operations", new
        {
            operationNumber = number, name, requiredMachineType = machineType,
            setupTimeSeconds = 60, cycleTimePerPartSeconds = 30, dependencyType = "INDEPENDENT"
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("caseOperationId").GetString()!;
    }

    private static async Task PatchOperationAsync(
        HttpClient client, string caseId, string operationId, int version, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/cases/{caseId}/operations/{operationId}")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"case-operation:{operationId}:v{version}\"");
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> CreateWorkOrderAsync(HttpClient client, string caseId, string number)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/batches", new
        {
            caseId, batchNumber = number, status = "waiting", plannedQuantity = 5,
            allocations = new[] { new { allocationType = "stock", quantity = 5 } }
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("batchId").GetString()!;
    }

    private static async Task SetReleaseAsync(HttpClient client, string batchId, bool released)
    {
        using var response = await client.PostAsync(
            $"/api/v1/batches/{batchId}/{(released ? "release" : "unrelease")}", null);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task ExecuteAsync(SqliteDatabase database, string sql)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteDatabase database, string sql)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task RunWithServerAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.WorkOrderRoute.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directory, "test.db")}"],
            builder => builder.UseTestServer());
        try
        {
            await application.StartAsync();
            await ExecuteAsync(application.Services.GetRequiredService<SqliteDatabase>(), """
                UPDATE edit_tokens
                SET holder_client_id = 'route-client', holder_user_id = 'planner', generation = 1,
                    acquired_at = '2026-09-27T00:00:00Z', version = version + 1
                WHERE id = 1;
                """);
            using var client = application.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "route-client");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
            await test(application, client);
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
