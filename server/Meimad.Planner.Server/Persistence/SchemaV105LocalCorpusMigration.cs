using Microsoft.Data.Sqlite;
namespace Meimad.Planner.Server.Persistence;
internal sealed class SchemaV105LocalCorpusMigration : IDatabaseMigration
{
    public int Version => 105;
    public string Name => "approved_local_document_index";
    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE local_ai_documents (
                id TEXT PRIMARY KEY, root_id TEXT NOT NULL, relative_path TEXT NOT NULL,
                content_hash TEXT NOT NULL, content TEXT NOT NULL, indexed_at TEXT NOT NULL,
                configuration_version INTEGER NOT NULL, search_text TEXT NOT NULL, UNIQUE(root_id,relative_path)
            );
            """;
        await command.ExecuteNonQueryAsync(token);
        foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            command.CommandText = $"""
                CREATE TRIGGER projection_revision_local_ai_documents_{action.ToLowerInvariant()}
                AFTER {action} ON local_ai_documents BEGIN UPDATE projection_revision SET revision=revision+1 WHERE id=1; END;
                """;
            await command.ExecuteNonQueryAsync(token);
        }
    }
}
