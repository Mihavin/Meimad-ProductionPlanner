using Meimad.Planner.Server.Domain.ProductionBatches;

namespace Meimad.Planner.Server.Tests.ProductionBatches;

public sealed class ProductionBatchValidationTests
{
    [Fact]
    public void Supports_one_multiple_split_stock_scrap_and_stock_only_patterns()
    {
        AssertValid(10, Allocation("order", "order-1", 10));
        AssertValid(
            10,
            Allocation("order", "order-1", 4),
            Allocation("order", "order-2", 6));
        AssertValid(4, Allocation("order", "order-1", 4));
        AssertValid(
            15,
            Allocation("order", "order-1", 10),
            Allocation("stock", null, 3),
            Allocation("scrapAllowance", null, 2));
        AssertValid(12, Allocation("stock", null, 12));
        AssertValid(
            13,
            Allocation("stock", null, 12),
            Allocation("scrapAllowance", null, 1));
    }

    [Theory]
    [InlineData(10, "order", "order-1", -1, "positive_required")]
    [InlineData(10, "order", "order-1", 0, "positive_required")]
    [InlineData(10, "unknown", null, 10, "invalid_allocation_type")]
    [InlineData(10, "order", null, 10, "required")]
    [InlineData(10, "stock", "order-1", 10, "forbidden")]
    [InlineData(10, "scrapAllowance", "order-1", 10, "forbidden")]
    public void Rejects_invalid_allocation_rows(
        int plannedQuantity,
        string type,
        string? orderId,
        int quantity,
        string expectedCode)
    {
        var exception = Assert.Throws<ProductionBatchValidationException>(() =>
            Validate(plannedQuantity, Allocation(type, orderId, quantity)));

        Assert.Contains(exception.Issues, issue => issue.Code == expectedCode);
    }

    [Fact]
    public void Rejects_empty_zero_total_mismatch_and_scrap_only_batches()
    {
        AssertInvalid(10, [], "required");
        AssertInvalid(0, [Allocation("stock", null, 1)], "positive_required");
        AssertInvalid(-1, [Allocation("stock", null, 1)], "positive_required");
        AssertInvalid(10, [Allocation("order", "order-1", 9)], "allocation_total_mismatch");
        AssertInvalid(
            3,
            [Allocation("scrapAllowance", null, 3)],
            "production_purpose_required");
    }

    [Fact]
    public void Rejects_duplicate_semantic_allocations()
    {
        AssertInvalid(
            10,
            [
                Allocation("order", "order-1", 5),
                Allocation("order", "order-1", 5)
            ],
            "duplicate_order_allocation");
        AssertInvalid(
            10,
            [
                Allocation("stock", null, 5),
                Allocation("stock", null, 5)
            ],
            "duplicate_stock_allocation");
        AssertInvalid(
            12,
            [
                Allocation("stock", null, 10),
                Allocation("scrapAllowance", null, 1),
                Allocation("scrapAllowance", null, 1)
            ],
            "duplicate_scrap_allowance");
    }

    [Fact]
    public void Uses_wide_arithmetic_and_rejects_integer_sum_overflow_as_mismatch()
    {
        AssertInvalid(
            int.MaxValue,
            [
                Allocation("order", "order-1", int.MaxValue),
                Allocation("stock", null, 1)
            ],
            "allocation_total_mismatch");
    }

    [Fact]
    public void Rejects_missing_and_cross_case_order_references()
    {
        var exception = Assert.Throws<ProductionBatchValidationException>(() =>
            ProductionBatchValidator.ValidateOrderCaseOwnership(
                "case-1",
                [
                    new OrderAllocationReference("missing", null),
                    new OrderAllocationReference("foreign", "case-2")
                ]));

        Assert.Contains(exception.Issues, issue => issue.Code == "invalid_reference");
        Assert.Contains(exception.Issues, issue => issue.Code == "cross_case_order");
    }

    [Fact]
    public void Rejects_cancelled_order_reference()
    {
        var exception = Assert.Throws<ProductionBatchValidationException>(() =>
            ProductionBatchValidator.ValidateOrderCaseOwnership(
                "case-1",
                [new OrderAllocationReference("cancelled", "case-1", IsCancelled: true)]));

        Assert.Contains(exception.Issues, issue => issue.Code == "cancelled_order");
    }

    private static void AssertValid(int plannedQuantity, params BatchAllocationValue[] allocations)
    {
        var values = Validate(plannedQuantity, allocations);
        Assert.Equal(plannedQuantity, values.Allocations.Sum(allocation => allocation.Quantity));
    }

    private static void AssertInvalid(
        int plannedQuantity,
        IReadOnlyList<BatchAllocationValue> allocations,
        string expectedCode)
    {
        var exception = Assert.Throws<ProductionBatchValidationException>(() =>
            Validate(plannedQuantity, allocations.ToArray()));
        Assert.Contains(exception.Issues, issue => issue.Code == expectedCode);
    }

    private static ValidatedProductionBatchValues Validate(
        int plannedQuantity,
        params BatchAllocationValue[] allocations) =>
        ProductionBatchValidator.ValidateAndNormalize(new ProductionBatchValues(
            "case-1",
            "B-100",
            "waiting",
            plannedQuantity,
            allocations));

    private static BatchAllocationValue Allocation(
        string type,
        string? orderId,
        int quantity) => new(type, orderId, quantity);
}
