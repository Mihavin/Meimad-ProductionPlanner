using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// The shared network folder that every Case link (working folder, preview picture, model files,
/// and through the working folder the saved G-code) is stored relative to. A path under the root,
/// or under one of its drive-letter aliases, is stored relative; the API returns it resolved
/// against the root (a UNC path), so any PC opens the same file whatever drive letters it maps.
/// Paths outside the root are kept as entered.
/// </summary>
internal sealed record NetworkFolderSettings(
    string? RootPath,
    IReadOnlyList<string> Aliases,
    string KitaronCaseFolder,
    int Version,
    DateTimeOffset UpdatedAt)
{
    internal static readonly NetworkFolderSettings None = new(null, [], "Meimad Cases", 1, DateTimeOffset.UnixEpoch);

    internal bool IsConfigured => !string.IsNullOrWhiteSpace(RootPath);

    /// <summary>The form to store: relative to the root when the path lies under it or an alias.</summary>
    internal string? ToStored(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsConfigured) return path;
        var value = Normalize(path.Trim());
        if (!Path.IsPathRooted(value)) return value;
        foreach (var prefix in Prefixes())
        {
            if (value.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                return ".";
            if (value.Length > prefix.Length
                && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && value[prefix.Length] == '\\')
            {
                var relative = value[(prefix.Length + 1)..];
                return relative.Length == 0 ? "." : relative;
            }
        }
        return value;
    }

    /// <summary>The form to hand out: a stored relative path resolved against the root.</summary>
    internal string? ToAbsolute(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored) || !IsConfigured) return stored;
        var value = Normalize(stored.Trim());
        if (Path.IsPathRooted(value)) return value;
        var root = Normalize(RootPath!.Trim());
        return value == "." ? root : root + "\\" + value;
    }

    /// <summary>The relative working folder a Kitaron-created Case gets under the root.</summary>
    internal string KitaronCaseRelativeFolder(string safePartFolder) =>
        Normalize(KitaronCaseFolder.Trim().Trim('\\', '/')) + "\\" + safePartFolder;

    private IEnumerable<string> Prefixes() =>
        new[] { RootPath! }.Concat(Aliases)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            // A drive-root alias such as J:\ compares as "J:", so J:\customers files matches it.
            .Select(item => Normalize(item.Trim()).TrimEnd('\\'))
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(item => item.Length);

    internal static string Normalize(string path)
    {
        var value = path.Replace('/', '\\');
        var unc = value.StartsWith(@"\\", StringComparison.Ordinal);
        while (value.Contains(@"\\", StringComparison.Ordinal))
            value = value.Replace(@"\\", @"\", StringComparison.Ordinal);
        if (unc) value = "\\" + value;
        return value.Length > 3 ? value.TrimEnd('\\') : value;
    }
}

internal static class SqliteNetworkFolderSettings
{
    /// <summary>
    /// SQL expression that resolves a stored Case link column against the network folder, for
    /// readers that select the column directly. Rooted paths (drive or UNC) pass through.
    /// </summary>
    internal static string ResolveSql(string column) => $"""
        (CASE
            WHEN {column} IS NULL
              OR (SELECT root_path FROM network_folder_settings WHERE id = 1) IS NULL
              OR substr({column}, 1, 2) = '\\' OR substr({column}, 2, 1) = ':'
            THEN {column}
            WHEN {column} = '.'
            THEN rtrim((SELECT root_path FROM network_folder_settings WHERE id = 1), '\')
            ELSE rtrim((SELECT root_path FROM network_folder_settings WHERE id = 1), '\') || '\' || {column}
        END)
        """;

    internal static async Task<NetworkFolderSettings> ReadAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT root_path, aliases_json, kitaron_case_folder, version, updated_at
            FROM network_folder_settings WHERE id = 1;
            """;
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return NetworkFolderSettings.None;
            return new NetworkFolderSettings(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? [],
                reader.GetString(2),
                reader.GetInt32(3),
                DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (SqliteException)
        {
            // Before schema v83 the table does not exist; paths are then used as stored.
            return NetworkFolderSettings.None;
        }
    }
}
