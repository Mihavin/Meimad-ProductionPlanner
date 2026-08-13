using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV10SetupMachineTypesAndOrderLifecycleMigration : IDatabaseMigration
{
    public int Version => 10;

    public string Name => "setup_machine_types_and_order_lifecycle";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE machine_types (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                capabilities_json TEXT NOT NULL DEFAULT '[]',
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );

            CREATE UNIQUE INDEX ux_machine_types_name_nocase
            ON machine_types (name COLLATE NOCASE);

            INSERT INTO machine_types (id, name)
            SELECT lower(hex(randomblob(16))), machine_type
            FROM machines
            GROUP BY machine_type COLLATE NOCASE;

            ALTER TABLE machines
            ADD COLUMN machine_type_id TEXT REFERENCES machine_types (id) ON DELETE RESTRICT;

            UPDATE machines
            SET machine_type_id = (
                SELECT machine_types.id
                FROM machine_types
                WHERE machine_types.name = machines.machine_type COLLATE NOCASE
                LIMIT 1);

            CREATE INDEX ix_machines_machine_type_id
            ON machines (machine_type_id);

            CREATE TABLE setup_calendar_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                working_calendar_id TEXT,
                legacy_fallback_enabled INTEGER NOT NULL DEFAULT 1 CHECK (legacy_fallback_enabled IN (0, 1)),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (working_calendar_id) REFERENCES working_calendars (id) ON DELETE RESTRICT
            );

            INSERT INTO setup_calendar_settings (id) VALUES (1);

            UPDATE orders
            SET status = CASE
                    WHEN status = 'cancelled' THEN 'cancelled'
                    WHEN EXISTS (
                        SELECT 1
                        FROM batch_allocations changed_allocation
                        WHERE changed_allocation.order_id = orders.id)
                     AND (
                        SELECT COALESCE(SUM(allocation.quantity), 0)
                        FROM batch_allocations allocation
                        WHERE allocation.order_id = orders.id) >= orders.quantity
                     AND NOT EXISTS (
                        SELECT 1
                        FROM batch_allocations allocation
                        WHERE allocation.order_id = orders.id
                          AND (
                            NOT EXISTS (
                                SELECT 1
                                FROM batch_operations operation
                                WHERE operation.production_batch_id = allocation.production_batch_id)
                            OR EXISTS (
                                SELECT 1
                                FROM batch_operations operation
                                WHERE operation.production_batch_id = allocation.production_batch_id
                                  AND operation.status <> 'completed')))
                        THEN 'complete'
                    WHEN EXISTS (
                        SELECT 1
                        FROM batch_allocations allocation
                        JOIN batch_operations operation
                          ON operation.production_batch_id = allocation.production_batch_id
                        WHERE allocation.order_id = orders.id
                          AND operation.status <> 'not_started')
                        THEN 'in_production'
                    ELSE 'active'
                END,
                version = version + 1,
                updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE EXISTS (
                SELECT 1
                FROM batch_allocations allocation
                WHERE allocation.order_id = orders.id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
