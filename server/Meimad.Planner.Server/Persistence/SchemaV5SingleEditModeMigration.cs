using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV5SingleEditModeMigration : IDatabaseMigration
{
    public int Version => 5;

    public string Name => "single_edit_mode_requests";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE edit_requests (
                id TEXT PRIMARY KEY,
                requester_client_id TEXT NOT NULL,
                requester_user_id TEXT NOT NULL,
                holder_generation_at_request INTEGER NOT NULL CHECK (holder_generation_at_request >= 0),
                status TEXT NOT NULL
                    CHECK (status IN ('pending', 'transferred', 'rejected', 'auto_transferred')),
                requested_at TEXT NOT NULL,
                decision_deadline TEXT NOT NULL,
                decided_at TEXT,
                granted_generation INTEGER CHECK (granted_generation IS NULL OR granted_generation >= 0),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                CHECK (
                    (status = 'pending' AND decided_at IS NULL AND granted_generation IS NULL)
                    OR (status = 'rejected' AND decided_at IS NOT NULL AND granted_generation IS NULL)
                    OR (status IN ('transferred', 'auto_transferred')
                        AND decided_at IS NOT NULL AND granted_generation IS NOT NULL)
                )
            );

            CREATE UNIQUE INDEX ux_edit_requests_single_pending
                ON edit_requests (status)
                WHERE status = 'pending';
            CREATE INDEX ix_edit_requests_requester
                ON edit_requests (requester_client_id, requested_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
