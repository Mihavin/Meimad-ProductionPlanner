using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV36ReleasedToolCapacityMigration : IDatabaseMigration
{
    public int Version => 36;

    public string Name => "released_tool_capacity";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE tool_table_releases ADD COLUMN required_tool_count INTEGER
                CHECK (required_tool_count IS NULL OR required_tool_count >= 0);

            CREATE TABLE tool_table_release_tools (
                id TEXT PRIMARY KEY,
                tool_table_release_id TEXT NOT NULL,
                row_number INTEGER NOT NULL CHECK (row_number > 0),
                tool_identifier TEXT NOT NULL CHECK (length(trim(tool_identifier)) > 0),
                description TEXT NOT NULL CHECK (length(trim(description)) > 0),
                is_required INTEGER NOT NULL CHECK (is_required IN (0, 1)),
                requires_magazine_position INTEGER NOT NULL
                    CHECK (requires_magazine_position IN (0, 1)),
                is_active INTEGER NOT NULL CHECK (is_active IN (0, 1)),
                magazine_position TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE (tool_table_release_id, row_number),
                FOREIGN KEY (tool_table_release_id)
                    REFERENCES tool_table_releases (id) ON DELETE RESTRICT
            );

            CREATE INDEX ix_tool_table_release_tools_release
            ON tool_table_release_tools (tool_table_release_id, row_number);

            CREATE TRIGGER tool_table_release_tools_immutable_update
            BEFORE UPDATE ON tool_table_release_tools
            BEGIN
                SELECT RAISE(ABORT, 'released tool rows are immutable');
            END;

            CREATE TRIGGER tool_table_release_tools_immutable_delete
            BEFORE DELETE ON tool_table_release_tools
            BEGIN
                SELECT RAISE(ABORT, 'released tool rows are immutable');
            END;

            CREATE TRIGGER tool_table_release_tools_no_late_insert
            BEFORE INSERT ON tool_table_release_tools
            WHEN EXISTS (
                SELECT 1 FROM process_revisions
                WHERE tool_table_release_id = NEW.tool_table_release_id)
            BEGIN
                SELECT RAISE(ABORT, 'released tool rows cannot be appended after process publication');
            END;

            CREATE TRIGGER process_revisions_tool_count_consistent
            BEFORE INSERT ON process_revisions
            WHEN (SELECT required_tool_count
                  FROM tool_table_releases
                  WHERE id = NEW.tool_table_release_id) IS NOT NULL
             AND (SELECT required_tool_count
                  FROM tool_table_releases
                  WHERE id = NEW.tool_table_release_id) <> (
                    SELECT COUNT(DISTINCT lower(trim(tool_identifier)))
                    FROM tool_table_release_tools
                    WHERE tool_table_release_id = NEW.tool_table_release_id
                      AND is_required = 1
                      AND requires_magazine_position = 1
                      AND is_active = 1)
            BEGIN
                SELECT RAISE(ABORT, 'released tool count does not match required magazine tools');
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
