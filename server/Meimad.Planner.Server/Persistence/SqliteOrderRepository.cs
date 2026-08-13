using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Orders;
using Meimad.Planner.Server.Domain.Orders;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteOrderRepository : IOrderRepository
{
    private const string Projection = """
        id,
        case_id,
        order_reference,
        quantity,
        work_finish_date,
        status,
        notes,
        version,
        created_at,
        updated_at
        """;

    private readonly SqliteDatabase database;

    public SqliteOrderRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<PlannerOrder> CreateAsync(
        PlannerOrder order,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        if (!await CaseExistsAsync(connection, transaction, order.CaseId, cancellationToken))
        {
            throw new OrderCaseNotFoundException(order.CaseId);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO orders (
                id,
                case_id,
                order_reference,
                customer_reference,
                quantity,
                work_finish_date,
                status,
                notes,
                version,
                created_at,
                updated_at)
            VALUES (
                $id,
                $caseId,
                $orderNumber,
                NULL,
                $quantity,
                $workFinishDate,
                $status,
                $notes,
                $version,
                $createdAt,
                $updatedAt);
            """;
        AddWriteParameters(command, order);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return order;
    }

    public async Task<PlannerOrder?> GetByIdAsync(
        string orderId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Projection} FROM orders WHERE id = $id;";
        command.Parameters.AddWithValue("$id", orderId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadOrder(reader) : null;
    }

    public async Task<IReadOnlyList<PlannerOrder>> ListByCaseAsync(
        string caseId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Projection}
            FROM orders
            WHERE case_id = $caseId
            ORDER BY work_finish_date, order_reference, id;
            """;
        command.Parameters.AddWithValue("$caseId", caseId);

        var orders = new List<PlannerOrder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            orders.Add(ReadOrder(reader));
        }

        return orders;
    }

    public async Task<PlannerOrder?> UpdateAsync(
        PlannerOrder order,
        int expectedVersion,
        bool statusWasExplicitlySet,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);

        var productionFacts = await SqliteOrderLifecycle.ReadFactsAsync(
            connection, transaction, order.OrderId, order.Quantity, cancellationToken);
        if (productionFacts.AllocatedQuantity > order.Quantity)
        {
            throw new OrderQuantityBelowAllocatedException(
                order.OrderId,
                productionFacts.AllocatedQuantity);
        }

        if (productionFacts.HasAllocatedBatch)
        {
            var derivedStatus = OrderLifecycle.Derive(productionFacts);
            if (!statusWasExplicitlySet && order.Status == OrderStatus.Cancelled)
            {
                // Cancellation is an explicit legacy/manual state. Automatic derivation must
                // not erase it; an explicit matching production status resumes the lifecycle.
            }
            else if (statusWasExplicitlySet && order.Status != derivedStatus)
            {
                throw new OrderDerivedStatusException(order.OrderId, derivedStatus);
            }
            else
            {
                order = order with { Status = derivedStatus };
            }
        }
        else if (statusWasExplicitlySet
                 && order.Status is OrderStatus.InProduction or OrderStatus.Complete)
        {
            throw new OrderManualProductionStatusException(
                "An unallocated Order status may be active or cancelled; production status is derived by the Server.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE orders
            SET order_reference = $orderNumber,
                quantity = $quantity,
                work_finish_date = $workFinishDate,
                status = $status,
                notes = $notes,
                version = $version,
                updated_at = $updatedAt
            WHERE id = $id AND version = $expectedVersion
            RETURNING {Projection};
            """;
        AddWriteParameters(command, order);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);

        PlannerOrder? updated;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            updated = await reader.ReadAsync(cancellationToken) ? ReadOrder(reader) : null;
        }

        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private static async Task<bool> CaseExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string caseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM cases WHERE id = $caseId);";
        command.Parameters.AddWithValue("$caseId", caseId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task EnsureEditAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(
            connection,
            transaction,
            DateTimeOffset.UtcNow,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT holder_client_id, generation
            FROM edit_tokens
            WHERE id = 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            throw new EditModeMutationException(
                "edit_mode_required",
                "No Windows client currently holds Edit Mode.");
        }

        if (!string.Equals(reader.GetString(0), editAuthority.ClientId, StringComparison.Ordinal)
            || reader.GetInt64(1) != editAuthority.Generation)
        {
            throw new EditModeMutationException(
                "edit_generation_stale",
                "This client does not hold the active Edit Mode generation.");
        }
    }

    private static void AddWriteParameters(SqliteCommand command, PlannerOrder order)
    {
        command.Parameters.AddWithValue("$id", order.OrderId);
        command.Parameters.AddWithValue("$caseId", order.CaseId);
        command.Parameters.AddWithValue("$orderNumber", order.OrderNumber);
        command.Parameters.AddWithValue("$quantity", order.Quantity);
        command.Parameters.AddWithValue(
            "$workFinishDate",
            order.WorkFinishDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$status", order.Status.ToContractToken());
        command.Parameters.AddWithValue("$notes", order.Notes is null ? DBNull.Value : order.Notes);
        command.Parameters.AddWithValue("$version", order.Version);
        command.Parameters.AddWithValue("$createdAt", FormatInstant(order.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatInstant(order.UpdatedAt));
    }

    private static PlannerOrder ReadOrder(SqliteDataReader reader)
    {
        var statusToken = reader.GetString(5);
        if (!OrderStatuses.TryParseContractToken(statusToken, out var status))
        {
            throw new InvalidDataException($"Stored Order status '{statusToken}' is invalid.");
        }

        return new PlannerOrder(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            DateOnly.ParseExact(reader.GetString(4), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            status,
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetInt32(7),
            ParseInstant(reader.GetString(8)),
            ParseInstant(reader.GetString(9)));
    }

    private static string FormatInstant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseInstant(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
