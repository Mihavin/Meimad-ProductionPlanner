using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV103OperationalHealthMigration : IDatabaseMigration
{
    public int Version => 103;
    public string Name => "operational_health_import_success";
    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE kitaron_sync_state ADD COLUMN last_successful_at TEXT;
            UPDATE kitaron_sync_state SET last_successful_at=last_completed_at WHERE sync_status='succeeded';
            CREATE TRIGGER kitaron_sync_preserve_last_success
            AFTER UPDATE OF sync_status,last_completed_at ON kitaron_sync_state
            WHEN NEW.sync_status='succeeded' AND NEW.last_completed_at IS NOT NULL
            BEGIN UPDATE kitaron_sync_state SET last_successful_at=NEW.last_completed_at WHERE id=NEW.id; END;
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
