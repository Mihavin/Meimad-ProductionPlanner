using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.ProductionBatches;
using Meimad.Planner.Server.Domain.ProductionBatches;
using Meimad.Planner.Server.Persistence;

namespace Meimad.Planner.Server.Tests.ProductionBatches;

public sealed class ProductionBatchPersistenceTests
{
    [Fact]
    public async Task Create_persists_balanced_allocations_and_snapshots_case_operations()
    {
        await using var fixture = await Persistence.TemporaryDatabase.CreateAsync();
        await SeedPlanningDataAsync(fixture.Database);
        var authority = await GrantEditModeAsync(fixture.Database);
        var service = CreateService(fixture.Database);

        var created = await service.CreateAsync(
            Command(
                "case-1",
                "B-MIXED",
                18,
                Allocation("order", "order-1", 10),
                Allocation("order", "order-2", 4),
                Allocation("stock", null, 2),
                Allocation("scrapAllowance", null, 2)),
            authority);

        Assert.Equal(4, created.Allocations.Count);
        Assert.Equal(18, created.Allocations.Sum(allocation => allocation.Quantity));
        Assert.Collection(
            created.Operations,
            operation =>
            {
                Assert.Equal("case-op-10", operation.SourceCaseOperationId);
                Assert.Equal(10, operation.OperationNumber);
                Assert.Equal("Saw", operation.Name);
                Assert.Equal("not_started", operation.Status);
            },
            operation =>
            {
                Assert.Equal("case-op-20", operation.SourceCaseOperationId);
                Assert.Equal(20, operation.OperationNumber);
                Assert.Equal("Mill", operation.Name);
                Assert.Equal(300, operation.CycleTimePerPartSeconds);
            });

        var reopenedDatabase = new SqliteDatabase(
            new Configuration.DatabaseOptions(fixture.DatabasePath));
        var reopened = await CreateService(reopenedDatabase).GetByIdAsync(created.BatchId);
        Assert.NotNull(reopened);
        Assert.Equal(4, reopened.Allocations.Count);
        Assert.Equal(2, reopened.Operations.Count);
    }

