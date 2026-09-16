using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Remembers Kitaron route Operations that a planner deliberately deleted in Meimad Planner.
/// Without this, the next synchronization would treat the missing target as a stale link and
/// recreate the Operation. A suppressed source key is skipped by the sync until an Operation
/// with the same number exists again on the Case, at which point Kitaron re-adopts it.
/// </summary>
internal sealed class SchemaV73KitaronSuppressedOperationsMigration : IDatabaseMigration
{
    public int Version => 73;
    public string Name => "kitaron_suppressed_operations";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE kitaron_suppressed_operations (
                source_key TEXT NOT NULL PRIMARY KEY,
                case_id TEXT NOT NULL,
                operation_number INTEGER NOT NULL,
                name TEXT NOT NULL,
                suppressed_at TEXT NOT NULL
            );
            CREATE INDEX ix_kitaron_suppressed_operations_case
                ON kitaron_suppressed_operations (case_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
