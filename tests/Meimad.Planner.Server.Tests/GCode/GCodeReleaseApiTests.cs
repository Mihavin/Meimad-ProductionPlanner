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
    private static int nextVerificationIdentity = 300000;
    [Fact]
    public async Task Manufacturing_program_api_creates_immutable_multi_output_revisions_and_rejects_invalid_recipes()
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
                    INSERT INTO case_operations (id, case_id, operation_number, route_position, name)
                    VALUES ('case-op-2', 'case-1', 20, 1, 'Deburr');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var initial = await ReleaseAsync(
                client, "post-a", "NEW_PROCESS_REVISION", "Initial program",
                Encoding.UTF8.GetBytes("G21 G90\nM30\n"),
                Encoding.UTF8.GetBytes("tool,position\nT1,1\n"),
                confirmNewProcess: true, reuseActiveTools: false,
                processDescription: "Initial single-output process");

            using (var compatibility = await client.GetAsync(
                       "/api/v1/manufacturing-programs/case-operation:case-op-1"))
            {
                compatibility.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await compatibility.Content.ReadAsStringAsync());
                var output = json.RootElement.GetProperty("activeRevision").GetProperty("outputs")[0];
                Assert.Equal("case-op-1", output.GetProperty("caseOperationId").GetString());
                Assert.Equal(1, output.GetProperty("quantityPerCycle").GetInt32());
            }

            var local = await ReleaseAsync(
                client, "post-a", "LOCAL_POST_REVISION", "Post-only correction",
                Encoding.UTF8.GetBytes("G21 G90\nG4 X1\nM30\n"), null,
                confirmNewProcess: false, reuseActiveTools: true);
            Assert.Equal(initial.ProcessRevisionId, local.ProcessRevisionId);
            using (var compatibility = await client.GetAsync(
                       "/api/v1/manufacturing-programs/case-operation:case-op-1"))
            {
                compatibility.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await compatibility.Content.ReadAsStringAsync());
                var output = json.RootElement.GetProperty("activeRevision").GetProperty("outputs")[0];
                Assert.Equal(1, output.GetProperty("quantityPerCycle").GetInt32());
                Assert.Single(json.RootElement.GetProperty("revisions").EnumerateArray());
            }

            using (var catalogResponse = await client.GetAsync(
                       "/api/v1/cases/case-1/operations/case-op-1/gcode"))
            {
                catalogResponse.EnsureSuccessStatusCode();
                using var catalog = JsonDocument.Parse(await catalogResponse.Content.ReadAsStringAsync());
                var estimate = catalog.RootElement.GetProperty("releases")[0]
                    .GetProperty("machineCycleEstimates")[0];
                Assert.Equal("NC_PROGRAM_EXECUTION_CYCLE", estimate.GetProperty("estimateBasis").GetString());
            }

            using var created = await client.PostAsJsonAsync("/api/v1/manufacturing-programs", new
            {
                name = "Two-up mill and deburr",
                sourceProcessRevisionId = initial.ProcessRevisionId,
                changeDescription = "Initial two-output recipe",
                outputs = new object[]
                {
                    new { caseOperationId = "case-op-1", quantityPerCycle = 2, displayOrder = 0, executionMetadataJson = "{\"station\":\"mill\"}" },
                    new { caseOperationId = "case-op-2", quantityPerCycle = 1, displayOrder = 1, executionMetadataJson = "{\"station\":\"deburr\"}" }
                }
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var programId = createdJson.RootElement.GetProperty("manufacturingProgramId").GetString()!;
            var revision1 = createdJson.RootElement.GetProperty("activeRevision").GetProperty("processRevisionId").GetString()!;
            Assert.Equal(2, createdJson.RootElement.GetProperty("activeRevision").GetProperty("outputs").GetArrayLength());

            var combinedNc = PrepareNcForRelease(
                Encoding.UTF8.GetBytes("O2000\n(PART: TWO-UP)\nG21 G90\nM30\n"), 222222);
            using (var content = new MultipartFormDataContent())
            {
                content.Add(new StringContent("post-a"), "postprocessorId");
                content.Add(new StringContent("LOCAL_POST_REVISION"), "changeScope");
                content.Add(new StringContent("First combined release"), "releaseComment");
                content.Add(new StringContent("First combined release"), "processChangeDescription");
                content.Add(new StringContent("false"), "confirmNewProcessRevision");
                content.Add(new StringContent("true"), "reuseActiveToolTable");
                content.Add(new StringContent("true"), "confirmToolTable");
                content.Add(new ByteArrayContent(combinedNc), "gcodeFile", "combined.nc");
                using var release = await client.PostAsync(
                    $"/api/v1/manufacturing-programs/{programId}/gcode-releases", content);
                Assert.Equal(HttpStatusCode.Created, release.StatusCode);
                using var releaseJson = JsonDocument.Parse(await release.Content.ReadAsStringAsync());
                var combinedReleaseId = releaseJson.RootElement.GetProperty("gCodeReleaseId").GetString()!;
                var combinedHash = releaseJson.RootElement.GetProperty("fileHash").GetString()!;
                Assert.Equal(Sha256(combinedNc), combinedHash);

                using var download = await client.GetAsync(
                    $"/api/v1/manufacturing-programs/{programId}/gcode-releases/{combinedReleaseId}/file");
                download.EnsureSuccessStatusCode();
                Assert.Equal(combinedNc, await download.Content.ReadAsByteArrayAsync());
                Assert.Equal(combinedHash,
                    download.Headers.GetValues("X-Meimad-Checksum-SHA256").Single());
            }

            using (var history = await client.GetAsync($"/api/v1/manufacturing-programs/{programId}"))
            {
                history.EnsureSuccessStatusCode();
                using var historyJson = JsonDocument.Parse(await history.Content.ReadAsStringAsync());
                Assert.Single(historyJson.RootElement.GetProperty("releases").EnumerateArray());
                Assert.Equal(revision1,
                    historyJson.RootElement.GetProperty("releases")[0].GetProperty("processRevisionId").GetString());
                Assert.Equal(222222, historyJson.RootElement.GetProperty("releases")[0]
                    .GetProperty("verificationHook").GetProperty("ncIdentityToken").GetInt32());
            }

            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "If-Match", $"\"manufacturing-program:{programId}:v1\"");
            using var revised = await client.PostAsJsonAsync($"/api/v1/manufacturing-programs/{programId}/revisions", new
            {
                sourceProcessRevisionId = revision1,
                changeDescription = "Run three mill parts per cycle",
                outputs = new object[]
                {
                    new { caseOperationId = "case-op-1", quantityPerCycle = 3, displayOrder = 0, executionMetadataJson = "{\"station\":\"mill\"}" },
                    new { caseOperationId = "case-op-2", quantityPerCycle = 1, displayOrder = 1, executionMetadataJson = "{\"station\":\"deburr\"}" }
                }
            });
            Assert.Equal(HttpStatusCode.Created, revised.StatusCode);
            using var revisedJson = JsonDocument.Parse(await revised.Content.ReadAsStringAsync());
            var revisions = revisedJson.RootElement.GetProperty("revisions");
            Assert.Equal(2, revisions.GetArrayLength());
            Assert.Equal(3, revisions[0].GetProperty("outputs")[0].GetProperty("quantityPerCycle").GetInt32());
            Assert.Equal(2, revisions[1].GetProperty("outputs")[0].GetProperty("quantityPerCycle").GetInt32());
            Assert.True(revisions[0].GetProperty("isActive").GetBoolean());
            Assert.False(revisions[1].GetProperty("isActive").GetBoolean());

            client.DefaultRequestHeaders.Remove("If-Match");
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "If-Match", $"\"manufacturing-program:{programId}:v2\"");

            using var duplicate = await client.PostAsJsonAsync($"/api/v1/manufacturing-programs/{programId}/revisions", new
            {
                sourceProcessRevisionId = revision1,
                changeDescription = "Invalid duplicate",
                outputs = new object[]
                {
                    new { caseOperationId = "case-op-1", quantityPerCycle = 1, displayOrder = 0 },
                    new { caseOperationId = "case-op-1", quantityPerCycle = 1, displayOrder = 1 }
                }
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);

            using var zero = await client.PostAsJsonAsync($"/api/v1/manufacturing-programs/{programId}/revisions", new
            {
                sourceProcessRevisionId = revision1,
                changeDescription = "Invalid zero",
                outputs = new[] { new { caseOperationId = "case-op-1", quantityPerCycle = 0, displayOrder = 0 } }
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, zero.StatusCode);

            using var negative = await client.PostAsJsonAsync($"/api/v1/manufacturing-programs/{programId}/revisions", new
            {
                sourceProcessRevisionId = revision1,
                changeDescription = "Invalid negative",
                outputs = new[] { new { caseOperationId = "case-op-1", quantityPerCycle = -1, displayOrder = 0 } }
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, negative.StatusCode);

            await using var verify = await application.Services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
            await using var active = verify.CreateCommand();
            active.CommandText = "SELECT COUNT(*) FROM process_revisions WHERE manufacturing_program_id = $id AND is_active = 1;";
            active.Parameters.AddWithValue("$id", programId);
            Assert.Equal(1L, (long)(await active.ExecuteScalarAsync())!);

            await using var immutable = verify.CreateCommand();
            immutable.CommandText = "UPDATE manufacturing_program_revision_outputs SET quantity_per_cycle = 99 WHERE process_revision_id = $id;";
            immutable.Parameters.AddWithValue("$id", revision1);
            var exception = await Assert.ThrowsAsync<SqliteException>(() => immutable.ExecuteNonQueryAsync());
            Assert.Contains("immutable", exception.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Release_requires_first_block_verification_hook_and_rejects_reused_NC_identity()
    {
        await RunAsync(async (application, client, _) =>
        {
            await SeedAsync(application.Services);
            AddEditorHeaders(client);
            var nc = Encoding.UTF8.GetBytes("O1234\nG90\nM30\n");
            var tools = Encoding.UTF8.GetBytes("tool,position\nT1,1\n");

            using (var missing = await SendReleaseAsync(
                       client, "post-a", "NEW_PROCESS_REVISION", "Missing hook", nc, tools,
                       confirmNewProcess: true, reuseActiveTools: false, confirmTools: true,
                       includeVerificationHook: false))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
                using var problem = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
                Assert.Equal("verification_hook_required",
                    problem.RootElement.GetProperty("error").GetProperty("details")[0].GetProperty("code").GetString());
            }

            const int identity = 765432;
            using (var first = await SendReleaseAsync(
                       client, "post-a", "NEW_PROCESS_REVISION", "Valid hook", nc, tools,
                       confirmNewProcess: true, reuseActiveTools: false, confirmTools: true,
                       verificationIdentity: identity))
                Assert.Equal(HttpStatusCode.Created, first.StatusCode);

            using (var duplicate = await SendReleaseAsync(
                       client, "post-a", "LOCAL_POST_REVISION", "Reused identity", nc, null,
                       confirmNewProcess: false, reuseActiveTools: true, confirmTools: true,
                       verificationIdentity: identity))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
                using var problem = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
                Assert.Equal("verification_identity_reused",
                    problem.RootElement.GetProperty("error").GetProperty("details")[0].GetProperty("code").GetString());
            }
        });
    }

    [Fact]
    public async Task Task8_end_to_end_workflow_preserves_history_recalculates_readiness_and_audits_changes()
    {
        await RunAsync(async (application, client, _) =>
        {
            await SeedAsync(application.Services);
            AddEditorHeaders(client);
            await using (var setupConnection = await application.Services
                             .GetRequiredService<SqliteDatabase>().OpenConnectionAsync())
            await using (var setup = setupConnection.CreateCommand())
            {
                setup.CommandText = """
                    UPDATE postprocessors SET name = 'Doosan 3X' WHERE id = 'post-a';
                    UPDATE postprocessors SET name = 'Doosan 4X' WHERE id = 'post-b';
                    DELETE FROM machine_assignments WHERE batch_operation_id = 'batch-op-1';
                    UPDATE batch_operations SET setup_seconds = 120, cycle_seconds = 300
                    WHERE id = 'batch-op-1';
                    """;
                await setup.ExecuteNonQueryAsync();
            }

            using (var configure = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/machines/machine-1"))
            {
                configure.Headers.TryAddWithoutValidation("If-Match", "\"machine:machine-1:v1\"");
                configure.Content = JsonContent.Create(new
                {
                    executionMode = "CNC_GCODE",
                    supportedPostprocessorIds = new[] { "post-a" },
                    usableToolPositions = 20,
                    rapidRateMillimetersPerMinute = 12000,
                    toolChangeTimeSeconds = 5,
                    machineTimeFactor = 1.1
                });
                using var configured = await client.SendAsync(configure);
                configured.EnsureSuccessStatusCode();
            }

            var fifteenTools = Encoding.UTF8.GetBytes("tool,position\n" + string.Join("\n",
                Enumerable.Range(1, 15).Select(number => $"T{number},{number}")) + "\n");
            var firstProgram = Encoding.UTF8.GetBytes("O1234\n(PART: PART-100)\nG21 G90\nG0 X1000\nG1 X1100 F500\nT1 M6\nG4 X1\nM30\n");
            var first = await ReleaseAsync(
                client, "post-a", "NEW_PROCESS_REVISION", "Initial Doosan 3X production release",
                firstProgram, fifteenTools, confirmNewProcess: true, reuseActiveTools: false,
                processDescription: "Initial Doosan 3X process");
            Assert.Equal("VALID", first.HeaderStatus);
            Assert.Equal("PART-100", first.HeaderPartName);

            using (var assign = await client.PutAsJsonAsync(
                       "/api/v1/batch-operations/batch-op-1/assignment",
                       new { machineId = "machine-1", backlogPosition = 0 }))
            {
                assign.EnsureSuccessStatusCode();
            }

            var board = await BoardOperationAsync(client, "machine-1");
            Assert.Equal("satisfied", board.GetProperty("toolCapacityStatus").GetString());
            Assert.Equal(15, board.GetProperty("requiredToolCount").GetInt32());
            Assert.Equal(20, board.GetProperty("availableToolPositions").GetInt32());
            Assert.Equal("nc_estimate", board.GetProperty("planningCycleTimeSource").GetString());
            Assert.True(board.GetProperty("ncEstimatedCycleTimePerPartSeconds").GetDouble() > 0);
            Assert.True(board.GetProperty("totalSetupTimeSeconds").GetDouble() > 0);

            using (var readinessResponse = await client.GetAsync(
                       "/api/v1/batch-operations/batch-op-1/readiness"))
            {
                readinessResponse.EnsureSuccessStatusCode();
                using var readiness = JsonDocument.Parse(await readinessResponse.Content.ReadAsStringAsync());
                Assert.Equal("MISSING", ReadinessState(readiness.RootElement, "toolOffsets"));
                Assert.Equal("MISSING", ReadinessState(readiness.RootElement, "material"));
                Assert.False(readiness.RootElement.GetProperty("isReadyForProduction").GetBoolean());
            }

            await ReconcileMaterialAsync(client, 1);

            using (var confirm = await client.PutAsJsonAsync(
                       "/api/v1/batch-operations/batch-op-1/readiness-inputs",
                       new
                       {
                           selectedGCodeReleaseId = first.ReleaseId,
                           materialStatus = "READY",
                           materialComment = (string?)null,
                           toolOffsetStatus = "READY",
                           toolOffsetComment = "Offsets verified on M-1"
                       }))
            {
                confirm.EnsureSuccessStatusCode();
                using var ready = JsonDocument.Parse(await confirm.Content.ReadAsStringAsync());
                Assert.Equal("READY_FOR_PRODUCTION", ready.RootElement.GetProperty("overallState").GetString());
                Assert.True(ready.RootElement.GetProperty("isReadyForProduction").GetBoolean());
                Assert.Equal("Ready for production", ready.RootElement.GetProperty("summary").GetString());
            }

            var fourAxisSameProcess = await ReleaseAsync(
                client, "post-b", "LOCAL_POST_REVISION", "Doosan 4X output for current process",
                Encoding.UTF8.GetBytes("G21 G90\nG1 X10 F100\nM30\n"), null,
                confirmNewProcess: false, reuseActiveTools: true);
            Assert.Equal(first.ProcessRevisionId, fourAxisSameProcess.ProcessRevisionId);
            var local = await ReleaseAsync(
                client, "post-a", "LOCAL_POST_REVISION", "Doosan 3X local correction",
                Encoding.UTF8.GetBytes("G21 G90\nG1 X20 F100\nM30\n"), null,
                confirmNewProcess: false, reuseActiveTools: true);
            Assert.Equal(2, local.PostSpecificRevision);

            using (var catalogResponse = await client.GetAsync(
                       "/api/v1/cases/case-1/operations/case-op-1/gcode"))
            {
                catalogResponse.EnsureSuccessStatusCode();
                using var catalog = JsonDocument.Parse(await catalogResponse.Content.ReadAsStringAsync());
                Assert.Equal("current", Status(catalog.RootElement, "post-a"));
                Assert.Equal("current", Status(catalog.RootElement, "post-b"));
            }

            var secondProcess = await ReleaseAsync(
                client, "post-b", "NEW_PROCESS_REVISION", "New Doosan 4X process",
                Encoding.UTF8.GetBytes("G21 G90\nT2 M6\nG1 X30 F100\nM30\n"), fifteenTools,
                confirmNewProcess: true, reuseActiveTools: false,
                processDescription: "Manufacturing method changed to Doosan 4X");
            Assert.NotEqual(first.ProcessRevisionId, secondProcess.ProcessRevisionId);
            using (var incompatibleResponse = await client.GetAsync(
                       "/api/v1/batch-operations/batch-op-1/readiness"))
            {
                incompatibleResponse.EnsureSuccessStatusCode();
                using var incompatible = JsonDocument.Parse(await incompatibleResponse.Content.ReadAsStringAsync());
                Assert.Equal("INCOMPATIBLE", ReadinessState(
                    incompatible.RootElement, "machinePostprocessorCompatibility"));
            }

            using (var catalogResponse = await client.GetAsync(
                       "/api/v1/cases/case-1/operations/case-op-1/gcode"))
            {
                catalogResponse.EnsureSuccessStatusCode();
                using var catalog = JsonDocument.Parse(await catalogResponse.Content.ReadAsStringAsync());
                Assert.Equal("stale", Status(catalog.RootElement, "post-a"));
                Assert.Equal(4, catalog.RootElement.GetProperty("releases").GetArrayLength());
            }

            var twentyFiveTools = Encoding.UTF8.GetBytes("tool,position\n" + string.Join("\n",
                Enumerable.Range(1, 25).Select(number => $"T{number},{number}")) + "\n");
            await ReleaseAsync(
                client, "post-a", "NEW_PROCESS_REVISION", "Expanded 25-tool process",
                Encoding.UTF8.GetBytes("G21 G90\nT25 M6\nG1 X40 F100\nM30\n"), twentyFiveTools,
                confirmNewProcess: true, reuseActiveTools: false,
                processDescription: "Tool requirement expanded to 25 prepared magazine tools");

            var mismatch = await BoardOperationAsync(client, "machine-1");
            Assert.Equal("tool_capacity_mismatch", mismatch.GetProperty("toolCapacityStatus").GetString());
            Assert.Equal(
                "Tool capacity mismatch: requires 25 tool positions; assigned machine supports 20.",
                mismatch.GetProperty("toolCapacityMessage").GetString());
            Assert.Equal("machine-1", mismatch.GetProperty("machineId").GetString());
            Assert.Equal("not_started", mismatch.GetProperty("status").GetString());

            Assert.Equal(PrepareNcForRelease(firstProgram, first.NcIdentityToken), await client.GetByteArrayAsync(
                $"/api/v1/cases/case-1/operations/case-op-1/gcode-releases/{first.ReleaseId}/file"));
            using (var finalCatalogResponse = await client.GetAsync(
                       "/api/v1/cases/case-1/operations/case-op-1/gcode"))
            {
                finalCatalogResponse.EnsureSuccessStatusCode();
                using var finalCatalog = JsonDocument.Parse(await finalCatalogResponse.Content.ReadAsStringAsync());
                Assert.Equal(5, finalCatalog.RootElement.GetProperty("releases").GetArrayLength());
                Assert.Equal(3, finalCatalog.RootElement.GetProperty("processRevisions").GetArrayLength());
            }

            await using var auditConnection = await application.Services
                .GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
            await using var audit = auditConnection.CreateCommand();
            audit.CommandText = "SELECT DISTINCT event_type FROM structured_event_log;";
            var eventTypes = new HashSet<string>(StringComparer.Ordinal);
            await using var auditReader = await audit.ExecuteReaderAsync();
            while (await auditReader.ReadAsync()) eventTypes.Add(auditReader.GetString(0));
            Assert.Contains("gcode_release_published", eventTypes);
            Assert.Contains("tool_table_release_published", eventTypes);
            Assert.Contains("process_revision_created", eventTypes);
            Assert.Contains("process_revision_activated", eventTypes);
            Assert.Contains("local_post_revision_published", eventTypes);
            Assert.Contains("tool_offsets_confirmation_recorded", eventTypes);
            Assert.Contains("production_readiness_transition", eventTypes);
            Assert.Contains("machine_compatibility_failure", eventTypes);
            Assert.Contains("tool_capacity_mismatch", eventTypes);
            Assert.Contains("nc_estimate_recalculated", eventTypes);
        });
    }

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
                Assert.Equal("MISSING", ReadinessState(initial.RootElement, "material"));
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
            await ReconcileMaterialAsync(client, 1);

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
                           materialComment = (string?)null,
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
    public async Task Cimatron_mht_tool_table_creates_the_process_release_without_conversion()
    {
        await RunAsync(async (application, client, _) =>
        {
            await SeedAsync(application.Services);
            AddEditorHeaders(client);
            var cimatronMht = Encoding.UTF8.GetBytes("""
                MIME-Version: 1.0
                Content-Type: multipart/related; boundary="cam-boundary"

                --cam-boundary
                Content-Transfer-Encoding: quoted-printable
                Content-Type: text/html; charset="us-ascii"

                <html><body><table class=3DMsoTableGrid>
                <tr><td>Number</td><td>Name</td><td>Dia</td><td>Holder</td></tr>
                <tr><td><b>T1</b></td><td>FLYCUTTER=5F80</td><td>80.</td><td>HOLDER5</td></tr>
                <tr><td><b>T17</b></td><td>DRILL=5F8.5</td><td>8.5</td><td>HOLDER5</td></tr>
                </table></body></html>
                --cam-boundary--
                """);

            var released = await ReleaseAsync(
                client,
                "post-a",
                "NEW_PROCESS_REVISION",
                "Native Cimatron tool-table release",
                Encoding.UTF8.GetBytes("T1 M06\nT17 M06\nM30\n"),
                cimatronMht,
                confirmNewProcess: true,
                reuseActiveTools: false,
                processDescription: "Initial Cimatron manufacturing process",
                toolFileName: "TP_MODEL.TOOLS.mht");

            Assert.Equal(1, released.ProcessRevisionNumber);
            using var catalogResponse = await client.GetAsync(
                "/api/v1/cases/case-1/operations/case-op-1/gcode");
            catalogResponse.EnsureSuccessStatusCode();
            using var catalog = JsonDocument.Parse(await catalogResponse.Content.ReadAsStringAsync());
            var toolTable = catalog.RootElement
                .GetProperty("activeProcessRevision")
                .GetProperty("toolTable");
            Assert.Equal("TP_MODEL.TOOLS.mht", toolTable.GetProperty("originalFileName").GetString());
            Assert.Equal(2, toolTable.GetProperty("requiredToolCount").GetInt32());
            Assert.Equal(2, toolTable.GetProperty("tools").GetArrayLength());
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
            Assert.Equal(Sha256(PrepareNcForRelease(firstGCode, first.NcIdentityToken)), first.FileHash);

            await ReconcileMaterialAsync(client, 1);

            using (var readiness = await client.PutAsJsonAsync(
                       "/api/v1/batch-operations/batch-op-1/readiness-inputs",
                       new
                       {
                           selectedGCodeReleaseId = first.ReleaseId,
                           materialStatus = "READY",
                           materialComment = (string?)null,
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

            Assert.Equal(PrepareNcForRelease(firstGCode, first.NcIdentityToken), await client.GetByteArrayAsync(
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

    private static async Task ReconcileMaterialAsync(HttpClient client, int quantity)
    {
        using var receipt = await client.PostAsJsonAsync("/api/v1/material-receipts", new
        {
            caseId = "case-1",
            quantity,
            receivedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            externalReference = "TEST-VERIFIED",
            comment = "Physically verified test receipt"
        });
        receipt.EnsureSuccessStatusCode();
        using var receiptJson = JsonDocument.Parse(await receipt.Content.ReadAsStringAsync());
        var receiptId = receiptJson.RootElement.GetProperty("receiptId").GetString();
        using var reservation = await client.PutAsJsonAsync(
            "/api/v1/batches/batch-1/material/reservations",
            new { reservations = new[] { new { receiptId, quantity } } });
        reservation.EnsureSuccessStatusCode();
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
        string? processDescription = null,
        string toolFileName = "tools.csv")
    {
        using var response = await SendReleaseAsync(
            client, postprocessorId, scope, comment, gcode, tools,
            confirmNewProcess, reuseActiveTools, confirmTools: true, processDescription,
            toolFileName);
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
            root.GetProperty("fileHash").GetString()!,
            root.GetProperty("headerMetadata").GetProperty("status").GetString()!,
            root.GetProperty("headerMetadata").GetProperty("partName").ValueKind == JsonValueKind.Null
                ? null : root.GetProperty("headerMetadata").GetProperty("partName").GetString(),
            root.GetProperty("verificationHook").GetProperty("ncIdentityToken").GetInt32());
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
        string? processDescription = null,
        string toolFileName = "tools.csv",
        bool includeVerificationHook = true,
        int? verificationIdentity = null)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(postprocessorId), "postprocessorId");
        content.Add(new StringContent(scope), "changeScope");
        content.Add(new StringContent(comment), "releaseComment");
        content.Add(new StringContent(processDescription ?? comment), "processChangeDescription");
        content.Add(new StringContent(confirmNewProcess.ToString()), "confirmNewProcessRevision");
        content.Add(new StringContent(reuseActiveTools.ToString()), "reuseActiveToolTable");
        content.Add(new StringContent(confirmTools.ToString()), "confirmToolTable");
        var identity = verificationIdentity ?? Interlocked.Increment(ref nextVerificationIdentity);
        var releaseBytes = includeVerificationHook ? PrepareNcForRelease(gcode, identity) : gcode;
        content.Add(new ByteArrayContent(releaseBytes), "gcodeFile", "program.nc");
        if (tools is not null)
        {
            content.Add(new ByteArrayContent(tools), "toolTableFile", toolFileName);
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

    private static byte[] PrepareNcForRelease(byte[] source, int identity)
    {
        var lines = Encoding.UTF8.GetString(source).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var index = 0;
        while (index < lines.Count)
        {
            var value = lines[index].Trim();
            if (value.Length == 0 || value == "%"
                || value.StartsWith('(') && value.EndsWith(')')
                || System.Text.RegularExpressions.Regex.IsMatch(value, @"^O\d{1,8}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                index++;
                continue;
            }
            break;
        }
        lines.Insert(index, $"G65 P9002 A{identity:D6}. (MEIMAD VERIFY V1)");
        return Encoding.UTF8.GetBytes(string.Join("\n", lines));
    }

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
        string FileHash,
        string HeaderStatus,
        string? HeaderPartName,
        int NcIdentityToken);
}
