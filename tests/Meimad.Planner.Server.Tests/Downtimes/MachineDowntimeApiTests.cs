using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Downtimes;

public sealed class MachineDowntimeApiTests
{
    [Fact]
    public async Task Planned_maintenance_can_be_created_and_edited_with_optimistic_version()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedMachineAsync(application.Services);
            await GrantEditAsync(application.Services);
            AddEditHeaders(client);
            using var create = await client.PostAsJsonAsync("/api/v1/downtimes", new
            {
                downtimeType = "planned_maintenance",
                machineId = "machine-1",
                startsAt = "2026-08-11T09:00:00Z",
                endsAt = "2026-08-11T10:00:00Z",
                reason = "Spindle service",
                plannedBy = "Maintenance lead"
            });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("downtimeId").GetString()!;
            Assert.NotNull(create.Headers.ETag);

            using var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/downtimes/{id}")
            {
                Content = JsonContent.Create(new
                {
                    machineId = "machine-1",
                    startsAt = "2026-08-11T09:30:00Z",
                    endsAt = "2026-08-11T10:30:00Z",
                    reason = "Spindle and lubrication service",
                    plannedBy = "Maintenance lead"
                })
            };
            patch.Headers.TryAddWithoutValidation("If-Match", create.Headers.ETag.Tag);
            using var updated = await client.SendAsync(patch);
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var json = await updated.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("2026-08-11T09:30:00+00:00", json.GetProperty("startsAt").GetString());
            Assert.Equal(2, json.GetProperty("version").GetInt32());

