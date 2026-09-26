using System.Globalization;
using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Api.Kitaron;

/// <summary>
/// Read-only list of the Kitaron material purchase-order lines the synchronization imports
/// (`kitaron_material_orders`). Kitaron stays authoritative; every client may read the list.
/// </summary>
internal static class KitaronMaterialOrderEndpoints
{
    internal static void MapKitaronMaterialOrderEndpoints(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/kitaron/material-orders", ListAsync);

    private static async Task<IResult> ListAsync(
        SqliteDatabase database, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_key, purchase_order_number, line_number, material_number, description, supplier,
                   ordered_quantity, received_quantity, unit, requested_delivery_date, approved_delivery_date,
                   approved_quantity, approval_note, status, closed, last_imported_at
            FROM kitaron_material_orders
            WHERE active = 1
            ORDER BY closed, COALESCE(approved_delivery_date, requested_delivery_date) IS NULL,
                     COALESCE(approved_delivery_date, requested_delivery_date) DESC,
                     purchase_order_number DESC, line_number;
            """;
        var items = new List<KitaronMaterialOrderResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var ordered = reader.GetDouble(6);
            double? received = reader.IsDBNull(7) ? null : reader.GetDouble(7);
            var requested = Date(reader, 9);
            var approved = Date(reader, 10);
            var closed = reader.GetInt64(14) == 1;
            items.Add(new KitaronMaterialOrderResponse(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                Text(reader, 4), Text(reader, 5), ordered, received, Text(reader, 8),
                requested, approved, reader.IsDBNull(11) ? null : reader.GetDouble(11),
                Text(reader, 12), Text(reader, 13), closed,
                DeliveryStatus(ordered, received, requested, approved, closed, today),
                DateTimeOffset.Parse(reader.GetString(15), CultureInfo.InvariantCulture)));
        }
        return Results.Ok(new KitaronMaterialOrderListResponse(items));
    }

    /// <summary>
    /// The delivery state the Kitaron facts report: Received or Closed for closed lines, else
    /// Partially received, Late (the promised or requested date passed), Supplier confirmed
    /// (the supplier approved a date), or Open.
    /// </summary>
    internal static string DeliveryStatus(
        double ordered, double? received, DateOnly? requested, DateOnly? approved, bool closed, DateOnly today)
    {
        var got = received ?? 0;
        if (closed) return got >= ordered ? "received" : "closed";
        if (got >= ordered) return "received";
        if (got > 0) return "partially_received";
        var due = approved ?? requested;
        if (due is not null && due < today) return "late";
        return approved is null ? "open" : "supplier_confirmed";
    }

    private static string? Text(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    private static DateOnly? Date(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : DateOnly.Parse(reader.GetString(index)[..10], CultureInfo.InvariantCulture);
}

internal sealed record KitaronMaterialOrderListResponse(IReadOnlyList<KitaronMaterialOrderResponse> Items);

internal sealed record KitaronMaterialOrderResponse(
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
    double? ApprovedQuantity,
    string? ApprovalNote,
    string? KitaronStatus,
    bool Closed,
    string DeliveryStatus,
    DateTimeOffset LastImportedAt);
