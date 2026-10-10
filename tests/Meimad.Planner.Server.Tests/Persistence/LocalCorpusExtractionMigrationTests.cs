using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class LocalCorpusExtractionMigrationTests
{
    [Fact]
    public async Task Version_112_keeps_indexed_documents_and_triggers_and_admits_spreadsheet_step_and_cmm_extraction()
    {
        await using var fixture = TemporaryDatabase.CreateUnmigrated();
        var migrator = new DatabaseMigrator(fixture.Database, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync(111);
        var hash = new string('a', 64);
        await using (var v111 = await fixture.Database.OpenConnectionAsync())
        {
            await ExecuteAsync(v111, $"""
                INSERT INTO local_ai_documents(id,root_id,relative_path,content_hash,content,indexed_at,configuration_version,search_text,extraction)
                VALUES('doc-1','root-1','drawing.pdf','{hash}','PARTS LIST','2026-10-09T08:00:00Z',3,'PARTS LIST','PDF_TEXT');
                INSERT INTO local_ai_documents_staging(job_id,id,root_id,relative_path,content_hash,content,indexed_at,configuration_version,search_text,extraction)
                VALUES('job-1','doc-1','root-1','drawing.pdf','{hash}','PARTS LIST','2026-10-09T08:00:00Z',3,'PARTS LIST','PDF_TEXT');
                INSERT INTO local_ai_extraction_cache(content_hash,extraction,content,extracted_at) VALUES('{hash}','PDF_TEXT','PARTS LIST','2026-10-09T08:00:00Z');
                """);
            await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(v111,
                "UPDATE local_ai_documents SET extraction='STEP' WHERE id='doc-1';"));
        }
        SqliteConnection.ClearAllPools();

        await migrator.MigrateAsync();

        await using var v112 = await fixture.Database.OpenConnectionAsync();
        Assert.Equal("PDF_TEXT|PARTS LIST|3", await ScalarAsync(v112,
            "SELECT extraction||'|'||content||'|'||configuration_version FROM local_ai_documents WHERE id='doc-1';"));
        Assert.Equal(1L, await ScalarAsync(v112, "SELECT COUNT(*) FROM local_ai_documents_staging WHERE job_id='job-1';"));
        Assert.Equal(1L, await ScalarAsync(v112, "SELECT COUNT(*) FROM local_ai_extraction_cache;"));
        Assert.Equal(1L, await ScalarAsync(v112,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_local_ai_documents_staging_job';"));

        // Each table's projection triggers came back with it.
        var before = (long)(await ScalarAsync(v112, "SELECT non_planning_revision FROM projection_revision WHERE id=1;"))!;
        foreach (var kind in new[] { "CSV", "XLSX", "XLS", "STEP", "DMIS", "RTF" })
            await ExecuteAsync(v112, $"UPDATE local_ai_documents SET extraction='{kind}' WHERE id='doc-1'; UPDATE local_ai_documents_staging SET extraction='{kind}' WHERE id='doc-1';");
        foreach (var kind in new[] { "XLSX", "XLS", "STEP", "RTF" })
            await ExecuteAsync(v112, $"UPDATE local_ai_extraction_cache SET extraction='{kind}';");
        Assert.Equal(before + 16, (long)(await ScalarAsync(v112, "SELECT non_planning_revision FROM projection_revision WHERE id=1;"))!);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(v112, "UPDATE local_ai_documents SET extraction='DOCX';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(v112, "UPDATE local_ai_documents_staging SET extraction='DOCX';"));
        // Plain text, CSV and DMIS programs are never cached.
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(v112, "UPDATE local_ai_extraction_cache SET extraction='CSV';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(v112, "UPDATE local_ai_extraction_cache SET extraction='DMIS';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(v112, "UPDATE local_ai_extraction_cache SET extraction='TEXT';"));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
