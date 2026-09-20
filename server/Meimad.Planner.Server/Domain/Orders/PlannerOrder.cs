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
    /// The status text the desktop client and REST API show (Api/Orders/OrderContracts.cs,
    /// OrderResponse.FromDomain): Kitaron's own live status text, verbatim, when this Order is
    /// Kitaron-managed and Kitaron has reported one, otherwise the Server's own stored Status.
    /// The two can disagree -- the stored Status column reflects this Server's last explicit
    /// write, while KitaronStatus reflects Kitaron's external system, which moves independently
    /// (e.g. Kitaron marks an Order's delivery closed, or reopens one, with no corresponding
    /// write here) -- Kitaron's own text is shown because it is more current.
    ///
    /// This is for a reader who already understands Kitaron's own status vocabulary (see
    /// KitaronMappingService's "orders"/"status" catalog entry) -- i.e. Meimad staff, not a
    /// customer. Do not reuse this for the cloud customer portal; see PortalStatusToken.
    /// </summary>
    internal static string DisplayStatusToken(this PlannerOrder order) =>
        order.IsKitaronManaged && order.KitaronStatus is not null
            ? order.KitaronStatus
            : order.Status.ToContractToken();

    /// <summary>
    /// The status token safe to send to the cloud customer portal
    /// (Application/ClientPortal/ClientPortalPushService.cs) -- always exactly one of
    /// OrderStatus's four contract tokens, never Kitaron's own raw text.
    ///
    /// Kitaron's status vocabulary (active / inactive / cancelled -- see
    /// KitaronMappingService's "orders"/"status" catalog entry) is both different from and
    /// smaller than the portal's four tokens: it has no notion of "in_production", and its
    /// "inactive" means the delivery row is closed/supplied, i.e. complete, not literally
    /// "inactive" in the portal's sense. The portal's ingest service rejects an entire
    /// customer's push outright if any one Order's status token isn't recognized (it validates
    /// the whole batch atomically) -- forwarding Kitaron's raw text here once broke syncing for
    /// a real customer for over eleven hours, the first time an Order of theirs went "inactive".
    ///
    /// Kitaron's cancelled/inactive are treated as authoritative -- Kitaron is the system of
    /// record for whether an Order has actually been delivered or pulled, and is typically more
    /// current than this Server's own stored Status for that. Kitaron's "active" only means
    /// "not yet closed"; it does not distinguish queued from currently running, so when Kitaron
    /// says active, the Server's own richer Status is used instead, unless that stored Status is
    /// itself complete or cancelled, which would contradict Kitaron's still-open signal and is
    /// exactly the kind of staleness this whole mechanism exists to correct. Any Kitaron status
    /// text this Server doesn't recognize at all falls back to the Server's own Status rather
    /// than being forwarded -- this method must never return anything outside OrderStatus's
    /// four contract tokens.
    /// </summary>
    internal static string PortalStatusToken(this PlannerOrder order)
    {
        if (!order.IsKitaronManaged || order.KitaronStatus is null)
        {
            return order.Status.ToContractToken();
        }

        return order.KitaronStatus switch
        {
            "inactive" => OrderStatuses.CompleteToken,
            "cancelled" => OrderStatuses.CancelledToken,
            "active" => order.Status is OrderStatus.Complete or OrderStatus.Cancelled
                ? OrderStatuses.ActiveToken
                : order.Status.ToContractToken(),
            _ => order.Status.ToContractToken()
        };
    }
}
