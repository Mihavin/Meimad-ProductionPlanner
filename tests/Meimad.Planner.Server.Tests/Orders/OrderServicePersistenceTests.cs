using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Orders;
using Meimad.Planner.Server.Domain.Orders;
using Meimad.Planner.Server.Persistence;

namespace Meimad.Planner.Server.Tests.Orders;

public sealed class OrderServicePersistenceTests
{
    [Fact]
    public async Task Create_update_read_and_reopen_preserve_order_as_case_demand()
    {
        await using var fixture = await Persistence.TemporaryDatabase.CreateAsync();
        await SeedCaseAsync(fixture.Database, "case-1");
        var editAuthority = await GrantEditModeAsync(fixture.Database);
        var service = CreateService(fixture.Database);

        var created = await service.CreateAsync(
            new CreateOrderCommand(
                "case-1",
                " WO-1042 ",
                50,
                "2026-08-20",
                "active",
                " First demand "),
            editAuthority);

        var updated = await service.UpdateAsync(
            created.OrderId,
            created.Version,
            new UpdateOrderCommand(
                OrderField<string?>.Unspecified,
                OrderField<int?>.Specified(60),
                OrderField<string?>.Unspecified,
                OrderField<string?>.Specified("complete"),
                OrderField<string?>.Specified("Completed demand")),
            editAuthority);

        var reopenedDatabase = new SqliteDatabase(
            new Configuration.DatabaseOptions(fixture.DatabasePath));
        var reopenedService = CreateService(reopenedDatabase);
        var reopened = await reopenedService.GetByIdAsync(created.OrderId);

        Assert.NotNull(reopened);
        Assert.Equal("case-1", reopened.CaseId);
        Assert.Equal("WO-1042", reopened.OrderNumber);
        Assert.Equal(60, reopened.Quantity);
        Assert.Equal(new DateOnly(2026, 8, 20), reopened.WorkFinishDate);
        Assert.Equal("complete", reopened.Status.ToContractToken());
        Assert.False(reopened.Status.IsActiveDemand());
        Assert.Equal("Completed demand", reopened.Notes);
        Assert.Equal(2, updated.Version);

        var listed = await reopenedService.ListByCaseAsync("case-1");
        Assert.Collection(listed, item => Assert.Equal(created.OrderId, item.OrderId));
    }

    [Fact]
    public async Task Create_rejects_missing_parent_case_without_orphaning_order()
    {
        await using var fixture = await Persistence.TemporaryDatabase.CreateAsync();
        var editAuthority = await GrantEditModeAsync(fixture.Database);
        var service = CreateService(fixture.Database);

        await Assert.ThrowsAsync<OrderCaseNotFoundException>(() => service.CreateAsync(
            new CreateOrderCommand(
                "missing-case",
                "WO-ORPHAN",
                1,
                "2026-08-20",
                "active",
                null),
            editAuthority));

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM orders;";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    private static OrderService CreateService(SqliteDatabase database) =>
        new(new SqliteOrderRepository(database), TimeProvider.System);

    private static async Task SeedCaseAsync(SqliteDatabase database, string caseId)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ($caseId, 'PN-ORDER', 'Order parent', 'C:\Cases\PN-ORDER');
            """;
        command.Parameters.AddWithValue("$caseId", caseId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<EditAuthority> GrantEditModeAsync(SqliteDatabase database)
    {
        var editAuthority = new EditAuthority("order-service-test-client", 1);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE edit_tokens
            SET holder_client_id = $clientId,
                holder_user_id = 'order-service-test-user',
                generation = $generation,
                acquired_at = '2026-08-11T00:00:00Z',
                version = version + 1,
                updated_at = '2026-08-11T00:00:00Z'
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$clientId", editAuthority.ClientId);
        command.Parameters.AddWithValue("$generation", editAuthority.Generation);
        await command.ExecuteNonQueryAsync();
        return editAuthority;
    }
}
