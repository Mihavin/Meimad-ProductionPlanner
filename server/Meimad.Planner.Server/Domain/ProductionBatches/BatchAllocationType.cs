namespace Meimad.Planner.Server.Domain.ProductionBatches;

internal enum BatchAllocationType
{
    Order,
    DerivedOrder,
    Stock,
    ScrapAllowance
}

internal static class BatchAllocationTypes
{
    internal const string OrderToken = "order";
    internal const string DerivedOrderToken = "derivedOrder";
    internal const string StockToken = "stock";
    internal const string ScrapAllowanceToken = "scrapAllowance";

    internal static string ToContractToken(this BatchAllocationType type) => type switch
    {
        BatchAllocationType.Order => OrderToken,
        BatchAllocationType.DerivedOrder => DerivedOrderToken,
        BatchAllocationType.Stock => StockToken,
        BatchAllocationType.ScrapAllowance => ScrapAllowanceToken,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown allocation type.")
    };

    internal static string ToStorageToken(this BatchAllocationType type) => type switch
    {
        BatchAllocationType.Order => OrderToken,
        BatchAllocationType.DerivedOrder => "derived_order",
        BatchAllocationType.Stock => StockToken,
        BatchAllocationType.ScrapAllowance => "scrap_allowance",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown allocation type.")
    };

    internal static bool TryParseContractToken(string? value, out BatchAllocationType type)
    {
        switch (value)
        {
            case OrderToken:
                type = BatchAllocationType.Order;
                return true;
            case DerivedOrderToken:
                type = BatchAllocationType.DerivedOrder;
                return true;
            case StockToken:
                type = BatchAllocationType.Stock;
                return true;
            case ScrapAllowanceToken:
                type = BatchAllocationType.ScrapAllowance;
                return true;
            default:
                type = default;
                return false;
        }
    }

    internal static bool TryParseStorageToken(string? value, out BatchAllocationType type)
    {
        if (value == "derived_order")
        {
            type = BatchAllocationType.DerivedOrder;
            return true;
        }
        if (value == "scrap_allowance")
        {
            type = BatchAllocationType.ScrapAllowance;
            return true;
        }

        return TryParseContractToken(value, out type);
    }
}
