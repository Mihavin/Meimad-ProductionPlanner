using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.ProductionPackages;
using Meimad.Planner.Server.Domain.Readiness;
using Meimad.Planner.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;

namespace Meimad.Planner.Server.Tests.Readiness;

public sealed class ReadinessEvidenceBatchTests
{
    [Theory]
    [InlineData("ARMED")]
    [InlineData("PENDING")]
    [InlineData("SUCCEEDED")]
    public async Task Batch_boundaries_preserve_exact_verification_offset_and_action_evidence(string state)
    {
        await using var fixture = await Fixture.CreateAsync(includeToolRows: true);
        var seed = new StringBuilder();
        for (var i = 0; i < 102; i++)
            seed.Append($"""
                INSERT INTO production_batches(id,case_id,batch_number,status,planned_quantity)
                VALUES('batch-{i}','case-package','batch-{i}','waiting',10);
                INSERT INTO batch_operations(id,production_batch_id,source_case_operation_id,operation_number,route_position,name,
                    required_machine_type,setup_seconds,cycle_seconds,status)
                VALUES('op-{i}','batch-{i}','case-operation-package',10,0,'Test','mill',60,60,'not_started');
                INSERT INTO machine_assignments(id,batch_operation_id,machine_id,backlog_position)
                VALUES('assignment-{i}','op-{i}','machine-package',{i + 1});
                """);
        await fixture.ExecuteAsync(seed.ToString());
        await fixture.ExecuteAsync("""
            INSERT INTO machine_package_capabilities(machine_id,allow_manual_dummy_tool_offsets,updated_at,updated_by)
            VALUES('machine-package',1,'2026-10-08','test');
            UPDATE machine_assignments SET released_at='2026-10-08' WHERE id='assignment-101';
            """);
        await fixture.App.Services.GetRequiredService<ProductionPackageService>().CreateAsync("operation-package", "test", "MANUAL_DUMMY");
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO machine_connections(id,machine_id,adapter_type,enabled,connection_status,polling_interval_ms,
                    connection_timeout_ms,maximum_reconnect_backoff_ms,allow_read,allow_write,configuration_json,
                    raw_telemetry_retention_days,version,created_at,updated_at)
                VALUES('test-dprnt','machine-package','HAAS_NGC',1,'OFFLINE',1000,3000,30000,1,0,$json,14,1,'2026-10-08','2026-10-08');
                INSERT INTO offset_loader_releases(id,production_run_id,machine_id,nc_release_id,tool_table_release_id,
                    verification_release_token,created_at,created_by)
                VALUES('test-loader','run:batch-operation:operation-package','machine-package','gcode-1','tools-1',999999,'2026-10-08','test');
                INSERT INTO production_run_current_offset_loaders(production_run_id,machine_id,offset_loader_release_id,selected_at,selected_by,version)
                VALUES('run:batch-operation:operation-package','machine-package','test-loader','2026-10-08','test',1)
                ON CONFLICT(production_run_id) DO UPDATE SET offset_loader_release_id=excluded.offset_loader_release_id;
                INSERT INTO production_run_workflow_events(id,production_run_id,machine_id,event_type,source,source_event_id,
                    server_received_at,nc_release_id,offset_loader_release_id,metadata_json)
                VALUES('test-loader-event','run:batch-operation:operation-package','machine-package','OFFSET_LOADER_COMPLETED','TEST',
                    'test-loader-event','2026-10-08','gcode-1','test-loader','{}'),
                    ('test-resolution','run:batch-operation:operation-package','machine-package','SETUP_VERIFICATION_SUCCEEDED','TEST',
                    'test-resolution','2026-10-08','gcode-1','test-loader','{}');
                INSERT INTO cnc_setup_verification_sessions(id,production_run_id,machine_id,nc_release_id,offset_loader_release_id,
                    nonce,macro_version,response_code_digits,state,created_at,pending_started_at,expires_at,resolved_at,
                    source_workflow_event_id,resolution_workflow_event_id)
                VALUES('test-session','run:batch-operation:operation-package','machine-package','gcode-1','test-loader',100000,3,4,
                    $state,'2026-10-08T00:00:00Z',$pending,$expires,$resolved,'test-loader-event',$resolution);
                """;
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(new
            { dprnt = new { source = "FILE", filePath = Path.Combine(fixture.ReleaseRoot, "test-dprnt.txt"), clearPolicy = "NEVER" } }));
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$pending", state == "ARMED" ? DBNull.Value : "2026-10-08T00:00:00Z");
            command.Parameters.AddWithValue("$expires", state == "ARMED" ? DBNull.Value : "2026-10-08T00:05:00Z");
            command.Parameters.AddWithValue("$resolved", state == "SUCCEEDED" ? "2026-10-08T00:01:00Z" : DBNull.Value);
            command.Parameters.AddWithValue("$resolution", state == "SUCCEEDED" ? "test-resolution" : DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
        await CompareAsync(fixture, state, dummy: true, loader: true);
        // A new snapshot must reject a stale package allocation and an obsolete loader binding.
        await fixture.ExecuteAsync("""
            UPDATE production_run_outputs SET target_quantity=9 WHERE batch_operation_id='operation-package';
            DELETE FROM production_run_current_offset_loaders;
            """);
        await CompareAsync(fixture, state, dummy: false, loader: false);
        await fixture.ExecuteAsync("UPDATE cnc_verification_settings SET enabled=0,version=version+1;");
        await CompareAsync(fixture, state, dummy: false, loader: false, required: false);
    }

    private static async Task CompareAsync(Fixture fixture, string state, bool dummy, bool loader, bool required = true)
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction(deferred: true);
        var ids = Enumerable.Range(0, 102).Select(i => $"op-{i}").Append("operation-package").ToArray();
        var batch = await SqliteProductionReadinessContextReader.ReadManyAsync(connection, transaction, ids, default);
        Assert.Equal(ids.Length, batch.Count);
        foreach (var id in ids)
        {
            var single = (await SqliteProductionReadinessContextReader.ReadAsync(connection, transaction, id, default))!;
            Assert.Equal(JsonSerializer.Serialize(single), JsonSerializer.Serialize(batch[id]));
            foreach (var action in Enum.GetValues<ProductionAction>())
                Assert.Equal(JsonSerializer.Serialize(ProductionActionPolicy.Evaluate(single, ProductionReadinessEvaluator.Evaluate(single), action)),
                    JsonSerializer.Serialize(ProductionActionPolicy.Evaluate(batch[id], ProductionReadinessEvaluator.Evaluate(batch[id]), action)));
        }
        var subject = batch["operation-package"];
        Assert.Equal(required, subject.VerificationRequired);
        Assert.Equal(1, subject.ReleasedToolCount);
        Assert.Equal(loader, subject.LoaderExecutionObserved);
        Assert.Equal(loader && state == "SUCCEEDED", subject.VerificationSucceeded);
        Assert.Equal(dummy ? "MANUAL_DUMMY" : "MEASURED", subject.ToolOffsetMode);
        Assert.False(batch["op-100"].LoaderExecutionObserved);
        Assert.False(batch["op-100"].VerificationSucceeded);
        Assert.Equal("MEASURED", batch["op-100"].ToolOffsetMode);
        Assert.Null(batch["op-101"].MachineAssignmentId);
        // Exercise exact output recipe hydration and evidence batching across the same boundary.
        var runs = await SqliteProductionRunGraphReader.ReadAllAsync(connection, transaction, default);
        var packages = await SqliteMachineWorkflowReporting.ReadVerifiedPackagesAsync(connection, transaction, default);
        var references = await SqliteProductionReadinessContextReader.ReadReferencesAsync(connection, transaction,
            runs.Where(run => run.Assignment is not null).Select(run => run.Assignment!.MachineId),
            runs.SelectMany(run => run.Programs).Select(program => program.ProcessRevisionId).OfType<string>(), ids, default);
        var exactFacts = await SqliteProductionReadinessContextReader.ReadOutputFactsAsync(
            connection, transaction, runs, batch, references, packages, default);
        var checkedOutputs = 0;
        foreach (var run in runs.Where(run => run.Assignment is not null))
        foreach (var program in run.Programs)
        foreach (var output in program.Outputs)
        {
            var exact = new ProductionPackageContext(run.Assignment!.MachineAssignmentId, run.ProductionRunId,
                program.ProductionRunProgramId, output.ProductionRunOutputId, output.BatchOperationId,
                run.Assignment.MachineId, program.ProcessRevisionId, "", output.TargetQuantity);
            var cached = exactFacts.Find(connection, transaction, exact);
            Assert.NotNull(cached);
            var individual = await SqliteProductionReadinessContextReader.ReadForPackageAsync(
                connection, transaction, exact, default, useProgramRevision: true);
            Assert.Equal(JsonSerializer.Serialize(individual), JsonSerializer.Serialize(cached));
            Assert.Null(exactFacts.Find(connection, transaction, exact with { TargetQuantity = exact.TargetQuantity + 1 }));
            checkedOutputs++;
        }
        Assert.Equal(102, checkedOutputs);
    }
}
