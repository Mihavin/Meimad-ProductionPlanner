using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV32UnifiedCasePoolMigration : IDatabaseMigration
{
    public int Version => 32;
    public string Name => "unified_case_pool_derived_order_allocations";

    public async Task ApplyAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE batch_allocations RENAME TO batch_allocations_v31;
            CREATE TABLE batch_allocations (
                id TEXT PRIMARY KEY,
                production_batch_id TEXT NOT NULL,
                allocation_type TEXT NOT NULL
                    CHECK (allocation_type IN ('order', 'derived_order', 'stock', 'scrap_allowance')),
                order_id TEXT,
                derived_order_key TEXT,
                quantity INTEGER NOT NULL CHECK (quantity > 0),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (production_batch_id) REFERENCES production_batches (id) ON DELETE RESTRICT,
                FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT,
                CHECK (
                    (allocation_type='order' AND order_id IS NOT NULL AND derived_order_key IS NULL)
                    OR (allocation_type='derived_order' AND order_id IS NULL AND derived_order_key IS NOT NULL)
                    OR (allocation_type IN ('stock', 'scrap_allowance') AND order_id IS NULL AND derived_order_key IS NULL)
                )
            );
            INSERT INTO batch_allocations (
                id, production_batch_id, allocation_type, order_id, derived_order_key,
                quantity, version, created_at, updated_at)
            SELECT id, production_batch_id, allocation_type, order_id, NULL,
                   quantity, version, created_at, updated_at
            FROM batch_allocations_v31;
            DROP TABLE batch_allocations_v31;
            CREATE INDEX ix_batch_allocations_batch ON batch_allocations (production_batch_id);
            CREATE INDEX ix_batch_allocations_derived_order ON batch_allocations (derived_order_key)
                WHERE derived_order_key IS NOT NULL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
