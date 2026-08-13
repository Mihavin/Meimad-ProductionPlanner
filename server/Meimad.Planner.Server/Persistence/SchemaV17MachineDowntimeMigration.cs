using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV17MachineDowntimeMigration : IDatabaseMigration
{
    public int Version => 17;
    public string Name => "machine downtime lifecycle";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE downtimes RENAME TO downtimes_v16;

            CREATE TABLE downtimes (
                id TEXT PRIMARY KEY,
                machine_id TEXT NOT NULL,
                downtime_type TEXT NOT NULL DEFAULT 'planned_maintenance' CHECK (downtime_type IN ('planned_maintenance', 'breakdown')),
                starts_at TEXT NOT NULL,
                ends_at TEXT,
                reason TEXT NOT NULL,
                planned_by TEXT DEFAULT 'Unspecified',
                repair_note TEXT,
                reported_by TEXT,
                status TEXT NOT NULL CHECK (status IN ('planned', 'active', 'restored')),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (machine_id) REFERENCES machines (id) ON DELETE RESTRICT,
                CHECK (ends_at IS NULL OR ends_at > starts_at),
                CHECK (
                    (downtime_type = 'planned_maintenance' AND ends_at IS NOT NULL AND planned_by IS NOT NULL AND status = 'planned')
                    OR
                    (downtime_type = 'breakdown' AND reported_by IS NOT NULL
                     AND ((status = 'active' AND ends_at IS NULL) OR (status = 'restored' AND ends_at IS NOT NULL)))
                )
            );

            INSERT INTO downtimes (
                id, machine_id, downtime_type, starts_at, ends_at, reason,
                planned_by, repair_note, reported_by, status, version, created_at, updated_at)
            SELECT id, machine_id, 'planned_maintenance', starts_at, ends_at, reason,
                   'Legacy record', NULL, NULL, 'planned', version, created_at, updated_at
            FROM downtimes_v16;

            DROP TABLE downtimes_v16;
            CREATE INDEX ix_downtimes_machine_start ON downtimes (machine_id, starts_at);
            CREATE INDEX ix_downtimes_active_breakdown ON downtimes (machine_id, status)
                WHERE downtime_type = 'breakdown' AND status = 'active';
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
