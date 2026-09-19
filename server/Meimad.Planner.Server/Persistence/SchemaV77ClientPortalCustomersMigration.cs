using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Moves the cloud customer-portal push mapping (Customer name -> portal customer id) out of
/// <c>appsettings.json</c> (<c>ClientPortal:Customers</c>, which needed a Server restart) into the
/// database, so the Windows client's Setup screen can manage it live. The portal customer id is the
/// primary key because it is the Firestore document id and Firebase Auth claim on the portal side,
/// and a case-insensitive unique index on the Customer name keeps one Customer from being pushed
/// under two different portal ids.
/// </summary>
internal sealed class SchemaV77ClientPortalCustomersMigration : IDatabaseMigration
{
    public int Version => 77;
    public string Name => "client_portal_customers";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE client_portal_customers (
                customer_id TEXT PRIMARY KEY
                    CHECK (length(customer_id) BETWEEN 1 AND 64),
                customer_name TEXT NOT NULL
                    CHECK (length(trim(customer_name)) > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE UNIQUE INDEX ux_client_portal_customers_name
                ON client_portal_customers (customer_name COLLATE NOCASE);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
