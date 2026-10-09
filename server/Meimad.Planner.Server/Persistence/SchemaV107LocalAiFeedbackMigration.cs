using Microsoft.Data.Sqlite;
namespace Meimad.Planner.Server.Persistence;
internal sealed class SchemaV107LocalAiFeedbackMigration : IDatabaseMigration
{
    public int Version => 107;
    public string Name => "local_ai_pilot_feedback";
    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE local_ai_feedback (
                request_id TEXT PRIMARY KEY REFERENCES local_ai_requests(id),
                useful INTEGER NOT NULL CHECK(useful IN (0,1)), correction TEXT NOT NULL CHECK(length(correction)<=500),
                version INTEGER NOT NULL CHECK(version>0), updated_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(token);
        foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            command.CommandText = $"""
                CREATE TRIGGER projection_revision_local_ai_feedback_{action.ToLowerInvariant()}
                AFTER {action} ON local_ai_feedback BEGIN UPDATE projection_revision SET revision=revision+1 WHERE id=1; END;
                """;
            await command.ExecuteNonQueryAsync(token);
        }
    }
}