            using var list = await client.GetAsync("/api/v1/downtimes?machineId=machine-1");
            list.EnsureSuccessStatusCode();
            Assert.Single((await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray());
        });
    }

    [Fact]
    public async Task Active_breakdown_blocks_to_horizon_then_restore_reopens_machine_and_timeline_explains_reason()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedTimelineAsync(application.Services);
            await GrantEditAsync(application.Services);
            AddEditHeaders(client);
            using var report = await client.PostAsJsonAsync("/api/v1/downtimes", new
            {
                downtimeType = "breakdown",
                machineId = "machine-1",
                startsAt = "2026-08-11T10:00:00Z",
                reason = "Hydraulic pressure loss",
                reportedBy = "Operator A"
            });
            Assert.Equal(HttpStatusCode.Created, report.StatusCode);
            var reported = await report.Content.ReadFromJsonAsync<JsonElement>();
            var id = reported.GetProperty("downtimeId").GetString()!;
            Assert.Equal(JsonValueKind.Null, reported.GetProperty("endsAt").ValueKind);

            using var blocked = await client.GetAsync("/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            blocked.EnsureSuccessStatusCode();
            var blockedJson = await blocked.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains(blockedJson.GetProperty("conflicts").EnumerateArray(),
                value => value.GetProperty("code").GetString() == "insufficient_availability");
            Assert.Contains(blockedJson.GetProperty("machines")[0].GetProperty("intervals").EnumerateArray(),
                value => value.GetProperty("type").GetString() == "downtime"
                    && value.GetProperty("detail").GetString()!.Contains("Hydraulic pressure loss", StringComparison.Ordinal));

            using var restoreRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/downtimes/{id}/restore")
            {
                Content = JsonContent.Create(new
                {
                    restoredAt = "2026-08-11T11:00:00Z",
                    repairNote = "Replaced pressure hose"
                })
            };
            restoreRequest.Headers.TryAddWithoutValidation("If-Match", report.Headers.ETag!.Tag);
            using var restored = await client.SendAsync(restoreRequest);
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);

            using var recalculated = await client.GetAsync("/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z");
            recalculated.EnsureSuccessStatusCode();
            var recalculatedJson = await recalculated.Content.ReadFromJsonAsync<JsonElement>();
            Assert.DoesNotContain(recalculatedJson.GetProperty("conflicts").EnumerateArray(),
                value => value.GetProperty("code").GetString() == "insufficient_availability");
            var operation = Assert.Single(recalculatedJson.GetProperty("machines")[0]
                .GetProperty("intervals").EnumerateArray(), value =>
                    value.GetProperty("operationId").GetString() == "operation-1"
                    && value.GetProperty("type").GetString() == "operation");
            var phases = operation.GetProperty("detail").GetString()!;
            Assert.Contains("to 2026-08-11T10:00:00", phases, StringComparison.Ordinal);
            Assert.Contains("Production 2026-08-11T11:00:00", phases, StringComparison.Ordinal);
            Assert.Contains(operation.GetProperty("phases").EnumerateArray(), phase =>
                phase.GetProperty("detail").GetString()?
                    .Contains("Hydraulic pressure loss", StringComparison.Ordinal) == true);
            var downtime = Assert.Single(recalculatedJson.GetProperty("machines")[0].GetProperty("intervals").EnumerateArray(),
                value => value.GetProperty("type").GetString() == "downtime");
            Assert.Equal(JsonValueKind.Null, downtime.GetProperty("operationId").ValueKind);
            Assert.Equal(JsonValueKind.Null, downtime.GetProperty("operationNumber").ValueKind);
            Assert.Equal(JsonValueKind.Null, downtime.GetProperty("machineAssignmentId").ValueKind);
            Assert.Contains("Operation delayed by Breakdown: Hydraulic pressure loss", downtime.GetProperty("detail").GetString(), StringComparison.Ordinal);
            Assert.Contains("Repair: Replaced pressure hose", downtime.GetProperty("detail").GetString(), StringComparison.Ordinal);

            await using var connection = await application.Services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT backlog_position FROM machine_assignments WHERE batch_operation_id = 'operation-1';";
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);

            using var events = await client.GetAsync("/api/v1/event-log?limit=50");
            events.EnsureSuccessStatusCode();
            var eventJson = await events.Content.ReadFromJsonAsync<JsonElement>();
            var eventTypes = eventJson.GetProperty("items").EnumerateArray()
                .Select(value => value.GetProperty("eventType").GetString()).ToArray();
            Assert.Contains("breakdown_reported", eventTypes);
            Assert.Contains("breakdown_restored", eventTypes);
            var conflict = Assert.Single(eventJson.GetProperty("items").EnumerateArray(), value =>
                value.GetProperty("eventType").GetString() == "timeline_conflict_detected"
                && value.GetProperty("reasonCode").GetString() == "insufficient_availability");
            Assert.Equal("system", conflict.GetProperty("user").GetString());
        });
    }

    private static async Task SeedMachineAsync(IServiceProvider services)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id, calendar_json)
            VALUES ('calendar-1', 'Day', 'UTC', '{"availability":[{"startsAt":"2026-08-11T08:00:00Z","endsAt":"2026-08-11T18:00:00Z"}]}');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES ('machine-1', 'M-1', 'Mill', 'mill', 'calendar-1', 'active', 1);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedTimelineAsync(IServiceProvider services)
    {
        await SeedMachineAsync(services);
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ('case-1', 'PN-1', 'Part', 'C:\Cases\PN-1');
            INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
            VALUES ('batch-1', 'case-1', 'B-1', 'waiting', 1);
            INSERT INTO case_operations (id, case_id, operation_number, route_position, name, required_machine_type, setup_seconds, cycle_seconds)
            VALUES ('case-operation-1', 'case-1', 10, 0, 'Mill', 'mill', 0, 14400);
            INSERT INTO batch_operations (id, production_batch_id, source_case_operation_id, operation_number, route_position, name, required_machine_type, setup_seconds, cycle_seconds, status)
            VALUES ('operation-1', 'batch-1', 'case-operation-1', 10, 0, 'Mill', 'mill', 0, 14400, 'not_started');
            INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position)
            VALUES ('assignment-1', 'operation-1', 'machine-1', 0);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static void AddEditHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "downtime-client");
        client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
    }

    private static async Task GrantEditAsync(IServiceProvider services)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE edit_tokens SET holder_client_id = 'downtime-client', holder_user_id = 'planner',
                generation = 1, acquired_at = '2026-08-11T00:00:00Z', version = version + 1 WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.Downtime.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directory, "test.db")}"],
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
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
