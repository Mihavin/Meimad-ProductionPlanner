namespace Meimad.Planner.Server.Domain.Orders;

internal sealed record PlannerOrder(
    string OrderId,
    string CaseId,
    string OrderNumber,
    int Quantity,
    DateOnly WorkFinishDate,
    OrderStatus Status,
    string? Notes,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    decimal? Price = null,
    bool IsKitaronManaged = false,
    string? KitaronStatus = null,
    bool IsHistorical = false);

internal static class PlannerOrderExtensions
{
    /// <summary>
    /// The status token every reader of an Order outside this Server should show: Kitaron's own
    /// live status when this Order is Kitaron-managed and Kitaron has reported one, otherwise the
    /// Server's own stored Status. The two can disagree -- the stored Status column reflects this
    /// Server's last explicit write, while KitaronStatus reflects Kitaron's external system,
    /// which can move independently (e.g. Kitaron marks an Order complete and later reopens it)
    /// without a corresponding write here.
    ///
    /// Every caller that surfaces an Order's status to something other than this Server's own
    /// storage MUST go through this, not order.Status.ToContractToken() directly -- see
    /// Api/Orders/OrderContracts.cs (OrderResponse.FromDomain, the desktop client's source of
    /// truth) and Application/ClientPortal/ClientPortalPushService.cs (the cloud customer
    /// portal), which both use it. A caller that reads order.Status directly for a
    /// Kitaron-managed Order will silently show stale data whenever the two disagree.
    /// </summary>
    internal static string EffectiveStatusToken(this PlannerOrder order) =>
        order.IsKitaronManaged && order.KitaronStatus is not null
            ? order.KitaronStatus
            : order.Status.ToContractToken();
}
