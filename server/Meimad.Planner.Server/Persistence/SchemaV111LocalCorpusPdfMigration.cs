using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Indexes PDF drawings, parts lists and specifications (owner request 2026-10-09). Each indexed document
/// records how its text was obtained (TEXT, PDF_TEXT, PDF_OCR or PDF_MIXED), and text extracted from a PDF
/// is cached by the file's SHA-256 so a rebuild never recognizes an unchanged PDF again.
/// </summary>
internal sealed class SchemaV111LocalCorpusPdfMigration : IDatabaseMigration
{
    public int Version => 111;
    public string Name => "local_corpus_pdf_extraction";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE local_ai_documents ADD COLUMN extraction TEXT NOT NULL DEFAULT 'TEXT'
                CHECK (extraction IN ('TEXT','PDF_TEXT','PDF_OCR','PDF_MIXED'));
            ALTER TABLE local_ai_documents_staging ADD COLUMN extraction TEXT NOT NULL DEFAULT 'TEXT'
                CHECK (extraction IN ('TEXT','PDF_TEXT','PDF_OCR','PDF_MIXED'));
            CREATE TABLE local_ai_extraction_cache (
                content_hash TEXT PRIMARY KEY CHECK (length(content_hash) = 64),
                extraction TEXT NOT NULL CHECK (extraction IN ('PDF_TEXT','PDF_OCR','PDF_MIXED')),
                content TEXT NOT NULL,
                extracted_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(token);
        foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            command.CommandText = $"""
                CREATE TRIGGER projection_revision_local_ai_extraction_cache_{action.ToLowerInvariant()}
                AFTER {action} ON local_ai_extraction_cache
                BEGIN UPDATE projection_revision SET non_planning_revision=non_planning_revision+1 WHERE id=1; END;
                """;
            await command.ExecuteNonQueryAsync(token);
        }
    }
}
