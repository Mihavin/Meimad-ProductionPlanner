using System.Globalization;
using Meimad.Planner.NcEngine.MachineDefinitions;
using Meimad.Planner.Server.Application.Machines;
using Meimad.Planner.Server.Domain.Machines;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Stores Machine simulation definitions and asset records (schema v114). Every write runs in one immediate transaction
/// that compares the draft version, checks the definition against the Machine's assets, writes the row (and on publish
/// the Machine's projected columns and cycle estimates) and appends the audit event. Legacy mesh paths are stored in
/// network-folder form, like <c>machines.nc_viewer_models_json</c>, and handed out resolved.
/// </summary>
internal sealed class SqliteMachineSimulationRepository(SqliteDatabase database) : IMachineSimulationRepository
{
    private const string SelectDefinition = """
        SELECT machine_id, draft_json, draft_version, published_json, published_version,
               published_at, published_by, updated_at, updated_by
        FROM machine_simulation_definitions WHERE machine_id = $id;
        """;

    private const string SelectAsset = """
        SELECT id, machine_id, file_name, media_type, file_size, file_hash, stored_relative_path, created_at, created_by
        FROM machine_simulation_assets
        """;

    public async Task<MachineSimulationRecord?> GetAsync(string machineId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var paths = await SqliteNetworkFolderSettings.ReadAsync(connection, transaction, cancellationToken);
        var record = await ReadAsync(connection, transaction, machineId, paths, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    public async Task<IReadOnlyList<(string MachineId, int PublishedVersion, MachineSimulationDefinition Published)>> ListPublishedAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var paths = await SqliteNetworkFolderSettings.ReadAsync(connection, transaction, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT machine_id, published_version, published_json FROM machine_simulation_definitions
            WHERE published_json IS NOT NULL ORDER BY machine_id;
            """;
        var result = new List<(string, int, MachineSimulationDefinition)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                result.Add((reader.GetString(0), reader.GetInt32(1), Resolved(MachineSimulationDefinition.Parse(reader.GetString(2)), paths)));
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<MachineSimulationWriteResult> SaveDraftAsync(
        string machineId,
        MachineSimulationDefinition draft,
        int expectedDraftVersion,
        MachineSimulationCheck check,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var paths = await SqliteNetworkFolderSettings.ReadAsync(connection, transaction, cancellationToken);
        var current = await ReadAsync(connection, transaction, machineId, paths, cancellationToken);
        if (current is null) return new(MachineSimulationOutcome.MachineNotFound, null, []);
        if (current.DraftVersion != expectedDraftVersion) return new(MachineSimulationOutcome.Stale, current, []);
        var checkResult = check(current, await AssetIdsAsync(connection, transaction, machineId, cancellationToken));
        if (checkResult.Refused) return new(MachineSimulationOutcome.Invalid, current, checkResult.Issues);

        var saved = current with { Draft = draft, DraftVersion = current.DraftVersion + 1, UpdatedAt = now, UpdatedBy = actor };
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE machine_simulation_definitions
                SET draft_json = $draft, draft_version = $version, updated_at = $now, updated_by = $actor
                WHERE machine_id = $id AND draft_version = $expected;
                """;
            command.Parameters.AddWithValue("$draft", Stored(draft, paths).ToJson());
            command.Parameters.AddWithValue("$version", saved.DraftVersion);
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$id", machineId);
            command.Parameters.AddWithValue("$expected", expectedDraftVersion);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return new(MachineSimulationOutcome.Stale, current, []);
        }
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction, new(
            "machine_simulation_definition_saved", now, actor,
            new Dictionary<string, string> { ["machineId"] = machineId },
            "planner_entered", null,
            new { draftVersion = current.DraftVersion },
            new { draftVersion = saved.DraftVersion, nodes = draft.Nodes.Count, template = draft.Template }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(MachineSimulationOutcome.Saved, saved, checkResult.Issues);
    }

    public async Task<MachineSimulationWriteResult> PublishAsync(
        string machineId,
        int expectedDraftVersion,
        MachineSimulationCheck check,
        Func<MachineSimulationDefinition, MachineSimulationProjection> projection,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var paths = await SqliteNetworkFolderSettings.ReadAsync(connection, transaction, cancellationToken);
        var current = await ReadAsync(connection, transaction, machineId, paths, cancellationToken);
        if (current is null) return new(MachineSimulationOutcome.MachineNotFound, null, []);
        if (current.DraftVersion != expectedDraftVersion) return new(MachineSimulationOutcome.Stale, current, []);
        var checkResult = check(current, await AssetIdsAsync(connection, transaction, machineId, cancellationToken));
        if (checkResult.Refused) return new(MachineSimulationOutcome.Invalid, current, checkResult.Issues);

        var published = current with
        {
            Published = current.Draft,
            PublishedVersion = current.PublishedVersion + 1,
            PublishedAt = now,
            PublishedBy = actor
        };
        var values = projection(current.Draft);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE machine_simulation_definitions
                SET published_json = draft_json, published_version = $version, published_at = $now, published_by = $actor
                WHERE machine_id = $id AND draft_version = $expected;
                """;
            command.Parameters.AddWithValue("$version", published.PublishedVersion);
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$id", machineId);
            command.Parameters.AddWithValue("$expected", expectedDraftVersion);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return new(MachineSimulationOutcome.Stale, current, []);

            // The Machine's former columns are projections of the published definition (one write path).
            command.Parameters.Clear();
            command.CommandText = """
                UPDATE machines
                SET usable_tool_positions = $usable,
                    rapid_rate_mm_per_min = $rapid,
                    tool_change_time_seconds = $change,
                    machine_time_factor = $factor,
                    nc_viewer_machine = $viewer,
                    nc_viewer_models_json = $models,
                    version = version + 1,
                    updated_at = $now
                WHERE id = $id
                  AND (usable_tool_positions IS NOT $usable
                       OR rapid_rate_mm_per_min IS NOT $rapid
                       OR tool_change_time_seconds IS NOT $change
                       OR machine_time_factor IS NOT $factor
                       OR nc_viewer_machine IS NOT $viewer
                       OR nc_viewer_models_json IS NOT $models);
                """;
            command.Parameters.AddWithValue("$usable", (object?)values.UsableToolPositions ?? DBNull.Value);
            command.Parameters.AddWithValue("$rapid", (object?)values.RapidRateMillimetersPerMinute ?? DBNull.Value);
            command.Parameters.AddWithValue("$change", (object?)values.ToolChangeTimeSeconds ?? DBNull.Value);
            command.Parameters.AddWithValue("$factor", values.MachineTimeFactor);
            command.Parameters.AddWithValue("$viewer", (object?)values.NcViewerMachine ?? DBNull.Value);
            command.Parameters.AddWithValue("$models",
                MachineNcViewerModels.Serialize(MachineNcViewerModels.MapPaths(values.NcViewerModels, paths.ToStored)));
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$id", machineId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                await SqliteNcCycleEstimateStore.RecalculateForMachineAsync(
                    connection, transaction, machineId, now, actor, "machine_simulation_definition_published", cancellationToken);
            }
        }
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction, new(
            "machine_simulation_definition_published", now, actor,
            new Dictionary<string, string> { ["machineId"] = machineId },
            "planner_entered", null,
            new { publishedVersion = current.PublishedVersion },
            new
            {
                publishedVersion = published.PublishedVersion,
                draftVersion = current.DraftVersion,
                values.UsableToolPositions,
                values.RapidRateMillimetersPerMinute,
                values.ToolChangeTimeSeconds,
                values.MachineTimeFactor,
                values.NcViewerMachine
            }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(MachineSimulationOutcome.Published, published, checkResult.Issues);
    }

    public async Task<IReadOnlyList<MachineSimulationAsset>?> ListAssetsAsync(string machineId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        if (!await MachineExistsAsync(connection, transaction, machineId, cancellationToken)) return null;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectAsset + " WHERE machine_id = $id ORDER BY created_at, id;";
        command.Parameters.AddWithValue("$id", machineId);
        var assets = new List<MachineSimulationAsset>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) assets.Add(ReadAsset(reader));
        }
        await transaction.CommitAsync(cancellationToken);
        return assets;
    }

    public async Task<MachineSimulationAsset?> GetAssetAsync(string machineId, string assetId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectAsset + " WHERE machine_id = $machine AND id = $id;";
        command.Parameters.AddWithValue("$machine", machineId);
        command.Parameters.AddWithValue("$id", assetId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAsset(reader) : null;
    }

    public async Task<MachineSimulationAsset?> AddAssetAsync(MachineSimulationAsset asset, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await MachineExistsAsync(connection, transaction, asset.MachineId, cancellationToken)) return null;
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = SelectAsset + " WHERE machine_id = $machine AND file_hash = $hash;";
            existing.Parameters.AddWithValue("$machine", asset.MachineId);
            existing.Parameters.AddWithValue("$hash", asset.Sha256);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) return ReadAsset(reader);
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO machine_simulation_assets (
                    id, machine_id, file_name, media_type, file_size, file_hash, stored_relative_path, created_at, created_by)
                VALUES ($id, $machine, $name, $type, $size, $hash, $path, $now, $actor);
                """;
            command.Parameters.AddWithValue("$id", asset.AssetId);
            command.Parameters.AddWithValue("$machine", asset.MachineId);
            command.Parameters.AddWithValue("$name", asset.FileName);
            command.Parameters.AddWithValue("$type", asset.MediaType);
            command.Parameters.AddWithValue("$size", asset.SizeBytes);
            command.Parameters.AddWithValue("$hash", asset.Sha256);
            command.Parameters.AddWithValue("$path", asset.StoredRelativePath);
            command.Parameters.AddWithValue("$now", Format(asset.CreatedAt));
            command.Parameters.AddWithValue("$actor", (object?)asset.CreatedBy ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction, new(
            "machine_simulation_asset_uploaded", asset.CreatedAt, asset.CreatedBy ?? "system",
            new Dictionary<string, string> { ["machineId"] = asset.MachineId, ["assetId"] = asset.AssetId },
            "planner_entered", null, null,
            new { asset.FileName, asset.MediaType, asset.SizeBytes, asset.Sha256 }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return asset;
    }

    private static async Task<MachineSimulationRecord?> ReadAsync(
        SqliteConnection connection, SqliteTransaction transaction, string machineId,
        NetworkFolderSettings paths, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectDefinition;
        command.Parameters.AddWithValue("$id", machineId);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                return new MachineSimulationRecord(
                    reader.GetString(0),
                    Resolved(MachineSimulationDefinition.Parse(reader.GetString(1)), paths),
                    reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : Resolved(MachineSimulationDefinition.Parse(reader.GetString(3)), paths),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : Parse(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    Parse(reader.GetString(7)),
                    reader.IsDBNull(8) ? null : reader.GetString(8));
            }
        }
        return await CreateMissingAsync(connection, transaction, machineId, paths, cancellationToken);
    }

    /// <summary>
    /// A Machine without a definition row (created by an older path) gets its initial definition now, published as is,
    /// so every Machine has exactly one definition.
    /// </summary>
    private static async Task<MachineSimulationRecord?> CreateMissingAsync(
        SqliteConnection connection, SqliteTransaction transaction, string machineId,
        NetworkFolderSettings paths, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT name, nc_viewer_machine, usable_tool_positions, rapid_rate_mm_per_min,
                   tool_change_time_seconds, machine_time_factor, nc_viewer_models_json, updated_at
            FROM machines WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", machineId);
        MachineSimulationDefinition definition;
        DateTimeOffset at;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            definition = MachineSimulationDefinitions.Initial(
                machineId,
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.GetDouble(5),
                MachineNcViewerModels.Deserialize(reader.GetString(6)));
            at = Parse(reader.GetString(7));
        }
        await InsertInitialAsync(connection, transaction, machineId, definition, at, null, cancellationToken);
        var resolved = Resolved(definition, paths);
        return new MachineSimulationRecord(machineId, resolved, 1, resolved, 1, at, null, at, null);
    }

    /// <summary>Inserts a Machine's first definition (draft = published, version 1); the definition holds stored paths.</summary>
    internal static async Task InsertInitialAsync(
        SqliteConnection connection, SqliteTransaction transaction, string machineId,
        MachineSimulationDefinition storedDefinition, DateTimeOffset at, string? actor, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO machine_simulation_definitions (
                machine_id, draft_json, draft_version, published_json, published_version,
                published_at, published_by, updated_at, updated_by)
            VALUES ($id, $json, 1, $json, 1, $at, $actor, $at, $actor);
            """;
        command.Parameters.AddWithValue("$id", machineId);
        command.Parameters.AddWithValue("$json", storedDefinition.ToJson());
        command.Parameters.AddWithValue("$at", Format(at));
        command.Parameters.AddWithValue("$actor", (object?)actor ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlySet<string>> AssetIdsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string machineId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM machine_simulation_assets WHERE machine_id = $id;";
        command.Parameters.AddWithValue("$id", machineId);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0));
        return ids;
    }

    private static async Task<bool> MachineExistsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string machineId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM machines WHERE id = $id);";
        command.Parameters.AddWithValue("$id", machineId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static MachineSimulationAsset ReadAsset(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt64(4),
        reader.GetString(5),
        reader.GetString(6),
        Parse(reader.GetString(7)),
        reader.IsDBNull(8) ? null : reader.GetString(8));

    private static MachineSimulationDefinition Resolved(MachineSimulationDefinition definition, NetworkFolderSettings paths) =>
        MachineSimulationDefinitions.MapLegacyPaths(definition, path => paths.ToAbsolute(path) ?? path);

    private static MachineSimulationDefinition Stored(MachineSimulationDefinition definition, NetworkFolderSettings paths) =>
        MachineSimulationDefinitions.MapLegacyPaths(definition, path => paths.ToStored(path) ?? path);

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
