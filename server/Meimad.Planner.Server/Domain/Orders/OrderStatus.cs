namespace Meimad.Planner.Server.Domain.Orders;

internal enum OrderStatus
{
    Active,
    InProduction,
    Complete,
    Cancelled
}

internal static class OrderStatuses
{
    internal const string ActiveToken = "active";
    internal const string InProductionToken = "in_production";
    internal const string CompleteToken = "complete";
    internal const string CancelledToken = "cancelled";

    internal static bool IsActiveDemand(this OrderStatus status) =>
        status is OrderStatus.Active or OrderStatus.InProduction;

    internal static string ToContractToken(this OrderStatus status) => status switch
    {
        OrderStatus.Active => ActiveToken,
        OrderStatus.InProduction => InProductionToken,
        OrderStatus.Complete => CompleteToken,
        OrderStatus.Cancelled => CancelledToken,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown Order status.")
    };

    internal static bool TryParseContractToken(string? value, out OrderStatus status)
    {
        switch (value)
        {
            case ActiveToken:
                status = OrderStatus.Active;
                return true;
            case InProductionToken:
                status = OrderStatus.InProduction;
                return true;
            case CompleteToken:
                status = OrderStatus.Complete;
                return true;
            case CancelledToken:
                status = OrderStatus.Cancelled;
                return true;
            default:
                status = default;
                return false;
        }
    }
}

internal sealed record OrderProductionFacts(
    int OrderQuantity,
    long AllocatedQuantity,
    bool HasAllocatedBatch,
    bool HasStartedOperation,
    bool EveryAllocatedBatchHasOperations,
    bool EveryAllocatedOperationIsCompleted);

internal static class OrderLifecycle
{
    internal static OrderStatus Derive(OrderProductionFacts facts)
    {
        if (facts.HasAllocatedBatch
            && facts.AllocatedQuantity >= facts.OrderQuantity
            && facts.EveryAllocatedBatchHasOperations
            && facts.EveryAllocatedOperationIsCompleted)
        {
            return OrderStatus.Complete;
        }

        return facts.HasStartedOperation
            ? OrderStatus.InProduction
            : OrderStatus.Active;
    }
}
