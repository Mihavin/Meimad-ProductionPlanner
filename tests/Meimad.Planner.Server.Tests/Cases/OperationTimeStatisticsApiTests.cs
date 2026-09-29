using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.ToolPreparations;
using Microsoft.AspNetCore.TestHost;

namespace Meimad.Planner.Server.Tests.Cases;

/// <summary>
/// Real Machine times before the NC and the Operation's own times, the operation statistics and
/// applying a time to the Case Operation with history (owner request 2026-09-29).
/// </summary>
public sealed class OperationTimeStatisticsApiTests
{
    private const string Statistics = "/api/v1/cases/case-package/operations/case-operation-package/time-statistics";

    [Fact]
    public async Task The_board_uses_the_operation_time_then_the_nc_time_then_the_real_machine_time()
    {
        await using var server = await ToolPreparationApiTests.TestServer.StartAsync(verificationEnabled: false);

        var operation = await BoardOperationAsync(server.Client);
        Assert.Equal("manual", operation.GetProperty("planningCycleTimeSource").GetString());
        Assert.Equal(60, operation.GetProperty("planningCycleTimePerPartSeconds").GetDouble());

        await SeedNcEstimateAsync(server, 45);
        operation = await BoardOperationAsync(server.Client);
        Assert.Equal("nc_estimate", operation.GetProperty("planningCycleTimeSource").GetString());
        Assert.Equal(45, operation.GetProperty("planningCycleTimePerPartSeconds").GetDouble());
        Assert.Equal("setup_estimate", operation.GetProperty("setupTimeSource").GetString());

        await SeedMeasuredHistoryAsync(server);
        operation = await BoardOperationAsync(server.Client);
        Assert.Equal("measured_median", operation.GetProperty("planningCycleTimeSource").GetString());
        Assert.Equal(120, operation.GetProperty("planningCycleTimePerPartSeconds").GetDouble());
        Assert.Equal(3, operation.GetProperty("measuredCycleSamples").GetInt32());
        Assert.Equal(("measured_median", 2400d), (operation.GetProperty("setupTimeSource").GetString(), operation.GetProperty("totalSetupTimeSeconds").GetDouble()));
        Assert.Equal(("measured_median", 600), (operation.GetProperty("qaTimeSource").GetString(), operation.GetProperty("qaTimeAfterSetupSeconds").GetInt32()));
        Assert.Equal(("measured_median", 40), (operation.GetProperty("loadUnloadTimeSource").GetString(), operation.GetProperty("loadUnloadTimeSeconds").GetInt32()));

        // The timeline plans with the same real time.
        using var timeline = await server.Client.GetAsync("/api/v1/timeline?from=2026-09-01T00:00:00Z&to=2026-09-30T00:00:00Z&asOf=2026-09-01T00:00:00Z");
        using var timelineJson = JsonDocument.Parse(await timeline.Content.ReadAsStringAsync());
        var interval = timelineJson.RootElement.GetProperty("machines").EnumerateArray()
            .SelectMany(machine => machine.GetProperty("intervals").EnumerateArray())
            .First(value => value.TryGetProperty("operationId", out var id) && id.GetString() == "operation-package");
        Assert.Equal("measured_median", interval.GetProperty("planningCycleTimeSource").GetString());
    }

