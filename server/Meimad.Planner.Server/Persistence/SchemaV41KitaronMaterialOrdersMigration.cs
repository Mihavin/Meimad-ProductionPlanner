using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV41KitaronMaterialOrdersMigration : IDatabaseMigration
{
    public int Version => 41;

    public string Name => "kitaron_material_orders_and_delivery_approvals";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE kitaron_material_orders (
                source_key TEXT PRIMARY KEY,
                purchase_order_number TEXT NOT NULL,
                line_number TEXT NOT NULL,
                material_number TEXT NOT NULL,
                description TEXT NULL,
                supplier TEXT NULL,
                ordered_quantity REAL NOT NULL CHECK (ordered_quantity > 0),
                received_quantity REAL NULL CHECK (received_quantity IS NULL OR received_quantity >= 0),
                unit TEXT NULL,
                requested_delivery_date TEXT NULL,
                approved_delivery_date TEXT NULL,
                approved_quantity REAL NULL CHECK (approved_quantity IS NULL OR approved_quantity >= 0),
                approval_note TEXT NULL,
                status TEXT NULL,
                closed INTEGER NOT NULL CHECK (closed IN (0, 1)),
                active INTEGER NOT NULL DEFAULT 1 CHECK (active IN (0, 1)),
                source_hash TEXT NOT NULL,
                first_imported_at TEXT NOT NULL,
                last_imported_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX ix_kitaron_material_orders_purchase_order
                ON kitaron_material_orders(purchase_order_number, line_number);

            CREATE INDEX ix_kitaron_material_orders_material_active
                ON kitaron_material_orders(material_number, active, closed, requested_delivery_date);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
