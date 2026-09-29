using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// The history of a Case Operation's cycle, setup, QC and load/unload times (owner decision
/// 2026-09-29): every change records the time kind, where the new value came from (the NC estimate
/// or the measured median on a Machine applied from the operation statistics, or a manual edit),
/// the previous and new value, and who changed it when. Rows are immutable; they leave only with
/// their Case Operation.
/// </summary>
internal sealed class SchemaV93CaseOperationTimeHistoryMigration : IDatabaseMigration
{
    public int Version => 93;

    public string Name => "case_operation_time_history";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE case_operation_time_changes (
                id TEXT PRIMARY KEY,
                case_operation_id TEXT NOT NULL,
                time_kind TEXT NOT NULL CHECK (time_kind IN ('cycle', 'setup', 'qa', 'load_unload')),
                source TEXT NOT NULL CHECK (source IN ('NC', 'MEASURED', 'MANUAL')),
                machine_id TEXT,
                previous_seconds INTEGER,
                new_seconds INTEGER,
                basis_seconds REAL,
                sample_count INTEGER CHECK (sample_count IS NULL OR sample_count > 0),
                gcode_release_id TEXT,
                changed_by TEXT NOT NULL,
                changed_at TEXT NOT NULL,
                FOREIGN KEY (case_operation_id) REFERENCES case_operations (id) ON DELETE CASCADE,
                CHECK (source = 'MANUAL' OR machine_id IS NOT NULL)
            );
            CREATE INDEX ix_case_operation_time_changes_operation
                ON case_operation_time_changes (case_operation_id, changed_at DESC);
            CREATE TRIGGER case_operation_time_changes_immutable
            BEFORE UPDATE ON case_operation_time_changes
            BEGIN
                SELECT RAISE(ABORT, 'case_operation_time_changes rows are immutable');
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
