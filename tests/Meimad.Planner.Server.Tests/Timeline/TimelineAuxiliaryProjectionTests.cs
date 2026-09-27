using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meimad.Planner.Server.Tests.Timeline;

/// <summary>
/// Auxiliary steps (rule 34) on the Timeline: the deterministic allocator places a Case Operation's
/// Workstation, Employee and External Resource requirements around the calculated Machine anchor,
/// reports missing configuration as conflicts, evaluates delivery risk only after every step has a
/// feasible slot, and honours planner pins. The Machine calculation itself is unchanged.
/// </summary>
public sealed class TimelineAuxiliaryProjectionTests
{
    private const string Horizon = "from=2026-08-11T00:00:00Z&to=2026-08-20T00:00:00Z&asOf=2026-08-11T08:00:00Z";

    [Fact]
    public async Task Requirements_are_placed_before_and_after_the_machine_and_sized_per_batch_and_per_part()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedAsync(application.Services, """
                INSERT INTO operation_resource_requirements (id, case_operation_id, sequence_position, resource_class, workstation_type_id,
                    capacity_required, estimated_duration_seconds, duration_per_unit_seconds, direction, name, step_number, created_at, updated_at)
                VALUES ('req-prepare', 'case-op-1', 0, 'WORKSTATION', 'type-inspection', 1, 1800, 0, 'BACKWARD', 'Incoming inspection', 20, '2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z'),
                       ('req-deburr', 'case-op-1', 1, 'WORKSTATION', 'type-deburr', 1, 600, 300, 'FORWARD', 'Deburr', 40, '2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z');
                INSERT INTO operation_resource_requirements (id, case_operation_id, sequence_position, resource_class, workstation_type_id,
                    capacity_required, estimated_duration_seconds, duration_per_unit_seconds, direction, name, step_number, predecessor_requirement_id, created_at, updated_at)
                VALUES ('req-final', 'case-op-1', 2, 'WORKSTATION', 'type-inspection', 1, 900, 0, 'FORWARD', 'Final inspection', 50, 'req-deburr', '2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z');
                """);

            using var response = await client.GetAsync($"/api/v1/timeline?{Horizon}");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;

            var machineOperation = root.GetProperty("machines")[0].GetProperty("intervals").EnumerateArray()
                .Where(interval => interval.GetProperty("operationId").GetString() == "op-1")
                .ToArray();
            var machineStart = machineOperation.Min(interval => interval.GetProperty("startsAt").GetDateTimeOffset());
            var machineEnd = machineOperation.Max(interval => interval.GetProperty("endsAt").GetDateTimeOffset());

            var lanes = root.GetProperty("resources").EnumerateArray().ToArray();
            var inspection = lanes.Single(lane => lane.GetProperty("resourceId").GetString() == "station-inspection");
            Assert.Equal("workstation", inspection.GetProperty("resourceClass").GetString());
            Assert.Equal("Inspection bench", inspection.GetProperty("name").GetString());
            var deburr = lanes.Single(lane => lane.GetProperty("resourceId").GetString() == "station-deburr");

            var prepare = inspection.GetProperty("intervals").EnumerateArray().Single(item => item.GetProperty("requirementId").GetString() == "req-prepare");
            Assert.Equal("BACKWARD", prepare.GetProperty("direction").GetString());
            Assert.Equal(machineStart, prepare.GetProperty("endsAt").GetDateTimeOffset());
            Assert.Equal(TimeSpan.FromMinutes(30), prepare.GetProperty("endsAt").GetDateTimeOffset() - prepare.GetProperty("startsAt").GetDateTimeOffset());
            Assert.Equal(20, prepare.GetProperty("stepNumber").GetInt32());
            Assert.Equal("B-1", prepare.GetProperty("batchNumber").GetString());
            Assert.False(prepare.GetProperty("isPinned").GetBoolean());

            var deburrStep = Assert.Single(deburr.GetProperty("intervals").EnumerateArray());
            Assert.Equal(machineEnd, deburrStep.GetProperty("startsAt").GetDateTimeOffset());
            // 600 s per batch plus 300 s per part for 2 parts.
            Assert.Equal(TimeSpan.FromSeconds(1200), deburrStep.GetProperty("endsAt").GetDateTimeOffset() - deburrStep.GetProperty("startsAt").GetDateTimeOffset());

