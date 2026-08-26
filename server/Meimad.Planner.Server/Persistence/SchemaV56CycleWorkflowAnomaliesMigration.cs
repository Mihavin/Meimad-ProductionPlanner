using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Extends immutable workflow anomaly evidence beyond transport sequencing so an
/// unmatched production-cycle END can be retained without inventing a START.
/// </summary>
internal sealed class SchemaV56CycleWorkflowAnomaliesMigration : IDatabaseMigration
{
    public int Version => 56;
    public string Name => "cycle_workflow_anomalies";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DROP TRIGGER production_run_workflow_anomalies_immutable_update;
            DROP TRIGGER production_run_workflow_anomalies_immutable_delete;
            DROP INDEX ix_production_run_workflow_anomalies_machine_time;

            CREATE TABLE production_run_workflow_anomalies_v56 (
                id TEXT PRIMARY KEY,
                production_run_id TEXT NOT NULL,
                machine_id TEXT NOT NULL,
                source TEXT NOT NULL,
                source_event_id TEXT NOT NULL,
                anomaly_type TEXT NOT NULL CHECK (anomaly_type IN (
                    'EVENT_SEQUENCE_GAP',
                    'EVENT_SEQUENCE_OUT_OF_ORDER',
                    'CYCLE_END_WITHOUT_START',
                    'CYCLE_END_SEQUENCE_MISMATCH')),
                previous_sequence INTEGER,
                expected_sequence INTEGER,
                received_sequence INTEGER NOT NULL,
                workflow_event_id TEXT NOT NULL,
                detected_at TEXT NOT NULL,
                details_json TEXT NOT NULL DEFAULT '{}'
                    CHECK (json_valid(details_json) AND json_type(details_json) = 'object'),
                UNIQUE (source, source_event_id, anomaly_type),
                FOREIGN KEY (production_run_id) REFERENCES production_runs(id) ON DELETE RESTRICT,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                FOREIGN KEY (workflow_event_id) REFERENCES production_run_workflow_events(id) ON DELETE RESTRICT
            );
            INSERT INTO production_run_workflow_anomalies_v56 (
                id,production_run_id,machine_id,source,source_event_id,
                anomaly_type,previous_sequence,expected_sequence,received_sequence,
                workflow_event_id,detected_at,details_json)
            SELECT id,production_run_id,machine_id,source,source_event_id,
                   anomaly_type,previous_sequence,expected_sequence,received_sequence,
                   workflow_event_id,detected_at,details_json
            FROM production_run_workflow_anomalies;
            DROP TABLE production_run_workflow_anomalies;
            ALTER TABLE production_run_workflow_anomalies_v56
                RENAME TO production_run_workflow_anomalies;
            CREATE INDEX ix_production_run_workflow_anomalies_machine_time
                ON production_run_workflow_anomalies(machine_id, detected_at DESC, id);
            CREATE TRIGGER production_run_workflow_anomalies_immutable_update
            BEFORE UPDATE ON production_run_workflow_anomalies
            BEGIN SELECT RAISE(ABORT, 'Workflow anomalies are immutable'); END;
            CREATE TRIGGER production_run_workflow_anomalies_immutable_delete
            BEFORE DELETE ON production_run_workflow_anomalies
            BEGIN SELECT RAISE(ABORT, 'Workflow anomalies are immutable'); END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
