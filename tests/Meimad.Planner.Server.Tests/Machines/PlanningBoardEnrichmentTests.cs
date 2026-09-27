using System.Net;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Machines;

public sealed class PlanningBoardEnrichmentTests
{
    [Fact]
    public async Task Planning_board_includes_quantity_order_references_and_server_estimated_time()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO working_calendars (id, name, time_zone_id, calendar_json)
                    VALUES ('board-calendar', 'Board calendar', 'UTC', '{}');
                    INSERT INTO machine_types (id, name, capabilities_json)
                    VALUES ('board-machine-type', 'Mill', '["five-axis","shared"]');
                    INSERT INTO machines (
                        id, number, name, machine_type, capabilities_json,
                        working_calendar_id, display_configuration_json, status,
                        machine_type_id, is_active)
                    VALUES (
                        'board-machine', 'M-BOARD', 'Board machine', 'Mill',
                        '["probing","SHARED"]', 'board-calendar', '{}', 'available',
                        'board-machine-type', 1);
                    INSERT INTO cases (id, part_number, name, working_folder_path)
                    VALUES ('board-case', 'PN-BOARD', 'Board case', 'C:\Cases\PN-BOARD');
                    INSERT INTO case_operations (
                        id, case_id, operation_number, route_position, name,
                        setup_seconds, cycle_seconds)
                    VALUES ('board-case-op', 'board-case', 10, 0, 'Mill', 60, 30);
                    INSERT INTO orders (id, case_id, order_reference, quantity, work_finish_date, status)
                    VALUES
                        ('board-order-z', 'board-case', 'SO-Z', 2, '2026-09-01', 'active'),
                        ('board-order-a', 'board-case', 'SO-A', 2, '2026-09-01', 'active');
                    INSERT INTO production_batches (
                        id, case_id, batch_number, status, planned_quantity, release_state)
                    VALUES ('board-batch', 'board-case', 'B-BOARD', 'waiting', 4, 'released');
                    INSERT INTO batch_allocations (
                        id, production_batch_id, allocation_type, order_id, quantity)
                    VALUES
                        ('board-allocation-z', 'board-batch', 'order', 'board-order-z', 2),
                        ('board-allocation-a', 'board-batch', 'order', 'board-order-a', 2);
                    INSERT INTO batch_operations (
                        id, production_batch_id, source_case_operation_id,
                        operation_number, route_position, name, required_machine_type,
                        setup_seconds, cycle_seconds, status)
                    VALUES ('board-op', 'board-batch', 'board-case-op',
                            10, 0, 'Mill', 'Mill', 60, 30, 'not_started');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync("/api/v1/planning-board");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var operation = Assert.Single(document.RootElement.GetProperty("pool").EnumerateArray());
            Assert.Equal(4, operation.GetProperty("plannedQuantity").GetInt32());
            Assert.Equal(
                ["SO-A", "SO-Z"],
                operation.GetProperty("orderReferences").EnumerateArray()
                    .Select(value => value.GetString()!).ToArray());
            Assert.Equal(180L, operation.GetProperty("estimatedTimeSeconds").GetInt64());
            Assert.Equal("Board case", operation.GetProperty("caseName").GetString());

