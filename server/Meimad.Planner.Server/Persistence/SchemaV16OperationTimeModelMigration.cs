using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV16OperationTimeModelMigration : IDatabaseMigration
{
    public int Version => 16;

    public string Name => "operation_time_model";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE case_operations ADD COLUMN qa_seconds INTEGER NOT NULL DEFAULT 0 CHECK (qa_seconds >= 0);
            ALTER TABLE case_operations ADD COLUMN load_unload_seconds INTEGER NOT NULL DEFAULT 0 CHECK (load_unload_seconds >= 0);
            ALTER TABLE case_operations ADD COLUMN load_unload_requires_worker INTEGER NOT NULL DEFAULT 0 CHECK (load_unload_requires_worker IN (0, 1));
            ALTER TABLE case_operations ADD COLUMN automatic_loading INTEGER NOT NULL DEFAULT 0 CHECK (automatic_loading IN (0, 1));
            ALTER TABLE case_operations ADD COLUMN load_unload_every_n_parts INTEGER CHECK (load_unload_every_n_parts IS NULL OR load_unload_every_n_parts > 0);
            ALTER TABLE case_operations ADD COLUMN day_shift_only INTEGER NOT NULL DEFAULT 0 CHECK (day_shift_only IN (0, 1));

            ALTER TABLE batch_operations ADD COLUMN qa_seconds INTEGER NOT NULL DEFAULT 0 CHECK (qa_seconds >= 0);
            ALTER TABLE batch_operations ADD COLUMN load_unload_seconds INTEGER NOT NULL DEFAULT 0 CHECK (load_unload_seconds >= 0);
            ALTER TABLE batch_operations ADD COLUMN load_unload_requires_worker INTEGER NOT NULL DEFAULT 0 CHECK (load_unload_requires_worker IN (0, 1));
            ALTER TABLE batch_operations ADD COLUMN automatic_loading INTEGER NOT NULL DEFAULT 0 CHECK (automatic_loading IN (0, 1));
            ALTER TABLE batch_operations ADD COLUMN load_unload_every_n_parts INTEGER CHECK (load_unload_every_n_parts IS NULL OR load_unload_every_n_parts > 0);
            ALTER TABLE batch_operations ADD COLUMN day_shift_only INTEGER NOT NULL DEFAULT 0 CHECK (day_shift_only IN (0, 1));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
