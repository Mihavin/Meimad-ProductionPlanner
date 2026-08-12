using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV9BatchLifecycleMigration : IDatabaseMigration
{
    public int Version => 9;

    public string Name => "batch_lifecycle_and_dependency_snapshots";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE batch_operations
            ADD COLUMN dependency_type TEXT NOT NULL DEFAULT 'independent'
                CHECK (dependency_type IN ('sequential', 'parallel_capable', 'independent', 'locked_simultaneous'));

            ALTER TABLE batch_operations
            ADD COLUMN predecessor_source_case_operation_id TEXT;

            ALTER TABLE batch_operations
            ADD COLUMN simultaneous_group_key TEXT;

            UPDATE batch_operations
            SET dependency_type = COALESCE(
                    (SELECT case_operations.dependency_type
                     FROM case_operations
                     WHERE case_operations.id = batch_operations.source_case_operation_id),
                    'independent'),
                predecessor_source_case_operation_id =
                    (SELECT case_operations.predecessor_case_operation_id
                     FROM case_operations
                     WHERE case_operations.id = batch_operations.source_case_operation_id),
                simultaneous_group_key =
                    (SELECT case_operations.simultaneous_group_key
                     FROM case_operations
                     WHERE case_operations.id = batch_operations.source_case_operation_id);

            UPDATE production_batches
            SET status = CASE
                    WHEN EXISTS (
                        SELECT 1
                        FROM batch_operations
                        WHERE batch_operations.production_batch_id = production_batches.id)
                     AND NOT EXISTS (
                        SELECT 1
                        FROM batch_operations
                        WHERE batch_operations.production_batch_id = production_batches.id
                          AND batch_operations.status <> 'completed')
                        THEN 'complete'
                    WHEN EXISTS (
                        SELECT 1
                        FROM batch_operations
                        WHERE batch_operations.production_batch_id = production_batches.id
                          AND batch_operations.status <> 'not_started')
                        THEN 'in_production'
                    ELSE 'waiting'
                END;

            CREATE INDEX ix_batch_operations_predecessor_snapshot
            ON batch_operations (predecessor_source_case_operation_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
