using Microsoft.Data.Sqlite;
namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV104LocalAiRequestsMigration : IDatabaseMigration
{
    public int Version => 104;
    public string Name => "local_ai_request_metadata";
    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE local_ai_requests (
                id TEXT PRIMARY KEY, owner_id TEXT NOT NULL, intent TEXT NOT NULL,
                status TEXT NOT NULL, created_at TEXT NOT NULL, completed_at TEXT,
                result_code TEXT, plan_revision TEXT, evidence_json TEXT,
                model_id TEXT, model_digest TEXT, runtime_version TEXT,
                input_tokens INTEGER, output_tokens INTEGER, duration_nanoseconds INTEGER
            );
            CREATE INDEX local_ai_requests_owner_time ON local_ai_requests(owner_id,created_at);
            """;
        await command.ExecuteNonQueryAsync(token);
        foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            command.CommandText = $"""
                CREATE TRIGGER projection_revision_local_ai_requests_{action.ToLowerInvariant()}
                AFTER {action} ON local_ai_requests
                BEGIN UPDATE projection_revision SET revision=revision+1 WHERE id=1; END;
                """;
            await command.ExecuteNonQueryAsync(token);
        }
    }
}
