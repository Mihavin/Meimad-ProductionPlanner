using System.Globalization;
using Meimad.Planner.Server.Application.ClientPortal;
using Meimad.Planner.Server.Domain.ClientPortal;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// The customer-portal push mapping table. No Edit Mode gating and no version column: see the
/// reasoning on <see cref="ClientPortalCustomerService"/> - this is admin configuration, not shared
/// planning data. Uniqueness is enforced by the schema (primary key on the portal id, a
/// case-insensitive unique index on the Customer name) and surfaced here as typed conflicts.
/// </summary>
internal sealed class SqliteClientPortalCustomerRepository(SqliteDatabase database)
    : IClientPortalCustomerRepository
{
    private const string Projection = "customer_id, customer_name, created_at, updated_at";

    public async Task<IReadOnlyList<ClientPortalCustomer>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {Projection} FROM client_portal_customers ORDER BY customer_name COLLATE NOCASE, customer_id;";
        var values = new List<ClientPortalCustomer>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values.Add(Read(reader));
        return values;
    }

    public async Task<ClientPortalCustomer?> GetAsync(string customerId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Projection} FROM client_portal_customers WHERE customer_id = $id;";
        command.Parameters.AddWithValue("$id", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<ClientPortalCustomer> CreateAsync(
        ClientPortalCustomer customer,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO client_portal_customers (customer_id, customer_name, created_at, updated_at)
            VALUES ($id, $name, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", customer.CustomerId);
        command.Parameters.AddWithValue("$name", customer.Customer);
        command.Parameters.AddWithValue("$createdAt", Format(customer.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", Format(customer.UpdatedAt));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (IsUniqueViolation(exception))
        {
            throw ConflictFor(exception, customer);
        }

        return customer;
    }

    public async Task<ClientPortalCustomer?> RenameAsync(
        string customerId,
        string customerName,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE client_portal_customers
            SET customer_name = $name,
                updated_at = $updatedAt
            WHERE customer_id = $id;
            """;
        command.Parameters.AddWithValue("$id", customerId);
        command.Parameters.AddWithValue("$name", customerName);
        command.Parameters.AddWithValue("$updatedAt", Format(updatedAt));
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return null;
        }
        catch (SqliteException exception) when (IsUniqueViolation(exception))
        {
            throw new ClientPortalCustomerNameConflictException(customerName);
        }

        return await GetAsync(customerId, cancellationToken);
    }

    public async Task<bool> DeleteAsync(string customerId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM client_portal_customers WHERE customer_id = $id;";
        command.Parameters.AddWithValue("$id", customerId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static bool IsUniqueViolation(SqliteException exception) =>
        exception.SqliteErrorCode == 19;

    private static Exception ConflictFor(SqliteException exception, ClientPortalCustomer customer) =>
        exception.Message.Contains("customer_name", StringComparison.OrdinalIgnoreCase)
            ? new ClientPortalCustomerNameConflictException(customer.Customer)
            : new ClientPortalCustomerIdConflictException(customer.CustomerId);

    private static ClientPortalCustomer Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        Parse(reader.GetString(2)),
        Parse(reader.GetString(3)));

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
