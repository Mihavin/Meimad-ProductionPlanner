using Meimad.Planner.Server.Application.Kitaron;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Kitaron;

public sealed class KitaronOrphanOrderCleanupTests
{
    [Fact]
    public async Task Partial_snapshot_removes_only_unlinked_orphans_from_durable_managed_cases()
    {
        await RunAsync(async application =>
        {
            var repository = application.Services.GetRequiredService<IKitaronSyncRepository>();
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            var caseA = new KitaronSyncCase(
                "case-a-source", "PART-A", "Part A", null, null, "a", "case-a-hash");
            var caseB = new KitaronSyncCase(
                "case-b-source", "PART-B", "Part B", null, null, "b", "case-b-hash");
            var orderA = new KitaronSyncOrder(
                "a-row", caseA.SourceKey, "A-100/1", 4,
                new DateOnly(2027, 1, 1), "active", "order-a-hash");
            var orderB = new KitaronSyncOrder(
                "b-row", caseB.SourceKey, "B-200/1", 6,
                new DateOnly(2027, 2, 1), "active", "order-b-hash");

            await repository.ApplyAsync(
                new KitaronSyncPlan(
                    2, [caseA, caseB], [orderA, orderB], [], [],
                    new HashSet<string>(), [], 1),
                new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero),
                CancellationToken.None);

            await using (var connection = await database.OpenConnectionAsync())
            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO orders (
                        id,case_id,order_reference,quantity,work_finish_date,status)
                    SELECT 'unlinked-orphan',id,'MANUAL-ORPHAN',2,'2027-03-01','active'
                    FROM cases WHERE part_number='PART-B';
                    INSERT INTO production_batches (
                        id,case_id,batch_number,status,planned_quantity)
                    SELECT 'orphan-batch',id,'ORPHAN-BATCH','waiting',2
                    FROM cases WHERE part_number='PART-B';
                    INSERT INTO batch_allocations (
                        id,production_batch_id,allocation_type,order_id,quantity)
                    VALUES ('orphan-allocation','orphan-batch','order','unlinked-orphan',2);

                    INSERT INTO orders (
                        id,case_id,order_reference,quantity,work_finish_date,status)
                    SELECT 'locked-history-order',id,'LOCKED-HISTORY',3,'2027-04-01','complete'
                    FROM cases WHERE part_number='PART-B';
                    INSERT INTO case_operations (
                        id,case_id,operation_number,route_position,name)
                    SELECT 'locked-case-operation',id,10,0,'Locked history operation'
                    FROM cases WHERE part_number='PART-B';
                    INSERT INTO production_batches (
                        id,case_id,batch_number,status,planned_quantity)
                    SELECT 'locked-batch',id,'LOCKED-BATCH','complete',3
                    FROM cases WHERE part_number='PART-B';
                    INSERT INTO batch_allocations (
                        id,production_batch_id,allocation_type,order_id,quantity)
                    VALUES ('locked-allocation','locked-batch','order','locked-history-order',3);
                    INSERT INTO batch_operations (
                        id,production_batch_id,source_case_operation_id,
                        operation_number,route_position,name,status)
                    VALUES (
                        'locked-batch-operation','locked-batch','locked-case-operation',
                        10,0,'Locked history operation','complete');
                    INSERT INTO production_runs (
                        id,status,shared_setup_seconds,setup_snapshot_json,
                        structure_locked_at,legacy_batch_operation_id,
                        version,created_at,updated_at)
                    VALUES (
                        'locked-run','COMPLETED',0,'{}','2026-09-02T10:01:00Z',
                        'locked-batch-operation',1,
                        '2026-09-02T10:00:00Z','2026-09-02T10:01:00Z');
                    """;
                await seed.ExecuteNonQueryAsync();
            }

            // This is deliberately a partial source snapshot: Case B is omitted.
            // Its linked canonical Order must survive, while its unlinked Planner
            // orphan is still safe to remove because the Case has a durable link.
            var result = await repository.ApplyAsync(
                new KitaronSyncPlan(
                    1, [caseA], [orderA], [], [],
                    new HashSet<string>(), [], 1),
                new DateTimeOffset(2026, 9, 2, 10, 2, 0, TimeSpan.Zero),
                CancellationToken.None);

            Assert.Contains("1 dependent Production Batch(es)", result.Message, StringComparison.Ordinal);
            Assert.Contains("1 non-Kitaron Order(s) removed", result.Message, StringComparison.Ordinal);
            Assert.Contains("1 superseded Order(s) retained", result.Message, StringComparison.Ordinal);

            await using var verifyConnection = await database.OpenConnectionAsync();
            await using var verify = verifyConnection.CreateCommand();
            verify.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM orders WHERE order_reference='B-200/1'),
                    (SELECT COUNT(*)
                     FROM kitaron_sync_links link
                     JOIN orders target ON target.id=link.target_id
                     WHERE link.source_entity='order'
                       AND target.order_reference='B-200/1'),
                    (SELECT COUNT(*) FROM orders WHERE id='unlinked-orphan'),
                    (SELECT COUNT(*) FROM production_batches WHERE id='orphan-batch'),
                    (SELECT COUNT(*) FROM batch_allocations WHERE id='orphan-allocation'),
                    (SELECT COUNT(*) FROM orders WHERE id='locked-history-order'),
                    (SELECT kitaron_history_only FROM orders WHERE id='locked-history-order'),
                    (SELECT COUNT(*) FROM production_batches WHERE id='locked-batch'),
                    (SELECT COUNT(*) FROM production_runs WHERE id='locked-run');
                """;
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(1, reader.GetInt32(1));
            Assert.Equal(0, reader.GetInt32(2));
            Assert.Equal(0, reader.GetInt32(3));
            Assert.Equal(0, reader.GetInt32(4));
            Assert.Equal(1, reader.GetInt32(5));
            Assert.Equal(1, reader.GetInt32(6));
            Assert.Equal(1, reader.GetInt32(7));
            Assert.Equal(1, reader.GetInt32(8));
        });
    }

    private static async Task RunAsync(Func<WebApplication, Task> test)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "MeimadPlanner.Kitaron.Orphan.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            [
                "--Server:Host=127.0.0.1",
                "--Server:Port=5099",
                $"--Database:Path={Path.Combine(directory, "kitaron-orphan-test.db")}"
            ],
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
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(100 * (attempt + 1));
                    SqliteConnection.ClearAllPools();
                }
            }
        }
    }
}
