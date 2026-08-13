using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV15MachineAssignmentOverridesMigration : IDatabaseMigration
{
    public int Version => 15;

    public string Name => "machine_assignment_overrides";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE machine_assignment_overrides (
                id TEXT PRIMARY KEY,
                batch_operation_id TEXT NOT NULL,
                machine_id TEXT NOT NULL,
                required_machine_type TEXT NOT NULL,
                selected_machine_type TEXT NOT NULL,
                reason TEXT NOT NULL CHECK (length(trim(reason)) > 0),
                confirmed_by_client_id TEXT NOT NULL,
                confirmed_by_user_id TEXT NOT NULL,
                confirmed_at TEXT NOT NULL,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX ix_machine_assignment_overrides_operation
            ON machine_assignment_overrides (batch_operation_id, confirmed_at);

            CREATE INDEX ix_machine_assignment_overrides_machine
            ON machine_assignment_overrides (machine_id, confirmed_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
