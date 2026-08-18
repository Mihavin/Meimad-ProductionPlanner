using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV31CaseComponentsMigration : IDatabaseMigration
{
    public int Version => 31;

    public string Name => "case_components_and_kitaron_bom_sync";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE case_components (
                id TEXT PRIMARY KEY,
                parent_case_id TEXT NOT NULL,
                child_case_id TEXT NOT NULL,
                quantity_per_parent REAL NOT NULL CHECK (quantity_per_parent > 0),
                sort_order INTEGER NOT NULL DEFAULT 0 CHECK (sort_order >= 0),
                notes TEXT,
                is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (parent_case_id) REFERENCES cases (id) ON DELETE RESTRICT,
                FOREIGN KEY (child_case_id) REFERENCES cases (id) ON DELETE RESTRICT,
                CHECK (parent_case_id <> child_case_id),
                UNIQUE (parent_case_id, child_case_id)
            );

            CREATE INDEX ix_case_components_parent
                ON case_components (parent_case_id, is_active, sort_order, id);
            CREATE INDEX ix_case_components_child
                ON case_components (child_case_id, is_active, parent_case_id, id);

            ALTER TABLE kitaron_sync_links RENAME TO kitaron_sync_links_v30;
            CREATE TABLE kitaron_sync_links (
                source_entity TEXT NOT NULL
                    CHECK (source_entity IN ('case', 'order', 'case_operation', 'case_component')),
                source_key TEXT NOT NULL,
                target_id TEXT NOT NULL,
                owns_target INTEGER NOT NULL CHECK (owns_target IN (0, 1)),
                source_hash TEXT NOT NULL,
                first_seen_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                PRIMARY KEY (source_entity, source_key),
                UNIQUE (source_entity, target_id)
            );
            INSERT INTO kitaron_sync_links (
                source_entity, source_key, target_id, owns_target,
                source_hash, first_seen_at, last_seen_at)
            SELECT source_entity, source_key, target_id, owns_target,
                   source_hash, first_seen_at, last_seen_at
            FROM kitaron_sync_links_v30;
            DROP TABLE kitaron_sync_links_v30;
            CREATE INDEX ix_kitaron_sync_links_target
                ON kitaron_sync_links (source_entity, target_id);

            ALTER TABLE kitaron_sync_state
                ADD COLUMN components_created INTEGER NOT NULL DEFAULT 0 CHECK (components_created >= 0);
            ALTER TABLE kitaron_sync_state
                ADD COLUMN components_updated INTEGER NOT NULL DEFAULT 0 CHECK (components_updated >= 0);
            ALTER TABLE kitaron_sync_state
                ADD COLUMN components_matched INTEGER NOT NULL DEFAULT 0 CHECK (components_matched >= 0);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
