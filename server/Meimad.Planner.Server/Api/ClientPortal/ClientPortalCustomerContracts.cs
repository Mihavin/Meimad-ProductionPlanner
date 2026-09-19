using System.Text.Json.Serialization;
using Meimad.Planner.Server.Domain.ClientPortal;

namespace Meimad.Planner.Server.Api.ClientPortal;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateClientPortalCustomerRequest(string? Customer, string? CustomerId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record UpdateClientPortalCustomerRequest(string? Customer);

internal sealed record ClientPortalCustomerResponse(
    string CustomerId,
    string Customer,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static ClientPortalCustomerResponse FromDomain(ClientPortalCustomer value) =>
        new(value.CustomerId, value.Customer, value.CreatedAt, value.UpdatedAt);
}

internal sealed record ClientPortalCustomerListResponse(
    IReadOnlyList<ClientPortalCustomerResponse> Items,
    string? NextCursor);
