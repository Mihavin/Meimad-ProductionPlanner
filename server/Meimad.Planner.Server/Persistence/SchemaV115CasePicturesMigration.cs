using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Case pictures stored on the Server (owner request 2026-10-11): <c>case_pictures</c> holds at most one picture per
/// Case, uploaded from the Case Workspace (a STEP snapshot or a chosen image) or copied from the Case's picture link.
/// The file lives below the release root (<c>case-pictures/&lt;case&gt;/&lt;sha256&gt;.&lt;ext&gt;</c>, so the recovery
/// set backs it up) and wins over the link in <c>cases.preview_reference</c>. Pictures are not planning inputs: the
/// triggers advance the all-input and non-planning revisions only.
///
/// The factory maps its network root <c>\\192.168.0.240\data</c> as drive <c>Z:</c> on the office PCs, so Case links
/// were saved as <c>Z:\customers files\…</c>, which the Server (a service without that drive) cannot open. On that
/// installation the migration adds <c>Z:\</c> as an alias of the root and stores the Case links (working folder,
/// picture, model files) relative to it, exactly as saving the alias under Setup → Network folder does; they are
/// served as <c>\\192.168.0.240\data\…</c>. Other installations are not changed.
/// </summary>
internal sealed class SchemaV115CasePicturesMigration : IDatabaseMigration
{
    internal const string FactoryRoot = @"\\192.168.0.240\data";
    internal const string FactoryDriveAlias = @"Z:\";

    public int Version => 115;
    public string Name => "case_pictures";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE case_pictures (
                case_id TEXT NOT NULL PRIMARY KEY REFERENCES cases (id) ON DELETE CASCADE,
                file_name TEXT NOT NULL CHECK (length(file_name) BETWEEN 1 AND 255),
                media_type TEXT NOT NULL CHECK (media_type IN ('image/png', 'image/jpeg', 'image/bmp', 'image/gif')),
                file_size INTEGER NOT NULL CHECK (file_size > 0),
                file_hash TEXT NOT NULL CHECK (length(file_hash) = 64 AND file_hash NOT GLOB '*[^0-9a-f]*'),
                stored_relative_path TEXT NOT NULL,
                source_reference TEXT NULL,
                created_at TEXT NOT NULL,
                created_by TEXT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(token);
        foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            command.CommandText = $"""
                CREATE TRIGGER projection_revision_case_pictures_{action.ToLowerInvariant()}
                AFTER {action} ON case_pictures
                BEGIN UPDATE projection_revision
                      SET revision = revision + 1, non_planning_revision = non_planning_revision + 1
                      WHERE id = 1; END;
                """;
            await command.ExecuteNonQueryAsync(token);
        }

        await AddFactoryDriveAliasAsync(connection, transaction, token);
    }

    private static async Task AddFactoryDriveAliasAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        var settings = await SqliteNetworkFolderSettings.ReadAsync(connection, transaction, token);
        if (!settings.IsConfigured
            || !string.Equals(settings.RootPath!.Trim().TrimEnd('\\'), FactoryRoot, StringComparison.OrdinalIgnoreCase)
            || settings.Aliases.Any(alias => alias.Trim().TrimEnd('\\').Equals("Z:", StringComparison.OrdinalIgnoreCase)))
            return;

        var aliases = settings.Aliases.Append(FactoryDriveAlias).ToArray();
        var updated = settings with { Aliases = aliases };
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE network_folder_settings SET aliases_json = $aliases, version = version + 1 WHERE id = 1;";
        command.Parameters.AddWithValue("$aliases", JsonSerializer.Serialize(aliases));
        await command.ExecuteNonQueryAsync(token);

        foreach (var (table, key, column) in new[]
        {
            ("cases", "id", "working_folder_path"),
            ("cases", "id", "preview_reference"),
            ("case_model_files", "id", "file_path")
        })
        {
            var rows = new List<(string Id, string Value)>();
            command.Parameters.Clear();
            command.CommandText = $"SELECT {key}, {column} FROM {table} WHERE {column} LIKE 'Z:%';";
            await using (var reader = await command.ExecuteReaderAsync(token))
            {
                while (await reader.ReadAsync(token)) rows.Add((reader.GetString(0), reader.GetString(1)));
            }
            command.CommandText = $"UPDATE {table} SET {column} = $value WHERE {key} = $id;";
            var value = command.Parameters.Add("$value", SqliteType.Text);
            var id = command.Parameters.Add("$id", SqliteType.Text);
            foreach (var row in rows)
            {
                var stored = updated.ToStored(row.Value);
                if (stored is null || stored == row.Value) continue;
                value.Value = stored;
                id.Value = row.Id;
                await command.ExecuteNonQueryAsync(token);
            }
        }
    }
}
