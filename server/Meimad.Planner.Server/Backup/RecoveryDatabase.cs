using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Backup;

internal sealed record RecoveryReference(string Path, long? Length, string Hash);
internal static class RecoveryDatabase
{
    internal static SqliteConnection Open(string path) => new(new SqliteConnectionStringBuilder
    { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());

    internal static async Task<(int Schema, IReadOnlyList<RecoveryReference> Files, IReadOnlyList<RecoveryDependency> Folders)>
        ReadAsync(string path, CancellationToken token)
    {
        await using var sql = Open(path);
        await sql.OpenAsync(token);
        await using var command = sql.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var schema = Convert.ToInt32(await command.ExecuteScalarAsync(token));
        command.CommandText = """
            SELECT 'gcode/' || stored_relative_path,file_size,file_hash FROM gcode_releases
            UNION ALL SELECT 'gcode/' || stored_relative_path,file_size,file_hash FROM tool_table_releases
            UNION ALL SELECT 'gcode/' || stored_relative_path,file_size,file_hash FROM gcode_release_subprograms
            UNION ALL SELECT 'production/' || stored_relative_path,file_size,file_hash FROM production_package_artifacts
            UNION ALL SELECT 'production/' || manifest_relative_path,NULL,manifest_hash FROM production_packages
            UNION ALL SELECT 'eink/' || storage_relative_path,byte_length,sha256 FROM eink_package_files;
            """;
        var files = new Dictionary<string, RecoveryReference>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                var item = new RecoveryReference(reader.GetString(0).Replace('\\', '/'), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetString(2));
                if (files.TryGetValue(item.Path, out var prior))
                {
                    if (!string.Equals(prior.Hash, item.Hash, StringComparison.OrdinalIgnoreCase)
                        || (prior.Length is not null && item.Length is not null && prior.Length != item.Length))
                        throw new InvalidDataException("Snapshot contains conflicting artifact references.");
                    if (prior.Length is not null) continue;
                }
                files[item.Path] = item;
            }
        command.CommandText = "SELECT id,working_folder_path FROM cases WHERE length(trim(working_folder_path))>0 ORDER BY id";
        var folders = new List<RecoveryDependency>();
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) folders.Add(new(reader.GetString(0), reader.GetString(1)));
        return (schema, files.Values.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray(), folders);
    }

    internal static async Task VerifySecretsAsync(string path, IDataProtectionProvider provider, CancellationToken token)
    {
        await using var sql = Open(path);
        await sql.OpenAsync(token);
        foreach (var (query, purpose) in new[]
        {
            ("SELECT protected_password FROM kitaron_connection_settings WHERE protected_password IS NOT NULL AND protected_password<>''",
                "Meimad.Planner.Kitaron.SqlPassword.v1"),
            ("SELECT smtp_password_protected FROM report_email_settings WHERE smtp_password_protected IS NOT NULL AND smtp_password_protected<>''",
                "Meimad.Planner.ReportEmail.SmtpPassword.v1")
        })
        {
            await using var command = sql.CreateCommand();
            command.CommandText = query;
            await using var reader = await command.ExecuteReaderAsync(token);
            var protector = provider.CreateProtector(purpose);
            while (await reader.ReadAsync(token)) _ = protector.Unprotect(reader.GetString(0));
        }
    }
}
