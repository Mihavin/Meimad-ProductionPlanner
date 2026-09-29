using System.Net;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.ProductionPackages;

/// <summary>
/// A newer G-code release reaches running work on "Refresh from Case" (owner decisions 2026-09-29,
/// schema v90): the operation goes back to setup like a new one and keeps its made parts.
/// </summary>
public sealed class SetupRestartApiTests
{
    [Fact]
    public async Task A_newer_local_version_sends_the_running_operation_back_to_setup_until_a_new_package_pins_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "MeimadPlanner.SetupRestart.Tests", Guid.NewGuid().ToString("N"));
        var releaseRoot = Path.Combine(root, "releases");
        Directory.CreateDirectory(root);
        await using var application = ServerApplication.Build(
            [
                "--Server:Host=127.0.0.1", "--Server:Port=5098",
                $"--Database:Path={Path.Combine(root, "test.db")}",
                $"--GCode:ReleaseRoot={releaseRoot}",
                $"--ProductionPackages:PackageRoot={Path.Combine(root, "packages")}"
            ], webHost => webHost.UseSignedInTestServer());
        try
        {
            await application.StartAsync();
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await SeedAsync(database, releaseRoot);
            using var client = application.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "planner-client");
            client.DefaultRequestHeaders.Add("X-Meimad-User-Id", "planner-user");

            var firstPackage = await CreatePackageAsync(client);
            Assert.Equal("gcode-1", firstPackage.GetProperty("gCodeReleaseId").GetString());
            var runId = await ScalarAsync(database, "SELECT id FROM production_runs WHERE legacy_batch_operation_id = 'operation-package';");
            await PutInProductionAsync(database, runId!);
            Assert.Equal(1L, await CountAsync(database, "SELECT COUNT(*) FROM production_run_current_offset_loaders;"));

            // Nothing is offered while the release in production is the newest.
            Assert.Empty((await PreviewAsync(client)).GetProperty("setupRestarts").EnumerateArray());

            await ReleaseAsync(database, releaseRoot, "gcode-2", "process-1", 2, 583921);
            var outdated = await ReadinessGCodeAsync(client);
            Assert.Equal("OUTDATED", outdated.GetProperty("state").GetString());
            Assert.Contains("Refresh the Work Order from its Case", outdated.GetProperty("message").GetString(), StringComparison.Ordinal);

            var preview = await PreviewAsync(client);
            var offered = Assert.Single(preview.GetProperty("setupRestarts").EnumerateArray());
            Assert.Equal("operation-package", offered.GetProperty("batchOperationId").GetString());
            Assert.Equal(10, offered.GetProperty("operationNumber").GetInt32());
            Assert.Equal("Package Mill", offered.GetProperty("machineName").GetString());
            Assert.Equal("Haas4x r1", offered.GetProperty("productionRelease").GetString());
            Assert.Equal("Haas4x r2", offered.GetProperty("newerRelease").GetString());
            Assert.Empty(preview.GetProperty("processRevisionNotSwitched").EnumerateArray());

            using (var refreshed = await client.PostAsync("/api/v1/batches/batch-package/refresh-operations", null))
            {
                Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
                using var document = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync());
                Assert.Equal("operation-package", Assert.Single(document.RootElement.GetProperty("setupRestarts").EnumerateArray())
                    .GetProperty("batchOperationId").GetString());
            }

            // Back in setup like a new run: ready for setup, the old program refused, the package retired.
            Assert.Equal("SETUP_RESTARTED", await ScalarAsync(database,
                $"SELECT event_type FROM production_run_workflow_events WHERE production_run_id = '{runId}' ORDER BY server_received_at DESC, id DESC LIMIT 1;"));
            Assert.Equal(0L, await CountAsync(database, "SELECT COUNT(*) FROM production_run_current_offset_loaders;"));
            Assert.Equal(0L, await CountAsync(database, "SELECT COUNT(*) FROM production_package_current;"));
            Assert.Equal("NC_RELEASE_REPLACED", await ScalarAsync(database, "SELECT reason FROM production_package_invalidations;"));
            using (var current = await client.GetAsync("/api/v1/batch-operations/operation-package/production-package"))
                Assert.Equal(HttpStatusCode.NotFound, current.StatusCode);
            // The parts already made stay counted.
            Assert.Equal("in_progress", await ScalarAsync(database, "SELECT status FROM batch_operations WHERE id = 'operation-package';"));
            Assert.Equal(3L, await CountAsync(database, "SELECT produced_quantity FROM production_run_outputs WHERE batch_operation_id = 'operation-package';"));

            var restarting = await ReadinessAsync(client);
            Assert.False(restarting.GetProperty("isReadyForProduction").GetBoolean());
            Assert.Equal("gcode-2", restarting.GetProperty("effectiveGCodeReleaseId").GetString());
            var gcode = Component(restarting, "gcode");
            Assert.Equal("OUTDATED", gcode.GetProperty("state").GetString());
            Assert.Contains("replaced by Haas4x r2", gcode.GetProperty("message").GetString(), StringComparison.Ordinal);
            // A second refresh does not restart it again.
            Assert.Empty((await PreviewAsync(client)).GetProperty("setupRestarts").EnumerateArray());

            // The new package takes the new release and pins it as the one in production.
            var secondPackage = await CreatePackageAsync(client);
            Assert.Equal("gcode-2", secondPackage.GetProperty("gCodeReleaseId").GetString());
            Assert.Equal(JsonValueKind.Null, secondPackage.GetProperty("supersedesProductionPackageId").ValueKind);
            Assert.Equal("gcode-2", await ScalarAsync(database, "SELECT production_gcode_release_id FROM batch_operations WHERE id = 'operation-package';"));
            Assert.Equal("gcode-2", await ScalarAsync(database, $"SELECT production_gcode_release_id FROM production_run_programs WHERE production_run_id = '{runId}';"));
            Assert.Equal("NEW_PACKAGE", await ScalarAsync(database, "SELECT resolution FROM batch_operation_setup_restarts;"));
            Assert.Equal(1L, await CountAsync(database, "SELECT COUNT(*) FROM production_run_current_offset_loaders;"));
            Assert.Equal("READY", Component(await ReadinessAsync(client), "gcode").GetProperty("state").GetString());

            // A new process revision is reported, not switched inside the running Production Run.
            await NewProcessRevisionAsync(database, releaseRoot);
            var reported = Assert.Single((await PreviewAsync(client)).GetProperty("processRevisionNotSwitched").EnumerateArray());
            Assert.True(reported.GetProperty("newProcessRevision").GetBoolean());
            using (var refreshed = await client.PostAsync("/api/v1/batches/batch-package/refresh-operations", null))
            {
                Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
                using var document = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync());
                Assert.Empty(document.RootElement.GetProperty("setupRestarts").EnumerateArray());
                Assert.Single(document.RootElement.GetProperty("processRevisionNotSwitched").EnumerateArray());
            }
            Assert.Equal(1L, await CountAsync(database, "SELECT COUNT(*) FROM batch_operation_setup_restarts;"));

            // Finishing ends an open restart: the operation no longer waits for a package.
            await ReleaseAsync(database, releaseRoot, "gcode-4", "process-1", 3, 683921);
            await ExecuteAsync(database,
                "UPDATE process_revisions SET is_active = 0 WHERE id = 'process-2'; UPDATE process_revisions SET is_active = 1 WHERE id = 'process-1';");
            using (var refreshed = await client.PostAsync("/api/v1/batches/batch-package/refresh-operations", null))
                Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
            Assert.Equal(1L, await CountAsync(database, "SELECT COUNT(*) FROM batch_operation_setup_restarts WHERE resolved_at IS NULL;"));
            await ExecuteAsync(database, "UPDATE batch_operations SET status = 'completed' WHERE id = 'operation-package';");
            Assert.Equal("OPERATION_FINISHED", await ScalarAsync(database,
                "SELECT resolution FROM batch_operation_setup_restarts ORDER BY requested_at DESC LIMIT 1;"));
        }
        finally
        {
            await application.StopAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static JsonElement Component(JsonElement readiness, string key) =>
        readiness.GetProperty("components").EnumerateArray().Single(value => value.GetProperty("key").GetString() == key);

    private static async Task<JsonElement> ReadinessAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/batch-operations/operation-package/readiness");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> ReadinessGCodeAsync(HttpClient client) =>
        Component(await ReadinessAsync(client), "gcode");

    private static async Task<JsonElement> PreviewAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/batches/batch-package/refresh-operations/preview");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> CreatePackageAsync(HttpClient client)
    {
        using var create = await client.PostAsync(
            "/api/v1/batch-operations/operation-package/production-package",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        var body = await create.Content.ReadAsStringAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>The operation has started with gcode-1, made 3 parts and passed first-part QC.</summary>
    private static Task PutInProductionAsync(SqliteDatabase database, string runId) => ExecuteAsync(database, $"""
        UPDATE batch_operations
        SET status = 'in_progress', actual_start = '2026-09-01T09:00:00Z', actual_machine_id = 'machine-package',
            production_process_revision_id = 'process-1', production_gcode_release_id = 'gcode-1',
            production_tool_table_release_id = 'tools-1',
            production_gcode_file_hash = (SELECT file_hash FROM gcode_releases WHERE id = 'gcode-1'),
            production_tool_table_file_hash = (SELECT file_hash FROM tool_table_releases WHERE id = 'tools-1')
        WHERE id = 'operation-package';
        UPDATE production_runs SET status = 'IN_PROGRESS', structure_locked_at = '2026-09-01T09:00:00Z' WHERE id = '{runId}';
        UPDATE production_run_programs
        SET status = 'ACTIVE', production_process_revision_id = 'process-1', production_gcode_release_id = 'gcode-1',
            production_tool_table_release_id = 'tools-1', completed_cycle_count = 3
        WHERE production_run_id = '{runId}';
        UPDATE production_run_outputs SET status = 'IN_PRODUCTION', produced_quantity = 3
        WHERE batch_operation_id = 'operation-package';
        INSERT INTO production_run_workflow_events (
            id, production_run_id, machine_id, event_type, source, source_event_id, server_received_at)
        VALUES ('qc-pass-1', '{runId}', 'machine-package', 'QC_PASS', 'WINDOWS_QC', 'QC:qc-pass-1', '2026-09-01T09:30:00.0000000+00:00');
        """);

    private static async Task SeedAsync(SqliteDatabase database, string releaseRoot)
    {
        var toolRelative = "operations/case-operation-package/tool-tables/tools-1/tools.csv";
        var toolPath = Path.Combine(releaseRoot, toolRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(toolPath)!);
        await File.WriteAllTextAsync(toolPath, "tool,description\n", Encoding.UTF8);
        var toolHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(toolPath)));
        await ExecuteAsync(database, $"""
            INSERT INTO working_calendars(id,name,time_zone_id,calendar_json)
            VALUES('calendar-package','Package Calendar','UTC','{"{}"}');
            INSERT INTO machines(id,number,name,machine_type,working_calendar_id,status,is_active,
                                 display_enabled,execution_mode,usable_tool_positions)
            VALUES('machine-package','M-PKG','Package Mill','mill','calendar-package','active',1,1,'CNC_GCODE',20);
            INSERT INTO cases(id,part_number,name,working_folder_path)
            VALUES('case-package','PN-PKG','Package Part','{Path.Combine(releaseRoot, "working").Replace("'", "''")}');
            INSERT INTO case_operations(id,case_id,operation_number,route_position,name,required_machine_type,
                                        setup_seconds,cycle_seconds)
            VALUES('case-operation-package','case-package',10,0,'Finish','mill',60,60);
            INSERT INTO tool_table_releases(
                id,case_operation_id,revision_number,original_file_name,stored_relative_path,file_size,file_hash,
                released_at,released_by,release_comment,created_at,updated_at,required_tool_count)
            VALUES('tools-1','case-operation-package',1,'tools.csv','{toolRelative}',20,'{toolHash}',
                   '2026-09-01T08:00:00Z','tool-user','Initial','2026-09-01T08:00:00Z','2026-09-01T08:00:00Z',0);
            INSERT INTO process_revisions(
                id,case_operation_id,revision_number,is_active,tool_table_release_id,created_at,created_by,
                change_description,version,updated_at,manufacturing_program_id)
            VALUES('process-1','case-operation-package',1,1,'tools-1','2026-09-01T08:00:00Z','nc-user','Initial',1,
                   '2026-09-01T08:00:00Z','case-operation:case-operation-package');
            INSERT INTO manufacturing_program_revision_outputs(
                id,process_revision_id,case_operation_id,quantity_per_cycle,display_order,execution_metadata_json,created_at)
            VALUES('output-1','process-1','case-operation-package',1,0,'{"{}"}','2026-09-01T08:00:00Z');
            INSERT INTO postprocessors(id,name,is_active,version,created_at,updated_at)
            VALUES('post-1','Haas4x',1,1,'2026-09-01T08:00:00Z','2026-09-01T08:00:00Z');
            INSERT INTO machine_supported_postprocessors(machine_id,postprocessor_id,created_at,updated_at)
            VALUES('machine-package','post-1','2026-09-01T08:00:00Z','2026-09-01T08:00:00Z');
            INSERT INTO cnc_verification_settings(
                machine_id,dprint_transport,dprint_port,challenge_program_number,verify_program_number,
                custom_gcode_alias,nonce_variable,response_variable,verification_state_variable,
                release_token_variable,expected_macro_version,response_code_digits,verification_timeout_seconds,
                enabled,version,created_at,updated_at,finalize_program_number,event_sequence_variable)
            VALUES('machine-package','HAAS_DPRNT_TCP',8080,9001,9002,NULL,10501,500,10502,10503,
                   10,6,120,1,1,'2026-09-01T08:00:00Z','2026-09-01T08:00:00Z',9003,10504);
            """);
        await ReleaseAsync(database, releaseRoot, "gcode-1", "process-1", 1, 483921);
        await ExecuteAsync(database, """
            INSERT INTO production_batches(id,case_id,batch_number,status,planned_quantity)
            VALUES('batch-package','case-package','B-PKG','waiting',10);
            INSERT INTO batch_operations(
                id,production_batch_id,source_case_operation_id,operation_number,route_position,name,
                required_machine_type,setup_seconds,cycle_seconds,status)
            VALUES('operation-package','batch-package','case-operation-package',10,0,'Finish','mill',60,60,'not_started');
            INSERT INTO machine_assignments(id,batch_operation_id,machine_id,backlog_position)
            VALUES('assignment-package','operation-package','machine-package',0);
            UPDATE machine_assignments SET selected_gcode_release_id='gcode-1' WHERE id='assignment-package';
            """);
    }

    /// <summary>Releases a canonical program for post-1 in the given process revision.</summary>
    private static async Task ReleaseAsync(
        SqliteDatabase database, string releaseRoot, string releaseId, string processId, int revision, int ncIdentity)
    {
        var relative = $"operations/case-operation-package/gcode/{releaseId}/main.nc";
        var path = Path.Combine(releaseRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, string.Join("\r\n", new[]
        {
            "%", $"O0{1994 + revision}",
            "(PART: [[MEIMAD:PART_NAME]])", "(OPERATION: [[MEIMAD:OPERATION_NAME]])",
            "(RUN: [[MEIMAD:PRODUCTION_RUN_ID]])", "(PACKAGE: [[MEIMAD:PRODUCTION_PACKAGE_ID]])",
            "(MACHINE: [[MEIMAD:MACHINE_ID]])", "(NC RELEASE: [[MEIMAD:NC_RELEASE_ID]])",
            "(OFFSET LOADER: [[MEIMAD:OFFSET_LOADER_RELEASE_ID]])",
            "[[MEIMAD:VERIFICATION_HOOK]]", "[[MEIMAD:EVENT_CONTEXT]]",
            "[[MEIMAD:CYCLE_START]]", $"G90 (R{revision})", "[[MEIMAD:CYCLE_END]]", "M30", "%", ""
        }), Encoding.ASCII);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)));
        var at = $"2026-09-0{Math.Min(revision, 9)}T08:00:00Z";
        await ExecuteAsync(database, $"""
            INSERT INTO gcode_releases(
                id,case_operation_id,process_revision_id,postprocessor_id,post_specific_revision,
                original_file_name,stored_relative_path,file_size,file_hash,released_at,released_by,
                change_scope,release_comment,tool_table_release_id,created_at,updated_at)
            VALUES('{releaseId}','case-operation-package','{processId}','post-1',{revision},'main.nc','{relative}',200,'{hash}',
                   '{at}','nc-user','{(revision == 1 ? "NEW_PROCESS_REVISION" : "LOCAL_POST_REVISION")}','R{revision}','tools-1','{at}','{at}');
            INSERT INTO gcode_release_verification_hooks(
                gcode_release_id,hook_version,invocation_kind,invocation_number,nc_identity_token,line_number,created_at,updated_at)
            VALUES('{releaseId}',1,'G65',9002,{ncIdentity},3,'{at}','{at}');
            """);
    }

    private static async Task NewProcessRevisionAsync(SqliteDatabase database, string releaseRoot)
    {
        await ExecuteAsync(database, """
            UPDATE process_revisions SET is_active = 0 WHERE id = 'process-1';
            INSERT INTO process_revisions(
                id,case_operation_id,revision_number,is_active,tool_table_release_id,created_at,created_by,
                change_description,version,updated_at,manufacturing_program_id)
            VALUES('process-2','case-operation-package',2,1,'tools-1','2026-09-05T08:00:00Z','nc-user','Second method',1,
                   '2026-09-05T08:00:00Z','case-operation:case-operation-package');
            INSERT INTO manufacturing_program_revision_outputs(
                id,process_revision_id,case_operation_id,quantity_per_cycle,display_order,execution_metadata_json,created_at)
            VALUES('output-2','process-2','case-operation-package',1,0,'{}','2026-09-05T08:00:00Z');
            """);
        await ReleaseAsync(database, releaseRoot, "gcode-3", "process-2", 1, 783921);
    }

    private static async Task ExecuteAsync(SqliteDatabase database, string sql)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarAsync(SqliteDatabase database, string sql)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    private static async Task<long> CountAsync(SqliteDatabase database, string sql) =>
        long.Parse((await ScalarAsync(database, sql))!, System.Globalization.CultureInfo.InvariantCulture);
}
