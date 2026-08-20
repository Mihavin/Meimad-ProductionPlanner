using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV35GCodeProcessRevisionsMigration : IDatabaseMigration
{
    public int Version => 35;

    public string Name => "gcode_process_revisions";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE tool_table_releases (
                id TEXT PRIMARY KEY,
                case_operation_id TEXT NOT NULL,
                revision_number INTEGER NOT NULL CHECK (revision_number > 0),
                original_file_name TEXT NOT NULL CHECK (length(trim(original_file_name)) > 0),
                stored_relative_path TEXT NOT NULL UNIQUE CHECK (length(trim(stored_relative_path)) > 0),
                file_size INTEGER NOT NULL CHECK (file_size > 0),
                file_hash TEXT NOT NULL CHECK (length(file_hash) = 64),
                released_at TEXT NOT NULL,
                released_by TEXT NOT NULL CHECK (length(trim(released_by)) > 0),
                release_comment TEXT NOT NULL CHECK (length(trim(release_comment)) > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE (case_operation_id, revision_number),
                FOREIGN KEY (case_operation_id) REFERENCES case_operations (id) ON DELETE RESTRICT
            );

            CREATE INDEX ix_tool_table_releases_operation
            ON tool_table_releases (case_operation_id, revision_number DESC);

            CREATE TABLE process_revisions (
                id TEXT PRIMARY KEY,
                case_operation_id TEXT NOT NULL,
                revision_number INTEGER NOT NULL CHECK (revision_number > 0),
                is_active INTEGER NOT NULL CHECK (is_active IN (0, 1)),
                tool_table_release_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                created_by TEXT NOT NULL CHECK (length(trim(created_by)) > 0),
                change_description TEXT NOT NULL CHECK (length(trim(change_description)) > 0),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                updated_at TEXT NOT NULL,
                UNIQUE (case_operation_id, revision_number),
                FOREIGN KEY (case_operation_id) REFERENCES case_operations (id) ON DELETE RESTRICT,
                FOREIGN KEY (tool_table_release_id) REFERENCES tool_table_releases (id) ON DELETE RESTRICT
            );

            CREATE UNIQUE INDEX ux_process_revisions_active_operation
            ON process_revisions (case_operation_id)
            WHERE is_active = 1;

            CREATE INDEX ix_process_revisions_operation_history
            ON process_revisions (case_operation_id, revision_number DESC);

            CREATE TABLE gcode_releases (
                id TEXT PRIMARY KEY,
                case_operation_id TEXT NOT NULL,
                process_revision_id TEXT NOT NULL,
                postprocessor_id TEXT NOT NULL,
                post_specific_revision INTEGER NOT NULL CHECK (post_specific_revision > 0),
                original_file_name TEXT NOT NULL CHECK (length(trim(original_file_name)) > 0),
                stored_relative_path TEXT NOT NULL UNIQUE CHECK (length(trim(stored_relative_path)) > 0),
                file_size INTEGER NOT NULL CHECK (file_size > 0),
                file_hash TEXT NOT NULL CHECK (length(file_hash) = 64),
                released_at TEXT NOT NULL,
                released_by TEXT NOT NULL CHECK (length(trim(released_by)) > 0),
                change_scope TEXT NOT NULL CHECK (change_scope IN ('LOCAL_POST_REVISION', 'NEW_PROCESS_REVISION')),
                release_comment TEXT NOT NULL CHECK (length(trim(release_comment)) > 0),
                tool_table_release_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE (process_revision_id, postprocessor_id, post_specific_revision),
                FOREIGN KEY (case_operation_id) REFERENCES case_operations (id) ON DELETE RESTRICT,
                FOREIGN KEY (process_revision_id) REFERENCES process_revisions (id) ON DELETE RESTRICT,
                FOREIGN KEY (postprocessor_id) REFERENCES postprocessors (id) ON DELETE RESTRICT,
                FOREIGN KEY (tool_table_release_id) REFERENCES tool_table_releases (id) ON DELETE RESTRICT
            );

            CREATE INDEX ix_gcode_releases_operation_history
            ON gcode_releases (case_operation_id, released_at DESC, id);
            CREATE INDEX ix_gcode_releases_current_post
            ON gcode_releases (process_revision_id, postprocessor_id, post_specific_revision DESC);

            CREATE TRIGGER tool_table_releases_immutable_update
            BEFORE UPDATE ON tool_table_releases
            BEGIN
                SELECT RAISE(ABORT, 'released tool-table metadata is immutable');
            END;

            CREATE TRIGGER tool_table_releases_immutable_delete
            BEFORE DELETE ON tool_table_releases
            BEGIN
                SELECT RAISE(ABORT, 'released tool-table metadata is immutable');
            END;

            CREATE TRIGGER gcode_releases_immutable_update
            BEFORE UPDATE ON gcode_releases
            BEGIN
                SELECT RAISE(ABORT, 'released G-code metadata is immutable');
            END;

            CREATE TRIGGER gcode_releases_immutable_delete
            BEFORE DELETE ON gcode_releases
            BEGIN
                SELECT RAISE(ABORT, 'released G-code metadata is immutable');
            END;

            ALTER TABLE batch_operations ADD COLUMN production_process_revision_id TEXT
                REFERENCES process_revisions (id) ON DELETE RESTRICT;
            ALTER TABLE batch_operations ADD COLUMN production_gcode_release_id TEXT
                REFERENCES gcode_releases (id) ON DELETE RESTRICT;
            ALTER TABLE batch_operations ADD COLUMN production_tool_table_release_id TEXT
                REFERENCES tool_table_releases (id) ON DELETE RESTRICT;
            ALTER TABLE batch_operations ADD COLUMN production_gcode_file_hash TEXT
                CHECK (production_gcode_file_hash IS NULL OR length(production_gcode_file_hash) = 64);
            ALTER TABLE batch_operations ADD COLUMN production_tool_table_file_hash TEXT
                CHECK (production_tool_table_file_hash IS NULL OR length(production_tool_table_file_hash) = 64);

            CREATE INDEX ix_batch_operations_production_release
            ON batch_operations (production_process_revision_id, production_gcode_release_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