    [Fact]
    public async Task Batch_operation_is_a_snapshot_after_case_operation_changes()
    {
        await using var fixture = await Persistence.TemporaryDatabase.CreateAsync();
        await SeedPlanningDataAsync(fixture.Database);
        var authority = await GrantEditModeAsync(fixture.Database);
        var service = CreateService(fixture.Database);
        var created = await service.CreateAsync(
            Command("case-1", "B-SNAPSHOT", 5, Allocation("stock", null, 5)),
            authority);

        await using (var connection = await fixture.Database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE case_operations
                SET name = 'Changed route name',
                    cycle_seconds = 999,
                    dependency_type = 'independent',
                    predecessor_case_operation_id = NULL
                WHERE id = 'case-op-20';
                """;
            await command.ExecuteNonQueryAsync();
        }

        var reopened = await service.GetByIdAsync(created.BatchId);
        Assert.NotNull(reopened);
        var snapshotted = Assert.Single(
            reopened.Operations,
            operation => operation.SourceCaseOperationId == "case-op-20");
        Assert.Equal("Mill", snapshotted.Name);
        Assert.Equal(300, snapshotted.CycleTimePerPartSeconds);

        await using var snapshotConnection = await fixture.Database.OpenConnectionAsync();
        await using var snapshotCommand = snapshotConnection.CreateCommand();
        snapshotCommand.CommandText = """
            SELECT dependency_type, predecessor_source_case_operation_id
            FROM batch_operations
            WHERE production_batch_id = $batchId
              AND source_case_operation_id = 'case-op-20';
            """;
        snapshotCommand.Parameters.AddWithValue("$batchId", created.BatchId);
        await using var snapshotReader = await snapshotCommand.ExecuteReaderAsync();
        Assert.True(await snapshotReader.ReadAsync());
        Assert.Equal("sequential", snapshotReader.GetString(0));
        Assert.Equal("case-op-10", snapshotReader.GetString(1));
    }

    [Theory]
    [InlineData("foreign-order", "cross_case_order")]
    [InlineData("missing-order", "invalid_reference")]
    public async Task Invalid_order_reference_rolls_back_every_batch_record(
        string orderId,
        string expectedCode)
    {
        await using var fixture = await Persistence.TemporaryDatabase.CreateAsync();
        await SeedPlanningDataAsync(fixture.Database);
        var authority = await GrantEditModeAsync(fixture.Database);
        var service = CreateService(fixture.Database);

        var exception = await Assert.ThrowsAsync<ProductionBatchValidationException>(() =>
            service.CreateAsync(
                Command("case-1", "B-INVALID", 5, Allocation("order", orderId, 5)),
                authority));
        Assert.Contains(exception.Issues, issue => issue.Code == expectedCode);

        await AssertTableCountAsync(fixture.Database, "production_batches", 0);
        await AssertTableCountAsync(fixture.Database, "batch_allocations", 0);
        await AssertTableCountAsync(fixture.Database, "batch_operations", 0);
    }

    [Fact]
    public async Task Waiting_stock_only_batch_makes_case_active_without_creating_order_demand()
    {
        await using var fixture = await Persistence.TemporaryDatabase.CreateAsync();
        await SeedPlanningDataAsync(fixture.Database);
        var authority = await GrantEditModeAsync(fixture.Database);
        await CreateService(fixture.Database).CreateAsync(
            Command("case-2", "B-STOCK", 8, Allocation("stock", null, 8)),
            authority);

        var caseService = new CaseService(
            new SqliteCaseRepository(fixture.Database),
            TimeProvider.System);
        var plannerCase = await caseService.GetByIdAsync("case-2");
        Assert.NotNull(plannerCase);
        Assert.True(plannerCase.IsActive);

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM orders WHERE case_id = 'case-2';";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Missing_batch_case_and_duplicate_number_are_rejected_atomically()
    {
        await using var fixture = await Persistence.TemporaryDatabase.CreateAsync();
        await SeedPlanningDataAsync(fixture.Database);
        var authority = await GrantEditModeAsync(fixture.Database);
        var service = CreateService(fixture.Database);

        await Assert.ThrowsAsync<ProductionBatchCaseNotFoundException>(() =>
            service.CreateAsync(
                Command("missing-case", "B-MISSING", 1, Allocation("stock", null, 1)),
                authority));

        await service.CreateAsync(
            Command("case-1", "B-UNIQUE", 1, Allocation("stock", null, 1)),
            authority);
        await Assert.ThrowsAsync<ProductionBatchNumberConflictException>(() =>
            service.CreateAsync(
                Command("case-1", "B-UNIQUE", 1, Allocation("stock", null, 1)),
                authority));

        await AssertTableCountAsync(fixture.Database, "production_batches", 1);
        await AssertTableCountAsync(fixture.Database, "batch_allocations", 1);
        await AssertTableCountAsync(fixture.Database, "batch_operations", 2);
    }

    private static ProductionBatchService CreateService(SqliteDatabase database) =>
        new(new SqliteProductionBatchRepository(database), TimeProvider.System);

    private static CreateProductionBatchCommand Command(
        string caseId,
        string batchNumber,
        int plannedQuantity,
        params CreateBatchAllocationCommand[] allocations) => new(
        caseId,
        batchNumber,
        "waiting",
        plannedQuantity,
        allocations);

    private static CreateBatchAllocationCommand Allocation(
        string type,
        string? orderId,
        int quantity) => new(type, orderId, quantity);

    private static async Task SeedPlanningDataAsync(SqliteDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES
                ('case-1', 'PN-1', 'Case One', 'C:\Cases\PN-1'),
                ('case-2', 'PN-2', 'Case Two', 'C:\Cases\PN-2');

            INSERT INTO orders (
                id, case_id, order_reference, quantity, work_finish_date, status)
            VALUES
                ('order-1', 'case-1', 'WO-1', 20, '2026-08-20', 'active'),
                ('order-2', 'case-1', 'WO-2', 10, '2026-08-21', 'active'),
                ('foreign-order', 'case-2', 'WO-FOREIGN', 10, '2026-08-22', 'complete');

            INSERT INTO case_operations (
                id, case_id, operation_number, route_position, name,
                required_machine_type, setup_seconds, cycle_seconds,
                dependency_type, predecessor_case_operation_id)
            VALUES
                ('case-op-10', 'case-1', 10, 0, 'Saw', 'saw', 120, 30,
                 'independent', NULL),
                ('case-op-20', 'case-1', 20, 1, 'Mill', 'mill', 600, 300,
                 'sequential', 'case-op-10');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<EditAuthority> GrantEditModeAsync(SqliteDatabase database)
    {
        var authority = new EditAuthority("batch-persistence-client", 1);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE edit_tokens
            SET holder_client_id = $clientId,
                holder_user_id = 'batch-persistence-user',
                generation = $generation,
                acquired_at = '2026-08-11T00:00:00Z',
                version = version + 1,
                updated_at = '2026-08-11T00:00:00Z'
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$clientId", authority.ClientId);
        command.Parameters.AddWithValue("$generation", authority.Generation);
        await command.ExecuteNonQueryAsync();
        return authority;
    }

    private static async Task AssertTableCountAsync(
        SqliteDatabase database,
        string table,
        long expected)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        Assert.Equal(expected, (long)(await command.ExecuteScalarAsync())!);
    }
}
