using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Application.Readiness;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.ProductionPackages;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.ProductionRuns;

public sealed class ProductionRunReadinessTests
{
    [Fact]
    public async Task Dummy_mode_is_resolved_from_the_exact_current_package_and_disappears_with_its_pointer()
    {
        await using var fixture = await ProductionPackageContextTests.Fixture.CreateAsync();
        await fixture.ExecuteAsync("""
            INSERT INTO machine_package_capabilities(machine_id,allow_manual_dummy_tool_offsets,updated_at,updated_by)
            VALUES('machine-package',1,'2026-10-08','test');
            """);
        await fixture.App.Services.GetRequiredService<Meimad.Planner.Server.Application.ProductionPackages.ProductionPackageService>()
            .CreateAsync("operation-package", "test", "MANUAL_DUMMY");
        await using var connection = await fixture.Database.OpenConnectionAsync();
        var context = await SqliteProductionReadinessContextReader.ReadAsync(connection, null, "operation-package", default);
        Assert.Equal("MANUAL_DUMMY", context!.ToolOffsetMode);
        await fixture.ExecuteAsync("UPDATE production_run_outputs SET target_quantity=9;");
        context = await SqliteProductionReadinessContextReader.ReadAsync(connection, null, "operation-package", default);
        Assert.Equal("MEASURED", context!.ToolOffsetMode);
        await fixture.ExecuteAsync("UPDATE production_run_outputs SET target_quantity=10;");
        await fixture.ExecuteAsync("DELETE FROM production_package_context_current;");
        context = await SqliteProductionReadinessContextReader.ReadAsync(connection, null, "operation-package", default);
        Assert.Equal("MEASURED", context!.ToolOffsetMode);
    }

    [Fact]
    public async Task Run_and_operation_share_engineering_gates_and_missing_material_does_not_block_package()
    {
        await using var fixture = await ProductionPackageContextTests.Fixture.CreateAsync();
        var run = (await fixture.App.Services.GetRequiredService<IProductionRunRepository>().ListAsync(default)).Single();
        var readiness = fixture.App.Services.GetRequiredService<ProductionRunReadinessService>();
        var result = await readiness.ReadAsync(run.ProductionRunId);
        Assert.False(result.IsReadyForProduction);
        Assert.Contains(result.Programs.Single().Outputs.Single().Components, x => x.Key == "material" && x.IsBlocking);
        await ReserveAsync(fixture);
        Assert.True((await readiness.ReadAsync(run.ProductionRunId)).IsReadyForProduction);
        await fixture.ExecuteAsync("DELETE FROM machine_supported_postprocessors;");
        var operation = await fixture.App.Services.GetRequiredService<IProductionReadinessRepository>().ReadAsync("operation-package", default);
        result = await readiness.ReadAsync(run.ProductionRunId);
        Assert.False(operation.IsReadyForProduction);
        Assert.False(result.IsReadyForProduction);
        Assert.Contains(result.Programs.Single().Outputs.Single().Components, x => x.Key == "gcode" && x.State == "INCOMPATIBLE");
    }

    [Fact]
    public async Task Start_rejects_changed_readiness_without_mutating_or_pinning_run()
    {
        await using var fixture = await ProductionPackageContextTests.Fixture.CreateAsync();
        await ReserveAsync(fixture);
        var run = (await fixture.App.Services.GetRequiredService<IProductionRunRepository>().ListAsync(default)).Single();
        var before = await fixture.App.Services.GetRequiredService<ProductionRunReadinessService>().ReadAsync(run.ProductionRunId);
        Assert.True(before.IsReadyForProduction);
        await fixture.ExecuteAsync("UPDATE machines SET usable_tool_positions=19 WHERE id='machine-package';");
        var repository = fixture.App.Services.GetRequiredService<IProductionRunExecutionRepository>();
        var error = await Assert.ThrowsAsync<ProductionRunStateException>(() => repository.StartAsync(run.ProductionRunId,
            run.Version, new("test", 0, "test-user"), default, before.ContextStamp));
        Assert.Equal("production_readiness_changed", error.Code);
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_runs WHERE status='IN_PROGRESS';"));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_run_programs WHERE production_process_revision_id IS NOT NULL;"));
    }

    [Fact]
    public async Task Direct_repository_start_cannot_bypass_readiness_and_resume_rechecks_material()
    {
        await using var fixture = await ProductionPackageContextTests.Fixture.CreateAsync();
        var runs = fixture.App.Services.GetRequiredService<IProductionRunRepository>();
        var run = (await runs.ListAsync(default)).Single();
        var repository = fixture.App.Services.GetRequiredService<IProductionRunExecutionRepository>();
        var authority = new Meimad.Planner.Server.Application.EditMode.EditAuthority("test", 0, "test-user");
        Assert.Equal("production_not_ready", (await Assert.ThrowsAsync<ProductionRunStateException>(() =>
            repository.StartAsync(run.ProductionRunId, run.Version, authority, default))).Code);
        await ReserveAsync(fixture);
        run = await repository.StartAsync(run.ProductionRunId, run.Version, authority, default);
        Assert.Equal("gcode-1", run.Programs.Single().ProductionPins.GCodeReleaseId);
        run = await repository.SuspendAsync(run.ProductionRunId, run.Version, "test", authority, default);
        await fixture.ExecuteAsync("DELETE FROM batch_material_reservations;");
        Assert.Equal("production_not_ready", (await Assert.ThrowsAsync<ProductionRunStateException>(() =>
            repository.ResumeAsync(run.ProductionRunId, run.Version, authority, default))).Code);
        Assert.Equal("SUSPENDED", (await runs.GetAsync(run.ProductionRunId, default))!.Status);
    }

    private static Task ReserveAsync(ProductionPackageContextTests.Fixture fixture) => fixture.ExecuteAsync("""
        INSERT INTO verified_material_receipts(id,case_id,quantity,received_at,verified_at,verified_by,source,created_at,updated_at)
        VALUES('receipt','case-package',10,'2026-10-08','2026-10-08','test','LOCAL_VERIFIED','2026-10-08','2026-10-08');
        INSERT INTO batch_material_reservations(id,receipt_id,production_batch_id,quantity,reserved_at,reserved_by,created_at,updated_at)
        VALUES('reservation','receipt','batch-package',10,'2026-10-08','test','2026-10-08','2026-10-08');
        """);
}
