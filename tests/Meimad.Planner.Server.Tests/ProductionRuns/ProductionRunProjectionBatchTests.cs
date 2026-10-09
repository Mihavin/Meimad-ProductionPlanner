using System.Text.Json;
using Meimad.Planner.Server.Application.PlanningBoard;
using Meimad.Planner.Server.Application.ProductionPackages;
using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;

namespace Meimad.Planner.Server.Tests.ProductionRuns;

public sealed class ProductionRunProjectionBatchTests
{
    [Theory]
    [InlineData("")]
    [InlineData("UPDATE machines SET usable_tool_positions=NULL")]
    [InlineData("UPDATE machines SET execution_mode='MANUAL'")]
    [InlineData("UPDATE machine_assignments SET released_at='2026-10-08T12:00:00Z'")]
    [InlineData("DELETE FROM machine_supported_postprocessors")]
    [InlineData("INSERT INTO tool_preparations(id,batch_operation_id,machine_id,tool_table_release_id,version_number,saved_at,saved_by,content_hash) VALUES('measured-v1','operation-package','machine-package','tools-1',1,'2026-10-08T10:00:00Z','test',lower(hex(zeroblob(32)))),('measured-v2','operation-package','machine-package','tools-1',2,'2026-10-08T11:00:00Z','test',lower(hex(zeroblob(32))))")]
    [InlineData("UPDATE production_run_outputs SET target_quantity=9")]
    [InlineData("UPDATE production_run_programs SET status='COMPLETED'")]
    [InlineData("UPDATE production_runs SET status='CANCELLED'")]
    [InlineData("UPDATE production_run_programs SET process_revision_id=NULL,selected_gcode_release_id=NULL,legacy_unmanaged=1")]
    [InlineData("UPDATE process_revisions SET is_active=0; INSERT INTO process_revisions(id,case_operation_id,revision_number,is_active,tool_table_release_id,created_at,created_by,change_description,version,updated_at,manufacturing_program_id) VALUES('process-2','case-operation-package',2,1,'tools-1','2026-10-08','test','New recipe',1,'2026-10-08','case-operation:case-operation-package')")]
    [InlineData("UPDATE production_run_programs SET production_process_revision_id='process-1',production_gcode_release_id='gcode-1',production_tool_table_release_id='tools-1',production_gcode_file_hash='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',production_tool_table_file_hash='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'")]
    public async Task Snapshot_graph_and_readiness_match_individual_reads(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (change.Length > 0) await fixture.ExecuteAsync(change);
        await CompareAsync(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Split_assignments_and_multiple_programs_keep_exact_contexts(bool multiplePrograms)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SplitAsync();
        if (multiplePrograms)
            await fixture.ExecuteAsync("""
                UPDATE production_run_programs SET target_cycle_count=3 WHERE production_run_id='run-2';
                UPDATE production_run_outputs SET target_quantity=3 WHERE production_run_program_id='zz-program-2';
                INSERT INTO production_run_programs(id,production_run_id,manufacturing_program_id,process_revision_id,selected_gcode_release_id,sequence_position,target_cycle_count,status,created_at,updated_at)
                VALUES('program-3','run-2','case-operation:case-operation-package','process-1','gcode-1',1,2,'PLANNED','2026-10-08','2026-10-08');
                INSERT INTO production_run_outputs(id,production_run_program_id,batch_operation_id,revision_output_id,quantity_per_cycle,target_quantity,created_at,updated_at)
                VALUES('run-output-3','program-3','operation-package','output-1',1,2,'2026-10-08','2026-10-08');
                UPDATE machines SET usable_tool_positions=NULL WHERE id='machine-2';
                INSERT INTO case_operations(id,case_id,operation_number,route_position,name,required_machine_type,setup_seconds,cycle_seconds)
                VALUES('coupled-case-op','case-package',20,1,'Coupled output','mill',60,60);
                INSERT INTO batch_operations(id,production_batch_id,source_case_operation_id,operation_number,route_position,name,required_machine_type,setup_seconds,cycle_seconds,status)
                VALUES('coupled-op','batch-package','coupled-case-op',20,1,'Coupled output','mill',60,60,'not_started');
                INSERT INTO manufacturing_program_revision_outputs(id,process_revision_id,case_operation_id,quantity_per_cycle,display_order,created_at)
                VALUES('coupled-recipe-output','process-1','coupled-case-op',2,1,'2026-10-08');
                INSERT INTO production_run_outputs(id,production_run_program_id,batch_operation_id,revision_output_id,quantity_per_cycle,target_quantity,created_at,updated_at)
                VALUES('coupled-output','zz-program-2','coupled-op','coupled-recipe-output',2,6,'2026-10-08','2026-10-08');
                """);
        await CompareAsync(fixture);
    }

    [Fact]
    public async Task Exact_dummy_package_and_material_readiness_survive_preloading()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("""
            INSERT INTO machine_package_capabilities(machine_id,allow_manual_dummy_tool_offsets,updated_at,updated_by)
            VALUES('machine-package',1,'2026-10-08','test');
            INSERT INTO verified_material_receipts(id,case_id,quantity,received_at,verified_at,verified_by,source,created_at,updated_at)
            VALUES('receipt','case-package',10,'2026-10-08','2026-10-08','test','LOCAL_VERIFIED','2026-10-08','2026-10-08');
            INSERT INTO batch_material_reservations(id,receipt_id,production_batch_id,quantity,reserved_at,reserved_by,created_at,updated_at)
            VALUES('reservation','receipt','batch-package',10,'2026-10-08','test','2026-10-08','2026-10-08');
            """);
        await fixture.App.Services.GetRequiredService<ProductionPackageService>().CreateAsync("operation-package", "test", "MANUAL_DUMMY");
        await CompareAsync(fixture);
        await fixture.ExecuteAsync("DELETE FROM production_package_context_current");
        await CompareAsync(fixture);
    }

