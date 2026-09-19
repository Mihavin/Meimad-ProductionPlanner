using Meimad.Planner.Server.Domain.ClientPortal;

namespace Meimad.Planner.Server.Application.ClientPortal;

/// <summary>
/// CRUD over the customer-portal push mapping.
/// </summary>
/// <remarks>
/// Deliberately lighter than the MachineTypes slice: no Edit Mode authority (<c>EditAuthority</c> /
/// <c>edit_tokens</c>) and no ETag/If-Match optimistic concurrency. Those exist to protect shared
/// planning data that several Windows clients edit at the same time. This list is deployment/admin
/// configuration - a handful of rows that decide which Customers leave the Server - closer to the
/// Kitaron connection settings than to a Machine Type, so it follows that lighter pattern: plain
/// CRUD whose only invariants (valid portal id, unique Customer name) are enforced by the database
/// and reported as ordinary 409/422 responses.
/// </remarks>
internal sealed class ClientPortalCustomerService(
    IClientPortalCustomerRepository repository,
    TimeProvider timeProvider)
{
    internal Task<IReadOnlyList<ClientPortalCustomer>> ListAsync(CancellationToken cancellationToken = default) =>
        repository.ListAsync(cancellationToken);

    internal Task<ClientPortalCustomer?> GetAsync(string customerId, CancellationToken cancellationToken = default) =>
        repository.GetAsync(NormalizeId(customerId), cancellationToken);

    internal Task<ClientPortalCustomer> CreateAsync(
        string? customerId,
        string? customer,
        CancellationToken cancellationToken = default)
    {
        var id = ValidateId(customerId);
        var name = ValidateName(customer);
        var now = timeProvider.GetUtcNow();
        return repository.CreateAsync(new ClientPortalCustomer(id, name, now, now), cancellationToken);
    }

    internal async Task<ClientPortalCustomer> RenameAsync(
        string customerId,
        string? customer,
        CancellationToken cancellationToken = default)
    {
        var id = NormalizeId(customerId);
        var name = ValidateName(customer);
        return await repository.RenameAsync(id, name, timeProvider.GetUtcNow(), cancellationToken)
            ?? throw new ClientPortalCustomerNotFoundException(id);
    }

    internal Task<bool> DeleteAsync(string customerId, CancellationToken cancellationToken = default) =>
        repository.DeleteAsync(NormalizeId(customerId), cancellationToken);

    private static string NormalizeId(string customerId) => (customerId ?? string.Empty).Trim();

    private static string ValidateId(string? customerId)
    {
        var id = NormalizeId(customerId ?? string.Empty);
        if (id.Length == 0)
        {
            throw new ClientPortalCustomerValidationException(
                "customerId", "required", "A portal customer id is required.");
        }

        if (!ClientPortalCustomer.IsValidCustomerId(id))
        {
            throw new ClientPortalCustomerValidationException(
                "customerId",
                "invalid_format",
                $"Portal customer id '{id}' must be 1-64 lowercase letters, digits, '-' or '_'.");
        }

        return id;
    }

    private static string ValidateName(string? customer)
    {
        var name = (customer ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new ClientPortalCustomerValidationException(
                "customer", "required", "A Customer name is required.");
        }

        if (name.Length > 200)
        {
            throw new ClientPortalCustomerValidationException(
                "customer", "too_long", "A Customer name may be at most 200 characters.");
        }

        return name;
    }
}

internal sealed class ClientPortalCustomerValidationException(string field, string code, string message)
    : Exception(message)
{
    internal string Field { get; } = field;
    internal string Code { get; } = code;
}

internal sealed class ClientPortalCustomerNotFoundException(string customerId)
    : Exception($"Client portal customer '{customerId}' was not found.");

internal sealed class ClientPortalCustomerIdConflictException(string customerId)
    : Exception($"Portal customer id '{customerId}' is already mapped.");

internal sealed class ClientPortalCustomerNameConflictException(string customer)
    : Exception($"Customer '{customer}' is already mapped to a portal customer id.");