            var machine = Assert.Single(document.RootElement.GetProperty("machines").EnumerateArray());
            Assert.Equal(
                ["probing", "SHARED", "five-axis"],
                machine.GetProperty("capabilities").EnumerateArray()
                    .Select(value => value.GetString()!).ToArray());
            Assert.Equal("current", document.RootElement.GetProperty("conflictCalculationStatus").GetString());
            Assert.DoesNotContain(
                document.RootElement.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString() == "unassigned_operation");
        });
    }

    [Fact]
    public async Task Planning_board_pool_lists_only_machine_operations_of_released_work_orders()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO working_calendars (id, name, time_zone_id, calendar_json)
                    VALUES ('board-calendar', 'Board calendar', 'UTC', '{}');
                    INSERT INTO machines (
                        id, number, name, machine_type, capabilities_json,
                        working_calendar_id, display_configuration_json, status, is_active)
                    VALUES (
                        'board-machine', 'M-BOARD', 'Board machine', 'Mill', '[]',
                        'board-calendar', '{}', 'available', 1);
                    INSERT INTO cases (id, part_number, name, working_folder_path)
                    VALUES ('board-case', 'PN-BOARD', 'Board case', 'C:\Cases\PN-BOARD'),
                           ('pending-case', 'PN-PENDING', 'Pending case', 'C:\Cases\PN-PENDING');
                    INSERT INTO production_batches (
                        id, case_id, batch_number, status, planned_quantity, release_state)
                    VALUES
                        ('released-batch', 'board-case', 'B-RELEASED', 'waiting', 4, 'released'),
                        ('pending-batch', 'pending-case', 'B-PENDING', 'waiting', 4, 'pending');
                    -- Each Work Order matches its Case, as a pending one always does.
                    INSERT INTO case_operations (
                        id, case_id, operation_number, route_position, name, required_machine_type,
                        setup_seconds, cycle_seconds)
                    VALUES
                        ('case-op-10', 'board-case', 10, 0, 'Mill', 'Mill', 60, 30),
                        ('case-op-20', 'board-case', 20, 1, 'Laser', 'laser', 60, 30),
                        ('case-op-30', 'board-case', 30, 2, 'MACHINE FINISH PER PS551170', NULL, NULL, NULL),
                        ('case-op-40', 'board-case', 40, 3, 'Blank type', '  ', NULL, NULL),
                        ('case-op-50', 'board-case', 50, 4, 'FOR CONTOUR SEE REPORT', 'Production Note', 0, 0),
                        ('case-op-60', 'board-case', 60, 5, 'Assigned text step', NULL, 60, 30),
                        ('pending-case-op-10', 'pending-case', 10, 0, 'Mill', 'Mill', 60, 30),
                        ('pending-case-op-20', 'pending-case', 20, 1, 'Mill', 'Mill', 60, 30);
                    INSERT INTO batch_operations (
                        id, production_batch_id, source_case_operation_id, operation_number,
                        route_position, name, required_machine_type, setup_seconds, cycle_seconds, status)
                    VALUES
                        ('typed-op', 'released-batch', 'case-op-10', 10, 0, 'Mill', 'Mill', 60, 30, 'not_started'),
                        ('foreign-type-op', 'released-batch', 'case-op-20', 20, 1, 'Laser', 'laser', 60, 30, 'not_started'),
                        ('untyped-op', 'released-batch', 'case-op-30', 30, 2, 'MACHINE FINISH PER PS551170', NULL, NULL, NULL, 'not_started'),
                        ('blank-type-op', 'released-batch', 'case-op-40', 40, 3, 'Blank type', '  ', NULL, NULL, 'not_started'),
                        ('note-op', 'released-batch', 'case-op-50', 50, 4, 'FOR CONTOUR SEE REPORT', 'Production Note', 0, 0, 'not_started'),
                        ('assigned-untyped-op', 'released-batch', 'case-op-60', 60, 5, 'Assigned text step', NULL, 60, 30, 'not_started'),
                        ('pending-op', 'pending-batch', 'pending-case-op-10', 10, 0, 'Mill', 'Mill', 60, 30, 'not_started'),
                        ('pending-assigned-op', 'pending-batch', 'pending-case-op-20', 20, 1, 'Mill', 'Mill', 60, 30, 'not_started');
                    INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position)
                    VALUES
                        ('assigned-untyped', 'assigned-untyped-op', 'board-machine', 0),
                        ('pending-assigned', 'pending-assigned-op', 'board-machine', 1);
                    UPDATE edit_tokens
                    SET holder_client_id = 'board-client', holder_user_id = 'planner', generation = 1,
                        acquired_at = '2026-09-27T00:00:00Z', version = version + 1
                    WHERE id = 1;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            Assert.Equal(["foreign-type-op", "typed-op"], await PoolIdsAsync(client));
            // Machine backlogs are not filtered by Machine Type or Work Order release state.
            Assert.Equal(["assigned-untyped-op", "pending-assigned-op"], await BacklogIdsAsync(client));

            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "board-client");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
            using (var release = await client.PostAsync("/api/v1/batches/pending-batch/release", null))
            {
                Assert.Equal(HttpStatusCode.OK, release.StatusCode);
            }
            Assert.Equal(["foreign-type-op", "pending-op", "typed-op"], await PoolIdsAsync(client));

            using (var unrelease = await client.PostAsync("/api/v1/batches/released-batch/unrelease", null))
            {
                Assert.Equal(HttpStatusCode.OK, unrelease.StatusCode);
            }
            Assert.Equal(["pending-op"], await PoolIdsAsync(client));
            Assert.Equal(["assigned-untyped-op", "pending-assigned-op"], await BacklogIdsAsync(client));
        });
    }

    [Fact]
    public async Task Planning_board_reports_timeline_engine_conflicts_for_assigned_work()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO working_calendars (id, name, time_zone_id, calendar_json)
                    VALUES ('board-calendar', 'Board calendar', 'UTC', '{}');
                    INSERT INTO machines (
                        id, number, name, machine_type, capabilities_json,
                        working_calendar_id, display_configuration_json, status, is_active)
                    VALUES (
                        'board-machine', 'M-BOARD', 'Board machine', 'Mill', '[]',
                        'board-calendar', '{}', 'available', 1);
                    INSERT INTO cases (id, part_number, name, working_folder_path)
                    VALUES ('board-case', 'PN-BOARD', 'Board case', 'C:\Cases\PN-BOARD');
                    INSERT INTO case_operations (
                        id, case_id, operation_number, route_position, name,
                        setup_seconds, cycle_seconds)
                    VALUES ('board-case-op', 'board-case', 10, 0, 'Mill', 60, 30);
                    INSERT INTO production_batches (
                        id, case_id, batch_number, status, planned_quantity)
                    VALUES ('board-batch', 'board-case', 'B-BOARD', 'waiting', 4);
                    INSERT INTO batch_operations (
                        id, production_batch_id, source_case_operation_id,
                        operation_number, route_position, name,
                        setup_seconds, cycle_seconds, status)
                    VALUES ('board-op', 'board-batch', 'board-case-op',
                            10, 0, 'Mill', 60, 30, 'not_started');
                    INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position)
                    VALUES ('board-assignment', 'board-op', 'board-machine', 0);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync("/api/v1/planning-board");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("current", document.RootElement.GetProperty("conflictCalculationStatus").GetString());
            Assert.Contains(
                "Timeline engine",
                document.RootElement.GetProperty("conflictCalculationMessage").GetString());

            var conflicts = document.RootElement.GetProperty("conflicts").EnumerateArray().ToArray();
            var calendarConflict = Assert.Single(
                conflicts,
                conflict => conflict.GetProperty("code").GetString() == "calendar_configuration_missing");
            Assert.Equal("blocking", calendarConflict.GetProperty("severity").GetString());
            Assert.Equal("Calendar configuration missing", calendarConflict.GetProperty("title").GetString());
            Assert.Equal(
                ["board-machine"],
                calendarConflict.GetProperty("machineIds").EnumerateArray()
                    .Select(value => value.GetString()!).ToArray());
            Assert.Equal("blocking", conflicts[0].GetProperty("severity").GetString());
            Assert.DoesNotContain(
                conflicts,
                conflict => conflict.GetProperty("code").GetString() == "unassigned_operation");

            Assert.Empty(await ReadConflictEventsAsync(client));

            using var timelineResponse = await client.GetAsync(
                "/api/v1/timeline?from=2026-09-01T00:00:00Z&to=2026-10-01T00:00:00Z");
            timelineResponse.EnsureSuccessStatusCode();
            var loggedOnce = await ReadConflictEventsAsync(client);
            Assert.Contains("calendar_configuration_missing", loggedOnce);

            using var repeatedTimelineResponse = await client.GetAsync(
                "/api/v1/timeline?from=2026-09-01T00:00:00Z&to=2026-10-01T00:00:00Z");
            repeatedTimelineResponse.EnsureSuccessStatusCode();
            Assert.Equal(loggedOnce, await ReadConflictEventsAsync(client));
        });
    }

    [Fact]
    public async Task Planning_board_keeps_readiness_context_per_operation_across_machines()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO working_calendars (id, name, time_zone_id, calendar_json)
                    VALUES ('board-calendar', 'Board calendar', 'UTC', '{}');
                    INSERT INTO machines (
                        id, number, name, machine_type, capabilities_json,
                        working_calendar_id, display_configuration_json, status, is_active,
                        usable_tool_positions)
                    VALUES
                        ('machine-a', 'M-A', 'Machine A', 'Mill', '[]', 'board-calendar', '{}', 'available', 1, 20),
                        ('machine-b', 'M-B', 'Machine B', 'Mill', '[]', 'board-calendar', '{}', 'available', 1, 30);
                    INSERT INTO cases (id, part_number, name, working_folder_path)
                    VALUES ('board-case', 'PN-BOARD', 'Board case', 'C:\Cases\PN-BOARD');
                    INSERT INTO case_operations (
                        id, case_id, operation_number, route_position, name,
                        setup_seconds, cycle_seconds)
                    VALUES ('board-case-op', 'board-case', 10, 0, 'Mill', 60, 30);
                    INSERT INTO production_batches (
                        id, case_id, batch_number, status, planned_quantity)
                    VALUES
                        ('batch-a', 'board-case', 'B-A', 'waiting', 4),
                        ('batch-b', 'board-case', 'B-B', 'waiting', 7);
                    INSERT INTO batch_operations (
                        id, production_batch_id, source_case_operation_id,
                        operation_number, route_position, name,
                        setup_seconds, cycle_seconds, status)
                    VALUES
                        ('op-a', 'batch-a', 'board-case-op', 10, 0, 'Mill', 60, 30, 'not_started'),
                        ('op-b', 'batch-b', 'board-case-op', 10, 0, 'Mill', 60, 30, 'not_started');
                    INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position)
                    VALUES
                        ('assignment-a', 'op-a', 'machine-a', 0),
                        ('assignment-b', 'op-b', 'machine-b', 0);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var response = await client.GetAsync("/api/v1/planning-board");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var operations = document.RootElement.GetProperty("machines").EnumerateArray()
                .SelectMany(machine => machine.GetProperty("backlog").EnumerateArray())
                .ToDictionary(operation => operation.GetProperty("batchOperationId").GetString()!);
            Assert.Equal(20, operations["op-a"].GetProperty("availableToolPositions").GetInt32());
            Assert.Equal(30, operations["op-b"].GetProperty("availableToolPositions").GetInt32());
        });
    }

    private static async Task<string[]> PoolIdsAsync(HttpClient client)
    {
        using var document = await ReadBoardAsync(client);
        return document.RootElement.GetProperty("pool").EnumerateArray()
            .Select(operation => operation.GetProperty("batchOperationId").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<string[]> BacklogIdsAsync(HttpClient client)
    {
        using var document = await ReadBoardAsync(client);
        return Assert.Single(document.RootElement.GetProperty("machines").EnumerateArray())
            .GetProperty("backlog").EnumerateArray()
            .Select(operation => operation.GetProperty("batchOperationId").GetString()!)
            .ToArray();
    }

    private static async Task<JsonDocument> ReadBoardAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/planning-board");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string[]> ReadConflictEventsAsync(HttpClient client)
    {
        using var response = await client.GetAsync(
            "/api/v1/event-log?eventType=timeline_conflict_detected&limit=500");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("reasonCode").GetString()!)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task RunWithServerAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.BoardEnrichment.Tests", Guid.NewGuid().ToString("N"));
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
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
