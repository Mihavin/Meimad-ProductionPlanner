using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV18OperationPauseEventsMigration : IDatabaseMigration
{
    public int Version => 18;
    public string Name => "structured operation pause events";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE operation_pause_events (
                id TEXT PRIMARY KEY,
                batch_operation_id TEXT NOT NULL,
                reason_type TEXT NOT NULL CHECK (reason_type IN ('additional_qa', 'tooling_problem', 'customer_request', 'other')),
                problem_description TEXT,
                tooling_item_description TEXT,
                customer_contact_name TEXT,
                request_description TEXT,
                comment TEXT,
                paused_by TEXT NOT NULL,
                pause_started_at TEXT NOT NULL,
                pause_ended_at TEXT,
                status TEXT NOT NULL CHECK (status IN ('active', 'closed')),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (batch_operation_id) REFERENCES batch_operations(id) ON DELETE RESTRICT,
                CHECK (
                    (reason_type = 'additional_qa' AND length(trim(COALESCE(problem_description, ''))) > 0) OR
                    (reason_type = 'tooling_problem' AND length(trim(COALESCE(tooling_item_description, ''))) > 0) OR
                    (reason_type = 'customer_request' AND length(trim(COALESCE(customer_contact_name, ''))) > 0 AND length(trim(COALESCE(request_description, ''))) > 0) OR
                    (reason_type = 'other' AND length(trim(COALESCE(comment, ''))) > 0)
                ),
                CHECK ((status = 'active' AND pause_ended_at IS NULL) OR (status = 'closed' AND pause_ended_at IS NOT NULL))
            );
            CREATE UNIQUE INDEX ux_operation_pause_events_active
                ON operation_pause_events(batch_operation_id) WHERE status = 'active';
            CREATE INDEX ix_operation_pause_events_reporting
                ON operation_pause_events(reason_type, pause_started_at);
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
