using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Permanent DPRNT log (requested 2026-10-07): every non-blank line a Machine's DPRNT output sends,
/// whatever it says, with the time the Server received it. Unlike raw CNC telemetry it is never
/// pruned automatically; an administrator may clear it in Server Maintenance. Lines are evidence
/// only and change no planning or workflow state.
/// </summary>
internal sealed class SchemaV96DprntLogMigration : IDatabaseMigration
{
    public int Version => 96;

    public string Name => "dprnt_log";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE machine_dprnt_lines (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                machine_id TEXT NOT NULL REFERENCES machines(id) ON DELETE RESTRICT,
                connection_id TEXT NOT NULL,
                received_at TEXT NOT NULL,
                line TEXT NOT NULL CHECK (length(line) BETWEEN 1 AND 4096)
            );
            CREATE INDEX ix_machine_dprnt_lines_machine_time
            ON machine_dprnt_lines (machine_id, received_at, id);

            -- Before the log existed only MEIMAD/ event lines were kept, in the pruned raw telemetry;
            -- the ones still there start the log.
            INSERT INTO machine_dprnt_lines (machine_id, connection_id, received_at, line)
            SELECT machine_id, connection_id, observed_at, raw_payload
            FROM machine_telemetry_raw
            WHERE operation = 'DPRINT_EVENT' AND length(raw_payload) BETWEEN 1 AND 4096
            ORDER BY observed_at, rowid;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
