using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV67KitaronHistoricalOrdersMigration : IDatabaseMigration
{
    public int Version => 67;

    public string Name => "Kitaron current demand and retained historical Orders";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE orders ADD COLUMN kitaron_history_only INTEGER NOT NULL DEFAULT 0
                CHECK (kitaron_history_only IN (0, 1));

            CREATE INDEX ix_batch_allocations_order_id
            ON batch_allocations(order_id) WHERE order_id IS NOT NULL;

            UPDATE orders
            SET kitaron_history_only = 1
            WHERE EXISTS (
                    SELECT 1
                    FROM kitaron_sync_links case_link
                    WHERE case_link.source_entity = 'case'
                      AND case_link.target_id = orders.case_id)
              AND NOT EXISTS (
                    SELECT 1
                    FROM kitaron_sync_links order_link
                    WHERE order_link.source_entity = 'order'
                      AND order_link.target_id = orders.id)
              AND EXISTS (
                    SELECT 1
                    FROM batch_allocations allocation
                    JOIN batch_operations operation
                      ON operation.production_batch_id = allocation.production_batch_id
                    WHERE (
                            allocation.order_id = orders.id
                            OR (allocation.allocation_type = 'derived_order'
                                AND instr(
                                    allocation.derived_order_key,
                                    'derived:' || orders.id || ':') = 1))
                      AND (
                            EXISTS (
                                SELECT 1
                                FROM production_runs legacy_run
                                WHERE legacy_run.legacy_batch_operation_id = operation.id
                                  AND legacy_run.structure_locked_at IS NOT NULL)
                            OR EXISTS (
                                SELECT 1
                                FROM production_run_outputs output
                                JOIN production_run_programs program
                                  ON program.id = output.production_run_program_id
                                JOIN production_runs output_run
                                  ON output_run.id = program.production_run_id
                                WHERE output.batch_operation_id = operation.id
                                  AND output_run.structure_locked_at IS NOT NULL)));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
