namespace Meimad.Planner.Server.Domain.ProductionBatches;

internal static class ProductionBatchValidator
{
    internal const string WaitingStatus = "waiting";
    internal const string InProductionStatus = "in_production";
    internal const string CompleteStatus = "complete";
    internal const string BatchOperationNotStartedStatus = "not_started";

    private const int IdentifierMaximum = 200;

    internal static ValidatedProductionBatchValues ValidateAndNormalize(
        ProductionBatchValues values)
    {
        var issues = new List<ProductionBatchValidationIssue>();
        var caseId = RequiredText(values.CaseId, "caseId", IdentifierMaximum, issues);
        var batchNumber = RequiredText(
            values.BatchNumber,
            "batchNumber",
            IdentifierMaximum,
            issues);
        var status = values.Status?.Trim();
        if (!string.Equals(status, WaitingStatus, StringComparison.Ordinal))
        {
            issues.Add(new ProductionBatchValidationIssue(
                "status",
                "invalid_status",
                "A new Production Batch status must be waiting."));
        }

        if (values.PlannedQuantity <= 0)
        {
            issues.Add(new ProductionBatchValidationIssue(
                "plannedQuantity",
                "positive_required",
                "plannedQuantity must be greater than zero."));
        }

        var allocations = ValidateAllocations(values.Allocations, issues);
        if (allocations.Count > 0)
        {
            var productionPurposeQuantity = allocations
                .Where(allocation => allocation.AllocationType is
                    BatchAllocationType.Order or BatchAllocationType.Stock)
                .Sum(allocation => (long)allocation.Quantity);
            if (productionPurposeQuantity <= 0)
            {
                issues.Add(new ProductionBatchValidationIssue(
                    "allocations",
                    "production_purpose_required",
                    "A batch must serve at least one Order or include stock quantity; scrap alone is invalid."));
            }

            var allocatedTotal = allocations.Sum(allocation => (long)allocation.Quantity);
            if (allocatedTotal != values.PlannedQuantity)
            {
                issues.Add(new ProductionBatchValidationIssue(
                    "plannedQuantity",
                    "allocation_total_mismatch",
                    "plannedQuantity must equal Order allocations plus stock plus scrap allowance."));
            }
        }

        if (issues.Count > 0)
        {
            throw new ProductionBatchValidationException(issues);
        }

        return new ValidatedProductionBatchValues(
            caseId!,
            batchNumber!,
            WaitingStatus,
            values.PlannedQuantity,
            allocations);
    }

    internal static void ValidateOrderCaseOwnership(
        string batchCaseId,
        IReadOnlyCollection<OrderAllocationReference> orderReferences)
    {
        var issues = new List<ProductionBatchValidationIssue>();
        foreach (var reference in orderReferences)
        {
            if (reference.OrderCaseId is null)
            {
                issues.Add(new ProductionBatchValidationIssue(
                    "allocations.orderId",
                    "invalid_reference",
                    $"Order '{reference.OrderId}' does not exist."));
            }
            else if (!string.Equals(reference.OrderCaseId, batchCaseId, StringComparison.Ordinal))
            {
                issues.Add(new ProductionBatchValidationIssue(
                    "allocations.orderId",
                    "cross_case_order",
                    $"Order '{reference.OrderId}' does not belong to the Batch Case."));
            }
        }

        if (issues.Count > 0)
        {
            throw new ProductionBatchValidationException(issues);
        }
    }

