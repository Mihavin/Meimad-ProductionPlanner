namespace Meimad.Planner.Server.Domain.Orders;

internal enum OrderStatus
{
    Active,
    Complete,
    Cancelled
}

internal static class OrderStatuses
{
    internal const string ActiveToken = "active";
    internal const string CompleteToken = "complete";
    internal const string CancelledToken = "cancelled";

    internal static bool IsActiveDemand(this OrderStatus status) =>
        status == OrderStatus.Active;

    internal static string ToContractToken(this OrderStatus status) => status switch
    {
        OrderStatus.Active => ActiveToken,
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
