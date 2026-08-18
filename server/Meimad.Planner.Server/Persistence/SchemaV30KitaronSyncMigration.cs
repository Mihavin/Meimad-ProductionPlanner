using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV30KitaronSyncMigration : IDatabaseMigration
{
    public int Version => 30;

    public string Name => "kitaron_one_way_sync";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE kitaron_sync_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                sync_status TEXT NOT NULL DEFAULT 'never_run'
                    CHECK (sync_status IN ('never_run', 'running', 'succeeded', 'failed', 'blocked')),
                message TEXT,
                last_started_at TEXT,
                last_completed_at TEXT,
                source_rows INTEGER NOT NULL DEFAULT 0 CHECK (source_rows >= 0),
                cases_created INTEGER NOT NULL DEFAULT 0 CHECK (cases_created >= 0),
                cases_updated INTEGER NOT NULL DEFAULT 0 CHECK (cases_updated >= 0),
                cases_matched INTEGER NOT NULL DEFAULT 0 CHECK (cases_matched >= 0),
                orders_created INTEGER NOT NULL DEFAULT 0 CHECK (orders_created >= 0),
                orders_updated INTEGER NOT NULL DEFAULT 0 CHECK (orders_updated >= 0),
                orders_matched INTEGER NOT NULL DEFAULT 0 CHECK (orders_matched >= 0),
                operations_created INTEGER NOT NULL DEFAULT 0 CHECK (operations_created >= 0),
                operations_updated INTEGER NOT NULL DEFAULT 0 CHECK (operations_updated >= 0),
                operations_matched INTEGER NOT NULL DEFAULT 0 CHECK (operations_matched >= 0),
                warning_count INTEGER NOT NULL DEFAULT 0 CHECK (warning_count >= 0),
                mapping_version INTEGER,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );

            CREATE TABLE kitaron_sync_links (
                source_entity TEXT NOT NULL
                    CHECK (source_entity IN ('case', 'order', 'case_operation')),
                source_key TEXT NOT NULL,
                target_id TEXT NOT NULL,
                owns_target INTEGER NOT NULL CHECK (owns_target IN (0, 1)),
                source_hash TEXT NOT NULL,
                first_seen_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                PRIMARY KEY (source_entity, source_key),
                UNIQUE (source_entity, target_id)
            );

            INSERT INTO kitaron_sync_state (id) VALUES (1);
            CREATE INDEX ix_kitaron_sync_links_target
                ON kitaron_sync_links (source_entity, target_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