    private static IReadOnlyList<ValidatedBatchAllocationValue> ValidateAllocations(
        IReadOnlyList<BatchAllocationValue>? allocations,
        ICollection<ProductionBatchValidationIssue> issues)
    {
        if (allocations is null || allocations.Count == 0)
        {
            issues.Add(new ProductionBatchValidationIssue(
                "allocations",
                "required",
                "At least one explicit allocation is required."));
            return [];
        }

        var validated = new List<ValidatedBatchAllocationValue>(allocations.Count);
        var orderIds = new HashSet<string>(StringComparer.Ordinal);
        var seenStock = false;
        var seenScrap = false;

        for (var index = 0; index < allocations.Count; index++)
        {
            var allocation = allocations[index];
            var field = $"allocations[{index}]";
            if (!BatchAllocationTypes.TryParseContractToken(
                    allocation.AllocationType?.Trim(),
                    out var type))
            {
                issues.Add(new ProductionBatchValidationIssue(
                    $"{field}.allocationType",
                    "invalid_allocation_type",
                    "allocationType must be order, stock, or scrapAllowance."));
                continue;
            }

            if (allocation.Quantity <= 0)
            {
                issues.Add(new ProductionBatchValidationIssue(
                    $"{field}.quantity",
                    "positive_required",
                    "Allocation quantity must be greater than zero; omit zero-valued rows."));
            }

            var orderId = Normalize(allocation.OrderId);
            if (type == BatchAllocationType.Order)
            {
                if (orderId is null)
                {
                    issues.Add(new ProductionBatchValidationIssue(
                        $"{field}.orderId",
                        "required",
                        "orderId is required for an Order allocation."));
                }
                else if (!orderIds.Add(orderId))
                {
                    issues.Add(new ProductionBatchValidationIssue(
                        $"{field}.orderId",
                        "duplicate_order_allocation",
                        "Each Order may appear only once in a Batch allocation set."));
                }
            }
            else if (orderId is not null)
            {
                issues.Add(new ProductionBatchValidationIssue(
                    $"{field}.orderId",
                    "forbidden",
                    "orderId is allowed only for an Order allocation."));
            }

            if (type == BatchAllocationType.Stock && seenStock)
            {
                issues.Add(new ProductionBatchValidationIssue(
                    $"{field}.allocationType",
                    "duplicate_stock_allocation",
                    "Use at most one stock allocation row."));
            }

            if (type == BatchAllocationType.ScrapAllowance && seenScrap)
            {
                issues.Add(new ProductionBatchValidationIssue(
                    $"{field}.allocationType",
                    "duplicate_scrap_allowance",
                    "Use at most one scrapAllowance row."));
            }

            seenStock |= type == BatchAllocationType.Stock;
            seenScrap |= type == BatchAllocationType.ScrapAllowance;
            validated.Add(new ValidatedBatchAllocationValue(type, orderId, allocation.Quantity));
        }

        return validated;
    }

    private static string? RequiredText(
        string? value,
        string field,
        int maximumLength,
        ICollection<ProductionBatchValidationIssue> issues)
    {
        var normalized = Normalize(value);
        if (normalized is null)
        {
            issues.Add(new ProductionBatchValidationIssue(field, "required", $"{field} is required."));
            return null;
        }

        if (normalized.Length > maximumLength)
        {
            issues.Add(new ProductionBatchValidationIssue(
                field,
                "too_long",
                $"{field} must contain at most {maximumLength} characters."));
        }

        return normalized;
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}

internal sealed record ProductionBatchValues(
    string? CaseId,
    string? BatchNumber,
    string? Status,
    int PlannedQuantity,
    IReadOnlyList<BatchAllocationValue>? Allocations);

internal sealed record BatchAllocationValue(
    string? AllocationType,
    string? OrderId,
    int Quantity);

internal sealed record ValidatedProductionBatchValues(
    string CaseId,
    string BatchNumber,
    string Status,
    int PlannedQuantity,
    IReadOnlyList<ValidatedBatchAllocationValue> Allocations);

internal sealed record ValidatedBatchAllocationValue(
    BatchAllocationType AllocationType,
    string? OrderId,
    int Quantity);

internal sealed record OrderAllocationReference(string OrderId, string? OrderCaseId);

internal sealed record ProductionBatchValidationIssue(string Field, string Code, string Message);

internal sealed class ProductionBatchValidationException : Exception
{
    internal ProductionBatchValidationException(IReadOnlyList<ProductionBatchValidationIssue> issues)
        : base("Production Batch validation failed.")
    {
        Issues = issues;
    }

    internal IReadOnlyList<ProductionBatchValidationIssue> Issues { get; }
}
