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
    public async Task Batch_delete_cascades_assignments_pauses_and_published_packages_and_compacts_backlog()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            AddHeaders(client);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO operation_pause_events (
                        id, batch_operation_id, reason_type, comment, paused_by,
                        pause_started_at, status, created_at, updated_at)
                    VALUES ('pause-delete', 'batch-op-busy', 'other', 'Paused', 'planner',
                            '2026-08-11T08:00:00Z', 'active', '2026-08-11T08:00:00Z', '2026-08-11T08:00:00Z');
                    INSERT INTO eink_package_revisions (
                        id, batch_operation_id, revision, published_at, production_batch_id)
                    VALUES ('package-delete', 'batch-op-busy', '1', '2026-08-11T08:00:00Z', 'batch-busy');
                    INSERT INTO eink_package_files (
                        id, package_revision_id, logical_path, storage_relative_path,
                        media_type, byte_length, sha256, modified_at, display_order)
                    VALUES ('file-delete', 'package-delete', 'job.txt', 'job.txt', 'text/plain', 0,
                            '0000000000000000000000000000000000000000000000000000000000000000',
                            '2026-08-11T08:00:00Z', 0);
                    INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
                    VALUES ('batch-after', 'case-busy', 'B-AFTER', 'waiting', 1);
                    INSERT INTO batch_operations (id, production_batch_id, source_case_operation_id, operation_number, route_position, name, status)
                    VALUES ('batch-op-after', 'batch-after', 'case-op-busy', 10, 0, 'Mill', 'not_started');
                    INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position)
                    VALUES ('assign-after', 'batch-op-after', 'machine-busy', 1);
                    """;
                await seed.ExecuteNonQueryAsync();
            }

            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/batches/batch-busy")).StatusCode);

            await using var verify = await database.OpenConnectionAsync();
            Assert.Equal(0L, await CountAsync(verify, "production_batches", "batch-busy"));
            Assert.Equal(0L, await CountAsync(verify, "machine_assignments", "assign-1"));
            Assert.Equal(0L, await CountAsync(verify, "operation_pause_events", "pause-delete"));
            Assert.Equal(0L, await CountAsync(verify, "eink_package_revisions", "package-delete"));
            Assert.Equal(0L, await CountAsync(verify, "eink_package_files", "file-delete"));
            await using var position = verify.CreateCommand();
            position.CommandText = "SELECT backlog_position FROM machine_assignments WHERE id = 'assign-after';";
            Assert.Equal(0L, (long)(await position.ExecuteScalarAsync())!);
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

    [Fact]
    public async Task Machine_used_as_employee_qualification_is_blocked()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            AddHeaders(client);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO employee_resources (
                        id, employee_number, name, resource_type, first_name, last_name,
                        skills_json, assigned_calendar_id, is_active)
                    VALUES ('resource-skilled', 'E-SKILL', 'Skilled Employee', 'setup_worker',
                            'Skilled', 'Employee', '["machine-empty"]', 'cal-1', 1);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await AssertBlockedAsync(client, "/api/v1/machines/machine-empty");
        });
    }

    [Fact]
    public async Task Batch_delete_recomputes_every_affected_order_after_last_allocation()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            AddHeaders(client);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO orders (
                        id, case_id, order_reference, quantity, work_finish_date,
                        status, version)
                    VALUES
                        ('delete-complete', 'case-1', 'SO-COMPLETE', 1, '2026-09-01', 'complete', 4),
                        ('delete-running', 'case-1', 'SO-RUNNING', 1, '2026-09-01', 'in_production', 5),
                        ('delete-cancelled', 'case-1', 'SO-CANCELLED', 1, '2026-09-01', 'cancelled', 6);
                    INSERT INTO production_batches (
                        id, case_id, batch_number, status, planned_quantity)
                    VALUES ('batch-delete-statuses', 'case-1', 'B-DELETE-STATUSES', 'complete', 3);
                    INSERT INTO batch_allocations (
                        id, production_batch_id, allocation_type, order_id, quantity)
                    VALUES
                        ('delete-allocation-complete', 'batch-delete-statuses', 'order', 'delete-complete', 1),
                        ('delete-allocation-running', 'batch-delete-statuses', 'order', 'delete-running', 1),
                        ('delete-allocation-cancelled', 'batch-delete-statuses', 'order', 'delete-cancelled', 1);
                    INSERT INTO batch_operations (
                        id, production_batch_id, source_case_operation_id,
                        operation_number, route_position, name, status)
                    VALUES (
                        'delete-status-operation', 'batch-delete-statuses', 'case-op-1',
                        10, 0, 'Complete work', 'completed');
                    """;
                await seed.ExecuteNonQueryAsync();
            }

            using var response = await client.DeleteAsync(
                "/api/v1/batches/batch-delete-statuses");
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            await using var verify = await database.OpenConnectionAsync();
            Assert.Equal(("active", 5), await ReadOrderStatusAndVersionAsync(
                verify,
                "delete-complete"));
            Assert.Equal(("active", 6), await ReadOrderStatusAndVersionAsync(
                verify,
                "delete-running"));
            Assert.Equal(("cancelled", 6), await ReadOrderStatusAndVersionAsync(
                verify,
                "delete-cancelled"));
            Assert.Equal(0L, await CountAsync(
                verify,
                "production_batches",
                "batch-delete-statuses"));
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

    private static async Task<(string Status, int Version)> ReadOrderStatusAndVersionAsync(
        SqliteConnection connection,
        string orderId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, version FROM orders WHERE id = $id;";
        command.Parameters.AddWithValue("$id", orderId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetInt32(1));
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
            VALUES ('batch-1', 'case-1', 'B-1', 'waiting', 1),
                   ('batch-busy', 'case-busy', 'B-2', 'waiting', 1);
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
