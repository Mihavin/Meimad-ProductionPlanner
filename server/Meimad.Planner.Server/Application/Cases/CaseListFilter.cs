namespace Meimad.Planner.Server.Application.Cases;

/// <summary>
/// Case pool filters. Every set filter must match (AND); a null filter is ignored.
/// Tokens: WorkOrders with|without, Release pending|released, Orders active|none,
/// Operations with|without, MaterialOrders verified|toVerify. Supply dates match an open Work
/// Order's Kitaron supply date or an active Order's delivery date; production start matches a Work
/// Order's actual start, else its Kitaron planned start.
/// </summary>
internal sealed record CaseListFilter(
    string? WorkOrders = null,
    string? Release = null,
    string? Orders = null,
    string? Operations = null,
    string? MaterialOrders = null,
    DateOnly? SupplyFrom = null,
    DateOnly? SupplyTo = null,
    DateOnly? StartFrom = null,
    DateOnly? StartTo = null)
{
    internal static readonly CaseListFilter None = new();

    internal static readonly IReadOnlyDictionary<string, string[]> Tokens = new Dictionary<string, string[]>
    {
        ["workOrders"] = ["with", "without"],
        ["release"] = ["pending", "released"],
        ["orders"] = ["active", "none"],
        ["operations"] = ["with", "without"],
        ["materialOrders"] = ["verified", "toVerify"]
    };
}
