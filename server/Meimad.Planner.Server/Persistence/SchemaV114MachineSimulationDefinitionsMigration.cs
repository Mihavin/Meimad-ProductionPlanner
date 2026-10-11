using System.Globalization;
using Meimad.Planner.Server.Domain.Machines;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Machine simulation definitions (docs/machine-simulation-definition.md). <c>machine_simulation_definitions</c> holds one
/// Server-authoritative definition per Machine as a draft and a published copy with their own versions;
/// <c>machine_simulation_assets</c> lists the Machine's uploaded meshes (stored below the release root under
/// <c>machine-assets/</c>, hashed, never a client path). Every existing Machine gets a definition built from its NC viewer
/// machine, its <c>nc_viewer_models_json</c> (as legacy paths) and its magazine and timing columns, published as is, so
/// the NC viewer, the Planner and NC analysis behave exactly as before. From now on the published definition is the
/// only write path of <c>usable_tool_positions</c>, <c>rapid_rate_mm_per_min</c>, <c>tool_change_time_seconds</c>,
/// <c>machine_time_factor</c>, <c>nc_viewer_machine</c> and <c>nc_viewer_models_json</c>, which become projections.
/// Definitions and assets are display/simulation context, not planning inputs: their triggers advance the all-input
/// revision and the non-planning counter (a publish that changes a projected column advances the Machine's own revision).
/// </summary>
internal sealed class SchemaV114MachineSimulationDefinitionsMigration : IDatabaseMigration
{
    public int Version => 114;
    public string Name => "machine_simulation_definitions";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE machine_simulation_definitions (
                machine_id TEXT NOT NULL PRIMARY KEY REFERENCES machines (id) ON DELETE CASCADE,
                draft_json TEXT NOT NULL CHECK (json_valid(draft_json) AND json_type(draft_json) = 'object'),
                draft_version INTEGER NOT NULL CHECK (draft_version > 0),
                published_json TEXT NULL CHECK (published_json IS NULL OR (json_valid(published_json) AND json_type(published_json) = 'object')),
                published_version INTEGER NOT NULL DEFAULT 0 CHECK (published_version >= 0),
                published_at TEXT NULL,
                published_by TEXT NULL,
                updated_at TEXT NOT NULL,
                updated_by TEXT NULL,
                CHECK ((published_json IS NULL) = (published_version = 0))
            );

            CREATE TABLE machine_simulation_assets (
                id TEXT NOT NULL PRIMARY KEY CHECK (length(id) = 32 AND id NOT GLOB '*[^0-9a-f]*'),
                machine_id TEXT NOT NULL REFERENCES machines (id) ON DELETE CASCADE,
                file_name TEXT NOT NULL CHECK (length(file_name) BETWEEN 1 AND 255),
                media_type TEXT NOT NULL CHECK (media_type IN ('model/gltf-binary', 'model/stl')),
                file_size INTEGER NOT NULL CHECK (file_size > 0),
                file_hash TEXT NOT NULL CHECK (length(file_hash) = 64 AND file_hash NOT GLOB '*[^0-9a-f]*'),
                stored_relative_path TEXT NOT NULL UNIQUE,
                created_at TEXT NOT NULL,
                created_by TEXT NULL,
                UNIQUE (machine_id, file_hash)
            );
            CREATE INDEX ix_machine_simulation_assets_machine ON machine_simulation_assets (machine_id, created_at);
            """;
        await command.ExecuteNonQueryAsync(token);
        foreach (var table in new[] { "machine_simulation_definitions", "machine_simulation_assets" })
        {
            foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                command.CommandText = $"""
                    CREATE TRIGGER projection_revision_{table}_{action.ToLowerInvariant()}
                    AFTER {action} ON {table}
                    BEGIN UPDATE projection_revision
                          SET revision = revision + 1, non_planning_revision = non_planning_revision + 1
                          WHERE id = 1; END;
                    """;
                await command.ExecuteNonQueryAsync(token);
            }
        }

        var machines = new List<(string Id, string Name, string? Viewer, int? Usable, double? Rapid, double? Change, double Factor, string Models)>();
        command.CommandText = """
            SELECT id, name, nc_viewer_machine, usable_tool_positions, rapid_rate_mm_per_min,
                   tool_change_time_seconds, machine_time_factor, nc_viewer_models_json
            FROM machines ORDER BY id;
            """;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                machines.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    reader.GetDouble(6),
                    reader.GetString(7)));
            }
        }

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        command.CommandText = """
            INSERT INTO machine_simulation_definitions (
                machine_id, draft_json, draft_version, published_json, published_version,
                published_at, published_by, updated_at, updated_by)
            VALUES ($id, $json, 1, $json, 1, $now, NULL, $now, NULL);
            """;
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var json = command.Parameters.Add("$json", SqliteType.Text);
        command.Parameters.AddWithValue("$now", now);
        foreach (var machine in machines)
        {
            // Model paths stay in their stored (network-folder relative) form, like the column they come from.
            var definition = MachineSimulationDefinitions.Initial(
                machine.Id, machine.Name, machine.Viewer, machine.Usable, machine.Rapid, machine.Change, machine.Factor,
                MachineNcViewerModels.Deserialize(machine.Models));
            id.Value = machine.Id;
            json.Value = definition.ToJson();
            await command.ExecuteNonQueryAsync(token);
        }
    }
}
