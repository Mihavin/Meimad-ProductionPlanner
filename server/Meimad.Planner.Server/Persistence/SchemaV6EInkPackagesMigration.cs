using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV6EInkPackagesMigration : IDatabaseMigration
{
    public int Version => 6;

    public string Name => "eink_package_metadata";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE eink_package_revisions (
                id TEXT PRIMARY KEY,
                batch_operation_id TEXT NOT NULL,
                revision TEXT NOT NULL CHECK (length(trim(revision)) > 0),
                tool_cart_id TEXT,
                published_at TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (batch_operation_id) REFERENCES batch_operations (id) ON DELETE RESTRICT,
                UNIQUE (batch_operation_id, revision)
            );

            CREATE TABLE eink_package_files (
                id TEXT PRIMARY KEY,
                package_revision_id TEXT NOT NULL,
                logical_path TEXT NOT NULL CHECK (length(trim(logical_path)) > 0),
                storage_relative_path TEXT NOT NULL CHECK (length(trim(storage_relative_path)) > 0),
                media_type TEXT NOT NULL CHECK (length(trim(media_type)) > 0),
                byte_length INTEGER NOT NULL CHECK (byte_length >= 0),
                sha256 TEXT NOT NULL CHECK (length(sha256) = 64 AND sha256 = lower(sha256)),
                modified_at TEXT NOT NULL,
                display_order INTEGER NOT NULL DEFAULT 0 CHECK (display_order >= 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (package_revision_id) REFERENCES eink_package_revisions (id) ON DELETE RESTRICT,
                UNIQUE (package_revision_id, logical_path),
                UNIQUE (package_revision_id, display_order)
            );

            CREATE INDEX ix_eink_package_revisions_operation_published
            ON eink_package_revisions (batch_operation_id, published_at DESC, id);

            CREATE INDEX ix_eink_package_files_revision
            ON eink_package_files (package_revision_id, display_order, id);

            CREATE TRIGGER eink_package_revisions_immutable_update
            BEFORE UPDATE ON eink_package_revisions
            BEGIN
                SELECT RAISE(ABORT, 'published E-Ink package revisions are immutable');
            END;

            CREATE TRIGGER eink_package_revisions_immutable_delete
            BEFORE DELETE ON eink_package_revisions
            BEGIN
                SELECT RAISE(ABORT, 'published E-Ink package revisions are immutable');
            END;

            CREATE TRIGGER eink_package_files_immutable_update
            BEFORE UPDATE ON eink_package_files
            BEGIN
                SELECT RAISE(ABORT, 'published E-Ink package files are immutable');
            END;

            CREATE TRIGGER eink_package_files_immutable_delete
            BEFORE DELETE ON eink_package_files
            BEGIN
                SELECT RAISE(ABORT, 'published E-Ink package files are immutable');
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
