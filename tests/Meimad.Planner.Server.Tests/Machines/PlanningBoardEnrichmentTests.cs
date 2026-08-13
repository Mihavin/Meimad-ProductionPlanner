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
        });
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
