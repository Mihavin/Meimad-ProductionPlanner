using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.ToolPreparations;

namespace Meimad.Planner.Server.Tests.MachineAssignments;

/// <summary>
/// The Planning Board emulates Machine telemetry for Machines without DPRNT output (owner decisions
/// 2026-09-29): a reported status becomes the Production Run workflow event telemetry would have
/// produced, and an operation finished outside the plan can be marked finished.
/// </summary>
public sealed class ManualWorkflowStatusApiTests
{
    private const string Status = "/api/v1/batch-operations/operation-package/workflow-status";

    [Fact]
    public async Task Reported_statuses_become_workflow_events_that_the_tablet_qc_queue_board_and_statistics_use()
    {
        await using var server = await ToolPreparationApiTests.TestServer.StartAsync(verificationEnabled: false);
        await ReserveMaterialAsync(server);
        var client = server.Client;

        var operation = await BoardOperationAsync(client);
        Assert.True(operation.GetProperty("manualWorkflowReporting").GetBoolean());
        Assert.Equal("READY_FOR_SETUP", operation.GetProperty("workflowStatus").GetString());

        using var invalid = await client.PostAsJsonAsync(Status, new { status = "SETUP_START" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);

        // Setup run starts the operation, like the Machine's Offset Loader would.
        var setup = await ReportAsync(client, "IN_SETUP_RUN");
        Assert.Equal("READY_FOR_SETUP", setup.GetProperty("previousStatus").GetString());
        Assert.Equal("in_progress", await server.ScalarAsync("SELECT status FROM batch_operations WHERE id = 'operation-package';"));
        Assert.Equal("MANUAL_SETUP_RUN|PLANNER_MANUAL", await server.ScalarAsync("""
            SELECT event_type || '|' || source FROM production_run_workflow_events
            WHERE production_run_id = 'run:batch-operation:operation-package' ORDER BY server_received_at DESC LIMIT 1;
            """));

        await ReportAsync(client, "IN_QC");
        using (var queue = await client.GetAsync("/api/v1/qc-queue"))
        {
            using var json = JsonDocument.Parse(await queue.Content.ReadAsStringAsync());
            var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("run:batch-operation:operation-package", item.GetProperty("productionRunId").GetString());
        }
        await ReportAsync(client, "READY_FOR_PRODUCTION");
        await ReportAsync(client, "IN_PRODUCTION");
        // The same status again changes nothing.
        var again = await ReportAsync(client, "IN_PRODUCTION");
        Assert.Equal(JsonValueKind.Null, again.GetProperty("eventId").ValueKind);
        Assert.Equal("IN_PRODUCTION", (await BoardOperationAsync(client)).GetProperty("workflowStatus").GetString());
        Assert.Equal(4L, await server.ScalarAsync(
            "SELECT COUNT(*) FROM production_run_workflow_events WHERE source = 'PLANNER_MANUAL';"));

        // Finishing closes the reported production session with the parts it made.
        using var finish = await client.PostAsync("/api/v1/batch-operations/operation-package/finish", null);
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        Assert.Equal("PRODUCTION_SESSION_CLOSED|10", await server.ScalarAsync("""
            SELECT event_type || '|' || json_extract(metadata_json, '$.producedQuantity') FROM production_run_workflow_events
            WHERE production_run_id = 'run:batch-operation:operation-package' ORDER BY server_received_at DESC LIMIT 1;
            """));
        using var finished = await client.PostAsJsonAsync(Status, new { status = "IN_QC" });
        Assert.Equal(HttpStatusCode.Conflict, finished.StatusCode);
        Assert.Contains("operation_finished", await finished.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The reported setup, QC and production session are collected times of the Operation.
        using var statistics = await client.GetAsync("/api/v1/cases/case-package/operations/case-operation-package/time-statistics");
        using var statisticsJson = JsonDocument.Parse(await statistics.Content.ReadAsStringAsync());
        var kinds = statisticsJson.RootElement.GetProperty("samples").EnumerateArray()
            .Where(value => value.GetProperty("source").GetString() == "MANUAL")
            .Select(value => value.GetProperty("kind").GetString()!).Order().ToArray();
        Assert.Equal(new[] { "cycle", "qa", "setup" }, kinds);
    }

    [Fact]
    public async Task A_machine_with_dprnt_output_reports_itself_until_its_dprnt_output_is_switched_off()
    {
        await using var server = await ToolPreparationApiTests.TestServer.StartAsync(verificationEnabled: false);
        await ReserveMaterialAsync(server);
        await server.ExecuteAsync("""
            INSERT INTO machine_connections (
                id, machine_id, adapter_type, enabled, connection_status, polling_interval_ms,
                connection_timeout_ms, maximum_reconnect_backoff_ms, allow_read, allow_write,
                configuration_json, raw_telemetry_retention_days, version, created_at, updated_at)
            VALUES ('connection-package', 'machine-package', 'CUSTOM', 1, 'OFFLINE', 1000,
                3000, 30000, 1, 0, '{"dprnt":{"source":"TCP"}}', 14, 1, '2026-09-01T08:00:00Z', '2026-09-01T08:00:00Z');
            """);
        Assert.False((await BoardOperationAsync(server.Client)).GetProperty("manualWorkflowReporting").GetBoolean());
        using var refused = await server.Client.PostAsJsonAsync(Status, new { status = "IN_SETUP_RUN" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("machine_reports_workflow", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await server.ExecuteAsync("""
            UPDATE machine_connections SET configuration_json = '{"dprnt":{"source":"TCP","enabled":false}}'
            WHERE id = 'connection-package';
            """);
        Assert.True((await BoardOperationAsync(server.Client)).GetProperty("manualWorkflowReporting").GetBoolean());
        await ReportAsync(server.Client, "IN_SETUP_RUN");
    }

    [Fact]
    public async Task An_operation_finished_outside_the_plan_is_marked_finished_before_it_starts()
    {
        await using var server = await ToolPreparationApiTests.TestServer.StartAsync(verificationEnabled: false);
        using var finish = await server.Client.PostAsync("/api/v1/batch-operations/operation-package/finish", null);
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        Assert.Equal("completed|", await server.ScalarAsync(
            "SELECT status || '|' || COALESCE(actual_start, '') FROM batch_operations WHERE id = 'operation-package';"));
        Assert.Equal(0L, await server.ScalarAsync(
            "SELECT COUNT(*) FROM machine_assignments WHERE batch_operation_id = 'operation-package' AND released_at IS NULL;"));
    }

    [Fact]
    public void A_switched_off_dprnt_output_reads_as_no_dprnt_source()
    {
        Assert.Equal(CncDprntSources.None, SqliteProductionPackageRepository.DprntSource("""{"dprnt":{"source":"FILE","enabled":false}}"""));
        Assert.Equal(CncDprntSources.File, SqliteProductionPackageRepository.DprntSource("""{"dprnt":{"source":"FILE","enabled":true}}"""));
        Assert.Equal(CncDprntSources.Tcp, SqliteProductionPackageRepository.DprntSource("{}"));
        Assert.Equal("IN_SETUP_RUN", ManualWorkflowStatuses.Project("MANUAL_SETUP_RUN"));
        Assert.Equal("READY_FOR_SETUP", ManualWorkflowStatuses.Project("MANUAL_READY_FOR_SETUP"));
    }

    /// <summary>The Work Order's 10 material pieces are verified and reserved, so the operation may start.</summary>
    private static Task ReserveMaterialAsync(ToolPreparationApiTests.TestServer server) => server.ExecuteAsync("""
        INSERT INTO verified_material_receipts (id, case_id, quantity, received_at, verified_at, verified_by, source, created_at, updated_at)
        VALUES ('receipt-package', 'case-package', 10, '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z', 'test', 'LOCAL_VERIFIED',
                '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z');
        INSERT INTO batch_material_reservations (id, receipt_id, production_batch_id, quantity, reserved_at, reserved_by, created_at, updated_at)
        VALUES ('reservation-package', 'receipt-package', 'batch-package', 10, '2026-09-01T00:00:00Z', 'test',
                '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z');
        """);

    private static async Task<JsonElement> ReportAsync(HttpClient client, string status)
    {
        using var response = await client.PostAsJsonAsync(Status, new { status });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> BoardOperationAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/planning-board");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("machines").EnumerateArray()
            .SelectMany(machine => machine.GetProperty("backlog").EnumerateArray())
            .Single(value => value.GetProperty("batchOperationId").GetString() == "operation-package").Clone();
    }
}
