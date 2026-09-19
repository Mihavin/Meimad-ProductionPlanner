using Meimad.Planner.Server.Domain.ClientPortal;

namespace Meimad.Planner.Server.Application.ClientPortal;

internal interface IClientPortalCustomerRepository
{
    Task<IReadOnlyList<ClientPortalCustomer>> ListAsync(CancellationToken cancellationToken);
    Task<ClientPortalCustomer?> GetAsync(string customerId, CancellationToken cancellationToken);
    Task<ClientPortalCustomer> CreateAsync(ClientPortalCustomer customer, CancellationToken cancellationToken);
    Task<ClientPortalCustomer?> RenameAsync(string customerId, string customerName, DateTimeOffset updatedAt, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string customerId, CancellationToken cancellationToken);
}
