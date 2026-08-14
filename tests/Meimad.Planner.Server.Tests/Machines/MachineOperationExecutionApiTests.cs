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
    public async Task Start_and_finish_capture_authoritative_actual_times_while_resume_retains_start()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            AddEditHeaders(client);

            using var started = await client.PostAsync("/api/v1/batch-operations/op-1/start", null);
            started.EnsureSuccessStatusCode();
            using var startedJson = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
            var actualStart = startedJson.RootElement.GetProperty("actualStart").GetDateTimeOffset();
            Assert.Equal("machine-1", startedJson.RootElement.GetProperty("actualMachineId").GetString());
            Assert.Equal(JsonValueKind.Null, startedJson.RootElement.GetProperty("actualEnd").ValueKind);

            Assert.Equal("suspended", await PostActionAsync(client, "op-1", "suspend"));
            using var resumed = await client.PostAsync("/api/v1/batch-operations/op-1/start", null);
            resumed.EnsureSuccessStatusCode();
            using var resumedJson = JsonDocument.Parse(await resumed.Content.ReadAsStringAsync());
            Assert.Equal(actualStart, resumedJson.RootElement.GetProperty("actualStart").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, resumedJson.RootElement.GetProperty("actualEnd").ValueKind);

            using var finished = await client.PostAsync("/api/v1/batch-operations/op-1/finish", null);
            finished.EnsureSuccessStatusCode();
            using var finishedJson = JsonDocument.Parse(await finished.Content.ReadAsStringAsync());
            var actualEnd = finishedJson.RootElement.GetProperty("actualEnd").GetDateTimeOffset();
            Assert.True(actualEnd >= actualStart);
            Assert.Equal(actualStart, finishedJson.RootElement.GetProperty("actualStart").GetDateTimeOffset());
            Assert.Equal("machine-1", finishedJson.RootElement.GetProperty("actualMachineId").GetString());

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            Assert.Equal(actualStart, DateTimeOffset.Parse((string)(await ScalarAsync(connection,
                "SELECT actual_start FROM batch_operations WHERE id = 'op-1';"))!));
            Assert.Equal(actualEnd, DateTimeOffset.Parse((string)(await ScalarAsync(connection,
                "SELECT actual_end FROM batch_operations WHERE id = 'op-1';"))!));
            Assert.Equal("machine-1", await ScalarAsync(connection,
                "SELECT actual_machine_id FROM batch_operations WHERE id = 'op-1';"));
        });
    }

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

            using var events = await client.GetAsync("/api/v1/event-log?limit=50");
            events.EnsureSuccessStatusCode();
            using var eventsJson = JsonDocument.Parse(await events.Content.ReadAsStringAsync());
            var types = eventsJson.RootElement.GetProperty("items").EnumerateArray()
                .Select(value => value.GetProperty("eventType").GetString()).ToArray();
            Assert.Contains("operation_started", types);
            Assert.Contains("operation_paused", types);
            Assert.Contains("operation_resumed", types);
            Assert.Contains("operation_finished", types);
            var paused = Assert.Single(eventsJson.RootElement.GetProperty("items").EnumerateArray(),
                value => value.GetProperty("eventType").GetString() == "operation_paused");
            Assert.Equal("other", paused.GetProperty("reasonCode").GetString());
            Assert.Equal("planner", paused.GetProperty("user").GetString());
            Assert.Equal("in_progress", paused.GetProperty("beforeData").GetProperty("status").GetString());
            Assert.Equal("suspended", paused.GetProperty("afterData").GetProperty("status").GetString());
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

            using var suspendBeforeStart = await client.PostAsJsonAsync(
                "/api/v1/batch-operations/op-1/suspend", new { reasonType = "other", comment = "test" });
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

    [Fact]
    public async Task Reset_returns_paused_operation_to_not_started_and_closes_pause()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            AddEditHeaders(client);

            Assert.Equal("in_progress", await PostActionAsync(client, "op-1", "start"));
            using (var resetWhileRunning = await client.PostAsync(
                       "/api/v1/batch-operations/op-1/reset", null))
            {
                Assert.Equal(HttpStatusCode.Conflict, resetWhileRunning.StatusCode);
                Assert.Equal("invalid_operation_transition", await ErrorCodeAsync(resetWhileRunning));
            }

            Assert.Equal("suspended", await PostActionAsync(client, "op-1", "suspend"));
            Assert.Equal("not_started", await PostActionAsync(client, "op-1", "reset"));

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            Assert.Equal("not_started", await ScalarAsync(connection,
                "SELECT status FROM batch_operations WHERE id = 'op-1';"));
            Assert.IsType<DBNull>(await ScalarAsync(connection,
                "SELECT actual_start FROM batch_operations WHERE id = 'op-1';"));
            Assert.IsType<DBNull>(await ScalarAsync(connection,
                "SELECT actual_end FROM batch_operations WHERE id = 'op-1';"));
            Assert.IsType<DBNull>(await ScalarAsync(connection,
                "SELECT actual_machine_id FROM batch_operations WHERE id = 'op-1';"));
            Assert.Equal("assignment-1", await ScalarAsync(connection,
                "SELECT id FROM machine_assignments WHERE batch_operation_id = 'op-1';"));
            Assert.Equal(0L, (long)(await ScalarAsync(connection,
                "SELECT backlog_position FROM machine_assignments WHERE batch_operation_id = 'op-1';"))!);
            Assert.Equal("closed", await ScalarAsync(connection,
                "SELECT status FROM operation_pause_events WHERE batch_operation_id = 'op-1';"));
            Assert.NotNull(await ScalarAsync(connection,
                "SELECT pause_ended_at FROM operation_pause_events WHERE batch_operation_id = 'op-1';"));
            Assert.Equal("waiting", await ScalarAsync(connection,
                "SELECT status FROM production_batches WHERE id = 'batch-1';"));

            using var events = await client.GetAsync("/api/v1/event-log?limit=20");
            events.EnsureSuccessStatusCode();
            using var eventJson = JsonDocument.Parse(await events.Content.ReadAsStringAsync());
            var reset = Assert.Single(eventJson.RootElement.GetProperty("items").EnumerateArray(),
                value => value.GetProperty("eventType").GetString() == "operation_reset");
            Assert.Equal("planner", reset.GetProperty("user").GetString());
            Assert.Equal("suspended", reset.GetProperty("beforeData").GetProperty("status").GetString());
            Assert.Equal("not_started", reset.GetProperty("afterData").GetProperty("status").GetString());

            Assert.Equal("in_progress", await PostActionAsync(client, "op-1", "start"));
        });
    }

    public static TheoryData<object, string> ValidPauseReasons => new()
    {
        { new { reasonType = "additional_qa", problemDescription = "Surface requires reinspection", comment = "Hold" }, "additional_qa" },
        { new { reasonType = "tooling_problem", toolingItemDescription = "10mm end mill" }, "tooling_problem" },
        { new { reasonType = "customer_request", customerContactName = "Dana", requestDescription = "Hold pending drawing review" }, "customer_request" },
        { new { reasonType = "other", comment = "Supervisor requested a safety review" }, "other" }
    };

    [Theory]
    [MemberData(nameof(ValidPauseReasons))]
    public async Task Structured_pause_reason_is_stored_and_resume_closes_event(object reason, string expectedType)
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            AddEditHeaders(client);
            Assert.Equal("in_progress", await PostActionAsync(client, "op-1", "start"));
            using var pause = await client.PostAsJsonAsync("/api/v1/batch-operations/op-1/suspend", reason);
            pause.EnsureSuccessStatusCode();

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            Assert.Equal(expectedType, await ScalarAsync(connection,
                "SELECT reason_type FROM operation_pause_events WHERE batch_operation_id = 'op-1' AND status = 'active';"));
            Assert.Equal("planner", await ScalarAsync(connection,
                "SELECT paused_by FROM operation_pause_events WHERE batch_operation_id = 'op-1';"));

            using var board = await client.GetAsync("/api/v1/planning-board");
            board.EnsureSuccessStatusCode();
            using (var boardJson = JsonDocument.Parse(await board.Content.ReadAsStringAsync()))
            {
                var paused = boardJson.RootElement.GetProperty("machines")[0]
                    .GetProperty("backlog")[0];
                Assert.Equal("suspended", paused.GetProperty("status").GetString());
                Assert.Contains(expectedType.Replace('_', ' '),
                    paused.GetProperty("activePauseReason").GetString(), StringComparison.Ordinal);
                Assert.Equal("planner", paused.GetProperty("pausedBy").GetString());
            }

            if (expectedType == "additional_qa")
            {
                var now = DateTimeOffset.UtcNow;
                using var timeline = await client.GetAsync(
                    $"/api/v1/timeline?from={Uri.EscapeDataString(now.AddMinutes(-5).ToString("O"))}&to={Uri.EscapeDataString(now.AddHours(1).ToString("O"))}");
                timeline.EnsureSuccessStatusCode();
                using var timelineJson = JsonDocument.Parse(await timeline.Content.ReadAsStringAsync());
                var intervals = timelineJson.RootElement.GetProperty("machines")[0]
                    .GetProperty("intervals").EnumerateArray().ToArray();
                Assert.Contains(intervals, value =>
                    value.GetProperty("type").GetString() == "waiting"
                    && value.GetProperty("operationId").GetString() == "op-1"
                    && value.GetProperty("detail").GetString()!.Contains("paused by planner", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(intervals, value =>
                    value.GetProperty("operationId").ValueKind == JsonValueKind.String
                    && value.GetProperty("operationId").GetString() == "op-1"
                    && value.GetProperty("type").GetString() == "operation");
            }

            Assert.Equal("in_progress", await PostActionAsync(client, "op-1", "start"));
            Assert.Equal("closed", await ScalarAsync(connection,
                "SELECT status FROM operation_pause_events WHERE batch_operation_id = 'op-1';"));
            Assert.NotNull(await ScalarAsync(connection,
                "SELECT pause_ended_at FROM operation_pause_events WHERE batch_operation_id = 'op-1';"));
        });
    }

    [Fact]
    public async Task Pause_without_required_structured_fields_is_rejected()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            AddEditHeaders(client);
            await PostActionAsync(client, "op-1", "start");
            using var response = await client.PostAsJsonAsync(
                "/api/v1/batch-operations/op-1/suspend", new { reasonType = "customer_request" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("validation_failed", await ErrorCodeAsync(response));
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            Assert.Equal("in_progress", await ScalarAsync(connection,
                "SELECT status FROM batch_operations WHERE id = 'op-1';"));
            Assert.Equal(0L, (long)(await ScalarAsync(connection,
                "SELECT COUNT(*) FROM operation_pause_events;"))!);
        });
    }

    private static async Task<string> PostActionAsync(HttpClient client, string operationId, string action)
    {
        using var response = action == "suspend"
            ? await client.PostAsJsonAsync($"/api/v1/batch-operations/{operationId}/{action}",
                new { reasonType = "other", comment = "Test pause" })
            : await client.PostAsync($"/api/v1/batch-operations/{operationId}/{action}", null);
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
