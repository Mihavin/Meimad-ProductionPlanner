using Meimad.Planner.Server.Application.Kitaron;

namespace Meimad.Planner.Server.Tests.Kitaron;

public sealed class KitaronBatchPlanTests
{
    private static readonly IReadOnlySet<string> Parts = new HashSet<string>(["PN-1"], StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_work_order_fills_each_open_order_by_due_date_and_the_cutting_reserve_becomes_scrap()
    {
        // Like work order 41043: Kitaron links the whole quantity to the first line only.
        var orders = new[]
        {
            Order("40454", 8, new DateOnly(2026, 10, 26)),
            Order("40449", 8, new DateOnly(2026, 8, 30)),
            Order("40410", 8, new DateOnly(2026, 12, 23)),
            Order("40411", 8, new DateOnly(2027, 1, 5), "complete")
        };
        var batches = KitaronSyncService.BuildBatches(
            [new KitaronSourceWorkOrder(41043, "PN-1", "40449", 21, 20, null, null)],
            [new KitaronSourceWorkOrderLink(41043, "40449", 21)],
            [], Parts, [], new List<string>(), orders);

        var batch = Assert.Single(batches);
        Assert.Equal("wo:41043", batch.SourceKey);
        Assert.Equal("41043", batch.BatchNumber);
        Assert.Equal(21, batch.PlannedQuantity);
        Assert.Equal(
            [
                new KitaronSyncBatchAllocation("40449", 8),
                new KitaronSyncBatchAllocation("40454", 8),
                new KitaronSyncBatchAllocation("40410", 4),
                new KitaronSyncBatchAllocation(null, 1, true)
            ],
            batch.Allocations);
        Assert.Equal("unknown", batch.MaterialState);
    }

    [Fact]
    public void Later_work_orders_take_the_demand_left_by_earlier_ones_and_the_rest_is_stock()
    {
        var orders = new[] { Order("1", 8, new DateOnly(2026, 9, 1)), Order("2", 8, new DateOnly(2026, 10, 1)) };
        var batches = KitaronSyncService.BuildBatches(
            [
                new KitaronSourceWorkOrder(502, "PN-1", "1", 10, 10, null, null),
                new KitaronSourceWorkOrder(501, "PN-1", "1", 10, 10, null, null)
            ],
            [], [], Parts, [], new List<string>(), orders);

        Assert.Equal(["501", "502"], batches.Select(item => item.BatchNumber));
        Assert.Equal([new KitaronSyncBatchAllocation("1", 8), new KitaronSyncBatchAllocation("2", 2)], batches[0].Allocations);
        Assert.Equal([new KitaronSyncBatchAllocation("2", 6), new KitaronSyncBatchAllocation(null, 4)], batches[1].Allocations);
    }

    private static KitaronSyncOrder Order(string recordId, int quantity, DateOnly due, string status = "active") =>
        new(recordId, "PN-1", $"SO/{recordId}", quantity, due, status, "h");

    [Fact]
    public void Material_state_follows_kitarons_calculation_and_open_purchase_orders()
    {
        var purchase = new KitaronSyncMaterialOrder("po-1", "PO-1", "1", "MAT-B", null, null, 100, 0, null,
            new DateOnly(2026, 10, 6), null, null, null, null, false, "h");
        var batches = KitaronSyncService.BuildBatches(
            [
                new KitaronSourceWorkOrder(1, "PN-1", "10", 5, 5, null, null),
                new KitaronSourceWorkOrder(2, "PN-1", "10", 5, 5, null, null),
                new KitaronSourceWorkOrder(3, "PN-1", "10", 5, 5, null, null)
            ],
            [],
            [
                new KitaronSourceWorkOrderMaterial(1, "MAT-A", 10, null, 50, null, 40),
                new KitaronSourceWorkOrderMaterial(2, "MAT-B", 10, null, 2, null, -8),
                new KitaronSourceWorkOrderMaterial(3, "MAT-C", 10, null, 0, null, -10)
            ],
            Parts, [purchase], new List<string>());

        Assert.Equal(["available", "on_order", "missing"], batches.Select(item => item.MaterialState));
        Assert.Contains("due 2026-10-06", batches[1].MaterialDetail);
        Assert.Contains("no open purchase order", batches[2].MaterialDetail);
        // No order link: the whole quantity is stock.
        Assert.Equal([new KitaronSyncBatchAllocation(null, 5)], batches[0].Allocations);
    }

    [Fact]
    public void Work_orders_of_unsynchronized_parts_or_fractional_quantities_are_skipped_with_a_warning()
    {
        var warnings = new List<string>();
        var batches = KitaronSyncService.BuildBatches(
            [
                new KitaronSourceWorkOrder(1, "OTHER", "10", 5, 5, null, null),
                new KitaronSourceWorkOrder(2, "PN-1", "10", 2.5, 2.5, null, null)
            ],
            [], [], Parts, [], warnings);

        Assert.Empty(batches);
        Assert.Equal(2, warnings.Count);
    }
}
