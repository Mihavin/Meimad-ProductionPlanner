using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Api.NetworkFolder;

/// <summary>
/// The shared network folder that Case links are stored relative to. Every client reads it; the
/// Single Edit Mode holder saves it. Saving converts the stored links once: paths under the root
/// or one of its drive-letter aliases become relative, and Case folders the Kitaron
/// synchronization generated on the Server move to the Kitaron Case folder on the share.
/// </summary>
internal static class NetworkFolderEndpoints
{
    internal static void MapNetworkFolderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/network-folder", GetAsync);
        endpoints.MapPut("/api/v1/network-folder", UpdateAsync);
    }

    private static async Task<IResult> GetAsync(SqliteDatabase database, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return Results.Ok(NetworkFolderResponse.From(
            await SqliteNetworkFolderSettings.ReadAsync(connection, null, cancellationToken), null));
    }

    private static async Task<IResult> UpdateAsync(
        NetworkFolderRequest request,
        HttpContext context,
        SqliteDatabase database,
        DatabaseOptions databaseOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        var root = string.IsNullOrWhiteSpace(request.RootPath) ? null : NetworkFolderSettings.Normalize(request.RootPath.Trim());
        if (root is not null && (!Path.IsPathRooted(root) || root.Length > 1000))
            return Invalid(context, "rootPath", "The network folder must be a full path, preferably a UNC path such as \\\\server\\share\\folder.");
        var aliases = (request.Aliases ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => NetworkFolderSettings.Normalize(item.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (aliases.Any(item => !Path.IsPathRooted(item)))
            return Invalid(context, "aliases", "Every alias must be a full path such as J:\\customers files.");
        var kitaronFolder = (request.KitaronCaseFolder ?? "Meimad Cases").Trim().Trim('\\', '/');
        if (kitaronFolder.Length is < 1 or > 200 || Path.IsPathRooted(kitaronFolder) || kitaronFolder.Contains("..", StringComparison.Ordinal))
            return Invalid(context, "kitaronCaseFolder", "The Kitaron Case folder must be a relative folder name under the network folder.");

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await EnsureEditAuthorityAsync(connection, transaction, authority!, cancellationToken);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(409, exception.Code, exception.Message, context);
        }
        var now = timeProvider.GetUtcNow();
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE network_folder_settings
                SET root_path = $root, aliases_json = $aliases, kitaron_case_folder = $kitaron,
                    version = version + 1, updated_at = $now
                WHERE id = 1 AND version = $version;
                """;
            update.Parameters.AddWithValue("$root", (object?)root ?? DBNull.Value);
            update.Parameters.AddWithValue("$aliases", JsonSerializer.Serialize(aliases));
            update.Parameters.AddWithValue("$kitaron", kitaronFolder);
            update.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$version", request.ExpectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                return PlanningHttpSupport.Error(412, "network_folder_stale", "The network folder settings changed; reload and try again.", context);
        }
        var settings = await SqliteNetworkFolderSettings.ReadAsync(connection, transaction, cancellationToken);
        var generatedRoot = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(databaseOptions.DatabasePath) ?? ".", "KitaronCases"));
        var converted = settings.IsConfigured
            ? await ConvertAsync(connection, transaction, settings, generatedRoot, now, cancellationToken)
            : 0;
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(NetworkFolderResponse.From(settings, converted));
    }

    /// <summary>Rewrites stored Case links to the relative form; returns the number of links changed.</summary>
    internal static async Task<int> ConvertAsync(
        SqliteConnection connection, SqliteTransaction transaction, NetworkFolderSettings settings,
        string generatedRoot, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var changed = 0;
        var generated = NetworkFolderSettings.Normalize(generatedRoot);
        var rows = new List<(string Table, string Id, string Column, string Value)>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT 'cases', id, 'working_folder_path', working_folder_path FROM cases WHERE working_folder_path IS NOT NULL
                UNION ALL
                SELECT 'cases', id, 'preview_reference', preview_reference FROM cases WHERE preview_reference IS NOT NULL
                UNION ALL
                SELECT 'case_model_files', id, 'file_path', file_path FROM case_model_files;
                """;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        foreach (var (table, id, column, value) in rows)
        {
            var normalized = NetworkFolderSettings.Normalize(value.Trim());
            string? next;
            if (column == "working_folder_path"
                && normalized.StartsWith(generated + "\\", StringComparison.OrdinalIgnoreCase))
                next = settings.KitaronCaseRelativeFolder(normalized[(generated.Length + 1)..]);
            else
                next = settings.ToStored(value);
            if (next is null || string.Equals(next, value, StringComparison.Ordinal)) continue;
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"UPDATE {table} SET {column} = $value, updated_at = $now WHERE id = $id;";
            update.Parameters.AddWithValue("$value", next);
            update.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", id);
            changed += await update.ExecuteNonQueryAsync(cancellationToken);
        }
        return changed;
    }

    private static async Task EnsureEditAuthorityAsync(
        SqliteConnection connection, SqliteTransaction transaction, EditAuthority authority, CancellationToken cancellationToken)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(connection, transaction, DateTimeOffset.UtcNow, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
            throw new EditModeMutationException("edit_mode_required", "No Windows client currently holds Edit Mode.");
        if (!string.Equals(reader.GetString(0), authority.ClientId, StringComparison.Ordinal)
            || reader.GetInt64(1) != authority.Generation)
            throw new EditModeMutationException("edit_generation_stale", "This client does not hold the active Edit Mode generation.");
    }

    private static IResult Invalid(HttpContext context, string field, string message) =>
        PlanningHttpSupport.Error(422, "validation_failed", message, context, [new { field }]);
}

internal sealed record NetworkFolderRequest(
    string? RootPath,
    IReadOnlyList<string>? Aliases,
    string? KitaronCaseFolder,
    int ExpectedVersion);

internal sealed record NetworkFolderResponse(
    string? RootPath,
    IReadOnlyList<string> Aliases,
    string KitaronCaseFolder,
    int Version,
    DateTimeOffset UpdatedAt,
    int? ConvertedLinks)
{
    internal static NetworkFolderResponse From(NetworkFolderSettings value, int? converted) => new(
        value.RootPath, value.Aliases, value.KitaronCaseFolder, value.Version, value.UpdatedAt, converted);
}
