using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>Stages long-running corpus rebuilds without holding the planning database transaction.</summary>
internal sealed class SchemaV110LocalCorpusStagingMigration : IDatabaseMigration
{
    public int Version => 110;
    public string Name => "local_corpus_rebuild_staging";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE local_ai_documents_staging (
                job_id TEXT NOT NULL,
                id TEXT NOT NULL,
                root_id TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                content TEXT NOT NULL,
                indexed_at TEXT NOT NULL,
                configuration_version INTEGER NOT NULL,
                search_text TEXT NOT NULL,
                PRIMARY KEY(job_id, id),
                UNIQUE(job_id, root_id, relative_path)
            );
            CREATE INDEX ix_local_ai_documents_staging_job ON local_ai_documents_staging(job_id);
            """;
        await command.ExecuteNonQueryAsync(token);
        foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            command.CommandText = $"""
                CREATE TRIGGER projection_revision_local_ai_documents_staging_{action.ToLowerInvariant()}
                AFTER {action} ON local_ai_documents_staging
                BEGIN UPDATE projection_revision SET non_planning_revision=non_planning_revision+1 WHERE id=1; END;
                """;
            await command.ExecuteNonQueryAsync(token);
        }
    }
}
