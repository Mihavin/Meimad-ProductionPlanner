using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.Timeline;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.GCode;

public sealed class GCodeReleaseApiTests
{
    [Fact]
    public async Task Released_nc_is_analyzed_once_and_evaluated_per_machine_without_overwriting_manual_cycle()
    {
        await RunAsync(async (application, client, _) =>
        {
            await SeedAsync(application.Services);
            AddEditorHeaders(client);
            await using (var connection = await application.Services
                             .GetRequiredService<SqliteDatabase>().OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE machines
                    SET rapid_rate_mm_per_min = 6000, tool_change_time_seconds = 10
                    WHERE id = 'machine-1';
                    UPDATE batch_operations
                    SET setup_seconds = 0, cycle_seconds = 300
                    WHERE id = 'batch-op-1';
                    INSERT INTO machines (
                        id, number, name, machine_type, working_calendar_id, status,
                        is_active, execution_mode, usable_tool_positions,
                        rapid_rate_mm_per_min, tool_change_time_seconds, machine_time_factor)
                    VALUES ('machine-2', 'M-2', 'CNC 2', 'mill', 'calendar-1', 'active',
                            1, 'CNC_GCODE', 30, 12000, 4, 1.2);
                    INSERT INTO machine_supported_postprocessors (machine_id, postprocessor_id)
                    VALUES ('machine-2', 'post-a');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var nc = Encoding.UTF8.GetBytes("""
                (machine-independent released program)
                N10 G21 G90
                N20 G0 X6000
                N30 G1 X6600 F600
                N40 T1 M6
                N50 G4 X2
                N60 M30
                """);
            var release = await ReleaseAsync(
                client, "post-a", "NEW_PROCESS_REVISION", "NC estimate release",
                nc, Encoding.UTF8.GetBytes("tool,position\nT1,1\n"),
                confirmNewProcess: true, reuseActiveTools: false,
                processDescription: "Initial estimated process");

            using (var response = await client.GetAsync(
                       "/api/v1/cases/case-1/operations/case-op-1/gcode"))
            {
                response.EnsureSuccessStatusCode();
                using var catalog = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var item = catalog.RootElement.GetProperty("releases").EnumerateArray()
                    .Single(value => value.GetProperty("gCodeReleaseId").GetString() == release.ReleaseId);
                var analysis = item.GetProperty("ncAnalysis");
                Assert.Equal("HIGH", analysis.GetProperty("confidence").GetString());
                Assert.Equal(60d, analysis.GetProperty("feedMotionSeconds").GetDouble(), 6);
                Assert.Equal(6000d, analysis.GetProperty("rapidDistanceMillimeters").GetDouble(), 6);
                Assert.Equal(2, item.GetProperty("machineCycleEstimates").GetArrayLength());
            }

            var machineOne = await BoardOperationAsync(client, "machine-1");
            Assert.Equal(300, machineOne.GetProperty("cycleTimePerPartSeconds").GetInt32());
            Assert.Equal("nc_estimate", machineOne.GetProperty("planningCycleTimeSource").GetString());
            Assert.Equal(132d, machineOne.GetProperty("planningCycleTimePerPartSeconds").GetDouble(), 6);
            Assert.Equal(60d, machineOne.GetProperty("toolLoadingTimeSeconds").GetDouble(), 6);
            Assert.Equal(0d, machineOne.GetProperty("fixtureSetupTimeSeconds").GetDouble(), 6);
            Assert.Equal(198d, machineOne.GetProperty("firstPieceProveOutTimeSeconds").GetDouble(), 6);
            Assert.Equal(258d, machineOne.GetProperty("totalSetupTimeSeconds").GetDouble(), 6);
            Assert.Equal(0, machineOne.GetProperty("remainingProductionQuantity").GetInt32());
            Assert.Equal(258d, machineOne.GetProperty("totalPlannedMachineTimeSeconds").GetDouble(), 6);
            Assert.True(machineOne.GetProperty("usesSetupOccupancyEstimate").GetBoolean());

            var timelineSource = application.Services.GetRequiredService<ITimelineSourceRepository>();
            var timelineSnapshot = await timelineSource.ReadAsync(
                DateTimeOffset.Parse("2026-08-20T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-21T00:00:00Z"),
                CancellationToken.None);
            var timelineOperation = Assert.Single(timelineSnapshot.Operations);
            Assert.Equal(132d, timelineOperation.CycleSeconds!.Value, 6);
            Assert.Equal(258d, timelineOperation.SetupSeconds!.Value, 6);
            Assert.Equal(0, timelineOperation.ProductionCycleQuantity);
            Assert.Equal(258d, timelineOperation.TotalPlannedMachineSeconds!.Value, 6);
            Assert.Equal(300, timelineOperation.ManualCycleSeconds);
            Assert.Equal("nc_estimate", timelineOperation.PlanningCycleTimeSource);

            using (var move = await client.PutAsJsonAsync(
                       "/api/v1/batch-operations/batch-op-1/assignment",
                       new { machineId = "machine-2", backlogPosition = 0 }))
            {
                move.EnsureSuccessStatusCode();
            }
            var machineTwo = await BoardOperationAsync(client, "machine-2");
            Assert.Equal(115.2d, machineTwo.GetProperty("planningCycleTimePerPartSeconds").GetDouble(), 6);

            using (var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/machines/machine-2"))
            {
                patch.Headers.TryAddWithoutValidation("If-Match", "\"machine:machine-2:v1\"");
                patch.Content = JsonContent.Create(new
                {
                    rapidRateMillimetersPerMinute = 6000,
                    toolChangeTimeSeconds = 4,
                    machineTimeFactor = 1.2
                });
                using var updated = await client.SendAsync(patch);
                updated.EnsureSuccessStatusCode();
            }

            var recalculated = await BoardOperationAsync(client, "machine-2");
            Assert.Equal(151.2d, recalculated.GetProperty("planningCycleTimePerPartSeconds").GetDouble(), 6);
            Assert.Equal(300, recalculated.GetProperty("cycleTimePerPartSeconds").GetInt32());

            await using var auditConnection = await application.Services
                .GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
            await using var audit = auditConnection.CreateCommand();
            audit.CommandText = """
                SELECT COUNT(*) FROM gcode_machine_cycle_estimates
                WHERE gcode_release_id = $releaseId AND machine_id = 'machine-2';
                """;
            audit.Parameters.AddWithValue("$releaseId", release.ReleaseId);
            Assert.True(Convert.ToInt32(await audit.ExecuteScalarAsync()) >= 3);
        });
    }

    [Fact]
    public async Task Contextual_readiness_is_explainable_plannable_selectable_and_blocks_start()
    {
        await RunAsync(async (application, client, _) =>
        {
            await SeedAsync(application.Services);
            AddEditorHeaders(client);
            var first = await ReleaseAsync(
                client, "post-a", "NEW_PROCESS_REVISION", "Initial readiness release",
                Encoding.UTF8.GetBytes("T1 M06\nM30\n"),
                Encoding.UTF8.GetBytes("tool,position\nT1,1\n"),
                confirmNewProcess: true, reuseActiveTools: false,
                processDescription: "Initial readiness process");

            using (var initialResponse = await client.GetAsync(
                       "/api/v1/batch-operations/batch-op-1/readiness"))
            {
                initialResponse.EnsureSuccessStatusCode();
                using var initial = JsonDocument.Parse(
                    await initialResponse.Content.ReadAsStringAsync());
                Assert.Equal("NOT_READY", initial.RootElement.GetProperty("overallState").GetString());
                Assert.Equal("READY", ReadinessState(initial.RootElement, "gcode"));
                Assert.Equal("READY", ReadinessState(initial.RootElement, "toolTable"));
                Assert.Equal("MISSING", ReadinessState(initial.RootElement, "toolOffsets"));
                Assert.Equal("UNVERIFIED", ReadinessState(initial.RootElement, "material"));
            }

            await SetOperationTimesAsync(application.Services);
            using (var timelineResponse = await client.GetAsync(
                       "/api/v1/timeline?from=2026-08-20T00:00:00Z&to=2026-08-21T00:00:00Z&asOf=2026-08-20T08:00:00Z"))
            {
                timelineResponse.EnsureSuccessStatusCode();
                using var timeline = JsonDocument.Parse(await timelineResponse.Content.ReadAsStringAsync());
                var interval = timeline.RootElement.GetProperty("machines").EnumerateArray()
                    .SelectMany(machine => machine.GetProperty("intervals").EnumerateArray())
                    .Single(value => value.TryGetProperty("operationId", out var id)
                        && id.GetString() == "batch-op-1");
                Assert.Equal("NOT_READY", interval.GetProperty("overallReadinessState").GetString());
                Assert.False(interval.GetProperty("isReadyForProduction").GetBoolean());
            }

            using (var blocked = await client.PostAsync(
                       "/api/v1/batch-operations/batch-op-1/start", null))
            {
                Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
                Assert.Equal("tool_offsets_missing", await ErrorCodeAsync(blocked));
            }
            await AssertOperationStillPlannedAsync(application.Services, "machine-1");

            await ReleaseAsync(
                client, "post-b", "LOCAL_POST_REVISION", "Second compatible post",
                Encoding.UTF8.GetBytes("T1 M06\nM30\n"), null,
                confirmNewProcess: false, reuseActiveTools: true);
            await AddMachinePostprocessorAsync(application.Services, "machine-1", "post-b");

            using (var multipleResponse = await client.GetAsync(
                       "/api/v1/batch-operations/batch-op-1/readiness"))
            {
                multipleResponse.EnsureSuccessStatusCode();
                using var multiple = JsonDocument.Parse(
                    await multipleResponse.Content.ReadAsStringAsync());
                Assert.True(multiple.RootElement.GetProperty("requiresExplicitGCodeSelection").GetBoolean());
                Assert.Equal("BLOCKED", ReadinessState(multiple.RootElement, "gcode"));
                Assert.Equal(2, multiple.RootElement.GetProperty("compatibleGCodeReleases").GetArrayLength());
            }

            using (var update = await client.PutAsJsonAsync(
                       "/api/v1/batch-operations/batch-op-1/readiness-inputs",
                       new
                       {
                           selectedGCodeReleaseId = first.ReleaseId,
                           materialStatus = "READY",
                           materialComment = "Material physically checked",
                           toolOffsetStatus = "READY",
                           toolOffsetComment = "Offsets checked on M-1"
                       }))
            {
                update.EnsureSuccessStatusCode();
                using var ready = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
                Assert.True(ready.RootElement.GetProperty("isReadyForProduction").GetBoolean());
                Assert.Equal("READY_FOR_PRODUCTION", ready.RootElement.GetProperty("overallState").GetString());
            }

            await AddMachineAsync(application.Services, "machine-2", "M-2", 30);
            await ReplaceMachinePostprocessorAsync(application.Services, "machine-2", "post-b");
            using (var move = await client.PutAsJsonAsync(
                       "/api/v1/batch-operations/batch-op-1/assignment",
                       new { machineId = "machine-2", backlogPosition = 0 }))
            {
                move.EnsureSuccessStatusCode();
            }
            var reassigned = await BoardOperationAsync(client, "machine-2");
            Assert.False(reassigned.GetProperty("isReadyForProduction").GetBoolean());
            Assert.Equal("INCOMPATIBLE",
                ReadinessState(reassigned, "gcode", "readinessComponents"));
            Assert.Equal("machine-2", reassigned.GetProperty("machineId").GetString());

            using (var moveBack = await client.PutAsJsonAsync(
                       "/api/v1/batch-operations/batch-op-1/assignment",
                       new { machineId = "machine-1", backlogPosition = 0 }))
            {
                moveBack.EnsureSuccessStatusCode();
            }
            var readyAgain = await BoardOperationAsync(client, "machine-1");
            Assert.True(readyAgain.GetProperty("isReadyForProduction").GetBoolean());

            using var started = await client.PostAsync(
                "/api/v1/batch-operations/batch-op-1/start", null);
            started.EnsureSuccessStatusCode();
        });
    }

    [Fact]
    public async Task Required_distinct_magazine_tools_drive_live_machine_capacity_and_block_start()
    {
        await RunAsync(async (application, client, _) =>
        {
            await SeedAsync(application.Services);
            AddEditorHeaders(client);
            var toolTable = Encoding.UTF8.GetBytes("""
                toolIdentifier,description,isRequired,requiresMagazinePosition,isActive,position
                T1,Drill,true,true,true,1
                T1,Duplicate setup row,true,true,true,1
                T99,Optional probe,false,true,true,2
                T50,Historical tool,true,true,false,3
                T200,External presetter,true,false,true,external
                T2,End mill,true,true,true,2
                """);
            await ReleaseAsync(
                client, "post-a", "NEW_PROCESS_REVISION", "Initial structured tools",
                Encoding.UTF8.GetBytes("M30\n"), toolTable,
                confirmNewProcess: true, reuseActiveTools: false,
                processDescription: "Initial manufacturing process");

            using (var catalogResponse = await client.GetAsync(
                       "/api/v1/cases/case-1/operations/case-op-1/gcode"))
            {
                catalogResponse.EnsureSuccessStatusCode();
                using var catalog = JsonDocument.Parse(await catalogResponse.Content.ReadAsStringAsync());
                var releasedTools = catalog.RootElement.GetProperty("activeProcessRevision").GetProperty("toolTable");
                Assert.Equal(2, releasedTools.GetProperty("requiredToolCount").GetInt32());
                Assert.Equal(6, releasedTools.GetProperty("tools").GetArrayLength());
            }

            await using (var immutableConnection = await application.Services
                             .GetRequiredService<SqliteDatabase>().OpenConnectionAsync())
            await using (var immutable = immutableConnection.CreateCommand())
            {
                immutable.CommandText = "UPDATE tool_table_release_tools SET is_required = 0;";
                await Assert.ThrowsAsync<SqliteException>(() => immutable.ExecuteNonQueryAsync());
            }

            var below = await BoardOperationAsync(client, "machine-1");
            Assert.Equal("satisfied", below.GetProperty("toolCapacityStatus").GetString());
            Assert.Equal(2, below.GetProperty("requiredToolCount").GetInt32());
            Assert.Equal(30, below.GetProperty("availableToolPositions").GetInt32());

            await SetMachineCapacityAsync(application.Services, "machine-1", 2);
            var equal = await BoardOperationAsync(client, "machine-1");
            Assert.Equal("satisfied", equal.GetProperty("toolCapacityStatus").GetString());
            Assert.True(equal.GetProperty("isToolCapacitySatisfied").GetBoolean());

            await SetMachineCapacityAsync(application.Services, "machine-1", 1);
            var mismatch = await BoardOperationAsync(client, "machine-1");
            Assert.Equal("tool_capacity_mismatch", mismatch.GetProperty("toolCapacityStatus").GetString());
            Assert.Equal(
                "Tool capacity mismatch: requires 2 tool positions; assigned machine supports 1.",
                mismatch.GetProperty("toolCapacityMessage").GetString());
            Assert.False(mismatch.GetProperty("isToolCapacitySatisfied").GetBoolean());
            Assert.Equal("machine-1", mismatch.GetProperty("machineId").GetString());

            using (var blocked = await client.PostAsync("/api/v1/batch-operations/batch-op-1/start", null))
            {
                Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
                Assert.Equal("tool_capacity_mismatch", await ErrorCodeAsync(blocked));
            }
            await AssertOperationStillPlannedAsync(application.Services, "machine-1");

            await AddMachineAsync(application.Services, "machine-2", "M-2", 2);
            using (var move = await client.PutAsJsonAsync(
                       "/api/v1/batch-operations/batch-op-1/assignment",
                       new { machineId = "machine-2", backlogPosition = 0 }))
            {
                move.EnsureSuccessStatusCode();
            }
            var changedMachine = await BoardOperationAsync(client, "machine-2");
            Assert.Equal("satisfied", changedMachine.GetProperty("toolCapacityStatus").GetString());
            Assert.Equal(2, changedMachine.GetProperty("availableToolPositions").GetInt32());

            var nextProcessTools = Encoding.UTF8.GetBytes("""
                toolIdentifier,description,isRequired,requiresMagazinePosition,isActive,position
                T1,Drill,true,true,true,1
                T2,End mill,true,true,true,2
                T3,Chamfer mill,true,true,true,3
                """);
            await ReleaseAsync(
                client, "post-a", "NEW_PROCESS_REVISION", "Added physical chamfer tool",
                Encoding.UTF8.GetBytes("T3 M06\nM30\n"), nextProcessTools,
                confirmNewProcess: true, reuseActiveTools: false,
                processDescription: "Added a required physical tool");
            var newProcessMismatch = await BoardOperationAsync(client, "machine-2");
            Assert.Equal(3, newProcessMismatch.GetProperty("requiredToolCount").GetInt32());
            Assert.Equal("tool_capacity_mismatch", newProcessMismatch.GetProperty("toolCapacityStatus").GetString());
            Assert.Equal("machine-2", newProcessMismatch.GetProperty("machineId").GetString());
        });
    }

    [Fact]
    public async Task Releases_preserve_process_post_and_file_history_and_started_work_stays_pinned()
    {
        await RunAsync(async (application, client, releaseRoot) =>
        {
            await SeedAsync(application.Services);
            AddEditorHeaders(client);

            var firstGCode = Encoding.UTF8.GetBytes("G90\nG0 X0\nM30\n");
            var firstTools = Encoding.UTF8.GetBytes("tool,position\nT1,1\n");
            var first = await ReleaseAsync(
                client, "post-a", "NEW_PROCESS_REVISION", "Initial production release",
                firstGCode, firstTools, confirmNewProcess: true, reuseActiveTools: false,
                processDescription: "Initial manufacturing process");
            Assert.Equal(1, first.ProcessRevisionNumber);
            Assert.Equal(1, first.PostSpecificRevision);
            Assert.Equal(Sha256(firstGCode), first.FileHash);

            using (var readiness = await client.PutAsJsonAsync(
                       "/api/v1/batch-operations/batch-op-1/readiness-inputs",
                       new
                       {
                           selectedGCodeReleaseId = first.ReleaseId,
                           materialStatus = "READY",
                           materialComment = "Physically verified",
                           toolOffsetStatus = "READY",
                           toolOffsetComment = "Offsets verified on Machine M-1"
                       }))
            {
                readiness.EnsureSuccessStatusCode();
            }

            using (var started = await client.PostAsync("/api/v1/batch-operations/batch-op-1/start", null))
            {
                started.EnsureSuccessStatusCode();
            }

            var postB = await ReleaseAsync(
                client, "post-b", "LOCAL_POST_REVISION", "Second postprocessor",
                Encoding.UTF8.GetBytes("G90\nM08\nM30\n"), null,
                confirmNewProcess: false, reuseActiveTools: true);
            Assert.Equal(first.ProcessRevisionId, postB.ProcessRevisionId);
            Assert.Equal(1, postB.PostSpecificRevision);
            Assert.Equal(first.ToolTableReleaseId, postB.ToolTableReleaseId);

            var local = await ReleaseAsync(
                client, "post-a", "LOCAL_POST_REVISION", "Post-only correction",
                Encoding.UTF8.GetBytes("G90\nG0 X1\nM30\n"), null,
                confirmNewProcess: false, reuseActiveTools: true);
            Assert.Equal(first.ProcessRevisionId, local.ProcessRevisionId);
            Assert.Equal(2, local.PostSpecificRevision);
            Assert.Equal(first.ToolTableReleaseId, local.ToolTableReleaseId);

            using (var localCatalogResponse = await client.GetAsync(
                       "/api/v1/cases/case-1/operations/case-op-1/gcode"))
            {
                localCatalogResponse.EnsureSuccessStatusCode();
                using var localCatalog = JsonDocument.Parse(
                    await localCatalogResponse.Content.ReadAsStringAsync());
                Assert.Equal(1, localCatalog.RootElement.GetProperty("activeProcessRevision")
                    .GetProperty("processRevisionNumber").GetInt32());
                Assert.Equal("current", Status(localCatalog.RootElement, "post-a"));
                Assert.Equal("current", Status(localCatalog.RootElement, "post-b"));
            }

            var secondTools = Encoding.UTF8.GetBytes("tool,position\nT2,2\n");
            var secondProcess = await ReleaseAsync(
                client, "post-a", "NEW_PROCESS_REVISION", "Changed tool sequence",
                Encoding.UTF8.GetBytes("G90\nT2 M06\nM30\n"), secondTools,
                confirmNewProcess: true, reuseActiveTools: false,
                processDescription: "Changed tool selection and machining sequence");
            Assert.Equal(2, secondProcess.ProcessRevisionNumber);
            Assert.Equal(1, secondProcess.PostSpecificRevision);
            Assert.NotEqual(first.ProcessRevisionId, secondProcess.ProcessRevisionId);
            Assert.NotEqual(first.ToolTableReleaseId, secondProcess.ToolTableReleaseId);

            using var catalogResponse = await client.GetAsync("/api/v1/cases/case-1/operations/case-op-1/gcode");
            catalogResponse.EnsureSuccessStatusCode();
            using var catalog = JsonDocument.Parse(await catalogResponse.Content.ReadAsStringAsync());
            var root = catalog.RootElement;
            Assert.Equal(2, root.GetProperty("activeProcessRevision").GetProperty("processRevisionNumber").GetInt32());
            Assert.Equal(2, root.GetProperty("processRevisions").GetArrayLength());
            Assert.Equal(4, root.GetProperty("releases").GetArrayLength());
            Assert.Equal("current", Status(root, "post-a"));
            Assert.Equal("stale", Status(root, "post-b"));
            Assert.DoesNotContain("draft", root.ToString(), StringComparison.OrdinalIgnoreCase);

            Assert.Equal(firstGCode, await client.GetByteArrayAsync(
                $"/api/v1/cases/case-1/operations/case-op-1/gcode-releases/{first.ReleaseId}/file"));
            Assert.Equal(firstTools, await client.GetByteArrayAsync(
                $"/api/v1/cases/case-1/operations/case-op-1/tool-table-releases/{first.ToolTableReleaseId}/file"));
            Assert.Equal(secondTools, await client.GetByteArrayAsync(
                $"/api/v1/cases/case-1/operations/case-op-1/tool-table-releases/{secondProcess.ToolTableReleaseId}/file"));

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            await using var pin = connection.CreateCommand();
            pin.CommandText = """
                SELECT production_process_revision_id, production_gcode_release_id,
                       production_tool_table_release_id, production_gcode_file_hash,
                       production_tool_table_file_hash
                FROM batch_operations WHERE id = 'batch-op-1';
                """;
            await using var reader = await pin.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(first.ProcessRevisionId, reader.GetString(0));
            Assert.Equal(first.ReleaseId, reader.GetString(1));
            Assert.Equal(first.ToolTableReleaseId, reader.GetString(2));
            Assert.Equal(first.FileHash, reader.GetString(3));
            Assert.Equal(Sha256(firstTools), reader.GetString(4));
            await reader.DisposeAsync();

            await using var immutable = connection.CreateCommand();
            immutable.CommandText = "UPDATE gcode_releases SET release_comment = 'overwrite' WHERE id = $id;";
            immutable.Parameters.AddWithValue("$id", first.ReleaseId);
            await Assert.ThrowsAsync<SqliteException>(() => immutable.ExecuteNonQueryAsync());

            Assert.Empty(Directory.GetDirectories(releaseRoot, ".staging-*", SearchOption.AllDirectories));
            Assert.Equal(6, Directory.GetFiles(releaseRoot, ".meimad-release-id", SearchOption.AllDirectories).Length);
        });
    }

    [Fact]
    public async Task Concurrent_local_releases_get_unique_post_revisions()
    {
        await RunAsync(async (application, client, releaseRoot) =>
        {
            await SeedAsync(application.Services);
            AddEditorHeaders(client);
            await ReleaseAsync(
                client, "post-a", "NEW_PROCESS_REVISION", "Initial",
                Encoding.UTF8.GetBytes("M30\n"), Encoding.UTF8.GetBytes("tool,position\nT1,1\n"),
                confirmNewProcess: true, reuseActiveTools: false,
                processDescription: "Initial manufacturing process");

            var releases = await Task.WhenAll(
                ReleaseAsync(
                    client, "post-a", "LOCAL_POST_REVISION", "Concurrent A",
                    Encoding.UTF8.GetBytes("G0 X1\nM30\n"), null,
                    confirmNewProcess: false, reuseActiveTools: true),
                ReleaseAsync(
                    client, "post-a", "LOCAL_POST_REVISION", "Concurrent B",
                    Encoding.UTF8.GetBytes("G0 X2\nM30\n"), null,
                    confirmNewProcess: false, reuseActiveTools: true));

            Assert.Equal(new[] { 2, 3 }, releases.Select(value => value.PostSpecificRevision).Order().ToArray());
            Assert.Equal(2, releases.Select(value => value.ReleaseId).Distinct().Count());
            Assert.Empty(Directory.GetDirectories(releaseRoot, ".staging-*", SearchOption.AllDirectories));

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var connection = await database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM gcode_releases WHERE postprocessor_id = 'post-a';";
            Assert.Equal(3L, (long)(await command.ExecuteScalarAsync())!);
        });
    }

    [Fact]
    public async Task Release_requires_explicit_process_and_tool_table_confirmation_without_partial_files()
    {
        await RunAsync(async (application, client, releaseRoot) =>
        {
            await SeedAsync(application.Services);
            AddEditorHeaders(client);
            using var response = await SendReleaseAsync(
                client, "post-a", "NEW_PROCESS_REVISION", "Unconfirmed",
                Encoding.UTF8.GetBytes("M30\n"), Encoding.UTF8.GetBytes("T1,1\n"),
                confirmNewProcess: false, reuseActiveTools: false, confirmTools: false,
                processDescription: "A real process change");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.False(Directory.Exists(releaseRoot));
        });
    }

    [Fact]
    public async Task Startup_recovery_removes_only_incomplete_or_marker_owned_orphans()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "MeimadPlanner.GCode.Recovery.Tests", Guid.NewGuid().ToString("N"));
        var releaseRoot = Path.Combine(root, "released-gcode");
        try
        {
            await using (var firstApplication = BuildApplication(root, releaseRoot))
            {
                await firstApplication.StartAsync();
                using var client = firstApplication.GetTestClient();
                await SeedAsync(firstApplication.Services);
                AddEditorHeaders(client);
                await ReleaseAsync(
                    client, "post-a", "NEW_PROCESS_REVISION", "Initial",
                    Encoding.UTF8.GetBytes("M30\n"), Encoding.UTF8.GetBytes("tool,position\nT1,1\n"),
                    confirmNewProcess: true, reuseActiveTools: false,
                    processDescription: "Initial manufacturing process");
                await firstApplication.StopAsync();
            }

            SqliteConnection.ClearAllPools();
            var knownMarkers = Directory.GetFiles(
                releaseRoot, ".meimad-release-id", SearchOption.AllDirectories);
            Assert.Equal(2, knownMarkers.Length);
            var staging = Path.Combine(releaseRoot, "operations", "case-op-1", "gcode", ".staging-crash");
            Directory.CreateDirectory(staging);
            await File.WriteAllTextAsync(Path.Combine(staging, "partial.nc"), "partial");
            var orphan = Path.Combine(releaseRoot, "operations", "case-op-1", "gcode", "orphan-id");
            Directory.CreateDirectory(orphan);
            await File.WriteAllTextAsync(Path.Combine(orphan, ".meimad-release-id"), "orphan-id");
            await File.WriteAllTextAsync(Path.Combine(orphan, "orphan.nc"), "M30");
            var unknown = Path.Combine(releaseRoot, "administrator-owned");
            Directory.CreateDirectory(unknown);
            await File.WriteAllTextAsync(Path.Combine(unknown, "keep.txt"), "keep");

            await using (var recoveredApplication = BuildApplication(root, releaseRoot))
            {
                await recoveredApplication.StartAsync();
                Assert.False(Directory.Exists(staging));
                Assert.False(Directory.Exists(orphan));
                Assert.True(File.Exists(Path.Combine(unknown, "keep.txt")));
                Assert.All(knownMarkers, marker => Assert.True(File.Exists(marker)));
                await recoveredApplication.StopAsync();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string Status(JsonElement catalog, string postprocessorId) =>
        catalog.GetProperty("postprocessors").EnumerateArray()
            .Single(value => value.GetProperty("postprocessorId").GetString() == postprocessorId)
            .GetProperty("status").GetString()!;

    private static async Task<JsonElement> BoardOperationAsync(HttpClient client, string machineId)
    {
        using var response = await client.GetAsync("/api/v1/planning-board");
        response.EnsureSuccessStatusCode();
        using var board = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return board.RootElement.GetProperty("machines").EnumerateArray()
            .Single(machine => machine.GetProperty("machineId").GetString() == machineId)
            .GetProperty("backlog")[0]
            .Clone();
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return error.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private static async Task SetMachineCapacityAsync(
        IServiceProvider services,
        string machineId,
        int capacity)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE machines SET usable_tool_positions = $capacity WHERE id = $id;";
        command.Parameters.AddWithValue("$capacity", capacity);
        command.Parameters.AddWithValue("$id", machineId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task AddMachineAsync(
        IServiceProvider services,
        string machineId,
        string number,
        int capacity)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO machines (
                id, number, name, machine_type, working_calendar_id, status,
                is_active, execution_mode, usable_tool_positions, machine_time_factor)
            VALUES ($id, $number, 'Second CNC', 'mill', 'calendar-1', 'active',
                    1, 'CNC_GCODE', $capacity, 1.0);
            INSERT INTO machine_supported_postprocessors (machine_id, postprocessor_id)
            VALUES ($id, 'post-a');
            """;
        command.Parameters.AddWithValue("$id", machineId);
        command.Parameters.AddWithValue("$number", number);
        command.Parameters.AddWithValue("$capacity", capacity);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetOperationTimesAsync(IServiceProvider services)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE batch_operations
            SET setup_seconds = 60, cycle_seconds = 60
            WHERE id = 'batch-op-1';
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AddMachinePostprocessorAsync(
        IServiceProvider services,
        string machineId,
        string postprocessorId)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO machine_supported_postprocessors (machine_id, postprocessor_id)
            VALUES ($machineId, $postprocessorId);
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        command.Parameters.AddWithValue("$postprocessorId", postprocessorId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ReplaceMachinePostprocessorAsync(
        IServiceProvider services,
        string machineId,
        string postprocessorId)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM machine_supported_postprocessors WHERE machine_id = $machineId;
            INSERT INTO machine_supported_postprocessors (machine_id, postprocessor_id)
            VALUES ($machineId, $postprocessorId);
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        command.Parameters.AddWithValue("$postprocessorId", postprocessorId);
        await command.ExecuteNonQueryAsync();
    }

    private static string ReadinessState(
        JsonElement root,
        string key,
        string componentsProperty = "components") =>
        root.GetProperty(componentsProperty).EnumerateArray()
            .Single(component => component.GetProperty("key").GetString() == key)
            .GetProperty("state").GetString()!;

    private static async Task AssertOperationStillPlannedAsync(
        IServiceProvider services,
        string expectedMachineId)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT batch_operations.status, machine_assignments.machine_id
            FROM batch_operations
            JOIN machine_assignments ON machine_assignments.batch_operation_id = batch_operations.id
            WHERE batch_operations.id = 'batch-op-1';
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("not_started", reader.GetString(0));
        Assert.Equal(expectedMachineId, reader.GetString(1));
    }

    private static async Task<ReleaseResult> ReleaseAsync(
        HttpClient client,
        string postprocessorId,
        string scope,
        string comment,
        byte[] gcode,
        byte[]? tools,
        bool confirmNewProcess,
        bool reuseActiveTools,
        string? processDescription = null)
    {
        using var response = await SendReleaseAsync(
            client, postprocessorId, scope, comment, gcode, tools,
            confirmNewProcess, reuseActiveTools, confirmTools: true, processDescription);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Release failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        return new ReleaseResult(
            root.GetProperty("gCodeReleaseId").GetString()!,
            root.GetProperty("processRevisionId").GetString()!,
            root.GetProperty("processRevisionNumber").GetInt32(),
            root.GetProperty("postSpecificRevision").GetInt32(),
            root.GetProperty("toolTableReleaseId").GetString()!,
            root.GetProperty("fileHash").GetString()!);
    }

    private static Task<HttpResponseMessage> SendReleaseAsync(
        HttpClient client,
        string postprocessorId,
        string scope,
        string comment,
        byte[] gcode,
        byte[]? tools,
        bool confirmNewProcess,
        bool reuseActiveTools,
        bool confirmTools,
        string? processDescription = null)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(postprocessorId), "postprocessorId");
        content.Add(new StringContent(scope), "changeScope");
        content.Add(new StringContent(comment), "releaseComment");
        content.Add(new StringContent(processDescription ?? comment), "processChangeDescription");
        content.Add(new StringContent(confirmNewProcess.ToString()), "confirmNewProcessRevision");
        content.Add(new StringContent(reuseActiveTools.ToString()), "reuseActiveToolTable");
        content.Add(new StringContent(confirmTools.ToString()), "confirmToolTable");
        content.Add(new ByteArrayContent(gcode), "gcodeFile", "program.nc");
        if (tools is not null)
        {
            content.Add(new ByteArrayContent(tools), "toolTableFile", "tools.csv");
        }

        return client.PostAsync(
            "/api/v1/cases/case-1/operations/case-op-1/gcode-releases",
            content);
    }

    private static void AddEditorHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "gcode-client");
        client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id)
            VALUES ('calendar-1', 'Day', 'UTC');
            INSERT INTO machines (
                id, number, name, machine_type, working_calendar_id, status,
                is_active, execution_mode, usable_tool_positions, machine_time_factor)
            VALUES ('machine-1', 'M-1', 'CNC 1', 'mill', 'calendar-1', 'active',
                    1, 'CNC_GCODE', 30, 1.0);
            INSERT INTO postprocessors (id, name, is_active)
            VALUES ('post-a', 'Post A', 1), ('post-b', 'Post B', 1);
            INSERT INTO machine_supported_postprocessors (machine_id, postprocessor_id)
            VALUES ('machine-1', 'post-a');
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ('case-1', 'PN-GCODE', 'G-code part', 'C:\Cases\PN-GCODE');
            INSERT INTO case_operations (id, case_id, operation_number, route_position, name)
            VALUES ('case-op-1', 'case-1', 10, 0, 'Mill');
            INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
            VALUES ('batch-1', 'case-1', 'B-1', 'waiting', 1);
            INSERT INTO batch_operations (
                id, production_batch_id, source_case_operation_id,
                operation_number, route_position, name, status)
            VALUES ('batch-op-1', 'batch-1', 'case-op-1', 10, 0, 'Mill', 'not_started');
            INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position)
            VALUES ('assignment-1', 'batch-op-1', 'machine-1', 0);
            UPDATE edit_tokens
            SET holder_client_id = 'gcode-client', holder_user_id = 'release-manager',
                generation = 1, acquired_at = '2026-08-20T00:00:00Z',
                updated_at = '2026-08-20T00:00:00Z'
            WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task RunAsync(
        Func<WebApplication, HttpClient, string, Task> test)
    {
        var root = Path.Combine(
            Path.GetTempPath(), "MeimadPlanner.GCode.Tests", Guid.NewGuid().ToString("N"));
        var releaseRoot = Path.Combine(root, "released-gcode");
        var application = BuildApplication(root, releaseRoot);
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            await test(application, client, releaseRoot);
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static WebApplication BuildApplication(string root, string releaseRoot) =>
        ServerApplication.Build(
            [
                "--Server:Host=127.0.0.1",
                "--Server:Port=5097",
                $"--Database:Path={Path.Combine(root, "test.db")}",
                $"--GCode:ReleaseRoot={releaseRoot}"
            ],
            webHost => webHost.UseTestServer());

    private sealed record ReleaseResult(
        string ReleaseId,
        string ProcessRevisionId,
        int ProcessRevisionNumber,
        int PostSpecificRevision,
        string ToolTableReleaseId,
        string FileHash);
}
