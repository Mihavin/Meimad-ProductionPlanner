using System.Net;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Deletion;

public sealed class PlanningDeletionApiTests
{
    [Fact]
    public async Task Related_records_block_delete_until_safe_order_is_followed()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            AddHeaders(client);

            await AssertBlockedAsync(client, "/api/v1/cases/case-1");
            await AssertBlockedAsync(client, "/api/v1/orders/order-1");
            await AssertBlockedAsync(client, "/api/v1/cases/case-1/operations/case-op-1");

            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/batches/batch-1")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/orders/order-1")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/cases/case-1/operations/case-op-1")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/cases/case-1")).StatusCode);

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            Assert.Equal(0L, await CountAsync(connection, "cases", "case-1"));
            Assert.Equal(0L, await CountAsync(connection, "production_batches", "batch-1"));
            Assert.Equal(0L, await CountAsync(connection, "batch_operations", "batch-op-1"));
        });
    }

    [Fact]
    public async Task Operation_delete_compacts_route_and_dependency_blocks_delete()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            AddHeaders(client);
            await using var connection = await application.Services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO case_operations (
                        id, case_id, operation_number, route_position, name,
                        dependency_type, predecessor_case_operation_id)
                    VALUES ('free-op-1', 'case-empty', 10, 0, 'First', 'independent', NULL),
                           ('free-op-2', 'case-empty', 20, 1, 'Second', 'independent', NULL),
                           ('free-op-3', 'case-empty', 30, 2, 'Third', 'sequential', 'free-op-2');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(
                "/api/v1/cases/case-empty/operations/free-op-1")).StatusCode);
            await AssertBlockedAsync(client, "/api/v1/cases/case-empty/operations/free-op-2");
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT route_position FROM case_operations WHERE id = 'free-op-2';";
            Assert.Equal(0L, (long)(await read.ExecuteScalarAsync())!);
        });
    }

    [Fact]
    public async Task Machine_with_backlog_is_blocked_but_empty_machine_deletes()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            AddHeaders(client);
            await AssertBlockedAsync(client, "/api/v1/machines/machine-busy");
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/machines/machine-empty")).StatusCode);
        });
    }

    private static async Task AssertBlockedAsync(HttpClient client, string path)
    {
        using var response = await client.DeleteAsync(path);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("delete_blocked", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string table, string id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static void AddHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "delete-client");
        client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id) VALUES ('cal-1', 'Day', 'UTC');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES ('machine-busy', 'M-1', 'Busy', 'mill', 'cal-1', 'active', 1),
                   ('machine-empty', 'M-2', 'Empty', 'mill', 'cal-1', 'active', 1);
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ('case-1', 'PN-1', 'Part', 'C:\Cases\PN-1'),
                   ('case-empty', 'PN-2', 'Empty', 'C:\Cases\PN-2'),
                   ('case-busy', 'PN-3', 'Busy', 'C:\Cases\PN-3');
            INSERT INTO orders (id, case_id, order_reference, quantity, work_finish_date, status)
            VALUES ('order-1', 'case-1', 'SO-1', 1, '2026-09-01', 'active');
            INSERT INTO case_operations (id, case_id, operation_number, route_position, name)
            VALUES ('case-op-1', 'case-1', 10, 0, 'Mill'),
                   ('case-op-busy', 'case-busy', 10, 0, 'Mill');
            INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
            VALUES ('batch-1', 'case-1', 'B-1', 'planned', 1),
                   ('batch-busy', 'case-busy', 'B-2', 'planned', 1);
            INSERT INTO batch_allocations (id, production_batch_id, allocation_type, order_id, quantity)
            VALUES ('allocation-1', 'batch-1', 'order', 'order-1', 1);
            INSERT INTO batch_operations (
                id, production_batch_id, source_case_operation_id,
                operation_number, route_position, name, status)
            VALUES ('batch-op-1', 'batch-1', 'case-op-1', 10, 0, 'Mill', 'not_started'),
                   ('batch-op-busy', 'batch-busy', 'case-op-busy', 10, 0, 'Mill', 'not_started');
            INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position)
            VALUES ('assign-1', 'batch-op-busy', 'machine-busy', 0);
            UPDATE edit_tokens SET holder_client_id = 'delete-client', holder_user_id = 'planner',
                generation = 1, acquired_at = '2026-08-11T00:00:00Z' WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var path = Path.Combine(Path.GetTempPath(), "MeimadPlanner.Delete.Tests", Guid.NewGuid().ToString("N"));
        var app = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(path, "test.db")}"],
            host => host.UseTestServer());
        try { await app.StartAsync(); using var client = app.GetTestClient(); await test(app, client); await app.StopAsync(); }
        finally { await app.DisposeAsync(); SqliteConnection.ClearAllPools(); if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
