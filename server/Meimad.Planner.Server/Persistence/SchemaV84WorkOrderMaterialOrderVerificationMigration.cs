using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Kitaron does not record which purchase line serves which work order (9,706 purchase lines, 16
/// with an order reference, none for open work orders; audit 2026-09-26). The shared raw material
/// only proposes candidates. A planner verifies the right material orders for a Work Order by hand;
/// only verified links count.
/// </summary>
internal sealed class SchemaV84WorkOrderMaterialOrderVerificationMigration : IDatabaseMigration
{
    public int Version => 84;

    public string Name => "work_order_material_order_verification";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE work_order_material_orders (
                production_batch_id TEXT NOT NULL
                    REFERENCES production_batches(id) ON DELETE CASCADE,
                material_order_source_key TEXT NOT NULL
                    REFERENCES kitaron_material_orders(source_key) ON DELETE CASCADE,
                verified_by TEXT NOT NULL,
                verified_at TEXT NOT NULL,
                PRIMARY KEY (production_batch_id, material_order_source_key)
            );
            CREATE INDEX ix_work_order_material_orders_material_order
                ON work_order_material_orders(material_order_source_key, production_batch_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
