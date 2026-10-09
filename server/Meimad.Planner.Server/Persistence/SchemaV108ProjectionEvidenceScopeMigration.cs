using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

// Keep conservative invalidation for future tables. Subtract only explicitly audited
// non-planning changes; both counters advance in the same source write transaction.
internal sealed class SchemaV108ProjectionEvidenceScopeMigration : IDatabaseMigration
{
    public int Version => 108;
    public string Name => "projection_evidence_scope";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "ALTER TABLE projection_revision ADD COLUMN non_planning_revision INTEGER NOT NULL DEFAULT 0 CHECK(typeof(non_planning_revision)='integer' AND non_planning_revision>=0);";
        await command.ExecuteNonQueryAsync(token);
        foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            var suffix = action.ToLowerInvariant();
            // AI event counts include derived diagnostics too. Planning ignores them.
            command.CommandText = $"""
                DROP TRIGGER projection_revision_structured_event_log_{suffix};
                CREATE TRIGGER projection_revision_structured_event_log_{suffix}
                AFTER {action} ON structured_event_log
                BEGIN UPDATE projection_revision SET revision=revision+1 WHERE id=1; END;
                """;
            await command.ExecuteNonQueryAsync(token);
            foreach (var table in new[] { "structured_event_log", "local_ai_requests", "local_ai_feedback", "local_ai_documents" })
            {
                // Timeline directly reads manual moves. Manual reports also maintain timing
                // tables via triggers; explicitly retain them here as source evidence.
                const string sourceTypes = "('manual_backlog_reorder','manual_operation_reported')";
                var condition = table != "structured_event_log" ? "" : " WHEN " + (action switch
                {
                    "INSERT" => $"NEW.event_type NOT IN {sourceTypes}",
                    "DELETE" => $"OLD.event_type NOT IN {sourceTypes}",
                    _ => $"NEW.event_type NOT IN {sourceTypes} AND OLD.event_type NOT IN {sourceTypes}"
                });
                command.CommandText = $"""
                    CREATE TRIGGER projection_non_planning_{table}_{suffix}
                    AFTER {action} ON {table}{condition}
                    BEGIN UPDATE projection_revision SET non_planning_revision=non_planning_revision+1 WHERE id=1; END;
                    """;
                await command.ExecuteNonQueryAsync(token);
            }
        }
    }
}
