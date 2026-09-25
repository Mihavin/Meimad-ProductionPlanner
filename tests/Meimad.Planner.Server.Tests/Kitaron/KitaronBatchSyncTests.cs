using System.Net.Http.Json;
using Meimad.Planner.Server.Application.Kitaron;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Kitaron;

/// <summary>
/// Production Batches mirror the open Kitaron work orders: an open work order becomes a waiting
/// batch with its order allocations, cutting-reserve scrap allowance, a snapshot of the Case route
/// and Kitaron's material verdict. Started or planner-planned batches are never rewritten or
/// removed by the synchronization.
/// </summary>
public sealed class KitaronBatchSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Open_work_orders_become_batches_with_allocations_and_material_checks()
    {
        await RunAsync(async application =>
        {
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await SeedAuthorityAsync(database);
            var repository = application.Services.GetRequiredService<IKitaronSyncRepository>();
            var batch = new KitaronSyncBatch(
                "wo:4001", "PN-ROUTE", "4001", 6,
                [new KitaronSyncBatchAllocation("7001", 5), new KitaronSyncBatchAllocation(null, 1, ScrapAllowance: true)],
                "on_order", "MAT-1: need 6, stock 2, on purchase order due 2026-10-06", "batch-hash-1");
            var first = await repository.ApplyAsync(Plan([Operation()], [batch]), Now, CancellationToken.None);

            Assert.Contains("1 Production Batch(es) imported from open Kitaron work orders", first.Message);
            await using (var connection = await database.OpenConnectionAsync())
            {
                Assert.Equal("4001|waiting|6", await ScalarAsync(connection,
                    "SELECT batch_number || '|' || status || '|' || planned_quantity FROM production_batches;"));
                Assert.Equal("order:5|scrap_allowance:1", await ScalarAsync(connection, """
                    SELECT group_concat(allocation_type || ':' || quantity, '|')
                    FROM (SELECT allocation_type, quantity FROM batch_allocations ORDER BY allocation_type);
                    """));
                // The order allocation points at the linked Kitaron Order of the Case.
                Assert.Equal(1L, await ScalarAsync(connection, """
                    SELECT COUNT(*) FROM batch_allocations allocation
                    JOIN kitaron_sync_links link ON link.target_id = allocation.order_id
                    WHERE link.source_entity = 'order' AND allocation.allocation_type = 'order';
                    """));
                Assert.Equal(1L, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM batch_operations WHERE operation_number = 30 AND status = 'not_started';"));
                Assert.Equal("on_order|MAT-1: need 6, stock 2, on purchase order due 2026-10-06",
                    await ScalarAsync(connection, "SELECT state || '|' || detail FROM kitaron_batch_material_checks;"));
            }

            // The unchanged work order is left alone; a changed one is re-applied while unplanned.
            var second = await repository.ApplyAsync(Plan([Operation()], [batch]), Now.AddMinutes(1), CancellationToken.None);
            Assert.Contains("0 Production Batch(es) imported", second.Message);
            var grown = batch with
            {
                PlannedQuantity = 8,
                Allocations = [new KitaronSyncBatchAllocation("7001", 5), new KitaronSyncBatchAllocation(null, 3, ScrapAllowance: true)],
                MaterialState = "available",
                MaterialDetail = "Kitaron stock covers 1 material line(s).",
                SourceHash = "batch-hash-2"
            };
            var third = await repository.ApplyAsync(Plan([Operation()], [grown]), Now.AddMinutes(2), CancellationToken.None);
            Assert.Contains("1 updated", third.Message);
            await using (var connection = await database.OpenConnectionAsync())
            {
                Assert.Equal(8L, await ScalarAsync(connection, "SELECT planned_quantity FROM production_batches;"));
                Assert.Equal(3L, await ScalarAsync(connection,
                    "SELECT quantity FROM batch_allocations WHERE allocation_type = 'scrap_allowance';"));
                Assert.Equal("available", await ScalarAsync(connection, "SELECT state FROM kitaron_batch_material_checks;"));
            }

            // Once the planner anchors the batch on a Machine, Kitaron changes are reported, not applied.
            await ExecuteAsync(database, """
                INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
                VALUES ('machine-1', '10', 'Machine 10', 'Mill 3x', 'calendar-r', 'active', 1);
                INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position)
                SELECT 'assignment-1', id, 'machine-1', 0 FROM batch_operations;
                """);
            var shrunk = grown with { PlannedQuantity = 4, Allocations = [new KitaronSyncBatchAllocation("7001", 4)], SourceHash = "batch-hash-3" };
            var fourth = await repository.ApplyAsync(Plan([Operation()], [shrunk]), Now.AddMinutes(3), CancellationToken.None);
            Assert.Contains("1 kept although the Kitaron work order closed or changed (PN-ROUTE batch 4001)", fourth.Message);
            await using (var verify = await database.OpenConnectionAsync())
            {
                Assert.Equal(8L, await ScalarAsync(verify, "SELECT planned_quantity FROM production_batches;"));
            }

            // The work order closes without started production: the batch and its backlog anchor go.
            var fifth = await repository.ApplyAsync(Plan([Operation()], []), Now.AddMinutes(4), CancellationToken.None);
            Assert.Contains("1 removed with their Kitaron work orders", fifth.Message);
            await using (var verify = await database.OpenConnectionAsync())
            {
                Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM production_batches;"));
                Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM machine_assignments;"));
                Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM kitaron_batch_material_checks;"));
                Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM kitaron_sync_links WHERE source_entity = 'production_batch';"));
            }
        });
    }

    [Fact]
    public async Task A_started_batch_is_never_rewritten_or_removed()
    {
        await RunAsync(async application =>
        {
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await SeedAuthorityAsync(database);
            var repository = application.Services.GetRequiredService<IKitaronSyncRepository>();
            var batch = new KitaronSyncBatch(
                "wo:4002", "PN-ROUTE", "4002", 5, [new KitaronSyncBatchAllocation("7001", 5)],
                "unknown", null, "hash-a");
            await repository.ApplyAsync(Plan([Operation()], [batch]), Now, CancellationToken.None);
            await ExecuteAsync(database,
                "UPDATE batch_operations SET status = 'in_progress'; UPDATE production_batches SET status = 'in_production';");

            // The closed work order leaves the started batch as production history.
            var closed = await repository.ApplyAsync(Plan([Operation()], []), Now.AddMinutes(1), CancellationToken.None);
            Assert.Contains("0 removed with their Kitaron work orders; 1 kept although the Kitaron work order closed or changed (PN-ROUTE batch 4002)", closed.Message);
            await using (var connection = await database.OpenConnectionAsync())
            {
                Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM production_batches;"));
                Assert.Equal("closed-in-kitaron", await ScalarAsync(connection,
                    "SELECT source_hash FROM kitaron_sync_links WHERE source_entity = 'production_batch';"));
            }

            // Reopened with other facts, it is still only reported: started structure is immutable.
            var reopened = await repository.ApplyAsync(
                Plan([Operation()], [batch with { PlannedQuantity = 9, SourceHash = "hash-b" }]),
                Now.AddMinutes(2), CancellationToken.None);
            Assert.Contains("1 kept although the Kitaron work order closed or changed", reopened.Message);
            await using (var verify = await database.OpenConnectionAsync())
            {
                Assert.Equal(5L, await ScalarAsync(verify, "SELECT planned_quantity FROM production_batches;"));
            }
        });
    }

    [Fact]
    public async Task A_work_order_waits_until_the_case_has_operations()
    {
        await RunAsync(async application =>
        {
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await SeedAuthorityAsync(database);
            var repository = application.Services.GetRequiredService<IKitaronSyncRepository>();
            var batch = new KitaronSyncBatch(
                "wo:4003", "PN-ROUTE", "4003", 5, [new KitaronSyncBatchAllocation("7001", 5)],
                "unknown", null, "hash-wait");

            var first = await repository.ApplyAsync(Plan([], [batch]), Now, CancellationToken.None);
            Assert.Contains("1 work order(s) wait for their Case route (PN-ROUTE work order 4003: no Case Operations yet)", first.Message);
            await using (var connection = await database.OpenConnectionAsync())
            {
                Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM production_batches;"));
            }

            // The route arrives (stations decided): the batch is created on the next pass.
            var second = await repository.ApplyAsync(Plan([Operation()], [batch]), Now.AddMinutes(1), CancellationToken.None);
            Assert.Contains("1 Production Batch(es) imported", second.Message);
            await using (var verify = await database.OpenConnectionAsync())
            {
                Assert.Equal(1L, await ScalarAsync(verify, "SELECT COUNT(*) FROM batch_operations WHERE operation_number = 30;"));
            }
        });
    }

    [Fact]
    public async Task The_route_master_locks_and_unlocks_the_case()
    {
        await RunAsync(async application =>
        {
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await SeedAuthorityAsync(database);
            var repository = application.Services.GetRequiredService<IKitaronSyncRepository>();

            await repository.ApplyAsync(Plan([Operation()], null), Now, CancellationToken.None);
            await using (var connection = await database.OpenConnectionAsync())
            {
                Assert.Equal(1L, await ScalarAsync(connection, "SELECT kitaron_route_locked FROM cases;"));
            }

            // The part loses its route master (view-only again): the Case unlocks.
            var viewOnly = Plan([Operation()], null) with
            {
                RoutePartNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            };
            await repository.ApplyAsync(viewOnly, Now.AddMinutes(1), CancellationToken.None);
            await using (var verify = await database.OpenConnectionAsync())
            {
                Assert.Equal(0L, await ScalarAsync(verify, "SELECT kitaron_route_locked FROM cases;"));
            }
        });
    }

    private static KitaronSyncOperation Operation() => new(
        KitaronRoutePlanner.OperationKey("PN-ROUTE", 30), "PN-ROUTE", 30, 0, "Mill", "Mill 3x", 600, 120, "op-hash");

    private static KitaronSyncPlan Plan(
        IReadOnlyList<KitaronSyncOperation> operations,
        IReadOnlyList<KitaronSyncBatch>? batches) => new(
        1,
        [new KitaronSyncCase("PN-ROUTE", "PN-ROUTE", "Routed part", null, null, @"C:\Kitaron\PN-ROUTE", "case-hash")],
        [new KitaronSyncOrder("7001", "PN-ROUTE", "SO-77/7001", 5, new DateOnly(2026, 12, 1), "active", "order-hash")],
        operations, [], new HashSet<string>(), [], 1, null, null, null, 0,
        new HashSet<string>(["PN-ROUTE"], StringComparer.OrdinalIgnoreCase),
        batches);

    private static async Task ExecuteAsync(SqliteDatabase database, string sql)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task SeedAuthorityAsync(SqliteDatabase database)
    {
        await ExecuteAsync(database, """
            INSERT INTO working_calendars (id, name, time_zone_id, calendar_json) VALUES ('calendar-r', 'Resources', 'UTC', '{}');
            """);
    }

    private static async Task RunAsync(Func<WebApplication, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.KitaronBatch.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directory, "batch-test.db")}"],
            webHost => webHost.UseTestServer());
        try
        {
            await application.StartAsync();
            await test(application);
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            for (var attempt = 0; Directory.Exists(directory); attempt++)
            {
                try { Directory.Delete(directory, recursive: true); }
                catch when (attempt < 5) { await Task.Delay(100); }
            }
        }
    }
}
