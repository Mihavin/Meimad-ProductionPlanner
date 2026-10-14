using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Indexes Cimatron files (owner request 2026-10-14): native documents (.elt) and Meimad exporter packages as their
/// attributes and summary (extraction <c>CIMATRON</c>), and Cimatron tool sheets and other single-file web pages
/// (.mht/.mhtml, extraction <c>MHT</c>). Both kinds join the check of the indexed and staged documents and are cached by
/// file hash like PDF text. As in v112, the three tables are rebuilt because the extraction check is part of each table
/// definition; columns, rows, indexes and triggers stay as they are.
/// </summary>
internal sealed class SchemaV121LocalCorpusCimatronMigration : IDatabaseMigration
{
    public int Version => 121;
    public string Name => "local_corpus_cimatron_extraction";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        foreach (var table in new[] { "local_ai_documents", "local_ai_documents_staging", "local_ai_extraction_cache" })
            await RebuildAsync(connection, transaction, table, token);
    }

    private static async Task RebuildAsync(SqliteConnection connection, SqliteTransaction transaction, string table, CancellationToken token)
    {
        var tableSql = string.Empty;
        var dependents = new List<string>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT type, sql FROM sqlite_master WHERE tbl_name = $table AND sql IS NOT NULL
                ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'index' THEN 1 ELSE 2 END, name;
                """;
            read.Parameters.AddWithValue("$table", table);
            await using var reader = await read.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (reader.GetString(0) == "table") tableSql = reader.GetString(1);
                else dependents.Add(reader.GetString(1));
            }
        }

        const string lastKind = "'RTF'";
        var position = tableSql.IndexOf(lastKind, StringComparison.Ordinal);
        if (position < 0 || tableSql.IndexOf(lastKind, position + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"Table {table} does not have exactly one extraction check to extend.");
        var rebuilt = tableSql.Insert(position + lastKind.Length, ",'CIMATRON','MHT'");
        rebuilt = Regex.Replace(rebuilt, $"^CREATE TABLE\\s+\"?{table}\"?", $"CREATE TABLE {table}_v121", RegexOptions.CultureInvariant);

        await using (var rebuild = connection.CreateCommand())
        {
            rebuild.Transaction = transaction;
            rebuild.CommandText = rebuilt + ";\n" + $"""
                INSERT INTO {table}_v121 SELECT * FROM {table};
                DROP TABLE {table};
                PRAGMA legacy_alter_table = ON;
                ALTER TABLE {table}_v121 RENAME TO {table};
                PRAGMA legacy_alter_table = OFF;
                """;
            await rebuild.ExecuteNonQueryAsync(token);
        }

        foreach (var sql in dependents)
        {
            await using var recreate = connection.CreateCommand();
            recreate.Transaction = transaction;
            recreate.CommandText = sql + ";";
            await recreate.ExecuteNonQueryAsync(token);
        }
    }
}
