using System.Diagnostics.Metrics;
using System.Text.Json;
using Meimad.Planner.Server.Application.PlanningBoard;
using Meimad.Planner.Server.Application.ProductionPackages;
using Meimad.Planner.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;

namespace Meimad.Planner.Server.Tests.Readiness;

public sealed class ReadinessSnapshotFactsTests
{
    [Fact]
    public async Task Projection_catalog_reads_are_constant_as_operation_count_grows()
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var i = 1; i <= 12; i++)
            await fixture.ExecuteAsync($"""
                INSERT INTO production_batches(id,case_id,batch_number,status,planned_quantity)
                VALUES('batch-{i}','case-package','batch-{i}','waiting',10);
                INSERT INTO batch_operations(id,production_batch_id,source_case_operation_id,operation_number,route_position,name,
                    required_machine_type,setup_seconds,cycle_seconds,status)
                VALUES('op-{i}','batch-{i}','case-operation-package',10,0,'Test operation','mill',60,60,'not_started');
                INSERT INTO machine_assignments(id,batch_operation_id,machine_id,backlog_position)
                VALUES('assignment-{i}','op-{i}','machine-package',{i});
                """);
        var reads = 0;
        var referenceLoads = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Meimad.Planner.Performance" && instrument.Name is "planner.readiness.workflow_packages" or "planner.readiness.references")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) =>
        {
            if (instrument.Name == "planner.readiness.workflow_packages") Interlocked.Increment(ref reads);
            else Interlocked.Increment(ref referenceLoads);
        });
        listener.Start();
        var cards = await fixture.App.Services.GetRequiredService<IProductionRunPlanningProjectionRepository>().ReadAsync(default);
        Assert.Equal(13, cards.Count);
        Assert.Equal(1, reads);
        Assert.InRange(referenceLoads, 1, 9); // Bounded bulk loaders, not three calls per output.
        reads = 0;
        Assert.Equal(13, (await new SqlitePreparationQueueRepository(fixture.Database).ReadSourcesAsync(default)).Count);
        Assert.Equal(1, reads);
        reads = 0;
        await fixture.App.Services.GetRequiredService<IPlanningBoardRepository>().ReadAsync(default);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task Reference_facts_reject_another_snapshot_and_uncovered_keys_fall_back_to_current_reads()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var connection = await fixture.Database.OpenConnectionAsync();
        SqliteProductionReadinessContextReader.ReferenceFacts previous;
        await using (var transaction = connection.BeginTransaction(deferred: true))
        {
            previous = await SqliteProductionReadinessContextReader.ReadReferencesAsync(connection, transaction,
                ["machine-package"], ["process-1"], ["operation-package"], default);
        }
        await fixture.ExecuteAsync("DELETE FROM machine_supported_postprocessors;");
        await using var next = connection.BeginTransaction(deferred: true);
        var context = Assert.Single(await SqliteProductionPackageContext.ListAsync(connection, next, null, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteProductionReadinessContextReader.ReadForPackageAsync(
            connection, next, context, default, useProgramRevision: true, references: previous));
        var current = await SqliteProductionReadinessContextReader.ReadForPackageAsync(connection, next, context, default, useProgramRevision: true);
        var incomplete = await SqliteProductionReadinessContextReader.ReadReferencesAsync(connection, next, [], [], [], default);
        var fallback = await SqliteProductionReadinessContextReader.ReadForPackageAsync(connection, next, context, default,
            useProgramRevision: true, references: incomplete);
        Assert.Equal(JsonSerializer.Serialize(current), JsonSerializer.Serialize(fallback));
        var fresh = await SqliteProductionReadinessContextReader.ReadReferencesAsync(connection, next,
            ["machine-package"], ["process-1"], ["operation-package"], default);
        var refreshed = await SqliteProductionReadinessContextReader.ReadForPackageAsync(connection, next, context, default,
            useProgramRevision: true, references: fresh);
        Assert.Empty(refreshed.SupportedPostprocessorIds);
        Assert.Equal(JsonSerializer.Serialize(current), JsonSerializer.Serialize(refreshed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batched_contexts_match_individual_reads_and_new_snapshot_observes_pointer_changes(bool split)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO machine_connections(id,machine_id,adapter_type,enabled,connection_status,polling_interval_ms,
                    connection_timeout_ms,maximum_reconnect_backoff_ms,allow_read,allow_write,configuration_json,
                    raw_telemetry_retention_days,version,created_at,updated_at)
                VALUES('test-dprnt','machine-package','HAAS_NGC',1,'OFFLINE',1000,3000,30000,1,0,$json,14,1,
                    '2026-10-08T00:00:00Z','2026-10-08T00:00:00Z');
                """;
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(new
            { dprnt = new { source = "FILE", filePath = Path.Combine(fixture.ReleaseRoot, "test-dprnt.txt"), clearPolicy = "NEVER" } }));
            await command.ExecuteNonQueryAsync();
        }
        await fixture.App.Services.GetRequiredService<ProductionPackageService>().CreateAsync("operation-package", "test");
        if (split) await fixture.SplitAsync();
        await CompareAsync(fixture, expectPackage: true);
        // Keep history intact, but remove both current-pointer protocols.
        await fixture.ExecuteAsync("DELETE FROM production_package_context_current; DELETE FROM production_package_current;");
        await CompareAsync(fixture, expectPackage: false);
    }

    [Fact]
    public async Task Exact_output_facts_reject_another_connection_or_completed_snapshot_and_allow_missing_key_fallback()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var connection = await fixture.Database.OpenConnectionAsync();
        SqliteProductionReadinessContextReader.OutputFacts previous;
        ProductionPackageContext context;
        await using (var transaction = connection.BeginTransaction(deferred: true))
        {
            context = Assert.Single(await SqliteProductionPackageContext.ListAsync(connection, transaction, null, default))
                with { ContextStamp = "", OutputAllocationStamp = null };
            var runs = await SqliteProductionRunGraphReader.ReadAllAsync(connection, transaction, default);
            var bases = await SqliteProductionReadinessContextReader.ReadManyAsync(connection, transaction, ["operation-package"], default);
            var packages = await SqliteMachineWorkflowReporting.ReadVerifiedPackagesAsync(connection, transaction, default);
            var references = await SqliteProductionReadinessContextReader.ReadReferencesAsync(connection, transaction,
                ["machine-package"], ["process-1"], ["operation-package"], default);
            previous = await SqliteProductionReadinessContextReader.ReadOutputFactsAsync(connection, transaction, runs, bases, references, packages, default);
            Assert.NotNull(previous.Find(connection, transaction, context));
            Assert.Null(previous.Find(connection, transaction, context with { ProductionRunOutputId = "not-loaded" }));
            await using var otherConnection = await fixture.Database.OpenConnectionAsync();
            await using var otherTransaction = otherConnection.BeginTransaction(deferred: true);
            Assert.Throws<InvalidOperationException>(() => previous.Find(otherConnection, otherTransaction, context));
            await transaction.CommitAsync();
            Assert.Throws<InvalidOperationException>(() => previous.Find(connection, transaction, context));
        }
        await using var next = connection.BeginTransaction(deferred: true);
        Assert.Throws<InvalidOperationException>(() => previous.Find(connection, next, context));
    }

    private static async Task CompareAsync(Fixture fixture, bool expectPackage)
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction(deferred: true);
        var packages = await SqliteMachineWorkflowReporting.ReadVerifiedPackagesAsync(connection, transaction, default);
        Assert.Equal(expectPackage, packages.Contains(("operation-package", "machine-package")));
        var contexts = await SqliteProductionPackageContext.ListAsync(connection, transaction, null, default);
        foreach (var context in contexts)
        {
            var individual = await SqliteProductionReadinessContextReader.ReadForPackageAsync(connection, transaction, context, default, useProgramRevision: true);
            var shared = await SqliteProductionReadinessContextReader.ReadForPackageAsync(connection, transaction, context, default,
                useProgramRevision: true, verifiedPackages: packages);
            Assert.Equal(JsonSerializer.Serialize(individual), JsonSerializer.Serialize(shared));
            if (context.MachineId == "machine-package")
                Assert.Equal(!expectPackage, shared.ManualSetupReportingSupported);
        }
    }
}
