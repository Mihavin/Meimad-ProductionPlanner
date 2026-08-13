using System.Globalization;
using Meimad.Planner.Server.Domain.Orders;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal static class SqliteOrderLifecycle
{
    internal static async Task<IReadOnlyList<OrderLifecycleCandidate>> ReadCandidatesForBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT orders.id, orders.quantity, orders.status
            FROM batch_allocations
            JOIN orders ON orders.id = batch_allocations.order_id
            WHERE batch_allocations.production_batch_id = $batchId
              AND batch_allocations.allocation_type = 'order'
            ORDER BY orders.id;
            """;
        command.Parameters.AddWithValue("$batchId", batchId);

        var candidates = new List<OrderLifecycleCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var statusToken = reader.GetString(2);
            if (!OrderStatuses.TryParseContractToken(statusToken, out var status))
            {
                throw new InvalidDataException($"Stored Order status '{statusToken}' is invalid.");
            }

            candidates.Add(new OrderLifecycleCandidate(
                reader.GetString(0),
                reader.GetInt32(1),
                status));
        }

        return candidates;
    }

    internal static async Task<OrderProductionFacts> ReadFactsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string orderId,
        int orderQuantity,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT COALESCE(SUM(quantity), 0)
                 FROM batch_allocations
                 WHERE order_id = $orderId),
                EXISTS(
                    SELECT 1 FROM batch_allocations
                    WHERE order_id = $orderId),
                EXISTS(
                    SELECT 1
                    FROM batch_allocations
                    JOIN batch_operations
                      ON batch_operations.production_batch_id = batch_allocations.production_batch_id
                    WHERE batch_allocations.order_id = $orderId
                      AND batch_operations.status <> 'not_started'),
                NOT EXISTS(
                    SELECT 1
                    FROM batch_allocations allocation
                    WHERE allocation.order_id = $orderId
                      AND NOT EXISTS(
                          SELECT 1
                          FROM batch_operations operation
                          WHERE operation.production_batch_id = allocation.production_batch_id)),
                NOT EXISTS(
                    SELECT 1
                    FROM batch_allocations allocation
                    JOIN batch_operations operation
                      ON operation.production_batch_id = allocation.production_batch_id
                    WHERE allocation.order_id = $orderId
                      AND operation.status <> 'completed');
            """;
        command.Parameters.AddWithValue("$orderId", orderId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new OrderProductionFacts(
            orderQuantity,
            reader.GetInt64(0),
            reader.GetBoolean(1),
            reader.GetBoolean(2),
            reader.GetBoolean(3),
            reader.GetBoolean(4));
    }

    internal static async Task RecomputeForBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidates = await ReadCandidatesForBatchAsync(
            connection,
            transaction,
            batchId,
            cancellationToken);
        await RecomputeAsync(connection, transaction, candidates, now, cancellationToken);
    }

    internal static async Task RecomputeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<OrderLifecycleCandidate> candidates,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates.DistinctBy(value => value.OrderId))
        {
            var facts = await ReadFactsAsync(
                connection,
                transaction,
                candidate.OrderId,
                candidate.Quantity,
                cancellationToken);
            var status = candidate.Status == OrderStatus.Cancelled
                ? OrderStatuses.CancelledToken
                : OrderLifecycle.Derive(facts).ToContractToken();
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE orders
                SET status = $status,
                    version = version + 1,
                    updated_at = $updatedAt
                WHERE id = $id
                  AND status <> $status;
            """;
            update.Parameters.AddWithValue("$status", status);
            update.Parameters.AddWithValue("$updatedAt", FormatInstant(now));
            update.Parameters.AddWithValue("$id", candidate.OrderId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string FormatInstant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

internal sealed record OrderLifecycleCandidate(
    string OrderId,
    int Quantity,
    OrderStatus Status);
