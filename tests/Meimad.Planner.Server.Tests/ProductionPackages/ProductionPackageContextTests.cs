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

public sealed class ProductionPackageContextTests
{
    [Fact]
    public async Task Work_order_restart_refuses_to_choose_between_split_run_contexts()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SplitAsync();
        await fixture.ExecuteAsync("""
            UPDATE batch_operations SET status='in_progress',production_process_revision_id='process-1',
                production_gcode_release_id='gcode-1',production_tool_table_release_id='tools-1' WHERE id='operation-package';
            UPDATE production_runs SET status='IN_PROGRESS',structure_locked_at='2026-10-08T08:00:00Z'
                WHERE legacy_batch_operation_id='operation-package';
            """);
        await ProductionPackageApiTests.SupersedeGCodeAsync(fixture.App.Services, fixture.ReleaseRoot);
        using var preview = await fixture.Client.GetAsync("/api/v1/batches/batch-package/refresh-operations/preview");
        Assert.Equal(HttpStatusCode.Conflict, preview.StatusCode);
        Assert.Contains("production_package_context_ambiguous", await preview.Content.ReadAsStringAsync());
        using var restart = await fixture.Client.PostAsync("/api/v1/batches/batch-package/refresh-operations", null);
        Assert.Equal(HttpStatusCode.Conflict, restart.StatusCode);
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM batch_operation_setup_restarts;"));
    }

    [Fact]
    public async Task Tool_preparation_rechecks_assignment_inside_the_save_transaction()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repository = new SqliteToolPreparationRepository(fixture.Database);
        var view = await repository.ReadViewAsync("operation-package", default);
        Assert.NotNull(view?.Context);
        await fixture.ExecuteAsync("UPDATE machine_assignments SET version=version+1 WHERE id='assignment-package';");
        var error = await Assert.ThrowsAsync<ProductionPackageBuildException>(() => repository.SaveAsync(
            new("preparation", view.BatchOperationId, view.MachineId, view.ToolTableReleaseId, 1,
                DateTimeOffset.UtcNow, "test", null, new string('a', 64), []), 0, view.Context.Selection, default));
        Assert.Equal("production_package_context_changed", error.Code);
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM tool_preparations;"));
    }

    [Fact]
    public async Task Upgrade_retains_legacy_history_without_inventing_a_current_context()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.App.Services.GetRequiredService<ProductionPackageService>();
        var package = await service.CreateAsync("operation-package", "test");
        await fixture.ExecuteAsync("""
            DROP TRIGGER package_publication_insert;
            DROP TRIGGER package_publication_update;
            DROP TRIGGER package_publication_delete;
            DROP TABLE production_package_publication_versions;
            DROP TABLE production_package_requests;
            DELETE FROM schema_migrations WHERE version=100;
            DROP TABLE production_package_context_current;
            DROP TABLE production_package_contexts;
            DELETE FROM schema_migrations WHERE version=99;
            PRAGMA user_version=98;
            """);
        await fixture.App.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        Assert.Equal(101L, await fixture.ScalarAsync("PRAGMA user_version;"));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_package_contexts;"));
        Assert.Null(await service.ReadCurrentAsync("operation-package"));
        var history = await service.ReadHistoricalAsync(package.ProductionPackageId, default);
        Assert.NotNull(history);
        Assert.Null(history.Context);
        Assert.Equal(package.ManifestHash, history.ManifestHash);
        Assert.Equal(package.Artifacts.Count, history.Artifacts.Count);
        var manifest = package.Artifacts.Single(item => item.ArtifactType == "MANIFEST");
        using var artifact = await fixture.Client.GetAsync($"/api/v1/production-packages/{package.ProductionPackageId}/artifacts/{manifest.ArtifactId}");
        Assert.Equal(HttpStatusCode.OK, artifact.StatusCode);
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await check.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Split_runs_keep_queue_packages_downloads_and_loaders_separate()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SplitAsync();
        var rows = await fixture.QueueAsync();
        Assert.Equal(2, rows.Length);
        Assert.Equal(2, rows.Select(row => row.GetProperty("context").GetProperty("productionRunId").GetString()).Distinct().Count());
        var crossed = Query(rows[0].GetProperty("context")).Replace(
            "productionRunOutputId=" + Uri.EscapeDataString(rows[0].GetProperty("context").GetProperty("productionRunOutputId").GetString()!),
            "productionRunOutputId=" + Uri.EscapeDataString(rows[1].GetProperty("context").GetProperty("productionRunOutputId").GetString()!));
        using var forged = await fixture.Client.PostAsync(Fixture.Path + crossed, Fixture.Body());
        Assert.Equal(HttpStatusCode.Conflict, forged.StatusCode);
        using var ambiguous = await fixture.Client.PostAsync(Fixture.Path, Fixture.Body());
        Assert.Equal(HttpStatusCode.Conflict, ambiguous.StatusCode);
        Assert.Contains("production_package_context_ambiguous", await ambiguous.Content.ReadAsStringAsync());
        using var tools = await fixture.Client.GetAsync("/api/v1/batch-operations/operation-package/tool-preparation");
        Assert.Equal(HttpStatusCode.Conflict, tools.StatusCode);
        var packages = new List<JsonElement>();
        foreach (var row in rows)
        {
            var query = Query(row.GetProperty("context"));
            using var response = await fixture.Client.PostAsync(Fixture.Path + query, Fixture.Body());
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            var package = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
            packages.Add(package);
            Assert.Equal(row.GetProperty("machineId").GetString(), package.GetProperty("machineId").GetString());
            Assert.Equal(row.GetProperty("productionRunId").GetString(), package.GetProperty("productionRunId").GetString());
            using var preparation = await fixture.Client.GetAsync("/api/v1/batch-operations/operation-package/tool-preparation" + query);
            Assert.Equal(HttpStatusCode.OK, preparation.StatusCode);
            var machine = JsonDocument.Parse(await preparation.Content.ReadAsStringAsync()).RootElement.GetProperty("machineId").GetString();
            Assert.Equal(row.GetProperty("machineId").GetString(), machine);
            var artifact = package.GetProperty("artifacts")[0].GetProperty("artifactId").GetString();
            using var download = await fixture.Client.GetAsync(Fixture.Path + "/artifacts/" + artifact + query);
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        }
        Assert.Equal(2, (await fixture.QueueAsync("SETUP_PENDING")).Length);
        using var wrongArtifact = await fixture.Client.GetAsync(Fixture.Path + "/artifacts/" + packages[0].GetProperty("artifacts")[0].GetProperty("artifactId").GetString() + Query(rows[1].GetProperty("context")));
        Assert.Equal(HttpStatusCode.NotFound, wrongArtifact.StatusCode);
        Assert.Equal(2L, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_package_context_current;"));
        Assert.Equal(2L, await fixture.ScalarAsync("SELECT COUNT(*) FROM offset_loader_releases loader JOIN production_packages package ON package.id=json_extract(loader.metadata_json,'$.productionPackageId') WHERE loader.production_run_id=package.production_run_id AND loader.machine_id=package.machine_id;"));
        await using var connection = await fixture.Database.OpenConnectionAsync();
        var verified = await SqliteMachineWorkflowReporting.ReadVerifiedPackagesAsync(connection, null, default);
        Assert.Contains(("operation-package", "machine-package"), verified);
        Assert.Contains(("operation-package", "machine-2"), verified);
    }

    [Fact]
    public async Task Cancelled_history_never_wins_current_resolution_and_remains_inspectable()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var created = await fixture.Client.PostAsync(Fixture.Path, Fixture.Body());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var old = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.Clone();
        await fixture.SplitAsync();
        await fixture.ExecuteAsync("UPDATE production_runs SET status='CANCELLED' WHERE legacy_batch_operation_id='operation-package'; UPDATE machine_assignments SET released_at='2026-10-08T12:00:00Z' WHERE id='assignment-package';");
        var row = Assert.Single(await fixture.QueueAsync());
        Assert.Equal("run-2", row.GetProperty("productionRunId").GetString());
        using var current = await fixture.Client.GetAsync(Fixture.Path);
        Assert.Equal(HttpStatusCode.NotFound, current.StatusCode);
        using var history = await fixture.Client.GetAsync("/api/v1/production-packages/" + old.GetProperty("productionPackageId").GetString());
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var oldContext = await fixture.Client.GetAsync(Fixture.Path + Query(old.GetProperty("context")));
        Assert.Equal(HttpStatusCode.Conflict, oldContext.StatusCode);
        using var next = await fixture.Client.PostAsync(Fixture.Path, Fixture.Body());
        Assert.Equal(HttpStatusCode.Created, next.StatusCode);
        Assert.Equal("run-2", JsonDocument.Parse(await next.Content.ReadAsStringAsync()).RootElement.GetProperty("productionRunId").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Assignment_change_between_build_and_activation_is_atomic(bool release)
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.App.Services.GetRequiredService<ProductionPackageService>();
        var package = await service.CreateAsync("operation-package", "test");
        var observed = await new SqliteProductionPackageRepository(fixture.Database).ReadBuildContextAsync("operation-package", default);
        await fixture.ExecuteAsync(release
            ? "UPDATE machine_assignments SET released_at='2026-10-08T12:00:00Z',version=version+1 WHERE id='assignment-package';"
            : "UPDATE machine_assignments SET version=version+1 WHERE id='assignment-package';");
        var repository = new SqliteProductionPackageRepository(fixture.Database);
        var error = await Assert.ThrowsAsync<ProductionPackageBuildException>(() => repository.PublishAsync(package with { ProductionPackageId = "stale-build" }, null, observed!, null, default));
        Assert.Equal("production_package_context_changed", error.Code);
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_packages;"));
        if (release)
        {
            Assert.Empty(await fixture.QueueAsync());
            using var current = await fixture.Client.GetAsync(Fixture.Path);
            Assert.Equal(HttpStatusCode.NotFound, current.StatusCode);
            using var create = await fixture.Client.PostAsync(Fixture.Path, Fixture.Body());
            Assert.False(create.IsSuccessStatusCode);
        }
    }

    [Fact]
    public async Task Multiple_programs_and_coupled_outputs_have_distinct_actionable_rows()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SplitAsync();
        await fixture.ExecuteAsync("""
            UPDATE production_run_programs SET target_cycle_count=3 WHERE production_run_id='run-2';
            UPDATE production_run_outputs SET target_quantity=3 WHERE production_run_program_id='zz-program-2';
            INSERT INTO production_run_programs(id,production_run_id,manufacturing_program_id,process_revision_id,selected_gcode_release_id,sequence_position,target_cycle_count,status,created_at,updated_at)
            VALUES('program-3','run-2','case-operation:case-operation-package','process-1','gcode-1',1,2,'PLANNED','2026-10-08','2026-10-08');
            INSERT INTO production_run_outputs(id,production_run_program_id,batch_operation_id,revision_output_id,quantity_per_cycle,target_quantity,created_at,updated_at)
            VALUES('run-output-3','program-3','operation-package','output-1',1,2,'2026-10-08','2026-10-08');
            INSERT INTO case_operations(id,case_id,operation_number,route_position,name,required_machine_type,setup_seconds,cycle_seconds)
            VALUES('coupled-case-op','case-package',20,1,'Coupled output','mill',60,60);
            INSERT INTO batch_operations(id,production_batch_id,source_case_operation_id,operation_number,route_position,name,required_machine_type,setup_seconds,cycle_seconds,status)
            VALUES('coupled-op','batch-package','coupled-case-op',20,1,'Coupled output','mill',60,60,'not_started');
            INSERT INTO manufacturing_program_revision_outputs(id,process_revision_id,case_operation_id,quantity_per_cycle,display_order,created_at)
            VALUES('coupled-recipe-output','process-1','coupled-case-op',2,1,'2026-10-08');
            INSERT INTO production_run_outputs(id,production_run_program_id,batch_operation_id,revision_output_id,quantity_per_cycle,target_quantity,created_at,updated_at)
            VALUES('coupled-output','zz-program-2','coupled-op','coupled-recipe-output',2,6,'2026-10-08','2026-10-08');
            """);
        var rows = await fixture.QueueAsync();
        Assert.Equal(4, rows.Length);
        Assert.Equal(4, rows.Select(row => row.GetProperty("context").GetProperty("productionRunOutputId").GetString()).Distinct().Count());
        var coupled = rows.Single(row => row.GetProperty("batchOperationId").GetString() == "coupled-op");
        Assert.Equal("case-operation-package", coupled.GetProperty("recipeCaseOperationId").GetString());
        using var created = await fixture.Client.PostAsync("/api/v1/batch-operations/coupled-op/production-package" + Query(coupled.GetProperty("context")), Fixture.Body());
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var package = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("zz-program-2", package.GetProperty("context").GetProperty("productionRunProgramId").GetString());
        Assert.Equal(6, package.GetProperty("context").GetProperty("targetQuantity").GetInt32());
        Assert.Equal("gcode-1", package.GetProperty("gCodeReleaseId").GetString());
        await fixture.ExecuteAsync("UPDATE production_run_outputs SET target_quantity=5,version=version+1 WHERE id='coupled-output';");
        using var invalid = await fixture.Client.PostAsync("/api/v1/batch-operations/coupled-op/production-package", Fixture.Body());
        Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode);
        Assert.Contains("production_package_context_invalid", await invalid.Content.ReadAsStringAsync());
    }

    private static string Query(JsonElement context) => "?" + string.Join("&", new[] { "machineAssignmentId", "productionRunId", "productionRunProgramId", "productionRunOutputId", "contextStamp" }
        .Select(key => key + "=" + Uri.EscapeDataString(context.GetProperty(key).GetString()!)));

    internal sealed class Fixture(WebApplication app, string root) : IAsyncDisposable
    {
        internal const string Path = "/api/v1/batch-operations/operation-package/production-package";
        internal WebApplication App => app;
        internal string ReleaseRoot => root + "/releases";
        internal SqliteDatabase Database => app.Services.GetRequiredService<SqliteDatabase>();
        internal HttpClient Client { get; } = app.GetTestClient();
        internal static StringContent Body() => new("{}", Encoding.UTF8, "application/json");
        internal static async Task<Fixture> CreateAsync()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Meimad.C03", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var app = ServerApplication.Build([$"--Database:Path={root}/test.db", $"--GCode:ReleaseRoot={root}/releases", $"--ProductionPackages:PackageRoot={root}/packages"], host => host.UseSignedInTestServer());
            await app.StartAsync();
            await ProductionPackageApiTests.SeedAsync(app.Services, root + "/releases", true);
            var fixture = new Fixture(app, root);
            fixture.Client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "test-client");
            fixture.Client.DefaultRequestHeaders.Add("X-Meimad-User-Id", "test-user");
            return fixture;
        }
        internal async Task ExecuteAsync(string sql)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var command = connection.CreateCommand(); command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        internal async Task<long> ScalarAsync(string sql)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var command = connection.CreateCommand(); command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        internal async Task<JsonElement[]> QueueAsync(string stage = "TOOL_PREPARATION_PENDING")
        {
            using var response = await Client.GetAsync("/api/v1/preparation-queues/" + stage);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.GetProperty("items").EnumerateArray().Select(row => row.Clone()).ToArray();
        }
        internal Task SplitAsync() => ExecuteAsync("""
            UPDATE production_run_programs SET target_cycle_count=5;
            UPDATE production_run_outputs SET target_quantity=5;
            INSERT INTO machines(id,number,name,machine_type,working_calendar_id,status,is_active,display_enabled,execution_mode,usable_tool_positions)
            VALUES('machine-2','M2','Second mill','mill','calendar-package','active',1,1,'CNC_GCODE',20);
            INSERT INTO machine_supported_postprocessors(machine_id,postprocessor_id,created_at,updated_at)
            VALUES('machine-2','post-1','2026-10-08','2026-10-08');
            INSERT INTO cnc_verification_settings(
                machine_id,dprint_transport,dprint_port,challenge_program_number,verify_program_number,
                nonce_variable,response_variable,verification_state_variable,release_token_variable,expected_macro_version,
                response_code_digits,verification_timeout_seconds,enabled,version,created_at,updated_at,finalize_program_number,event_sequence_variable)
            SELECT 'machine-2',dprint_transport,dprint_port,challenge_program_number,verify_program_number,
                nonce_variable,response_variable,verification_state_variable,release_token_variable,expected_macro_version,
                response_code_digits,verification_timeout_seconds,enabled,version,created_at,updated_at,finalize_program_number,event_sequence_variable
            FROM cnc_verification_settings WHERE machine_id='machine-package';
            INSERT INTO production_runs(id,status) VALUES('run-2','PLANNED');
            INSERT INTO production_run_programs(id,production_run_id,manufacturing_program_id,process_revision_id,selected_gcode_release_id,sequence_position,target_cycle_count,status,created_at,updated_at)
            VALUES('zz-program-2','run-2','case-operation:case-operation-package','process-1','gcode-1',0,5,'PLANNED','2026-10-08','2026-10-08');
            INSERT INTO production_run_outputs(id,production_run_program_id,batch_operation_id,revision_output_id,quantity_per_cycle,target_quantity,created_at,updated_at)
            VALUES('run-output-2','zz-program-2','operation-package','output-1',1,5,'2026-10-08','2026-10-08');
            INSERT INTO machine_assignments(id,batch_operation_id,production_run_id,machine_id,backlog_position)
            VALUES('assignment-2','operation-package','run-2','machine-2',0);
            """);
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