    [Fact]
    public async Task Two_programs_for_one_operation_keep_dummy_packages_scoped_to_the_exact_output()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("""
            UPDATE production_run_programs SET target_cycle_count=5;
            UPDATE production_run_outputs SET target_quantity=5;
            INSERT INTO production_run_programs(id,production_run_id,manufacturing_program_id,process_revision_id,selected_gcode_release_id,sequence_position,target_cycle_count,status,created_at,updated_at)
            VALUES('program-2','run:batch-operation:operation-package','case-operation:case-operation-package','process-1','gcode-1',1,5,'PLANNED','2026-10-08','2026-10-08');
            INSERT INTO production_run_outputs(id,production_run_program_id,batch_operation_id,revision_output_id,quantity_per_cycle,target_quantity,created_at,updated_at)
            VALUES('run-output-2','program-2','operation-package','output-1',1,5,'2026-10-08','2026-10-08');
            INSERT INTO machine_package_capabilities(machine_id,allow_manual_dummy_tool_offsets,updated_at,updated_by)
            VALUES('machine-package',1,'2026-10-08','test');
            """);
        ProductionPackageContext selected;
        await using (var connection = await fixture.Database.OpenConnectionAsync())
            selected = (await SqliteProductionPackageContext.ListAsync(connection, null, null, default))
                .Single(context => context.ProductionRunOutputId == "run-output-2");
        await fixture.App.Services.GetRequiredService<ProductionPackageService>().CreateAsync(
            "operation-package", "test", "MANUAL_DUMMY", selection: selected.Selection);
        await CompareAsync(fixture);

        await using var readConnection = await fixture.Database.OpenConnectionAsync();
        await using var transaction = readConnection.BeginTransaction(deferred: true);
        var runs = await SqliteProductionRunGraphReader.ReadAllAsync(readConnection, transaction, default);
        var bases = await SqliteProductionReadinessContextReader.ReadManyAsync(readConnection, transaction, ["operation-package"], default);
        var packages = await SqliteMachineWorkflowReporting.ReadVerifiedPackagesAsync(readConnection, transaction, default);
        var references = await SqliteProductionReadinessContextReader.ReadReferencesAsync(readConnection, transaction,
            ["machine-package"], ["process-1"], ["operation-package"], default);
        var facts = await SqliteProductionReadinessContextReader.ReadOutputFactsAsync(readConnection, transaction, runs, bases, references, packages, default);
        foreach (var context in await SqliteProductionPackageContext.ListAsync(readConnection, transaction, null, default))
        {
            var key = context with { ContextStamp = "", ProgramNumber = 1, OutputAllocationStamp = null };
            var actual = facts.Find(readConnection, transaction, key);
            Assert.NotNull(actual);
            Assert.Equal(context.ProductionRunOutputId == "run-output-2" ? "MANUAL_DUMMY" : "MEASURED", actual.ToolOffsetMode);
        }
    }

    private static async Task CompareAsync(Fixture fixture)
    {
        var repository = fixture.App.Services.GetRequiredService<IProductionRunRepository>();
        var listed = await repository.ListAsync(default);
        Assert.NotEmpty(listed);
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        await using (var transaction = connection.BeginTransaction(deferred: true))
        {
            var packages = await SqliteMachineWorkflowReporting.ReadVerifiedPackagesAsync(connection, transaction, default);
            var references = await SqliteProductionReadinessContextReader.ReadReferencesAsync(connection, transaction,
                listed.Where(run => run.Assignment is not null).Select(run => run.Assignment!.MachineId),
                listed.SelectMany(run => run.Programs).Select(program => program.ProductionPins.ProcessRevisionId ?? program.ProcessRevisionId).OfType<string>(),
                listed.SelectMany(run => run.Programs).SelectMany(program => program.Outputs).Select(output => output.BatchOperationId), default);
            var bases = await SqliteProductionReadinessContextReader.ReadManyAsync(connection, transaction,
                listed.SelectMany(run => run.Programs).SelectMany(program => program.Outputs)
                    .Select(output => output.BatchOperationId).ToArray(), default);
            var outputFacts = await SqliteProductionReadinessContextReader.ReadOutputFactsAsync(
                connection, transaction, listed, bases, references, packages, default);
            foreach (var run in listed)
            {
                Assert.Equal(JsonSerializer.Serialize(await SqliteProductionRunRepository.ReadAsync(connection, transaction, run.ProductionRunId, default)),
                    JsonSerializer.Serialize(run));
                var individual = await SqliteProductionRunReadinessRepository.ReadAsync(connection, transaction, run.ProductionRunId, default);
                var batched = await SqliteProductionRunReadinessRepository.ReadAsync(connection, transaction, run.ProductionRunId, default, run, bases, packages, references, outputFacts);
                // Includes all reasons, physical-verification evidence and the exact action/context stamp.
                Assert.Equal(JsonSerializer.Serialize(individual), JsonSerializer.Serialize(batched));
            }
        }
        var cards = await fixture.App.Services.GetRequiredService<IProductionRunPlanningProjectionRepository>().ReadAsync(default);
        Assert.Equal(listed.Count(run => run.Status is not ("CANCELLED" or "ABORTED")), cards.Count);
        foreach (var card in cards)
        {
            var individual = await fixture.App.Services.GetRequiredService<ProductionRunReadinessService>().ReadAsync(card.ProductionRunId);
            Assert.Equal(individual.IsReadyForProduction, card.IsReady);
            Assert.Equal(individual.OverallState, card.ReadinessState);
        }
    }
}
