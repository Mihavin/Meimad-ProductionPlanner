using System.Net;
using System.Text.Json;
using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.ProductionRuns;

public sealed class ProductionRunDebugTimelineTests
{
    [Fact]
    public async Task Read_projects_human_messages_raw_clocks_attempt_state_and_anomalies()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var service = new ProductionRunDebugTimelineService(
            new SqliteProductionRunDebugTimelineRepository(fixture.Database));

        var value = await service.ReadAsync(
            "machine-debug", "run-debug", 50);

        Assert.Equal("10", value.MachineNumber);
        Assert.Equal("Debug Mill", value.MachineName);
        Assert.Equal("IN_PROGRESS", value.ProductionRunStatus);
        Assert.Equal(8, value.Items.Count);
        Assert.Equal(value.Items.OrderBy(item => item.ServerReceivedAt), value.Items);

        var verification = Assert.Single(value.Items,
            item => item.EventType == "SETUP_VERIFICATION_SUCCEEDED");
        Assert.Equal("Setup verification accepted; setup run started.", verification.Message);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-26T20:13:55Z"),
            verification.MachineTimestamp);

        var qcFail = Assert.Single(value.Items, item => item.EventType == "QC_FAIL");
        Assert.Equal(
            "QC failed; returned to setup run. Reason: Surface finish.",
            qcFail.Message);

        var start = Assert.Single(value.Items, item => item.EventType == "CYCLE_START");
        Assert.Equal(211L, start.SourceSequence);
        Assert.Equal("COMPLETED", start.AttemptState);
        Assert.Equal("Cycle started #211.", start.Message);

        var end = Assert.Single(value.Items, item => item.EventType == "CYCLE_END");
        Assert.Equal("COMPLETED", end.AttemptState);
        Assert.Equal("Cycle completed #212.", end.Message);

        var interruption = Assert.Single(value.Items,
            item => item.EventType == "CYCLE_INTERRUPTED");
        Assert.Equal(
            "Cycle CYCLE-213 interrupted by new START CYCLE-214; output was not counted.",
            interruption.Message);

        var anomaly = Assert.Single(value.Items, item => item.IsAnomaly);
        Assert.Equal("DATA_QUALITY_ANOMALY", anomaly.Kind);
        Assert.Equal("EVENT_SEQUENCE_GAP", anomaly.EventType);
        Assert.Equal(
            "CNC event sequence gap: expected 213, received 215.",
            anomaly.Message);

        var json = JsonSerializer.Serialize(value);
        Assert.DoesNotContain("metadataJson", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rawLine", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_applies_limit_across_workflow_and_anomaly_items()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var service = new ProductionRunDebugTimelineService(
            new SqliteProductionRunDebugTimelineRepository(fixture.Database));

        var value = await service.ReadAsync("machine-debug", "run-debug", 2);

        Assert.Equal(2, value.Items.Count);
        Assert.Equal("CYCLE_INTERRUPTED", value.Items[0].EventType);
        Assert.Equal("EVENT_SEQUENCE_GAP", value.Items[1].EventType);
    }

    [Fact]
    public async Task Read_rejects_invalid_limit_and_unrelated_machine_run_pair()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var service = new ProductionRunDebugTimelineService(
            new SqliteProductionRunDebugTimelineRepository(fixture.Database));

        await Assert.ThrowsAsync<ProductionRunDebugTimelineValidationException>(
            () => service.ReadAsync("machine-debug", "run-debug", 501));
        await Assert.ThrowsAsync<ProductionRunDebugTimelineNotFoundException>(
            () => service.ReadAsync("machine-other-debug", "run-debug", 50));
    }

    [Fact]
    public async Task Read_only_endpoint_returns_contract_and_maps_request_errors()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "MeimadPlanner.DebugTimeline.Tests",
            Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            [$"--Database:Path={Path.Combine(directory, "test.db")}"],
            webHost => webHost.UseTestServer());
        try
        {
            await application.StartAsync();
            await SeedAsync(application.Services.GetRequiredService<SqliteDatabase>());
            using var client = application.GetTestClient();

            using var response = await client.GetAsync(
                "/api/v1/machines/machine-debug/production-runs/run-debug/debug-timeline?limit=2");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            Assert.Equal("machine-debug", root.GetProperty("machineId").GetString());
            Assert.Equal("run-debug", root.GetProperty("productionRunId").GetString());
            Assert.Equal(2, root.GetProperty("items").GetArrayLength());
            Assert.False(root.TryGetProperty("metadataJson", out _));

            using var invalid = await client.GetAsync(
                "/api/v1/machines/machine-debug/production-runs/run-debug/debug-timeline?limit=501");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Contains(
                "invalid_debug_timeline_request",
                await invalid.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            using var missing = await client.GetAsync(
                "/api/v1/machines/machine-other-debug/production-runs/run-debug/debug-timeline");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            await application.StopAsync();
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static async Task SeedAsync(SqliteDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars(id,name,time_zone_id)
            VALUES('calendar-debug','Calendar','UTC');
            INSERT INTO machines(id,number,name,machine_type,working_calendar_id,status,is_active)
            VALUES
                ('machine-debug','10','Debug Mill','mill','calendar-debug','active',1),
                ('machine-other-debug','11','Other Mill','mill','calendar-debug','active',1);
            INSERT INTO cases(id,part_number,name,working_folder_path)
            VALUES('case-debug','P-12345','Debug Part','C:\\Cases\\P-12345');
            INSERT INTO production_batches(id,case_id,batch_number,status,planned_quantity)
            VALUES('batch-debug','case-debug','B-DEBUG','in_production',10);
            INSERT INTO case_operations(id,case_id,operation_number,route_position,name)
            VALUES('operation-master-debug','case-debug',30,0,'Mill');
            INSERT INTO batch_operations(
                id,production_batch_id,source_case_operation_id,operation_number,
                route_position,name,status)
            VALUES('operation-debug','batch-debug','operation-master-debug',30,0,'Mill','started');
            INSERT INTO production_runs(
                id,status,shared_setup_seconds,setup_snapshot_json,
                structure_locked_at,version,created_at,updated_at)
            VALUES('run-debug','DRAFT',0,'{}',NULL,1,
                   '2026-08-26T20:00:00Z','2026-08-26T20:00:00Z');
            INSERT INTO production_run_programs(
                id,production_run_id,manufacturing_program_id,sequence_position,
                target_cycle_count,completed_cycle_count,status,legacy_unmanaged,
                version,created_at,updated_at)
            VALUES('program-debug','run-debug','case-operation:operation-master-debug',
                   0,10,1,'ACTIVE',1,1,
                   '2026-08-26T20:00:00Z','2026-08-26T20:00:00Z');
            INSERT INTO machine_assignments(
                id,batch_operation_id,machine_id,backlog_position,planning_mode,
                production_run_id)
            VALUES('assignment-debug','operation-debug','machine-debug',0,'manual','run-debug');

            UPDATE production_runs
            SET status='IN_PROGRESS',structure_locked_at='2026-08-26T20:00:00Z'
            WHERE id='run-debug';

            INSERT INTO production_run_workflow_events(
                id,production_run_id,machine_id,event_type,source,source_event_id,
                source_sequence,server_received_at,machine_timestamp,user_id,metadata_json)
            VALUES
                ('debug-olc','run-debug','machine-debug','OFFSET_LOADER_COMPLETED',
                 'HAAS_DPRINT:MACHINE-DEBUG','OLC-1817',1817,
                 '2026-08-26T20:01:00Z',NULL,NULL,'{}'),
                ('debug-svs','run-debug','machine-debug','SETUP_VERIFICATION_SUCCEEDED',
                 'HAAS_DPRINT:MACHINE-DEBUG','SVS-1818',1818,
                 '2026-08-26T20:14:00Z','2026-08-26T20:13:55Z',NULL,'{}'),
                ('debug-qc-send','run-debug','machine-debug','SEND_TO_QC',
                 'TABLET','SEND-1',NULL,'2026-08-26T20:49:00Z',NULL,NULL,'{}'),
                ('debug-qc-fail','run-debug','machine-debug','QC_FAIL',
                 'WINDOWS_QC','QC-1',NULL,'2026-08-26T20:57:00Z',NULL,'quality-1',
                 '{"reason":"Surface finish."}'),
                ('debug-start','run-debug','machine-debug','CYCLE_START',
                 'HAAS_DPRINT:MACHINE-DEBUG','CYCLE-211',211,
                 '2026-08-26T21:27:00Z','2026-08-26T21:26:59Z',NULL,
                 '{"productionRunProgramId":"program-debug","rawLine":"secret raw evidence"}'),
                ('debug-end','run-debug','machine-debug','CYCLE_END',
                 'HAAS_DPRINT:MACHINE-DEBUG','CYCLE-212',212,
                 '2026-08-26T21:32:00Z','2026-08-26T21:31:58Z',NULL,
                 '{"productionRunProgramId":"program-debug"}'),
                ('debug-interrupted','run-debug','machine-debug','CYCLE_INTERRUPTED',
                 'SERVER_CYCLE','INTERRUPTED-CYCLE-213',NULL,
                 '2026-08-26T21:39:00Z',NULL,NULL,
                 '{"interruptedSourceEventId":"CYCLE-213","interruptedBySourceEventId":"CYCLE-214"}');
            INSERT INTO production_run_cycle_events(
                id,production_run_id,production_run_program_id,source,source_event_id,
                observed_at,completed_cycle_count,created_at,updated_at)
            VALUES('debug-cycle','run-debug','program-debug',
                   'HAAS_DPRINT:MACHINE-DEBUG','CYCLE-212','2026-08-26T21:32:00Z',1,
                   '2026-08-26T21:32:00Z','2026-08-26T21:32:00Z');
            INSERT INTO production_run_workflow_anomalies(
                id,production_run_id,machine_id,source,source_event_id,anomaly_type,
                previous_sequence,expected_sequence,received_sequence,
                workflow_event_id,detected_at,details_json)
            VALUES('debug-gap','run-debug','machine-debug','HAAS_DPRINT:MACHINE-DEBUG',
                   'CYCLE-215','EVENT_SEQUENCE_GAP',212,213,215,'debug-end',
                   '2026-08-26T21:40:00Z','{}');
            """;
        await command.ExecuteNonQueryAsync();
    }
}
