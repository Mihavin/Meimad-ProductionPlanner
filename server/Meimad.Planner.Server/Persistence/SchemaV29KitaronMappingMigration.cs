using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV29KitaronMappingMigration : IDatabaseMigration
{
    public int Version => 29;

    public string Name => "kitaron_connector_mapping_draft";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE kitaron_mapping_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                model_mode TEXT NOT NULL DEFAULT 'domain_aligned'
                    CHECK (model_mode IN ('domain_aligned', 'flat_requested')),
                mapping_status TEXT NOT NULL DEFAULT 'draft'
                    CHECK (mapping_status IN ('draft', 'ready_for_implementation')),
                mappings_json TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(mappings_json)),
                detected_columns_json TEXT NOT NULL DEFAULT '[]'
                    CHECK (json_valid(detected_columns_json)),
                notes TEXT,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );

            INSERT INTO kitaron_mapping_settings (id) VALUES (1);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
