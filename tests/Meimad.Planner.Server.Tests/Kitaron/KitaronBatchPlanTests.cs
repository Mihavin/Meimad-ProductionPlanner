using Meimad.Planner.Server.Application.Kitaron;

namespace Meimad.Planner.Server.Tests.Kitaron;

public sealed class KitaronBatchPlanTests
{
    private static readonly IReadOnlySet<string> Parts = new HashSet<string>(["PN-1"], StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_work_order_allocates_its_order_lines_and_the_cutting_reserve_becomes_scrap()
    {
        var warnings = new List<string>();
        var batches = KitaronSyncService.BuildBatches(
            [new KitaronSourceWorkOrder(41518, "PN-1", "41968", 42, 40, null, null)],
            [new KitaronSourceWorkOrderLink(41518, "41968", 40)],
            [], Parts, [], warnings);

        var batch = Assert.Single(batches);
        Assert.Equal("wo:41518", batch.SourceKey);
        Assert.Equal("41518", batch.BatchNumber);
        Assert.Equal(42, batch.PlannedQuantity);
        Assert.Equal([new KitaronSyncBatchAllocation("41968", 40), new KitaronSyncBatchAllocation(null, 2, true)], batch.Allocations);
        Assert.Equal("unknown", batch.MaterialState);
    }

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
