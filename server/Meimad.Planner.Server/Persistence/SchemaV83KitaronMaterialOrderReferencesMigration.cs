using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Kitaron material purchase lines gain their price and the customer order row they name, and a
/// snapshot of the open Kitaron work orders (with raw material and customer order) lets each
/// purchase line list the batches and customer orders that use its material. Imported batches
/// record their assigned material orders and a planner release state (pending / released).
/// </summary>
internal sealed class SchemaV83KitaronMaterialOrderReferencesMigration : IDatabaseMigration
{
    public int Version => 83;

    public string Name => "kitaron_material_order_references";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE kitaron_material_orders ADD COLUMN unit_price REAL NULL;
            ALTER TABLE kitaron_material_orders ADD COLUMN line_total REAL NULL;
            ALTER TABLE kitaron_material_orders ADD COLUMN customer_order_reference TEXT NULL;

            CREATE TABLE kitaron_work_orders (
                work_order_number INTEGER PRIMARY KEY,
                part_number TEXT NOT NULL,
                raw_material_id TEXT NULL,
                customer_order_number TEXT NULL,
                customer TEXT NULL,
                quantity INTEGER NULL,
                supply_date TEXT NULL,
                imported_at TEXT NOT NULL
            );
            CREATE INDEX ix_kitaron_work_orders_raw_material
                ON kitaron_work_orders(raw_material_id, work_order_number);

            -- The open raw-material purchase lines assigned to an imported batch.
            ALTER TABLE kitaron_batch_material_checks ADD COLUMN material_order_keys TEXT NOT NULL DEFAULT '[]'
                CHECK (json_valid(material_order_keys) AND json_type(material_order_keys) = 'array');
            ALTER TABLE kitaron_batch_material_checks ADD COLUMN material_orders_text TEXT NULL;

            -- Planner release of a batch imported from Kitaron: pending until released.
            ALTER TABLE production_batches ADD COLUMN release_state TEXT NOT NULL DEFAULT 'pending'
                CHECK (release_state IN ('pending', 'released'));
            ALTER TABLE production_batches ADD COLUMN released_at TEXT NULL;
            ALTER TABLE production_batches ADD COLUMN released_by TEXT NULL;

            -- The shared network folder all Case links are stored relative to, so every PC opens the
            -- same working folder, previews, models and G-code. Aliases are the drive-letter forms of
            -- the same share (for example J:\customers files) that are converted on save.
            CREATE TABLE network_folder_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                root_path TEXT NULL CHECK (root_path IS NULL OR length(root_path) <= 1000),
                aliases_json TEXT NOT NULL DEFAULT '[]'
                    CHECK (json_valid(aliases_json) AND json_type(aliases_json) = 'array'),
                kitaron_case_folder TEXT NOT NULL DEFAULT 'Meimad Cases'
                    CHECK (length(kitaron_case_folder) BETWEEN 1 AND 200),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );
            INSERT INTO network_folder_settings (id) VALUES (1);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
