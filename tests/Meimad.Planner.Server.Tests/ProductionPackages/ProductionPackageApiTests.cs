using System.Net;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.ProductionPackages;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.ProductionPackages;

public sealed class ProductionPackageApiTests
{
    [Fact]
    public async Task Manual_dummy_offsets_are_explicit_auditable_and_keep_verification_identity()
    {
        var root = Path.Combine(Path.GetTempPath(), "MeimadPlanner.ManualOffsets.Tests", Guid.NewGuid().ToString("N"));
        var releaseRoot = Path.Combine(root, "releases");
        var packageRoot = Path.Combine(root, "packages");
        Directory.CreateDirectory(root);
        await using var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5098", $"--Database:Path={Path.Combine(root, "test.db")}",
             $"--GCode:ReleaseRoot={releaseRoot}", $"--ProductionPackages:PackageRoot={packageRoot}"],
            webHost => webHost.UseTestServer());
        try
        {
            await application.StartAsync();
            await SeedAsync(application.Services, releaseRoot, true);
            await using (var connection = await application.Services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync())
            await using (var enable = connection.CreateCommand())
            {
                enable.CommandText = "INSERT INTO machine_package_capabilities(machine_id,allow_manual_dummy_tool_offsets,updated_at,updated_by) VALUES('machine-package',1,'2026-09-01T08:00:00Z','test');";
                await enable.ExecuteNonQueryAsync();
            }
            File.Delete(Path.Combine(releaseRoot,"operations","case-operation-package","tool-tables","tools-1","tools.csv"));
            using var client = application.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "tool-room-client");
            client.DefaultRequestHeaders.Add("X-Meimad-User-Id", "tool-room-user");
            using var create = await client.PostAsync(
                "/api/v1/batch-operations/operation-package/production-package?toolOffsetMode=MANUAL_DUMMY",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            using var document = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
            Assert.Equal("MANUAL_DUMMY",document.RootElement.GetProperty("toolOffsetMode").GetString());
            Assert.True(document.RootElement.GetProperty("verificationEnabled").GetBoolean());
            var artifacts=document.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();
            Assert.DoesNotContain(artifacts,value=>value.GetProperty("artifactType").GetString()=="TOOL_TABLE");
            Assert.Contains(artifacts,value=>value.GetProperty("artifactType").GetString()=="OFFSET_LOADER");
            var loader=artifacts.Single(value=>value.GetProperty("artifactType").GetString()=="OFFSET_LOADER");
            var loaderBytes=await client.GetByteArrayAsync($"/api/v1/batch-operations/operation-package/production-package/artifacts/{loader.GetProperty("artifactId").GetString()}");
            var loaderText=Encoding.ASCII.GetString(loaderBytes);
            Assert.Contains("PRODUCTION PACKAGE",loaderText,StringComparison.Ordinal);
            Assert.Contains("G65 P9001",loaderText,StringComparison.Ordinal);
            Assert.DoesNotContain("G10",loaderText,StringComparison.OrdinalIgnoreCase);
            var manifest=artifacts.Single(value=>value.GetProperty("artifactType").GetString()=="MANIFEST");
            using var manifestDocument=JsonDocument.Parse(await client.GetByteArrayAsync($"/api/v1/batch-operations/operation-package/production-package/artifacts/{manifest.GetProperty("artifactId").GetString()}"));
            Assert.Equal("MANUAL_DUMMY",manifestDocument.RootElement.GetProperty("toolOffsetMode").GetString());
            Assert.True(manifestDocument.RootElement.GetProperty("setupistMustEnterToolOffsetsManually").GetBoolean());
            Assert.Equal(JsonValueKind.Null,manifestDocument.RootElement.GetProperty("toolTableSourceHash").ValueKind);
        }
        finally
        {
            await application.StopAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root,true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cnc_package_is_atomic_machine_bound_and_controls_setup_readiness(bool verificationEnabled)
    {
        var root = Path.Combine(Path.GetTempPath(), "MeimadPlanner.ProductionPackage.Tests", Guid.NewGuid().ToString("N"));
        var releaseRoot = Path.Combine(root, "releases");
        var packageRoot = Path.Combine(root, "packages");
        Directory.CreateDirectory(root);
        await using var application = ServerApplication.Build(
            [
                "--Server:Host=127.0.0.1", "--Server:Port=5098",
                $"--Database:Path={Path.Combine(root, "test.db")}",
                $"--GCode:ReleaseRoot={releaseRoot}",
                $"--ProductionPackages:PackageRoot={packageRoot}"
            ], webHost => webHost.UseTestServer());
        try
        {
            await application.StartAsync();
            await SeedAsync(application.Services, releaseRoot, verificationEnabled);
            using var client = application.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "tool-room-client");
            client.DefaultRequestHeaders.Add("X-Meimad-User-Id", "tool-room-user");

            Assert.Equal(["operation-package"], await QueueIdsAsync(client, "TOOL_PREPARATION_PENDING"));
            Assert.Empty(await QueueIdsAsync(client, "SETUP_PENDING"));

            using var create = await client.PostAsync(
                "/api/v1/batch-operations/operation-package/production-package",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            using var document = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
            var package = document.RootElement;
            Assert.Equal("operation-package", package.GetProperty("batchOperationId").GetString());
            Assert.Equal("machine-package", package.GetProperty("machineId").GetString());
            Assert.Equal("gcode-1", package.GetProperty("gCodeReleaseId").GetString());
            Assert.Equal("tools-1", package.GetProperty("toolTableReleaseId").GetString());
            Assert.Equal("tool-room-user", package.GetProperty("createdBy").GetString());
            Assert.True(package.GetProperty("fileExportAvailable").GetBoolean());
            Assert.False(package.GetProperty("directTransferConfigured").GetBoolean());
            Assert.Equal(verificationEnabled, package.GetProperty("verificationEnabled").GetBoolean());
            var types = package.GetProperty("artifacts").EnumerateArray()
                .Select(value => value.GetProperty("artifactType").GetString()).ToArray();
            Assert.Contains("RUNNABLE_NC", types);
            Assert.Contains("TOOL_TABLE", types);
            Assert.Contains("MANIFEST", types);
            Assert.Equal(verificationEnabled, types.Contains("OFFSET_LOADER"));
            Assert.Equal(verificationEnabled,
                package.GetProperty("offsetLoaderReleaseId").ValueKind == JsonValueKind.String);

            var packageId = package.GetProperty("productionPackageId").GetString()!;
            var nc = package.GetProperty("artifacts").EnumerateArray()
                .Single(value => value.GetProperty("artifactType").GetString() == "RUNNABLE_NC");
            using var ncDownload = await client.GetAsync(
                $"/api/v1/batch-operations/operation-package/production-package/artifacts/{nc.GetProperty("artifactId").GetString()}");
            var ncText = await ncDownload.Content.ReadAsStringAsync();
            Assert.DoesNotContain("MEIMAD PACKAGE ", ncText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(verificationEnabled,
                ncText.Contains("G65 P9002 A483921. (MEIMAD VERIFY V1)", StringComparison.Ordinal));
            Assert.Equal(verificationEnabled,
                ncText.Contains("MACROVERSION/10/PROGRAM/483921", StringComparison.Ordinal));

            var manifest = package.GetProperty("artifacts").EnumerateArray()
                .Single(value => value.GetProperty("artifactType").GetString() == "MANIFEST");
            using var manifestDownload = await client.GetAsync(
                $"/api/v1/batch-operations/operation-package/production-package/artifacts/{manifest.GetProperty("artifactId").GetString()}");
            using var manifestDocument = JsonDocument.Parse(await manifestDownload.Content.ReadAsStringAsync());
            Assert.Equal(packageId, manifestDocument.RootElement.GetProperty("productionPackageId").GetString());
            Assert.Equal("operation-package", manifestDocument.RootElement.GetProperty("batchOperationId").GetString());
            Assert.Equal("machine-package", manifestDocument.RootElement.GetProperty("machine").GetProperty("id").GetString());
            Assert.Equal("gcode-1", manifestDocument.RootElement.GetProperty("gCodeReleaseId").GetString());
            Assert.Equal("tools-1", manifestDocument.RootElement.GetProperty("toolTableReleaseId").GetString());
            Assert.Equal("tool-room-user", manifestDocument.RootElement.GetProperty("createdBy").GetString());

            Assert.Empty(await QueueIdsAsync(client, "TOOL_PREPARATION_PENDING"));
            Assert.Equal(["operation-package"], await QueueIdsAsync(client, "SETUP_PENDING"));

            using var opened = await client.GetAsync(
                "/api/v1/batch-operations/operation-package/production-package");
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
            Assert.Equal(["operation-package"], await QueueIdsAsync(client, "SETUP_PENDING"));

            if (verificationEnabled)
            {
                var firstLoaderId = package.GetProperty("offsetLoaderReleaseId").GetString();
                using var replacement = await client.PostAsync(
                    "/api/v1/batch-operations/operation-package/production-package",
                    new StringContent("{}", Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
                using var replacementDocument = JsonDocument.Parse(await replacement.Content.ReadAsStringAsync());
                Assert.NotEqual(firstLoaderId,
                    replacementDocument.RootElement.GetProperty("offsetLoaderReleaseId").GetString());
                Assert.Equal(packageId,
                    replacementDocument.RootElement.GetProperty("supersedesProductionPackageId").GetString());
            }

            Assert.True(Directory.Exists(Path.Combine(packageRoot, packageId)));
            Assert.Empty(Directory.GetDirectories(packageRoot, ".staging-*"));

            if (verificationEnabled)
                await ChangeVerificationConfigurationAsync(application.Services);
            else
                await SupersedeGCodeAsync(application.Services, releaseRoot);
            using var stale = await client.GetAsync(
                "/api/v1/batch-operations/operation-package/production-package");
            Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
            Assert.Equal(["operation-package"], await QueueIdsAsync(client, "TOOL_PREPARATION_PENDING"));
            Assert.Empty(await QueueIdsAsync(client, "SETUP_PENDING"));
        }
        finally
        {
            await application.StopAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Manual_machine_package_contains_only_applicable_non_executable_artifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), "MeimadPlanner.ManualPackage.Tests", Guid.NewGuid().ToString("N"));
        var releaseRoot = Path.Combine(root, "releases");
        var packageRoot = Path.Combine(root, "packages");
        Directory.CreateDirectory(root);
        await using var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5098",
             $"--Database:Path={Path.Combine(root, "test.db")}",
             $"--GCode:ReleaseRoot={releaseRoot}", $"--ProductionPackages:PackageRoot={packageRoot}"],
            webHost => webHost.UseTestServer());
        try
        {
            await application.StartAsync();
            await SeedAsync(application.Services, releaseRoot, false);
            await using (var connection = await application.Services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync())
            await using (var update = connection.CreateCommand())
            {
                update.CommandText = "UPDATE machines SET execution_mode='MANUAL' WHERE id='machine-package';";
                await update.ExecuteNonQueryAsync();
            }
            using var client = application.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "tool-room-client");
            client.DefaultRequestHeaders.Add("X-Meimad-User-Id", "tool-room-user");
            using var create = await client.PostAsync(
                "/api/v1/batch-operations/operation-package/production-package",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            using var document = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
            var types = document.RootElement.GetProperty("artifacts").EnumerateArray()
                .Select(value => value.GetProperty("artifactType").GetString()).ToArray();
            Assert.Equal(new[] { "TOOL_TABLE", "MANIFEST" }, types);
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("gCodeReleaseId").ValueKind);
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("offsetLoaderReleaseId").ValueKind);
            Assert.False(document.RootElement.GetProperty("verificationEnabled").GetBoolean());

            await using (var connection = await application.Services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync())
            await using (var reassign = connection.CreateCommand())
            {
                reassign.CommandText = """
                    INSERT INTO machines(id,number,name,machine_type,working_calendar_id,status,is_active,
                                         display_enabled,execution_mode,usable_tool_positions)
                    VALUES('machine-package-2','M-PKG-2','Second Manual Machine','mill','calendar-package',
                           'active',1,1,'MANUAL',20);
                    UPDATE machine_assignments SET machine_id='machine-package-2'
                    WHERE id='assignment-package';
                    """;
                await reassign.ExecuteNonQueryAsync();
            }
            using var stale = await client.GetAsync(
                "/api/v1/batch-operations/operation-package/production-package");
            Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        }
        finally
        {
            await application.StopAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Corrupt_source_fails_atomically_and_never_creates_setup_readiness()
    {
        var root = Path.Combine(Path.GetTempPath(), "MeimadPlanner.AtomicPackage.Tests", Guid.NewGuid().ToString("N"));
        var releaseRoot = Path.Combine(root, "releases");
        var packageRoot = Path.Combine(root, "packages");
        Directory.CreateDirectory(root);
        await using var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5098",
             $"--Database:Path={Path.Combine(root, "test.db")}",
             $"--GCode:ReleaseRoot={releaseRoot}", $"--ProductionPackages:PackageRoot={packageRoot}"],
            webHost => webHost.UseTestServer());
        try
        {
            await application.StartAsync();
            await SeedAsync(application.Services, releaseRoot, false);
            var source = Path.Combine(releaseRoot, "operations", "case-operation-package", "gcode", "gcode-1", "main.nc");
            await File.AppendAllTextAsync(source, "(CORRUPTION)\r\n", Encoding.ASCII);

            using var client = application.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "tool-room-client");
            client.DefaultRequestHeaders.Add("X-Meimad-User-Id", "tool-room-user");
            using var create = await client.PostAsync(
                "/api/v1/batch-operations/operation-package/production-package",
                new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, create.StatusCode);
            using var problem = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
            Assert.Equal("production_package_source_corrupt",
                problem.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(["operation-package"], await QueueIdsAsync(client, "TOOL_PREPARATION_PENDING"));
            Assert.Empty(await QueueIdsAsync(client, "SETUP_PENDING"));
            using var current = await client.GetAsync(
                "/api/v1/batch-operations/operation-package/production-package");
            Assert.Equal(HttpStatusCode.NotFound, current.StatusCode);
            Assert.Empty(Directory.Exists(packageRoot) ? Directory.GetDirectories(packageRoot) : []);
        }
        finally
        {
            await application.StopAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task<IReadOnlyList<string>> QueueIdsAsync(HttpClient client, string stage)
    {
        using var response = await client.GetAsync($"/api/v1/preparation-queues/{stage}");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Select(value => value.GetProperty("batchOperationId").GetString()!).ToArray();
    }

    private static async Task SeedAsync(IServiceProvider services, string releaseRoot, bool verificationEnabled)
    {
        var gcodeRelative = "operations/case-operation-package/gcode/gcode-1/main.nc";
        var toolRelative = "operations/case-operation-package/tool-tables/tools-1/tools.csv";
        var gcodePath = Path.Combine(releaseRoot, gcodeRelative.Replace('/', Path.DirectorySeparatorChar));
        var toolPath = Path.Combine(releaseRoot, toolRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(gcodePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(toolPath)!);
        var template = string.Join("\r\n", new[]
        {
            "%", "O01995", "(MEIMAD PACKAGE VERIFY V1 NCID=483921)",
            "(MEIMAD PACKAGE CYCLE START V1)", "G90", "(MEIMAD PACKAGE CYCLE END V1)", "M30", "%", ""
        });
        await File.WriteAllTextAsync(gcodePath, template, Encoding.ASCII);
        await File.WriteAllTextAsync(toolPath, "tool,description\n", Encoding.UTF8);
        var gcodeBytes = await File.ReadAllBytesAsync(gcodePath);
        var toolBytes = await File.ReadAllBytesAsync(toolPath);
        var now = "2026-09-01T08:00:00Z";
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars(id,name,time_zone_id,calendar_json)
            VALUES('calendar-package','Package Calendar','UTC','{}');
            INSERT INTO machines(id,number,name,machine_type,working_calendar_id,status,is_active,
                                 display_enabled,execution_mode,usable_tool_positions)
            VALUES('machine-package','M-PKG','Package Mill','mill','calendar-package','active',1,1,'CNC_GCODE',20);
            INSERT INTO cases(id,part_number,name,working_folder_path)
            VALUES('case-package','PN-PKG','Package Part',$workingFolder);
            INSERT INTO case_operations(id,case_id,operation_number,route_position,name,required_machine_type,
                                        setup_seconds,cycle_seconds)
            VALUES('case-operation-package','case-package',10,0,'Finish','mill',60,60);
            INSERT INTO tool_table_releases(
                id,case_operation_id,revision_number,original_file_name,stored_relative_path,file_size,file_hash,
                released_at,released_by,release_comment,created_at,updated_at,required_tool_count)
            VALUES('tools-1','case-operation-package',1,'tools.csv',$toolPath,20,$toolHash,$at,'tool-user','Initial',$at,$at,0);
            INSERT INTO process_revisions(
                id,case_operation_id,revision_number,is_active,tool_table_release_id,created_at,created_by,
                change_description,version,updated_at,manufacturing_program_id)
            VALUES('process-1','case-operation-package',1,1,'tools-1',$at,'nc-user','Initial',1,$at,
                   'case-operation:case-operation-package');
            INSERT INTO manufacturing_program_revision_outputs(
                id,process_revision_id,case_operation_id,quantity_per_cycle,display_order,execution_metadata_json,created_at)
            VALUES('output-1','process-1','case-operation-package',1,0,'{}',$at);
            INSERT INTO postprocessors(id,name,is_active,version,created_at,updated_at)
            VALUES('post-1','Haas4x',1,1,$at,$at);
            INSERT INTO machine_supported_postprocessors(machine_id,postprocessor_id,created_at,updated_at)
            VALUES('machine-package','post-1',$at,$at);
            INSERT INTO gcode_releases(
                id,case_operation_id,process_revision_id,postprocessor_id,post_specific_revision,
                original_file_name,stored_relative_path,file_size,file_hash,released_at,released_by,
                change_scope,release_comment,tool_table_release_id,created_at,updated_at)
            VALUES('gcode-1','case-operation-package','process-1','post-1',1,'main.nc',$gcodePath,200,$gcodeHash,
                   $at,'nc-user','NEW_PROCESS_REVISION','Initial','tools-1',$at,$at);
            INSERT INTO gcode_release_verification_hooks(
                gcode_release_id,hook_version,invocation_kind,invocation_number,nc_identity_token,line_number,created_at,updated_at)
            VALUES('gcode-1',1,'G65',9002,483921,3,$at,$at);
            INSERT INTO production_batches(id,case_id,batch_number,status,planned_quantity)
            VALUES('batch-package','case-package','B-PKG','waiting',10);
            INSERT INTO batch_operations(
                id,production_batch_id,source_case_operation_id,operation_number,route_position,name,
                required_machine_type,setup_seconds,cycle_seconds,status)
            VALUES('operation-package','batch-package','case-operation-package',10,0,'Finish','mill',60,60,'not_started');
            INSERT INTO machine_assignments(id,batch_operation_id,machine_id,backlog_position)
            VALUES('assignment-package','operation-package','machine-package',0);
            UPDATE machine_assignments SET selected_gcode_release_id='gcode-1'
            WHERE id='assignment-package';
            """;
        command.Parameters.AddWithValue("$workingFolder", Path.Combine(releaseRoot, "working"));
        command.Parameters.AddWithValue("$toolPath", toolRelative);
        command.Parameters.AddWithValue("$gcodePath", gcodeRelative);
        command.Parameters.AddWithValue("$toolHash", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(toolBytes)));
        command.Parameters.AddWithValue("$gcodeHash", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(gcodeBytes)));
        command.Parameters.AddWithValue("$at", now);
        await command.ExecuteNonQueryAsync();

        if (verificationEnabled)
        {
            await using var verification = connection.CreateCommand();
            verification.CommandText = """
                INSERT INTO cnc_verification_settings(
                    machine_id,dprint_transport,dprint_port,challenge_program_number,verify_program_number,
                    custom_gcode_alias,nonce_variable,response_variable,verification_state_variable,
                    release_token_variable,expected_macro_version,response_code_digits,verification_timeout_seconds,
                    enabled,version,created_at,updated_at,finalize_program_number,event_sequence_variable)
                VALUES('machine-package','HAAS_DPRNT_TCP',8080,9001,9002,NULL,10501,500,10502,10503,
                       10,6,120,1,1,$at,$at,9003,10504);
                """;
            verification.Parameters.AddWithValue("$at", now);
            await verification.ExecuteNonQueryAsync();
        }
    }

    private static async Task SupersedeGCodeAsync(IServiceProvider services, string releaseRoot)
    {
        var relative = "operations/case-operation-package/gcode/gcode-2/main-r2.nc";
        var path = Path.Combine(releaseRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path,
            "%\r\nO01996\r\n(MEIMAD PACKAGE VERIFY V1 NCID=583921)\r\nM30\r\n%\r\n",
            Encoding.ASCII);
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO gcode_releases(
                id,case_operation_id,process_revision_id,postprocessor_id,post_specific_revision,
                original_file_name,stored_relative_path,file_size,file_hash,released_at,released_by,
                change_scope,release_comment,tool_table_release_id,created_at,updated_at)
            VALUES('gcode-2','case-operation-package','process-1','post-1',2,'main-r2.nc',$path,100,$hash,
                   $at,'nc-user','LOCAL_POST_REVISION','R2','tools-1',$at,$at);
            INSERT INTO gcode_release_verification_hooks(
                gcode_release_id,hook_version,invocation_kind,invocation_number,nc_identity_token,line_number,created_at,updated_at)
            VALUES('gcode-2',1,'G65',9002,583921,3,$at,$at);
            UPDATE machine_assignments SET selected_gcode_release_id='gcode-2'
            WHERE id='assignment-package';
            """;
        command.Parameters.AddWithValue("$path", relative);
        command.Parameters.AddWithValue("$hash", new string('c', 64));
        command.Parameters.AddWithValue("$at", "2026-09-01T09:00:00Z");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ChangeVerificationConfigurationAsync(IServiceProvider services)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE cnc_verification_settings
            SET expected_macro_version=11,version=version+1,updated_at='2026-09-01T09:30:00Z'
            WHERE machine_id='machine-package';
            """;
        await command.ExecuteNonQueryAsync();
    }
}
