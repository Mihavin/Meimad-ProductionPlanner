using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV101KitaronPushIntentMigration : IDatabaseMigration
{
    public int Version => 101;
    public string Name => "durable_kitaron_push_intent";
    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE kitaron_push_runs ADD COLUMN lifecycle_state TEXT NOT NULL DEFAULT 'Preparing'
                CHECK(lifecycle_state IN ('Preparing','Prepared','Writing','Succeeded','FailedBeforeCommit','OutcomeUnknown','Reconciled'));
            ALTER TABLE kitaron_push_runs ADD COLUMN intent_version INTEGER NOT NULL DEFAULT 1 CHECK(intent_version > 0);
            UPDATE kitaron_push_runs SET lifecycle_state = CASE status
                WHEN 'succeeded' THEN 'Succeeded' ELSE 'OutcomeUnknown' END,
                message = CASE WHEN status <> 'succeeded' THEN
                    'Historical push has no durable intent/commit evidence. Review its outcome before another push. ' || COALESCE(message,'')
                    ELSE message END;
            CREATE TABLE kitaron_push_intents (
                run_id TEXT PRIMARY KEY REFERENCES kitaron_push_runs(id) ON DELETE CASCADE,
                payload_json TEXT NOT NULL CHECK(json_valid(payload_json)),
                prepared_at TEXT NOT NULL
            );
            CREATE TRIGGER kitaron_push_intent_immutable BEFORE UPDATE ON kitaron_push_intents
                BEGIN SELECT RAISE(ABORT, 'Kitaron push intent is immutable'); END;
            CREATE TABLE kitaron_push_reconciliations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL REFERENCES kitaron_push_runs(id) ON DELETE CASCADE,
                actor TEXT NOT NULL, observed_at TEXT NOT NULL,
                evidence_json TEXT NOT NULL CHECK(json_valid(evidence_json))
            );
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
