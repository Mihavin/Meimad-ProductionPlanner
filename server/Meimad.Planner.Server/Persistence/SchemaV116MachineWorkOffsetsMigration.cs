using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Measured work offsets from the Machines (exporter definition §20, execution plan stage 6):
/// <c>machine_work_offsets</c> keeps every received measurement - G54..G59, G54.1 Pn, G154 Pn, G15 Hn - as
/// append-only evidence with its raw message, hash, session, source, Machine, Case Operation and
/// NC release. Rows are never deleted and their measurement never changes; only the status moves
/// along the allowed transitions (VALID → SUPERSEDED when a newer VALID record of the same key
/// arrives, REVIEW_REQUIRED → VALID or REJECTED by a reviewer) with an optimistic version. At most one
/// VALID row exists per Machine + Case Operation + NC release + work offset: the current measurement.
/// Unmatched evidence (unknown or ambiguous Machine, NC release or Operation) is kept in the same
/// table with status UNMATCHED and the matched references left null. Measurements are not planning
/// inputs: the triggers advance the all-input and non-planning revisions only.
/// </summary>
internal sealed class SchemaV116MachineWorkOffsetsMigration : IDatabaseMigration
{
    public int Version => 116;
    public string Name => "machine_work_offsets";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE machine_work_offsets (
                id TEXT NOT NULL PRIMARY KEY,
                machine_id TEXT NULL REFERENCES machines (id) ON DELETE RESTRICT,
                case_operation_id TEXT NULL REFERENCES case_operations (id) ON DELETE RESTRICT,
                program_release_id TEXT NULL REFERENCES gcode_releases (id) ON DELETE RESTRICT,
                work_offset TEXT NOT NULL CHECK (length(work_offset) BETWEEN 1 AND 32),
                x_mm REAL NULL, y_mm REAL NULL, z_mm REAL NULL,
                a_deg REAL NULL, b_deg REAL NULL, c_deg REAL NULL,
                units TEXT NULL CHECK (units IS NULL OR units IN ('MM', 'INCH')),
                source TEXT NOT NULL CHECK (length(source) BETWEEN 1 AND 32),
                source_session TEXT NOT NULL CHECK (length(source_session) BETWEEN 1 AND 128),
                session_key TEXT NOT NULL CHECK (length(session_key) BETWEEN 1 AND 300),
                source_sequence INTEGER NULL,
                claimed_machine TEXT NULL,
                claimed_program TEXT NULL,
                claimed_operation TEXT NULL,
                claimed_release TEXT NULL,
                raw_message TEXT NOT NULL CHECK (length(raw_message) BETWEEN 1 AND 16384),
                raw_message_hash TEXT NOT NULL UNIQUE CHECK (length(raw_message_hash) BETWEEN 8 AND 128),
                measured_at TEXT NOT NULL,
                received_at TEXT NOT NULL,
                received_by TEXT NOT NULL CHECK (length(received_by) BETWEEN 1 AND 200),
                initial_status TEXT NOT NULL
                    CHECK (initial_status IN ('VALID', 'REVIEW_REQUIRED', 'REJECTED', 'UNMATCHED')),
                status TEXT NOT NULL
                    CHECK (status IN ('VALID', 'REVIEW_REQUIRED', 'REJECTED', 'SUPERSEDED', 'UNMATCHED')),
                status_reason TEXT NULL,
                supersedes_id TEXT NULL REFERENCES machine_work_offsets (id) ON DELETE RESTRICT,
                reviewed_by TEXT NULL,
                reviewed_at TEXT NULL,
                review_comment TEXT NULL CHECK (review_comment IS NULL OR length(review_comment) <= 1000),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                CHECK (status NOT IN ('VALID', 'SUPERSEDED', 'REVIEW_REQUIRED')
                       OR (machine_id IS NOT NULL AND case_operation_id IS NOT NULL
                           AND program_release_id IS NOT NULL AND units IS NOT NULL
                           AND (x_mm IS NOT NULL OR y_mm IS NOT NULL OR z_mm IS NOT NULL)))
            );

