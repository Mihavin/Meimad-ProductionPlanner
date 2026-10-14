using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Work Order target times (owner request 2026-10-14): a work-hour price in NIS on each Machine
/// (<c>machines.hourly_rate_nis</c>, null meaning the 240 NIS default) and each Workstation Type
/// (<c>workstation_types.hourly_rate_nis</c>, null meaning the 100 NIS default), and
/// <c>work_order_target_times</c>, at most one per Work Order (Production Batch): the part price and the material total
/// the planner entered, the quantity and margin they gave, and per resource of the route the hourly price used and the
/// target hours the whole margin buys at that price (<c>resources_json</c>, a snapshot of the calculation at save time).
/// Not a planning input: the triggers of the new table advance the all-input and non-planning revisions only.
/// </summary>
internal sealed class SchemaV122WorkOrderTargetTimesMigration : IDatabaseMigration
{
    public int Version => 122;
    public string Name => "work_order_target_times";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE machines ADD COLUMN hourly_rate_nis REAL NULL
                CHECK (hourly_rate_nis IS NULL OR (hourly_rate_nis >= 0 AND hourly_rate_nis <= 100000));
            ALTER TABLE workstation_types ADD COLUMN hourly_rate_nis REAL NULL
                CHECK (hourly_rate_nis IS NULL OR (hourly_rate_nis >= 0 AND hourly_rate_nis <= 100000));
            CREATE TABLE work_order_target_times (
                production_batch_id TEXT PRIMARY KEY REFERENCES production_batches (id) ON DELETE CASCADE,
                part_price_nis REAL NOT NULL CHECK (part_price_nis >= 0 AND part_price_nis <= 100000000),
                material_total_nis REAL NOT NULL CHECK (material_total_nis >= 0 AND material_total_nis <= 1000000000),
                quantity INTEGER NOT NULL CHECK (quantity > 0),
                margin_nis REAL NOT NULL,
                resources_json TEXT NOT NULL CHECK (json_valid(resources_json) AND json_type(resources_json) = 'array'),
                notes TEXT NULL CHECK (notes IS NULL OR length(notes) <= 500),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                updated_by TEXT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(token);
        foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            command.CommandText = $"""
                CREATE TRIGGER projection_revision_work_order_target_times_{action.ToLowerInvariant()}
                AFTER {action} ON work_order_target_times
                BEGIN UPDATE projection_revision
                      SET revision = revision + 1, non_planning_revision = non_planning_revision + 1
                      WHERE id = 1; END;
                """;
            await command.ExecuteNonQueryAsync(token);
        }
    }
}