            var final = inspection.GetProperty("intervals").EnumerateArray().Single(item => item.GetProperty("requirementId").GetString() == "req-final");
            Assert.Equal(deburrStep.GetProperty("endsAt").GetDateTimeOffset(), final.GetProperty("startsAt").GetDateTimeOffset());

            var batch = root.GetProperty("batches").EnumerateArray().Single(item => item.GetProperty("batchId").GetString() == "batch-1");
            Assert.Equal(final.GetProperty("endsAt").GetDateTimeOffset(), batch.GetProperty("predictedCompletion").GetDateTimeOffset());
            Assert.DoesNotContain(root.GetProperty("conflicts").EnumerateArray(),
                conflict => conflict.GetProperty("code").GetString()!.StartsWith("auxiliary_", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Missing_configuration_is_a_conflict_and_delivery_risk_needs_a_feasible_route()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedAsync(application.Services, """
                INSERT INTO operation_resource_requirements (id, case_operation_id, sequence_position, resource_class, workstation_type_id,
                    capacity_required, estimated_duration_seconds, duration_per_unit_seconds, direction, name, step_number, created_at, updated_at)
                VALUES ('req-paint', 'case-op-2', 0, 'WORKSTATION', 'type-paint', 1, 600, 0, 'FORWARD', 'Paint', 60, '2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z');
                """);

            using var response = await client.GetAsync($"/api/v1/timeline?{Horizon}");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var conflicts = document.RootElement.GetProperty("conflicts").EnumerateArray().ToArray();
            var missing = Assert.Single(conflicts, conflict => conflict.GetProperty("code").GetString() == "auxiliary_resource_configuration_missing");
            Assert.Equal("warning", missing.GetProperty("severity").GetString());
            Assert.Contains("step 60 'Paint'", missing.GetProperty("message").GetString());
            Assert.Contains("op-2", missing.GetProperty("operationIds").EnumerateArray().Select(value => value.GetString()));
            Assert.DoesNotContain(conflicts, conflict => conflict.GetProperty("code").GetString() == "delivery_at_risk");
            Assert.Empty(document.RootElement.GetProperty("resources").EnumerateArray());
        });
    }

    [Fact]
    public async Task External_lead_time_is_added_after_the_machine_and_flags_delivery_risk()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedAsync(application.Services, """
                INSERT INTO external_resources (id, name, supplier_name, promised_lead_time_minutes, safety_buffer_minutes, created_at, updated_at)
                VALUES ('external-chrome', 'Chrome plating', 'Chromate Ltd', 14400, 1440, '2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z');
                INSERT INTO operation_resource_requirements (id, case_operation_id, sequence_position, resource_class, external_resource_id,
                    capacity_required, estimated_duration_seconds, duration_per_unit_seconds, direction, name, step_number, created_at, updated_at)
                VALUES ('req-chrome', 'case-op-2', 0, 'EXTERNAL', 'external-chrome', 1, 0, 0, 'FORWARD', 'Chrome plate', 70, '2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z');
                """);

            using var response = await client.GetAsync($"/api/v1/timeline?{Horizon}");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            var lane = Assert.Single(root.GetProperty("resources").EnumerateArray());
            Assert.Equal("external", lane.GetProperty("resourceClass").GetString());
            var chrome = Assert.Single(lane.GetProperty("intervals").EnumerateArray());
            Assert.Equal(TimeSpan.FromMinutes(14400 + 1440), chrome.GetProperty("endsAt").GetDateTimeOffset() - chrome.GetProperty("startsAt").GetDateTimeOffset());
            var risk = Assert.Single(root.GetProperty("conflicts").EnumerateArray(), conflict => conflict.GetProperty("code").GetString() == "delivery_at_risk");
            Assert.Equal("attention", risk.GetProperty("severity").GetString());
            Assert.Contains("2026-08-12", risk.GetProperty("message").GetString());
        });
    }

    [Fact]
    public async Task A_pin_fixes_the_workstation_and_start_and_is_removed_again()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedAsync(application.Services, """
                INSERT INTO workstations (id, name, workstation_type_id, working_calendar_id, capacity, created_at, updated_at)
                VALUES ('station-inspection-2', 'Inspection bench 2', 'type-inspection', 'calendar-1', 1, '2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z');
                INSERT INTO operation_resource_requirements (id, case_operation_id, sequence_position, resource_class, workstation_type_id,
                    capacity_required, estimated_duration_seconds, duration_per_unit_seconds, direction, name, step_number, created_at, updated_at)
                VALUES ('req-final', 'case-op-1', 0, 'WORKSTATION', 'type-inspection', 1, 900, 0, 'FORWARD', 'Final inspection', 50, '2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z');
                UPDATE edit_tokens SET holder_client_id = 'pin-editor', holder_user_id = 'planner', generation = 1,
                    acquired_at = '2026-08-11T00:00:00Z', version = version + 1 WHERE id = 1;
                """);

            using var before = await client.GetAsync($"/api/v1/timeline?{Horizon}");
            using var beforeDocument = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
            var placed = beforeDocument.RootElement.GetProperty("resources").EnumerateArray()
                .Single(lane => lane.GetProperty("resourceId").GetString() == "station-inspection")
                .GetProperty("intervals").EnumerateArray().Single();
            var start = placed.GetProperty("startsAt").GetDateTimeOffset();

            using var forbidden = await client.PutAsJsonAsync("/api/v1/timeline/auxiliary-pins", new
            {
                batchOperationId = "op-1", requirementId = "req-final", workstationId = "station-inspection-2",
                plannedStartsAt = start.AddHours(1), plannedEndsAt = start.AddHours(1).AddMinutes(15), pinStart = true
            });
            Assert.Equal(HttpStatusCode.PreconditionRequired, forbidden.StatusCode);

            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "pin-editor");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
            using var pinned = await client.PutAsJsonAsync("/api/v1/timeline/auxiliary-pins", new
            {
                batchOperationId = "op-1", requirementId = "req-final", workstationId = "station-inspection-2",
                plannedStartsAt = start.AddHours(1), plannedEndsAt = start.AddHours(1).AddMinutes(15), pinStart = true
            });
            Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);

            using var after = await client.GetAsync($"/api/v1/timeline?{Horizon}");
            using var afterDocument = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
            var lane = afterDocument.RootElement.GetProperty("resources").EnumerateArray()
                .Single(value => value.GetProperty("resourceId").GetString() == "station-inspection-2");
            var interval = Assert.Single(lane.GetProperty("intervals").EnumerateArray());
            Assert.True(interval.GetProperty("isPinned").GetBoolean());
            Assert.Equal(start.AddHours(1), interval.GetProperty("startsAt").GetDateTimeOffset());
            Assert.DoesNotContain(afterDocument.RootElement.GetProperty("resources").EnumerateArray(),
                value => value.GetProperty("resourceId").GetString() == "station-inspection");

            using var cleared = await client.DeleteAsync("/api/v1/timeline/auxiliary-pins/op-1/req-final");
            Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
            using var clearedAgain = await client.DeleteAsync("/api/v1/timeline/auxiliary-pins/op-1/req-final");
            Assert.Equal(HttpStatusCode.NotFound, clearedAgain.StatusCode);

            using var restored = await client.GetAsync($"/api/v1/timeline?{Horizon}");
            using var restoredDocument = JsonDocument.Parse(await restored.Content.ReadAsStringAsync());
            var restoredInterval = restoredDocument.RootElement.GetProperty("resources").EnumerateArray()
                .Single(value => value.GetProperty("resourceId").GetString() == "station-inspection")
                .GetProperty("intervals").EnumerateArray().Single();
            Assert.False(restoredInterval.GetProperty("isPinned").GetBoolean());
            Assert.Equal(start, restoredInterval.GetProperty("startsAt").GetDateTimeOffset());
        });
    }

    private static async Task SeedAsync(IServiceProvider services, string extraSql)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id, calendar_json)
            VALUES ('calendar-1', 'Day shift', 'UTC',
                '{"availability":[{"startsAt":"2026-08-11T06:00:00Z","endsAt":"2026-08-11T22:00:00Z"},{"startsAt":"2026-08-12T06:00:00Z","endsAt":"2026-08-12T22:00:00Z"},{"startsAt":"2026-08-13T06:00:00Z","endsAt":"2026-08-19T22:00:00Z"}]}');
            INSERT INTO application_settings (key, value)
            VALUES ('timeline.setup_calendar_json', '{"availability":[{"startsAt":"2026-08-11T06:00:00Z","endsAt":"2026-08-19T22:00:00Z"}]}');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES ('machine-1', 'M-1', 'Mill One', 'mill', 'calendar-1', 'active', 1);
            INSERT INTO employee_resources (id, employee_number, name, resource_type, first_name, last_name, skills_json, assigned_calendar_id, is_active)
            VALUES ('resource-setup', 'E-SETUP', 'Setup Worker', 'setup_worker', 'Setup', 'Worker', '["machine-1"]', 'calendar-1', 1),
                   ('resource-qa', 'E-QA', 'QA Worker', 'qa_worker', 'QA', 'Worker', '[]', 'calendar-1', 1),
                   ('resource-regular', 'E-REG', 'Regular Worker', 'regular_worker', 'Regular', 'Worker', '[]', 'calendar-1', 1);
            INSERT INTO workstation_types (id, name, created_at, updated_at)
            VALUES ('type-inspection', 'Inspection', '2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z'),
                   ('type-deburr', 'Deburring', '2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z'),
                   ('type-paint', 'Painting', '2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z');
            INSERT INTO workstations (id, name, workstation_type_id, working_calendar_id, capacity, created_at, updated_at)
            VALUES ('station-inspection', 'Inspection bench', 'type-inspection', 'calendar-1', 1, '2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z'),
                   ('station-deburr', 'Deburr bench', 'type-deburr', 'calendar-1', 1, '2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z');
            INSERT INTO cases (id, part_number, name, working_folder_path) VALUES ('case-1', 'PN-1', 'Timeline Part', 'C:\Cases\PN-1');
            INSERT INTO orders (id, case_id, order_reference, quantity, work_finish_date, status)
            VALUES ('order-1', 'case-1', 'SO-10', 2, '2026-08-12', 'active');
            INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity) VALUES ('batch-1', 'case-1', 'B-1', 'waiting', 2);
            INSERT INTO batch_allocations (id, production_batch_id, allocation_type, order_id, quantity) VALUES ('allocation-1', 'batch-1', 'order', 'order-1', 2);
            INSERT INTO case_operations (id, case_id, operation_number, route_position, name, required_machine_type, setup_seconds, cycle_seconds, dependency_type, predecessor_case_operation_id)
            VALUES ('case-op-1', 'case-1', 10, 0, 'First', 'mill', 1800, 1800, 'independent', NULL),
                   ('case-op-2', 'case-1', 20, 1, 'Second', 'mill', 0, 900, 'sequential', 'case-op-1');
            INSERT INTO batch_operations (id, production_batch_id, source_case_operation_id, operation_number, route_position, name, required_machine_type,
                setup_seconds, cycle_seconds, status, dependency_type, predecessor_source_case_operation_id)
            VALUES ('op-1', 'batch-1', 'case-op-1', 10, 0, 'First', 'mill', 1800, 1800, 'not_started', 'independent', NULL),
                   ('op-2', 'batch-1', 'case-op-2', 20, 1, 'Second', 'mill', 0, 900, 'not_started', 'sequential', 'case-op-1');
            INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position)
            VALUES ('assignment-1', 'op-1', 'machine-1', 0), ('assignment-2', 'op-2', 'machine-1', 1);
            """ + extraSql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunWithServerAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "MeimadPlanner.TimelineAuxiliary.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directoryPath, "api-test.db")}", "--Timeline:TimeZoneId=UTC"],
            webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(new AuxiliaryFixedTimeProvider(DateTimeOffset.Parse("2026-08-11T08:00:00Z")));
                });
            });
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
            if (Directory.Exists(directoryPath)) Directory.Delete(directoryPath, recursive: true);
        }
    }

    private sealed class AuxiliaryFixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
