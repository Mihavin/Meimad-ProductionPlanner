using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Part thumbnails of a Case Operation (owner request 2026-10-12, TV Dashboard part picture):
/// <c>case_operation_thumbnails</c> keeps at most one image per Case Operation and source:
/// <c>package</c>, the finished-part thumbnail of the Cimatron simulation package, and
/// <c>snapshot</c>, a screenshot or preview stored at import or uploaded from the NC viewer or the
/// Case Workspace. The file lives below the release root
/// (<c>operation-thumbnails/&lt;operation&gt;/&lt;sha256&gt;.&lt;ext&gt;</c>) so the recovery set backs it up.
/// The TV Dashboard uses the Case picture first, then the package thumbnail, then the snapshot.
/// Thumbnails are not planning inputs: the triggers advance the all-input and non-planning
/// revisions only.
/// </summary>
internal sealed class SchemaV118OperationThumbnailsMigration : IDatabaseMigration
{
    public int Version => 118;
    public string Name => "case_operation_thumbnails";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE case_operation_thumbnails (
                case_operation_id TEXT NOT NULL REFERENCES case_operations (id) ON DELETE CASCADE,
                source TEXT NOT NULL CHECK (source IN ('package', 'snapshot')),
                file_name TEXT NOT NULL CHECK (length(file_name) BETWEEN 1 AND 255),
                media_type TEXT NOT NULL CHECK (media_type IN ('image/png', 'image/jpeg', 'image/bmp', 'image/gif')),
                file_size INTEGER NOT NULL CHECK (file_size > 0),
                file_hash TEXT NOT NULL CHECK (length(file_hash) = 64 AND file_hash NOT GLOB '*[^0-9a-f]*'),
                stored_relative_path TEXT NOT NULL,
                source_reference TEXT NULL,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                updated_at TEXT NOT NULL,
                updated_by TEXT NULL,
                PRIMARY KEY (case_operation_id, source)
            );
            CREATE INDEX ix_case_operation_thumbnails_hash ON case_operation_thumbnails (file_hash);
            CREATE INDEX ix_case_pictures_hash ON case_pictures (file_hash);
            """;
        await command.ExecuteNonQueryAsync(token);
        foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            command.CommandText = $"""
                CREATE TRIGGER projection_revision_case_operation_thumbnails_{action.ToLowerInvariant()}
                AFTER {action} ON case_operation_thumbnails
                BEGIN UPDATE projection_revision
                      SET revision = revision + 1, non_planning_revision = non_planning_revision + 1
                      WHERE id = 1; END;
                """;
            await command.ExecuteNonQueryAsync(token);
        }
    }
}
