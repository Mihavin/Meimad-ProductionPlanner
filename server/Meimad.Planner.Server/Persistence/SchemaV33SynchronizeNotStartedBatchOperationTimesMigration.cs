using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV33SynchronizeNotStartedBatchOperationTimesMigration : IDatabaseMigration
{
    public int Version => 33;
    public string Name => "synchronize_not_started_batch_operation_times";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE batch_operations
            SET setup_seconds = (
                    SELECT case_operations.setup_seconds
                    FROM case_operations
                    WHERE case_operations.id = batch_operations.source_case_operation_id),
                cycle_seconds = (
                    SELECT case_operations.cycle_seconds
                    FROM case_operations
                    WHERE case_operations.id = batch_operations.source_case_operation_id),
                version = version + 1,
                updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE status = 'not_started'
              AND EXISTS (
                    SELECT 1
                    FROM case_operations
                    WHERE case_operations.id = batch_operations.source_case_operation_id)
              AND (
                    setup_seconds IS NOT (
                        SELECT case_operations.setup_seconds
                        FROM case_operations
                        WHERE case_operations.id = batch_operations.source_case_operation_id)
                    OR cycle_seconds IS NOT (
                        SELECT case_operations.cycle_seconds
                        FROM case_operations
                        WHERE case_operations.id = batch_operations.source_case_operation_id));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
