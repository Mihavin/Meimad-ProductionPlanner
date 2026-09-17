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
                        id, case_id, batch_number, status, planned_quantity)
                    VALUES ('board-batch', 'board-case', 'B-BOARD', 'waiting', 4);
                    INSERT INTO batch_allocations (
                        id, production_batch_id, allocation_type, order_id, quantity)
                    VALUES
                        ('board-allocation-z', 'board-batch', 'order', 'board-order-z', 2),
                        ('board-allocation-a', 'board-batch', 'order', 'board-order-a', 2);
                    INSERT INTO batch_operations (
                        id, production_batch_id, source_case_operation_id,
                        operation_number, route_position, name,
                        setup_seconds, cycle_seconds, status)
                    VALUES ('board-op', 'board-batch', 'board-case-op',
                            10, 0, 'Mill', 60, 30, 'not_started');
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
