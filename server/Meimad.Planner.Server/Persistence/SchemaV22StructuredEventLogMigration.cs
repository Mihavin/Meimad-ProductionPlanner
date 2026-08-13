using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV22StructuredEventLogMigration : IDatabaseMigration
{
    public int Version => 22;
    public string Name => "structured_event_log";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE structured_event_log (
                id TEXT PRIMARY KEY,
                event_type TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                user_id TEXT NOT NULL,
                related_entity_ids_json TEXT NOT NULL,
                reason_code TEXT,
                comment TEXT,
                before_data_json TEXT,
                after_data_json TEXT,
                event_key TEXT
            );
            CREATE INDEX ix_structured_event_log_time ON structured_event_log(occurred_at, id);
            CREATE INDEX ix_structured_event_log_type ON structured_event_log(event_type, occurred_at);
            CREATE UNIQUE INDEX ux_structured_event_log_key ON structured_event_log(event_key) WHERE event_key IS NOT NULL;
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