            CREATE UNIQUE INDEX ux_machine_work_offsets_current
            ON machine_work_offsets (machine_id, case_operation_id, program_release_id, work_offset)
            WHERE status = 'VALID';

            CREATE INDEX ix_machine_work_offsets_operation
            ON machine_work_offsets (machine_id, case_operation_id, work_offset, measured_at);

            CREATE INDEX ix_machine_work_offsets_status
            ON machine_work_offsets (status, received_at);

            CREATE INDEX ix_machine_work_offsets_session
            ON machine_work_offsets (session_key);

            CREATE TRIGGER machine_work_offsets_append_only_delete
            BEFORE DELETE ON machine_work_offsets
            BEGIN SELECT RAISE(ABORT, 'machine work offsets are append-only evidence'); END;

            CREATE TRIGGER machine_work_offsets_measurement_immutable
            BEFORE UPDATE ON machine_work_offsets
            WHEN NEW.id IS NOT OLD.id
              OR NEW.machine_id IS NOT OLD.machine_id
              OR NEW.case_operation_id IS NOT OLD.case_operation_id
              OR NEW.program_release_id IS NOT OLD.program_release_id
              OR NEW.work_offset IS NOT OLD.work_offset
              OR NEW.x_mm IS NOT OLD.x_mm OR NEW.y_mm IS NOT OLD.y_mm OR NEW.z_mm IS NOT OLD.z_mm
              OR NEW.a_deg IS NOT OLD.a_deg OR NEW.b_deg IS NOT OLD.b_deg OR NEW.c_deg IS NOT OLD.c_deg
              OR NEW.units IS NOT OLD.units
              OR NEW.source IS NOT OLD.source
              OR NEW.source_session IS NOT OLD.source_session
              OR NEW.session_key IS NOT OLD.session_key
              OR NEW.source_sequence IS NOT OLD.source_sequence
              OR NEW.claimed_machine IS NOT OLD.claimed_machine
              OR NEW.claimed_program IS NOT OLD.claimed_program
              OR NEW.claimed_operation IS NOT OLD.claimed_operation
              OR NEW.claimed_release IS NOT OLD.claimed_release
              OR NEW.raw_message IS NOT OLD.raw_message
              OR NEW.raw_message_hash IS NOT OLD.raw_message_hash
              OR NEW.measured_at IS NOT OLD.measured_at
              OR NEW.received_at IS NOT OLD.received_at
              OR NEW.received_by IS NOT OLD.received_by
              OR NEW.initial_status IS NOT OLD.initial_status
              OR NEW.status_reason IS NOT OLD.status_reason
              OR (OLD.supersedes_id IS NOT NULL AND NEW.supersedes_id IS NOT OLD.supersedes_id)
            BEGIN SELECT RAISE(ABORT, 'a measured work offset never changes; only its status moves'); END;

            CREATE TRIGGER machine_work_offsets_status_transition
            BEFORE UPDATE ON machine_work_offsets
            WHEN NEW.version <> OLD.version + 1
              OR NOT (
                    (OLD.status = 'VALID' AND NEW.status = 'SUPERSEDED')
                 OR (OLD.status = 'REVIEW_REQUIRED' AND NEW.status IN ('VALID', 'REJECTED')))
            BEGIN SELECT RAISE(ABORT, 'invalid measured work offset status transition'); END;
            """;
        await command.ExecuteNonQueryAsync(token);
        foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            command.CommandText = $"""
                CREATE TRIGGER projection_revision_machine_work_offsets_{action.ToLowerInvariant()}
                AFTER {action} ON machine_work_offsets
                BEGIN UPDATE projection_revision
                      SET revision = revision + 1, non_planning_revision = non_planning_revision + 1
                      WHERE id = 1; END;
                """;
            await command.ExecuteNonQueryAsync(token);
        }
    }
}