    [Fact]
    public async Task The_statistics_show_the_collected_times_and_applying_one_changes_the_operation_and_keeps_history()
    {
        await using var server = await ToolPreparationApiTests.TestServer.StartAsync(verificationEnabled: false);
        await SeedNcEstimateAsync(server, 45);
        await SeedMeasuredHistoryAsync(server);

        var statistics = await ReadAsync(server.Client);
        Assert.Equal((60, 60, 0, 0), (statistics.GetProperty("setupSeconds").GetInt32(), statistics.GetProperty("cycleSeconds").GetInt32(),
            statistics.GetProperty("qaSeconds").GetInt32(), statistics.GetProperty("loadUnloadSeconds").GetInt32()));
        var machine = Assert.Single(statistics.GetProperty("machines").EnumerateArray());
        Assert.Equal(("machine-package", "M-PKG"), (machine.GetProperty("machineId").GetString(), machine.GetProperty("machineNumber").GetString()));
        Assert.Equal(45, machine.GetProperty("ncCycleSeconds").GetDouble());
        // NC setup: 2 tools x 60 s + the Operation's 60 s + one first piece at 1.5 x 45 s.
        Assert.Equal(247.5, machine.GetProperty("ncSetupSeconds").GetDouble());
        Assert.Equal((120d, 3), (machine.GetProperty("measuredCycle").GetProperty("medianSeconds").GetDouble(),
            machine.GetProperty("measuredCycle").GetProperty("sampleCount").GetInt32()));
        Assert.Equal(2400, machine.GetProperty("measuredSetup").GetProperty("medianSeconds").GetDouble());
        Assert.Equal(600, machine.GetProperty("measuredQa").GetProperty("medianSeconds").GetDouble());
        Assert.Equal(40, machine.GetProperty("measuredLoadUnload").GetProperty("medianSeconds").GetDouble());
        // The setup time is the fixture part: 2400 - 2 x 60 tool loading - 1.5 x 120 first piece.
        Assert.Equal(2100, machine.GetProperty("setupApplySeconds").GetInt32());
        var samples = statistics.GetProperty("samples").EnumerateArray().ToArray();
        Assert.Equal(3, samples.Count(value => value.GetProperty("kind").GetString() == "cycle" && value.GetProperty("inMedian").GetBoolean()));
        Assert.Contains(samples, value => value.GetProperty("batchNumber").GetString() == "B-OLD");

        // Only the Cases permission applies a time, based on the version it read.
        using var anonymous = new HttpClient(server.Application.GetTestServer().CreateHandler()) { BaseAddress = server.Client.BaseAddress };
        using (anonymous.SignedInWithOnly(Permissions.PrepareTools))
        {
            using var refused = await anonymous.PostAsJsonAsync(Statistics + "/apply", Apply("cycle", "NC", 1));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        using var ncSetup = await server.Client.PostAsJsonAsync(Statistics + "/apply", Apply("setup", "NC", 1));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ncSetup.StatusCode);
        Assert.Contains("nc_time_unavailable", await ncSetup.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var applied = await server.Client.PostAsJsonAsync(Statistics + "/apply", Apply("cycle", "NC", 1));
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var afterNc = JsonDocument.Parse(await applied.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal((45, 2), (afterNc.GetProperty("cycleSeconds").GetInt32(), afterNc.GetProperty("version").GetInt32()));
        // The pending Work Order follows its Case Operation.
        Assert.Equal(45L, await server.ScalarAsync("SELECT cycle_seconds FROM batch_operations WHERE id = 'operation-package';"));

        using var stale = await server.Client.PostAsJsonAsync(Statistics + "/apply", Apply("setup", "MEASURED", 1));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("resource_version_stale", await stale.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        foreach (var (kind, version) in new[] { ("setup", 2), ("qa", 3), ("load_unload", 4), ("cycle", 5) })
        {
            using var response = await server.Client.PostAsJsonAsync(Statistics + "/apply", Apply(kind, "MEASURED", version));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var final = await ReadAsync(server.Client);
        Assert.Equal((2100, 120, 600, 40, 6), (final.GetProperty("setupSeconds").GetInt32(), final.GetProperty("cycleSeconds").GetInt32(),
            final.GetProperty("qaSeconds").GetInt32(), final.GetProperty("loadUnloadSeconds").GetInt32(), final.GetProperty("version").GetInt32()));

        // A manual edit of a time is kept in the same history.
        using var edit = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/cases/case-package/operations/case-operation-package")
        {
            Content = JsonContent.Create(new { qaTimeAfterSetupSeconds = 300 })
        };
        edit.Headers.TryAddWithoutValidation("If-Match", "\"case-operation:case-operation-package:v6\"");
        using var edited = await server.Client.SendAsync(edit);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var history = (await ReadAsync(server.Client)).GetProperty("history").EnumerateArray().ToArray();
        Assert.Equal(6, history.Length);
        Assert.Equal(("qa", "MANUAL", 600, 300), (history[0].GetProperty("kind").GetString(), history[0].GetProperty("source").GetString(),
            history[0].GetProperty("previousSeconds").GetInt32(), history[0].GetProperty("newSeconds").GetInt32()));
        var nc = history.Last();
        Assert.Equal(("cycle", "NC", 60, 45, "gcode-1"), (nc.GetProperty("kind").GetString(), nc.GetProperty("source").GetString(),
            nc.GetProperty("previousSeconds").GetInt32(), nc.GetProperty("newSeconds").GetInt32(), nc.GetProperty("gCodeReleaseId").GetString()));
        var setup = history.Single(value => value.GetProperty("kind").GetString() == "setup");
        Assert.Equal(("MEASURED", 2400d, 1, "M-PKG"), (setup.GetProperty("source").GetString(), setup.GetProperty("basisSeconds").GetDouble(),
            setup.GetProperty("sampleCount").GetInt32(), setup.GetProperty("machineNumber").GetString()));
        await Assert.ThrowsAnyAsync<Exception>(() => server.ScalarAsync("UPDATE case_operation_time_changes SET new_seconds = 1;"));
    }

    [Fact]
    public void The_median_uses_the_last_ten_measurements()
    {
        var samples = Enumerable.Range(1, 12)
            .Select(index => new OperationTimeSample("cycle", "op", "m", index == 12 ? 1000 : index * 10,
                DateTimeOffset.UnixEpoch.AddMinutes(index), "CNC", null))
            .ToArray();
        // The two oldest (10 and 20 s) fall out; the median of 30..110 and 1000 is (70 + 80) / 2.
        var median = SqliteOperationTimeMeasurements.Median(samples, "cycle")!;
        Assert.Equal((75d, 10), (median.MedianSeconds, median.SampleCount));
        Assert.Null(SqliteOperationTimeMeasurements.Median(samples, "setup"));
        Assert.False(SqliteOperationTimeMeasurements.LoadingIsPerPart(automaticLoading: true, null));
        Assert.False(SqliteOperationTimeMeasurements.LoadingIsPerPart(false, 5));
        Assert.True(SqliteOperationTimeMeasurements.LoadingIsPerPart(false, 1));
    }

    private static object Apply(string kind, string source, int expectedVersion) =>
        new { kind, source, machineId = "machine-package", expectedVersion };

    private static async Task<JsonElement> ReadAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Statistics);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> BoardOperationAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/planning-board");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("machines").EnumerateArray()
            .SelectMany(machine => machine.GetProperty("backlog").EnumerateArray())
            .Single(value => value.GetProperty("batchOperationId").GetString() == "operation-package").Clone();
    }

    private static Task SeedNcEstimateAsync(ToolPreparationApiTests.TestServer server, double cycleSeconds) => server.ExecuteAsync($"""
        INSERT INTO gcode_release_analyses(gcode_release_id,parser_version,status,raw_feed_seconds,rapid_distance_mm,
            tool_change_count,dwell_seconds,warnings_json,unsupported_constructs_json,confidence,analyzed_at)
        VALUES('gcode-1','nc-engine/1','COMPLETE',30,100,2,0,'[]','[]','HIGH','2026-09-01T08:00:00Z');
        INSERT INTO gcode_machine_cycle_estimates(id,gcode_release_id,machine_id,parser_version,raw_feed_seconds,
            rapid_distance_mm,tool_change_count,dwell_seconds,machine_time_factor,raw_cycle_seconds,
            estimated_cycle_seconds,warnings_json,confidence,calculated_at)
        VALUES('estimate-1','gcode-1','machine-package','nc-engine/1',30,100,2,0,1,{cycleSeconds},{cycleSeconds},'[]','HIGH','2026-09-01T08:00:00Z');
        """);

    /// <summary>
    /// An earlier, finished Work Order of the same Case Operation on the same Machine: setup from the
    /// Offset Loader to Send to QC 40 min, QC 10 min, cycles of 100, 120 and 140 s with 30 and 50 s
    /// between them.
    /// </summary>
    private static Task SeedMeasuredHistoryAsync(ToolPreparationApiTests.TestServer server) => server.ExecuteAsync("""
        INSERT INTO production_batches(id,case_id,batch_number,status,planned_quantity)
        VALUES('batch-old','case-package','B-OLD','complete',3);
        INSERT INTO batch_operations(id,production_batch_id,source_case_operation_id,operation_number,route_position,name,
            required_machine_type,setup_seconds,cycle_seconds,status)
        VALUES('operation-old','batch-old','case-operation-package',10,0,'Finish','mill',60,60,'completed');
        INSERT INTO production_runs(id,status,legacy_batch_operation_id) VALUES('run-old','PLANNED','operation-old');
        INSERT INTO production_run_programs(id,production_run_id,manufacturing_program_id,process_revision_id,sequence_position,
            target_cycle_count,completed_cycle_count,status,cycle_seconds_snapshot,created_at,updated_at)
        VALUES('program-old','run-old','case-operation:case-operation-package','process-1',0,3,3,'COMPLETED',60,
               '2026-08-01T08:00:00Z','2026-08-01T08:00:00Z');
        INSERT INTO production_run_outputs(id,production_run_program_id,batch_operation_id,revision_output_id,quantity_per_cycle,
            target_quantity,produced_quantity,status,created_at,updated_at)
        VALUES('output-old','program-old','operation-old','output-1',1,3,3,'COMPLETED','2026-08-01T08:00:00Z','2026-08-01T08:00:00Z');
        UPDATE production_runs SET status = 'COMPLETED', structure_locked_at = '2026-08-01T08:00:00Z' WHERE id = 'run-old';
        INSERT INTO production_run_workflow_events(id,production_run_id,machine_id,event_type,source,source_event_id,source_sequence,server_received_at)
        VALUES('old-olc','run-old','machine-package','OFFSET_LOADER_COMPLETED','CNC','olc-1',1,'2026-08-01T08:00:00.0000000+00:00'),
              ('old-qc','run-old','machine-package','SEND_TO_QC','TABLET','qc-1',NULL,'2026-08-01T08:40:00.0000000+00:00'),
              ('old-pass','run-old','machine-package','QC_PASS','WINDOWS_QC','pass-1',NULL,'2026-08-01T08:50:00.0000000+00:00'),
              ('old-s1','run-old','machine-package','CYCLE_START','CNC','c1s',2,'2026-08-01T09:00:00.0000000+00:00'),
              ('old-e1','run-old','machine-package','CYCLE_END','CNC','c1e',3,'2026-08-01T09:01:40.0000000+00:00'),
              ('old-s2','run-old','machine-package','CYCLE_START','CNC','c2s',4,'2026-08-01T09:02:10.0000000+00:00'),
              ('old-e2','run-old','machine-package','CYCLE_END','CNC','c2e',5,'2026-08-01T09:04:10.0000000+00:00'),
              ('old-s3','run-old','machine-package','CYCLE_START','CNC','c3s',6,'2026-08-01T09:05:00.0000000+00:00'),
              ('old-e3','run-old','machine-package','CYCLE_END','CNC','c3e',7,'2026-08-01T09:07:20.0000000+00:00');
        -- The cycle starts opened their attempts; the counted cycle ends complete them.
        INSERT INTO production_run_cycle_attempt_outcomes(attempt_id,completion_state,outcome_workflow_event_id,boundary_source,
            boundary_source_event_id,boundary_source_sequence,end_server_received_at,end_machine_timestamp,created_at)
        SELECT 'attempt:old-s' || n, 'COMPLETED', 'old-e' || n, 'CNC', 'c' || n || 'e', 1 + 2 * n, server_received_at, NULL, server_received_at
        FROM (SELECT 1 AS n UNION ALL SELECT 2 UNION ALL SELECT 3) numbers
        JOIN production_run_workflow_events ON id = 'old-e' || n;
        """);
}
