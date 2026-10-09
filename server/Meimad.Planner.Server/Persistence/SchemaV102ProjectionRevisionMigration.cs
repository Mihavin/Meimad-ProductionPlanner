using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV102ProjectionRevisionMigration : IDatabaseMigration
{
    public int Version => 102;
    public string Name => "coherent_projection_revision";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS projection_revision (
                id INTEGER PRIMARY KEY CHECK(id=1),
                epoch TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>=0)
            );
            INSERT OR IGNORE INTO projection_revision VALUES(1,lower(hex(randomblob(16))),0);
            """;
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT IN ('projection_revision','schema_migrations') ORDER BY name";
        var tables = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) tables.Add(reader.GetString(0));
        // Conservative invalidation: all persisted inputs, including imported/configuration facts.
        // Only the two derived Timeline diagnostic event types are excluded to avoid self-invalidation.
        foreach (var table in tables)
        foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
            var condition = table != "structured_event_log" ? "" : " WHEN " + (action switch
            {
                "INSERT" => "NEW.event_type NOT IN ('timeline_conflict_detected','resource_wait_detected')",
                "DELETE" => "OLD.event_type NOT IN ('timeline_conflict_detected','resource_wait_detected')",
                _ => "NEW.event_type NOT IN ('timeline_conflict_detected','resource_wait_detected') OR OLD.event_type NOT IN ('timeline_conflict_detected','resource_wait_detected')"
            });
            command.CommandText = $"""
                CREATE TRIGGER IF NOT EXISTS {Quote($"projection_revision_{table}_{action.ToLowerInvariant()}")}
                AFTER {action} ON {Quote(table)}{condition}
                BEGIN UPDATE projection_revision SET revision=revision+1 WHERE id=1; END;
                """;
            await command.ExecuteNonQueryAsync(token);
        }
    }
}
