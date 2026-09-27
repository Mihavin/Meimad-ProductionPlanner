using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Api.ProductionBatches;

/// <summary>
/// Material orders of a Work Order. Kitaron records no purchase-to-work-order link, so the open
/// purchase lines of the Work Order's raw material are only candidates; a planner verifies the
/// right ones by hand (Single Edit Mode) and only verified links count.
/// </summary>
internal static class WorkOrderMaterialOrderEndpoints
{
    internal static void MapWorkOrderMaterialOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/batches/{batchId}/material-orders", ListAsync);
        endpoints.MapPut("/api/v1/batches/{batchId}/material-orders/{sourceKey}", VerifyAsync);
        endpoints.MapDelete("/api/v1/batches/{batchId}/material-orders/{sourceKey}", UnverifyAsync);
    }

    private static async Task<IResult> ListAsync(string batchId, SqliteDatabase database, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        if (!await BatchExistsAsync(connection, null, batchId, cancellationToken))
            return Results.NotFound();
        return Results.Ok(new WorkOrderMaterialOrderListResponse(
            await ReadAsync(connection, batchId, cancellationToken)));
    }

    private static Task<IResult> VerifyAsync(
        string batchId, string sourceKey, HttpContext context, SqliteDatabase database,
        TimeProvider timeProvider, CancellationToken cancellationToken) =>
        MutateAsync(batchId, sourceKey, verify: true, context, database, timeProvider, cancellationToken);

    private static Task<IResult> UnverifyAsync(
        string batchId, string sourceKey, HttpContext context, SqliteDatabase database,
        TimeProvider timeProvider, CancellationToken cancellationToken) =>
        MutateAsync(batchId, sourceKey, verify: false, context, database, timeProvider, cancellationToken);

    private static async Task<IResult> MutateAsync(
        string batchId, string sourceKey, bool verify, HttpContext context, SqliteDatabase database,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        string actor;
        try
        {
            actor = await EnsureEditAuthorityAsync(connection, transaction, authority!, cancellationToken);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(409, exception.Code, exception.Message, context);
        }
        if (!await BatchExistsAsync(connection, transaction, batchId, cancellationToken))
            return PlanningHttpSupport.Error(404, "resource_not_found", "The requested Work Order was not found.", context);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            if (verify)
            {
                command.CommandText = """
                    SELECT EXISTS(SELECT 1 FROM kitaron_material_orders WHERE source_key = $key AND active = 1);
                    """;
                command.Parameters.AddWithValue("$key", sourceKey);
                if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
                    return PlanningHttpSupport.Error(404, "material_order_not_found", "The Kitaron material order line was not found.", context);
                command.CommandText = """
                    INSERT INTO work_order_material_orders (production_batch_id, material_order_source_key, verified_by, verified_at)
                    VALUES ($batch, $key, $actor, $now)
                    ON CONFLICT (production_batch_id, material_order_source_key) DO NOTHING;
                    """;
                command.Parameters.AddWithValue("$batch", batchId);
                command.Parameters.AddWithValue("$actor", actor);
                command.Parameters.AddWithValue("$now", timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            }
            else
            {
                command.CommandText = """
                    DELETE FROM work_order_material_orders
                    WHERE production_batch_id = $batch AND material_order_source_key = $key;
                    """;
                command.Parameters.AddWithValue("$batch", batchId);
                command.Parameters.AddWithValue("$key", sourceKey);
            }
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(new WorkOrderMaterialOrderListResponse(await ReadAsync(connection, batchId, cancellationToken)));
    }

    /// <summary>The candidate lines (open purchase lines of the raw material) plus every verified line.</summary>
    internal static async Task<IReadOnlyList<WorkOrderMaterialOrderResponse>> ReadAsync(
        SqliteConnection connection, string batchId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH candidates AS (
                SELECT json_each.value AS source_key
                FROM kitaron_batch_material_checks, json_each(kitaron_batch_material_checks.material_order_keys)
                WHERE kitaron_batch_material_checks.production_batch_id = $batch
                UNION
                SELECT material_order_source_key FROM work_order_material_orders WHERE production_batch_id = $batch
            )
            SELECT m.source_key, m.purchase_order_number, m.line_number, m.material_number, m.description, m.supplier,
                   m.ordered_quantity, m.received_quantity, m.unit, m.requested_delivery_date, m.approved_delivery_date,
                   m.closed, v.verified_by, v.verified_at
            FROM candidates c
            JOIN kitaron_material_orders m ON m.source_key = c.source_key
            LEFT JOIN work_order_material_orders v
              ON v.production_batch_id = $batch AND v.material_order_source_key = m.source_key
            ORDER BY v.verified_at IS NULL, COALESCE(m.approved_delivery_date, m.requested_delivery_date), m.purchase_order_number;
            """;
        command.Parameters.AddWithValue("$batch", batchId);
        var items = new List<WorkOrderMaterialOrderResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new WorkOrderMaterialOrderResponse(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                Text(reader, 4), Text(reader, 5), reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7), Text(reader, 8),
                Date(reader, 9), Date(reader, 10), reader.GetInt64(11) == 1,
                !reader.IsDBNull(12), Text(reader, 12),
                reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture)));
        }
        return items;
    }

    private static async Task<bool> BatchExistsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string batchId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM production_batches WHERE id = $id);";
        command.Parameters.AddWithValue("$id", batchId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<string> EnsureEditAuthorityAsync(
        SqliteConnection connection, SqliteTransaction transaction, EditAuthority authority, CancellationToken cancellationToken)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(connection, transaction, DateTimeOffset.UtcNow, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, holder_user_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
            throw new EditModeMutationException("edit_mode_required", "No Windows client currently holds Edit Mode.");
        if (!string.Equals(reader.GetString(0), authority.ClientId, StringComparison.Ordinal)
            || reader.GetInt64(2) != authority.Generation)
            throw new EditModeMutationException("edit_generation_stale", "This client does not hold the active Edit Mode generation.");
        return reader.IsDBNull(1) ? authority.ClientId : reader.GetString(1);
    }

    private static string? Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);

    private static DateOnly? Date(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : DateOnly.Parse(reader.GetString(index)[..10], CultureInfo.InvariantCulture);
}

internal sealed record WorkOrderMaterialOrderListResponse(IReadOnlyList<WorkOrderMaterialOrderResponse> Items);

internal sealed record WorkOrderMaterialOrderResponse(
    string SourceKey,
    string PurchaseOrderNumber,
    string LineNumber,
    string MaterialNumber,
    string? Description,
    string? Supplier,
    double OrderedQuantity,
    double? ReceivedQuantity,
    string? Unit,
    DateOnly? RequestedDeliveryDate,
    DateOnly? ApprovedDeliveryDate,
    bool Closed,
    bool Verified,
    string? VerifiedBy,
    DateTimeOffset? VerifiedAt);
