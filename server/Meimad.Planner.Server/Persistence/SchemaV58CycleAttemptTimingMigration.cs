using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Preserves raw per-attempt START evidence and a separate immutable outcome fact.
/// Analytics remain derived and no statistical formula is persisted.
/// </summary>
internal sealed class SchemaV58CycleAttemptTimingMigration : IDatabaseMigration
{
    public int Version => 58;
    public string Name => "cycle_attempt_timing";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE production_run_cycle_attempts (
                id TEXT PRIMARY KEY,
                production_run_id TEXT NOT NULL,
                production_run_program_id TEXT,
                machine_id TEXT NOT NULL,
                start_workflow_event_id TEXT NOT NULL UNIQUE,
                start_source TEXT NOT NULL,
                start_source_event_id TEXT NOT NULL,
                start_source_sequence INTEGER NOT NULL,
                start_server_received_at TEXT NOT NULL,
                start_machine_timestamp TEXT,
                created_at TEXT NOT NULL,
                FOREIGN KEY(production_run_id) REFERENCES production_runs(id) ON DELETE RESTRICT,
                FOREIGN KEY(production_run_program_id) REFERENCES production_run_programs(id) ON DELETE RESTRICT,
                FOREIGN KEY(machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                FOREIGN KEY(start_workflow_event_id) REFERENCES production_run_workflow_events(id) ON DELETE RESTRICT
            );
            CREATE INDEX ix_production_run_cycle_attempts_run_start
                ON production_run_cycle_attempts(production_run_id,start_server_received_at,id);
            CREATE INDEX ix_production_run_cycle_attempts_machine_start
                ON production_run_cycle_attempts(machine_id,start_server_received_at,id);
            CREATE TRIGGER production_run_cycle_attempts_immutable_update
            BEFORE UPDATE ON production_run_cycle_attempts
            BEGIN SELECT RAISE(ABORT,'Production cycle attempts are immutable'); END;
            CREATE TRIGGER production_run_cycle_attempts_immutable_delete
            BEFORE DELETE ON production_run_cycle_attempts
            BEGIN SELECT RAISE(ABORT,'Production cycle attempts are immutable'); END;

            CREATE TABLE production_run_cycle_attempt_outcomes (
                attempt_id TEXT PRIMARY KEY,
                completion_state TEXT NOT NULL CHECK(completion_state IN('COMPLETED','INTERRUPTED')),
                outcome_workflow_event_id TEXT NOT NULL UNIQUE,
                boundary_source TEXT NOT NULL,
                boundary_source_event_id TEXT NOT NULL,
                boundary_source_sequence INTEGER NOT NULL,
                end_server_received_at TEXT NOT NULL,
                end_machine_timestamp TEXT,
                created_at TEXT NOT NULL,
                FOREIGN KEY(attempt_id) REFERENCES production_run_cycle_attempts(id) ON DELETE RESTRICT,
                FOREIGN KEY(outcome_workflow_event_id) REFERENCES production_run_workflow_events(id) ON DELETE RESTRICT
            );
            CREATE INDEX ix_production_run_cycle_attempt_outcomes_state_time
                ON production_run_cycle_attempt_outcomes(completion_state,end_server_received_at,attempt_id);
            CREATE TRIGGER production_run_cycle_attempt_outcomes_immutable_update
            BEFORE UPDATE ON production_run_cycle_attempt_outcomes
            BEGIN SELECT RAISE(ABORT,'Production cycle attempt outcomes are immutable'); END;
            CREATE TRIGGER production_run_cycle_attempt_outcomes_immutable_delete
            BEFORE DELETE ON production_run_cycle_attempt_outcomes
            BEGIN SELECT RAISE(ABORT,'Production cycle attempt outcomes are immutable'); END;

            INSERT INTO production_run_cycle_attempts(
                id,production_run_id,production_run_program_id,machine_id,
                start_workflow_event_id,start_source,start_source_event_id,
                start_source_sequence,start_server_received_at,start_machine_timestamp,created_at)
            SELECT 'attempt:' || event.id,event.production_run_id,
                   CASE WHEN EXISTS(
                       SELECT 1 FROM production_run_programs program
                       WHERE program.id=json_extract(event.metadata_json,'$.productionRunProgramId'))
                       THEN json_extract(event.metadata_json,'$.productionRunProgramId') END,
                   event.machine_id,event.id,event.source,event.source_event_id,
                   event.source_sequence,event.server_received_at,event.machine_timestamp,
                   event.server_received_at
            FROM production_run_workflow_events event
            WHERE event.event_type='CYCLE_START'
              AND event.source_event_id IS NOT NULL
              AND event.source_sequence IS NOT NULL;

            INSERT OR IGNORE INTO production_run_cycle_attempt_outcomes(
                attempt_id,completion_state,outcome_workflow_event_id,boundary_source,
                boundary_source_event_id,boundary_source_sequence,end_server_received_at,
                end_machine_timestamp,created_at)
            SELECT attempt.id,'COMPLETED',ending.id,ending.source,
                   ending.source_event_id,ending.source_sequence,
                   ending.server_received_at,ending.machine_timestamp,ending.server_received_at
            FROM production_run_cycle_attempts attempt
            JOIN production_run_workflow_events start
              ON start.id=attempt.start_workflow_event_id
            JOIN production_run_workflow_events ending
              ON ending.production_run_id=start.production_run_id
             AND ending.machine_id=start.machine_id
             AND ending.event_type='CYCLE_END'
             AND ending.source=start.source
             AND ending.source_sequence=start.source_sequence+1
             AND json_extract(ending.metadata_json,'$.productionRunProgramId')
                 IS json_extract(start.metadata_json,'$.productionRunProgramId')
            JOIN production_run_cycle_events cycle
              ON cycle.source=ending.source
             AND cycle.source_event_id=ending.source_event_id;

            INSERT OR IGNORE INTO production_run_cycle_attempt_outcomes(
                attempt_id,completion_state,outcome_workflow_event_id,boundary_source,
                boundary_source_event_id,boundary_source_sequence,end_server_received_at,
                end_machine_timestamp,created_at)
            SELECT attempt.id,'INTERRUPTED',interrupted.id,
                   start.source,
                   json_extract(interrupted.metadata_json,'$.interruptedBySourceEventId'),
                   json_extract(interrupted.metadata_json,'$.interruptedBySequence'),
                   interrupted.server_received_at,NULL,interrupted.server_received_at
            FROM production_run_cycle_attempts attempt
            JOIN production_run_workflow_events start
              ON start.id=attempt.start_workflow_event_id
            JOIN production_run_workflow_events interrupted
              ON interrupted.event_type='CYCLE_INTERRUPTED'
             AND json_extract(interrupted.metadata_json,'$.interruptedWorkflowEventId')=start.id
            WHERE json_type(interrupted.metadata_json,'$.interruptedBySourceEventId')='text'
              AND json_type(interrupted.metadata_json,'$.interruptedBySequence')='integer';

            CREATE TRIGGER production_run_cycle_attempt_from_start
            AFTER INSERT ON production_run_workflow_events
            WHEN NEW.event_type='CYCLE_START'
             AND NEW.source_event_id IS NOT NULL
             AND NEW.source_sequence IS NOT NULL
            BEGIN
                INSERT INTO production_run_cycle_attempts(
                    id,production_run_id,production_run_program_id,machine_id,
                    start_workflow_event_id,start_source,start_source_event_id,
                    start_source_sequence,start_server_received_at,start_machine_timestamp,created_at)
                VALUES(
                    'attempt:' || NEW.id,NEW.production_run_id,
                    CASE WHEN EXISTS(
                        SELECT 1 FROM production_run_programs program
                        WHERE program.id=json_extract(NEW.metadata_json,'$.productionRunProgramId'))
                        THEN json_extract(NEW.metadata_json,'$.productionRunProgramId') END,
                    NEW.machine_id,NEW.id,NEW.source,NEW.source_event_id,
                    NEW.source_sequence,NEW.server_received_at,NEW.machine_timestamp,
                    NEW.server_received_at);
            END;

            CREATE TRIGGER production_run_cycle_attempt_interrupted
            AFTER INSERT ON production_run_workflow_events
            WHEN NEW.event_type='CYCLE_INTERRUPTED'
             AND json_type(NEW.metadata_json,'$.interruptedWorkflowEventId')='text'
             AND json_type(NEW.metadata_json,'$.interruptedBySourceEventId')='text'
             AND json_type(NEW.metadata_json,'$.interruptedBySequence')='integer'
            BEGIN
                INSERT OR IGNORE INTO production_run_cycle_attempt_outcomes(
                    attempt_id,completion_state,outcome_workflow_event_id,boundary_source,
                    boundary_source_event_id,boundary_source_sequence,end_server_received_at,
                    end_machine_timestamp,created_at)
                SELECT attempt.id,'INTERRUPTED',NEW.id,attempt.start_source,
                       json_extract(NEW.metadata_json,'$.interruptedBySourceEventId'),
                       json_extract(NEW.metadata_json,'$.interruptedBySequence'),
                       NEW.server_received_at,NEW.machine_timestamp,NEW.server_received_at
                FROM production_run_cycle_attempts attempt
                WHERE attempt.start_workflow_event_id=
                      json_extract(NEW.metadata_json,'$.interruptedWorkflowEventId');
            END;

            CREATE TRIGGER production_run_cycle_attempt_completed
            AFTER INSERT ON production_run_cycle_events
            BEGIN
                INSERT OR IGNORE INTO production_run_cycle_attempt_outcomes(
                    attempt_id,completion_state,outcome_workflow_event_id,boundary_source,
                    boundary_source_event_id,boundary_source_sequence,end_server_received_at,
                    end_machine_timestamp,created_at)
                SELECT attempt.id,'COMPLETED',ending.id,ending.source,
                       ending.source_event_id,ending.source_sequence,
                       ending.server_received_at,ending.machine_timestamp,ending.server_received_at
                FROM production_run_workflow_events ending
                JOIN production_run_workflow_events start
                  ON start.production_run_id=ending.production_run_id
                 AND start.machine_id=ending.machine_id
                 AND start.event_type='CYCLE_START'
                 AND start.source=ending.source
                 AND start.source_sequence=ending.source_sequence-1
                 AND json_extract(start.metadata_json,'$.productionRunProgramId')
                     IS json_extract(ending.metadata_json,'$.productionRunProgramId')
                JOIN production_run_cycle_attempts attempt
                  ON attempt.start_workflow_event_id=start.id
                WHERE ending.event_type='CYCLE_END'
                  AND ending.source=NEW.source
                  AND ending.source_event_id=NEW.source_event_id;
            END;

            CREATE VIEW production_run_cycle_attempt_timing AS
            SELECT attempt.id,attempt.production_run_id,attempt.production_run_program_id,
                   attempt.machine_id,attempt.start_workflow_event_id,
                   attempt.start_source,attempt.start_source_event_id,
                   attempt.start_source_sequence,attempt.start_server_received_at,
                   attempt.start_machine_timestamp,
                   COALESCE(outcome.completion_state,'OPEN') AS completion_state,
                   outcome.outcome_workflow_event_id,outcome.boundary_source,
                   outcome.boundary_source_event_id,outcome.boundary_source_sequence,
                   outcome.end_server_received_at,outcome.end_machine_timestamp
            FROM production_run_cycle_attempts attempt
            LEFT JOIN production_run_cycle_attempt_outcomes outcome
              ON outcome.attempt_id=attempt.id;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
