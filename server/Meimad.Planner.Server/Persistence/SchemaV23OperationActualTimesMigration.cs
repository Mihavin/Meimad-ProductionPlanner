using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV23OperationActualTimesMigration : IDatabaseMigration
{
    public int Version => 23;

    public string Name => "operation_actual_times";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE batch_operations ADD COLUMN actual_start TEXT;
            ALTER TABLE batch_operations ADD COLUMN actual_end TEXT;
            ALTER TABLE batch_operations ADD COLUMN actual_machine_id TEXT
                REFERENCES machines (id) ON DELETE RESTRICT;

            -- Legacy lifecycle timestamps cannot be reconstructed authoritatively.
            -- Keep them NULL instead of fabricating actual history from updated_at.

            CREATE INDEX ix_batch_operations_actual_machine_time
            ON batch_operations (actual_machine_id, actual_start, actual_end);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
