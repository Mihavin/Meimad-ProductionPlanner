using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Indexes spreadsheets, STEP model metadata and CMM inspection files (owner decisions 2026-10-09/10): CSV
/// files as text, Excel workbooks (.xlsx/.xlsm, and Excel 97-2003 .xls) as sheet rows, STEP files (.stp/.step)
/// as their header, product, assembly, unit and property records, DMIS inspection programs (.dmi) as text and
/// RTF documents (.rtf, such as CMM reports) as plain text. The extraction kinds CSV, XLSX, XLS, STEP, DMIS and
/// RTF join the check of the indexed and staged documents; XLSX, XLS, STEP and RTF text is cached by file hash
/// like PDF text. The three tables are rebuilt because the extraction check is part of each table definition;
/// columns, rows and triggers stay as they are.
/// </summary>
internal sealed class SchemaV112LocalCorpusFormatsMigration : IDatabaseMigration
{
    public int Version => 112;
    public string Name => "local_corpus_spreadsheet_step_cmm_extraction";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await RebuildAsync(connection, transaction, "local_ai_documents", "'PDF_MIXED','CSV','XLSX','XLS','STEP','DMIS','RTF'", token);
        await RebuildAsync(connection, transaction, "local_ai_documents_staging", "'PDF_MIXED','CSV','XLSX','XLS','STEP','DMIS','RTF'", token);
        await RebuildAsync(connection, transaction, "local_ai_extraction_cache", "'PDF_MIXED','XLSX','XLS','STEP','RTF'", token);
    }

    private static async Task RebuildAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, string extendedKinds, CancellationToken token)
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

        const string lastKind = "'PDF_MIXED'";
        var position = tableSql.IndexOf(lastKind, StringComparison.Ordinal);
        if (position < 0 || tableSql.IndexOf(lastKind, position + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"Table {table} does not have exactly one extraction check to extend.");
        var rebuilt = tableSql.Remove(position, lastKind.Length).Insert(position, extendedKinds);
        rebuilt = Regex.Replace(rebuilt, $"^CREATE TABLE\\s+\"?{table}\"?", $"CREATE TABLE {table}_v112", RegexOptions.CultureInvariant);

        await using (var rebuild = connection.CreateCommand())
        {
            rebuild.Transaction = transaction;
            rebuild.CommandText = rebuilt + ";\n" + $"""
                INSERT INTO {table}_v112 SELECT * FROM {table};
                DROP TABLE {table};
                PRAGMA legacy_alter_table = ON;
                ALTER TABLE {table}_v112 RENAME TO {table};
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
