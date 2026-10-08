using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.ToolPreparations;
using Microsoft.Extensions.DependencyInjection;

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
    public async Task Supported_manual_setup_can_precede_material_but_physical_production_cannot()
    {
        await using var server = await ToolPreparationApiTests.TestServer.StartAsync(verificationEnabled: false);
        await ReportAsync(server.Client, "IN_SETUP_RUN");
        using var production = await server.Client.PostAsJsonAsync(Status, new { status = "IN_PRODUCTION" });
        Assert.Equal(HttpStatusCode.Conflict, production.StatusCode);
        Assert.Contains("production_not_ready", await production.Content.ReadAsStringAsync());
        Assert.Equal(0L, await server.ScalarAsync("SELECT COUNT(*) FROM production_run_workflow_events WHERE event_type='PRODUCTION_SESSION_OPENED';"));
        await ReserveMaterialAsync(server);
        await ReportAsync(server.Client, "IN_PRODUCTION");
    }

    [Theory]
    [InlineData("UPDATE machines SET usable_tool_positions=19 WHERE id='machine-package';")]
    [InlineData("UPDATE batch_operations SET version=version+1 WHERE id='operation-package';")]
    [InlineData("UPDATE machine_assignments SET version=version+1 WHERE id='assignment-package';")]
    public async Task Operation_start_rejects_a_stale_readiness_stamp_even_if_run_version_is_unchanged(string change)
    {
        await using var server = await ToolPreparationApiTests.TestServer.StartAsync(verificationEnabled: false);
        await ReserveMaterialAsync(server);
        using var response = await server.Client.GetAsync("/api/v1/batch-operations/operation-package/readiness");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var stamp = document.RootElement.GetProperty("actions").EnumerateArray()
            .Single(x => x.GetProperty("action").GetString() == "RunStart").GetProperty("contextStamp").GetString();
        await server.ExecuteAsync(change);
        using var start = new HttpRequestMessage(HttpMethod.Post, "/api/v1/batch-operations/operation-package/start");
        start.Headers.Add("If-Readiness-Match", stamp);
        using var result = await server.Client.SendAsync(start);
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        Assert.Contains("production_readiness_changed", await result.Content.ReadAsStringAsync());
        Assert.Equal("not_started", await server.ScalarAsync("SELECT status FROM batch_operations WHERE id='operation-package';"));
    }

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
    public async Task A_dprnt_machine_without_a_verified_package_gets_its_setup_reported_by_hand_and_counts_production_itself()
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

        // No package with Server verification: no Offset Loader can start the setup, so the planner
        // reports setup, QC and QC pass (owner decision 2026-10-07); production stays with DPRNT.
        var operation = await BoardOperationAsync(server.Client);
        Assert.True(operation.GetProperty("manualWorkflowReporting").GetBoolean());
        Assert.False(operation.GetProperty("manualProductionReporting").GetBoolean());
        using (var production = await server.Client.PostAsJsonAsync(Status, new { status = "IN_PRODUCTION" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, production.StatusCode);
            Assert.Contains("machine_reports_production", await production.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        await ReportAsync(server.Client, "IN_SETUP_RUN");
        await ReportAsync(server.Client, "IN_QC");
        await ReportAsync(server.Client, "READY_FOR_PRODUCTION");

        // After QC pass the Machine's next cycle start is production.
        var programId = (string)(await server.ScalarAsync(
            "SELECT id FROM production_run_programs WHERE production_run_id = 'run:batch-operation:operation-package';"))!;
        var cycle = await server.Application.Services.GetRequiredService<IProductionRunCncObservationRepository>()
            .ConsumeCycleEventAsync(new CncCycleObservation(
                "machine-package", "CYCLE_START", "NC-1-S-1", 1, 10, null, programId, "MEIMAD/V/1/EVENT/CST/ID/NC-1-S-1"), default);
        Assert.True(cycle.Accepted, cycle.Code);
        Assert.Equal("IN_PRODUCTION", (await BoardOperationAsync(server.Client)).GetProperty("workflowStatus").GetString());
        using (var parts = await server.Client.PostAsJsonAsync(
                   "/api/v1/batch-operations/operation-package/machined-parts", new { quantity = 2 }))
        {
            Assert.Equal(HttpStatusCode.Conflict, parts.StatusCode);
            Assert.Contains("machine_reports_workflow", await parts.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // A package with Server verification has an Offset Loader: the Machine reports everything.
        await server.ExecuteAsync("""
            INSERT INTO offset_loader_releases (
                id, production_run_id, machine_id, nc_release_id, tool_table_release_id,
                verification_release_token, created_at, created_by)
            VALUES ('offset-verified', 'run:batch-operation:operation-package', 'machine-package', 'gcode-1', 'tools-1',
                4242, '2026-09-01T08:00:00Z', 'test');
            INSERT INTO production_packages (
                id, batch_operation_id, machine_assignment_id, machine_id, tool_table_release_id, offset_loader_release_id, production_run_id,
                execution_mode, verification_enabled, verification_configuration_version, verification_macro_version,
                manifest_relative_path, manifest_hash, created_at, created_by)
            VALUES ('package-verified', 'operation-package', 'assignment-package', 'machine-package', 'tools-1', 'offset-verified', 'run:batch-operation:operation-package',
                'CNC_GCODE', 1, 1, 10, 'package-verified/manifest.json',
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', '2026-09-01T08:00:00Z', 'test');
            INSERT INTO production_package_current (batch_operation_id, machine_id, production_package_id, activated_at)
            VALUES ('operation-package', 'machine-package', 'package-verified', '2026-09-01T08:00:00Z');
            """);
        Assert.False((await BoardOperationAsync(server.Client)).GetProperty("manualWorkflowReporting").GetBoolean());
        using (var refused = await server.Client.PostAsJsonAsync(Status, new { status = "IN_SETUP_RUN" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("machine_reports_workflow", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // With the DPRNT output switched off everything is reported by hand again.
        await server.ExecuteAsync("""
            UPDATE machine_connections SET configuration_json = '{"dprnt":{"source":"TCP","enabled":false}}'
            WHERE id = 'connection-package';
            """);
        operation = await BoardOperationAsync(server.Client);
        Assert.True(operation.GetProperty("manualWorkflowReporting").GetBoolean());
        Assert.True(operation.GetProperty("manualProductionReporting").GetBoolean());
        await ReportAsync(server.Client, "IN_SETUP_RUN");
    }

    [Fact]
    public async Task Reported_machined_parts_set_the_current_quantity_and_leave_only_the_remaining_parts_to_plan()
    {
        await using var server = await ToolPreparationApiTests.TestServer.StartAsync(verificationEnabled: false);
        await ReserveMaterialAsync(server);
        var client = server.Client;
        var parts = "/api/v1/batch-operations/operation-package/machined-parts";

        using var notRunning = await client.PostAsJsonAsync(parts, new { quantity = 3 });
        Assert.Equal(HttpStatusCode.Conflict, notRunning.StatusCode);
        Assert.Contains("operation_not_in_progress", await notRunning.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await ReportAsync(client, "IN_PRODUCTION");
        using var negative = await client.PostAsJsonAsync(parts, new { quantity = -1 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, negative.StatusCode);

        using var reported = await client.PostAsJsonAsync(parts, new { quantity = 4, expectedQuantity = 0 });
        var body = await reported.Content.ReadAsStringAsync();
        Assert.True(reported.StatusCode == HttpStatusCode.OK, body);
        using (var json = JsonDocument.Parse(body))
        {
            Assert.Equal(4, json.RootElement.GetProperty("quantity").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("previousQuantity").GetInt32());
            Assert.Equal(10, json.RootElement.GetProperty("targetQuantity").GetInt32());
        }
        Assert.Equal("4|4", await server.ScalarAsync("""
            SELECT output.produced_quantity || '|' || program.completed_cycle_count
            FROM production_run_outputs output
            JOIN production_run_programs program ON program.id = output.production_run_program_id
            WHERE output.batch_operation_id = 'operation-package';
            """));

        // A stale report is refused without changing anything; a correction downward is allowed.
        using var stale = await client.PostAsJsonAsync(parts, new { quantity = 6, expectedQuantity = 0 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("machined_quantity_stale", await stale.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var corrected = await client.PostAsJsonAsync(parts, new { quantity = 3, expectedQuantity = 4 });
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);

        // Every part machined is finished with the existing Finish action, not by a count.
        using var all = await client.PostAsJsonAsync(parts, new { quantity = 10 });
        Assert.Equal(HttpStatusCode.Conflict, all.StatusCode);
        Assert.Contains("quantity_reaches_target", await all.Content.ReadAsStringAsync(), StringComparison.Ordinal);
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
