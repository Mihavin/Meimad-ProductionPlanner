using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Machines;

public sealed class MachineOperationExecutionApiTests
{
    [Fact]
    public async Task Start_suspend_resume_and_finish_advance_machine_backlog()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            AddEditHeaders(client);

            Assert.Equal("in_progress", await PostActionAsync(client, "op-1", "start"));
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var statusConnection = await database.OpenConnectionAsync())
            {
                Assert.Equal("in_production", await ScalarAsync(
                    statusConnection,
                    "SELECT status FROM production_batches WHERE id = 'batch-1';"));
            }
            using (var unassignRunning = await client.DeleteAsync(
                       "/api/v1/batch-operations/op-1/assignment"))
            {
                Assert.Equal(HttpStatusCode.Conflict, unassignRunning.StatusCode);
                Assert.Equal("operation_in_progress", await ErrorCodeAsync(unassignRunning));
            }
            Assert.Equal("suspended", await PostActionAsync(client, "op-1", "suspend"));
            Assert.Equal("in_progress", await PostActionAsync(client, "op-1", "start"));
            Assert.Equal("completed", await PostActionAsync(client, "op-1", "finish"));

            using var board = await client.GetAsync("/api/v1/planning-board");
            board.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await board.Content.ReadAsStringAsync());
            var pool = Assert.Single(document.RootElement.GetProperty("pool").EnumerateArray());
            Assert.Equal("op-3", pool.GetProperty("batchOperationId").GetString());
            var remaining = Assert.Single(document.RootElement.GetProperty("machines")[0]
                .GetProperty("backlog").EnumerateArray());
            Assert.Equal("op-2", remaining.GetProperty("batchOperationId").GetString());
            Assert.Equal(0, remaining.GetProperty("backlogPosition").GetInt32());

            using var reassignCompleted = await client.PutAsJsonAsync(
                "/api/v1/batch-operations/op-1/assignment",
                new { machineId = "machine-1", backlogPosition = 1 });
            Assert.Equal(HttpStatusCode.Conflict, reassignCompleted.StatusCode);
            Assert.Equal("operation_completed", await ErrorCodeAsync(reassignCompleted));

            Assert.Equal("in_progress", await PostActionAsync(client, "op-2", "start"));
            Assert.Equal("completed", await PostActionAsync(client, "op-2", "finish"));
            await using var connection = await database.OpenConnectionAsync();
            Assert.Equal("completed", await ScalarAsync(
                connection, "SELECT status FROM batch_operations WHERE id = 'op-1';"));
            Assert.Equal(0L, (long)(await ScalarAsync(
                connection, "SELECT COUNT(*) FROM machine_assignments WHERE batch_operation_id = 'op-1';"))!);
            Assert.Equal("complete", await ScalarAsync(
                connection, "SELECT status FROM production_batches WHERE id = 'batch-1';"));
            Assert.Equal("waiting", await ScalarAsync(
                connection, "SELECT status FROM production_batches WHERE id = 'batch-2';"));
        });
    }

    [Fact]
    public async Task Invalid_execution_transitions_are_rejected_without_mutation()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            AddEditHeaders(client);

            using var nonFirst = await client.PostAsync("/api/v1/batch-operations/op-2/start", null);
            Assert.Equal(HttpStatusCode.Conflict, nonFirst.StatusCode);
            Assert.Equal("operation_not_first_in_backlog", await ErrorCodeAsync(nonFirst));

            using var suspendBeforeStart = await client.PostAsync("/api/v1/batch-operations/op-1/suspend", null);
            Assert.Equal(HttpStatusCode.Conflict, suspendBeforeStart.StatusCode);
            Assert.Equal("invalid_operation_transition", await ErrorCodeAsync(suspendBeforeStart));

            using var unassigned = await client.PostAsync("/api/v1/batch-operations/op-3/start", null);
            Assert.Equal(HttpStatusCode.Conflict, unassigned.StatusCode);
            Assert.Equal("operation_not_assigned", await ErrorCodeAsync(unassigned));

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            Assert.Equal("not_started", await ScalarAsync(
                connection, "SELECT status FROM batch_operations WHERE id = 'op-1';"));
            Assert.Equal("not_started", await ScalarAsync(
                connection, "SELECT status FROM batch_operations WHERE id = 'op-2';"));
        });
    }

    [Fact]
    public async Task Execution_commands_require_current_edit_mode()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            using var response = await client.PostAsync("/api/v1/batch-operations/op-1/start", null);
            Assert.Equal((HttpStatusCode)428, response.StatusCode);
        });
    }

    [Fact]
    public async Task Concurrent_duplicate_start_accepts_exactly_one_transition()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            AddEditHeaders(client);

            var responses = await Task.WhenAll(
                client.PostAsync("/api/v1/batch-operations/op-1/start", null),
                client.PostAsync("/api/v1/batch-operations/op-1/start", null));
            try
            {
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
                var database = application.Services.GetRequiredService<SqliteDatabase>();
                await using var connection = await database.OpenConnectionAsync();
                Assert.Equal("in_progress", await ScalarAsync(
                    connection, "SELECT status FROM batch_operations WHERE id = 'op-1';"));
            }
            finally
            {
                foreach (var response in responses) response.Dispose();
            }
        });
    }

    private static async Task<string> PostActionAsync(HttpClient client, string operationId, string action)
    {
        using var response = await client.PostAsync($"/api/v1/batch-operations/{operationId}/{action}", null);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("status").GetString()!;
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static void AddEditHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "execution-client");
        client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id) VALUES ('calendar-1', 'Day', 'UTC');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES ('machine-1', 'M-1', 'Mill 1', 'mill', 'calendar-1', 'active', 1);
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ('case-1', 'PN-1', 'Part', 'C:\Cases\PN-1');
            INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
            VALUES ('batch-1', 'case-1', 'B-1', 'waiting', 1),
                   ('batch-2', 'case-1', 'B-2', 'waiting', 1);
            INSERT INTO case_operations (id, case_id, operation_number, route_position, name)
            VALUES ('case-op-1', 'case-1', 10, 0, 'First'),
                   ('case-op-2', 'case-1', 20, 1, 'Second'),
                   ('case-op-3', 'case-1', 30, 2, 'Unassigned');
            INSERT INTO batch_operations (
                id, production_batch_id, source_case_operation_id,
                operation_number, route_position, name, status)
            VALUES ('op-1', 'batch-1', 'case-op-1', 10, 0, 'First', 'not_started'),
                   ('op-2', 'batch-1', 'case-op-2', 20, 1, 'Second', 'not_started'),
                   ('op-3', 'batch-2', 'case-op-3', 30, 0, 'Unassigned', 'not_started');
            INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position)
            VALUES ('assignment-1', 'op-1', 'machine-1', 0),
                   ('assignment-2', 'op-2', 'machine-1', 1);
            UPDATE edit_tokens
            SET holder_client_id = 'execution-client', holder_user_id = 'planner',
                generation = 1, acquired_at = '2026-08-11T00:00:00Z'
            WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunWithServerAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "MeimadPlanner.Execution.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directoryPath, "test.db")}"],
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
            if (Directory.Exists(directoryPath)) Directory.Delete(directoryPath, true);
        }
    }
}
