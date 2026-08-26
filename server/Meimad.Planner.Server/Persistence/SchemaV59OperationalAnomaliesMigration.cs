using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>Creates one append-only anomaly ledger without granting anomaly facts mutation authority.</summary>
internal sealed class SchemaV59OperationalAnomaliesMigration : IDatabaseMigration
{
    public int Version => 59;
    public string Name => "operational_anomalies";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE operational_anomalies (
                id TEXT PRIMARY KEY,
                anomaly_type TEXT NOT NULL CHECK(anomaly_type IN(
                    'wrong_nc_program',
                    'active_nc_identity_unavailable',
                    'stale_offset_loader',
                    'offset_loader_not_executed',
                    'offset_loader_interrupted',
                    'verification_failed',
                    'verification_expired',
                    'verification_macro_version_mismatch',
                    'cycle_started_before_qc_pass',
                    'cycle_end_without_start',
                    'cycle_interrupted',
                    'cnc_event_sequence_gap',
                    'duplicate_cnc_event',
                    'unknown_production_run',
                    'ambiguous_production_run',
                    'tablet_offline',
                    'tablet_credential_revoked')),
                machine_id TEXT,
                production_run_id TEXT,
                tablet_device_id TEXT,
                source TEXT NOT NULL,
                source_event_id TEXT,
                workflow_event_id TEXT,
                detected_at TEXT NOT NULL,
                details_json TEXT NOT NULL DEFAULT '{}'
                    CHECK(json_valid(details_json) AND json_type(details_json)='object'),
                dedupe_key TEXT NOT NULL UNIQUE,
                FOREIGN KEY(machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                FOREIGN KEY(production_run_id) REFERENCES production_runs(id) ON DELETE RESTRICT,
                FOREIGN KEY(tablet_device_id) REFERENCES device_registry(id) ON DELETE RESTRICT,
                FOREIGN KEY(workflow_event_id) REFERENCES production_run_workflow_events(id) ON DELETE RESTRICT
            );
            CREATE INDEX ix_operational_anomalies_machine_time
                ON operational_anomalies(machine_id,detected_at DESC,id);
            CREATE INDEX ix_operational_anomalies_run_time
                ON operational_anomalies(production_run_id,detected_at DESC,id);
            CREATE INDEX ix_operational_anomalies_type_time
                ON operational_anomalies(anomaly_type,detected_at DESC,id);
            CREATE TRIGGER operational_anomalies_immutable_update
            BEFORE UPDATE ON operational_anomalies
            BEGIN SELECT RAISE(ABORT,'Operational anomalies are immutable'); END;
            CREATE TRIGGER operational_anomalies_immutable_delete
            BEFORE DELETE ON operational_anomalies
            BEGIN SELECT RAISE(ABORT,'Operational anomalies are immutable'); END;

            INSERT OR IGNORE INTO operational_anomalies(
                id,anomaly_type,machine_id,production_run_id,source,source_event_id,
                workflow_event_id,detected_at,details_json,dedupe_key)
            SELECT 'workflow-anomaly:' || anomaly.id,
                   CASE anomaly.anomaly_type
                       WHEN 'CYCLE_END_WITHOUT_START' THEN 'cycle_end_without_start'
                       WHEN 'CYCLE_END_SEQUENCE_MISMATCH' THEN 'cycle_end_without_start'
                       ELSE 'cnc_event_sequence_gap' END,
                   anomaly.machine_id,anomaly.production_run_id,anomaly.source,
                   anomaly.source_event_id,anomaly.workflow_event_id,
                   anomaly.detected_at,
                   json_object(
                       'workflowAnomalyType',anomaly.anomaly_type,
                       'previousSequence',anomaly.previous_sequence,
                       'expectedSequence',anomaly.expected_sequence,
                       'receivedSequence',anomaly.received_sequence),
                   'workflow-anomaly:' || anomaly.id
            FROM production_run_workflow_anomalies anomaly;

            INSERT OR IGNORE INTO operational_anomalies(
                id,anomaly_type,machine_id,production_run_id,source,source_event_id,
                workflow_event_id,detected_at,details_json,dedupe_key)
            SELECT 'workflow-event-anomaly:' || event.id,
                   CASE event.event_type
                       WHEN 'SETUP_VERIFICATION_FAILED' THEN 'verification_failed'
                       ELSE 'cycle_interrupted' END,
                   event.machine_id,event.production_run_id,event.source,event.source_event_id,
                   event.id,event.server_received_at,'{}','workflow-event-anomaly:' || event.id
            FROM production_run_workflow_events event
            WHERE event.event_type IN('SETUP_VERIFICATION_FAILED','CYCLE_INTERRUPTED');

            INSERT OR IGNORE INTO operational_anomalies(
                id,anomaly_type,machine_id,tablet_device_id,source,source_event_id,
                detected_at,details_json,dedupe_key)
            SELECT 'tablet-revoked:' || device.id || ':' || device.version,
                   'tablet_credential_revoked',device.machine_id,device.id,'DEVICE_REGISTRY',
                   device.id,device.updated_at,'{}',
                   'tablet-revoked:' || device.id || ':' || device.version
            FROM device_registry device
            WHERE device.device_type='eink' AND device.is_enabled=0;

            CREATE TRIGGER operational_anomaly_from_workflow_anomaly
            AFTER INSERT ON production_run_workflow_anomalies
            BEGIN
                INSERT OR IGNORE INTO operational_anomalies(
                    id,anomaly_type,machine_id,production_run_id,source,source_event_id,
                    workflow_event_id,detected_at,details_json,dedupe_key)
                VALUES(
                    'workflow-anomaly:' || NEW.id,
                    CASE NEW.anomaly_type
                        WHEN 'CYCLE_END_WITHOUT_START' THEN 'cycle_end_without_start'
                        WHEN 'CYCLE_END_SEQUENCE_MISMATCH' THEN 'cycle_end_without_start'
                        ELSE 'cnc_event_sequence_gap' END,
                    NEW.machine_id,NEW.production_run_id,NEW.source,NEW.source_event_id,
                    NEW.workflow_event_id,NEW.detected_at,
                    json_object(
                        'workflowAnomalyType',NEW.anomaly_type,
                        'previousSequence',NEW.previous_sequence,
                        'expectedSequence',NEW.expected_sequence,
                        'receivedSequence',NEW.received_sequence),
                    'workflow-anomaly:' || NEW.id);
            END;

            CREATE TRIGGER operational_anomaly_from_workflow_event
            AFTER INSERT ON production_run_workflow_events
            WHEN NEW.event_type IN('SETUP_VERIFICATION_FAILED','CYCLE_INTERRUPTED')
            BEGIN
                INSERT OR IGNORE INTO operational_anomalies(
                    id,anomaly_type,machine_id,production_run_id,source,source_event_id,
                    workflow_event_id,detected_at,details_json,dedupe_key)
                VALUES(
                    'workflow-event-anomaly:' || NEW.id,
                    CASE NEW.event_type
                        WHEN 'SETUP_VERIFICATION_FAILED' THEN 'verification_failed'
                        ELSE 'cycle_interrupted' END,
                    NEW.machine_id,NEW.production_run_id,NEW.source,NEW.source_event_id,
                    NEW.id,NEW.server_received_at,'{}','workflow-event-anomaly:' || NEW.id);
            END;

            CREATE TRIGGER operational_anomaly_from_expired_verification
            AFTER UPDATE OF state ON cnc_setup_verification_sessions
            WHEN OLD.state <> 'EXPIRED' AND NEW.state='EXPIRED'
            BEGIN
                INSERT OR IGNORE INTO operational_anomalies(
                    id,anomaly_type,machine_id,production_run_id,source,source_event_id,
                    workflow_event_id,detected_at,details_json,dedupe_key)
                VALUES(
                    'verification-expired:' || NEW.id,'verification_expired',
                    NEW.machine_id,NEW.production_run_id,'SETUP_VERIFICATION_SESSION',NEW.id,
                    NEW.source_workflow_event_id,NEW.resolved_at,'{}',
                    'verification-expired:' || NEW.id);
            END;

            CREATE TRIGGER operational_anomaly_from_tablet_revoke
            AFTER UPDATE OF is_enabled ON device_registry
            WHEN OLD.device_type='eink' AND OLD.is_enabled=1 AND NEW.is_enabled=0
            BEGIN
                INSERT OR IGNORE INTO operational_anomalies(
                    id,anomaly_type,machine_id,tablet_device_id,source,source_event_id,
                    detected_at,details_json,dedupe_key)
                VALUES(
                    'tablet-revoked:' || NEW.id || ':' || NEW.version,
                    'tablet_credential_revoked',NEW.machine_id,NEW.id,'DEVICE_REGISTRY',
                    NEW.id,NEW.updated_at,'{}',
                    'tablet-revoked:' || NEW.id || ':' || NEW.version);
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
