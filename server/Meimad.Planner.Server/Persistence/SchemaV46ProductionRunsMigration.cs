using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Introduces Production Runs as the durable Machine-session/backlog aggregate. The legacy
/// Batch Operation column remains as a compatibility projection while clients are migrated.
/// </summary>
internal sealed class SchemaV46ProductionRunsMigration : IDatabaseMigration
{
    public int Version => 46;
    public string Name => "production_runs_and_assignment_ownership";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE production_runs (
                id TEXT PRIMARY KEY,
                status TEXT NOT NULL CHECK (status IN ('DRAFT','PLANNED','IN_PROGRESS','SUSPENDED','COMPLETED','CANCELLED','ABORTED')),
                shared_setup_seconds INTEGER NOT NULL DEFAULT 0 CHECK (shared_setup_seconds >= 0),
                setup_snapshot_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(setup_snapshot_json)),
                structure_locked_at TEXT,
                legacy_batch_operation_id TEXT UNIQUE,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (legacy_batch_operation_id) REFERENCES batch_operations(id) ON DELETE RESTRICT,
                CHECK ((status IN ('DRAFT','PLANNED') AND structure_locked_at IS NULL)
                    OR status NOT IN ('DRAFT','PLANNED'))
            );

            CREATE TABLE production_run_programs (
                id TEXT PRIMARY KEY,
                production_run_id TEXT NOT NULL,
                manufacturing_program_id TEXT NOT NULL,
                process_revision_id TEXT,
                selected_gcode_release_id TEXT,
                sequence_position INTEGER NOT NULL CHECK (sequence_position >= 0),
                target_cycle_count INTEGER NOT NULL CHECK (target_cycle_count > 0),
                completed_cycle_count INTEGER NOT NULL DEFAULT 0 CHECK (completed_cycle_count >= 0),
                status TEXT NOT NULL CHECK (status IN ('PLANNED','ACTIVE','SUSPENDED','COMPLETED','CANCELLED','ABORTED')),
                cycle_seconds_snapshot REAL CHECK (cycle_seconds_snapshot IS NULL OR cycle_seconds_snapshot >= 0),
                production_process_revision_id TEXT,
                production_gcode_release_id TEXT,
                production_tool_table_release_id TEXT,
                production_gcode_file_hash TEXT CHECK (production_gcode_file_hash IS NULL OR length(production_gcode_file_hash) = 64),
                production_tool_table_file_hash TEXT CHECK (production_tool_table_file_hash IS NULL OR length(production_tool_table_file_hash) = 64),
                legacy_unmanaged INTEGER NOT NULL DEFAULT 0 CHECK (legacy_unmanaged IN (0,1)),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE (production_run_id, sequence_position),
                FOREIGN KEY (production_run_id) REFERENCES production_runs(id) ON DELETE RESTRICT,
                FOREIGN KEY (manufacturing_program_id) REFERENCES manufacturing_programs(id) ON DELETE RESTRICT,
                FOREIGN KEY (process_revision_id) REFERENCES process_revisions(id) ON DELETE RESTRICT,
                FOREIGN KEY (selected_gcode_release_id) REFERENCES gcode_releases(id) ON DELETE RESTRICT,
                FOREIGN KEY (production_process_revision_id) REFERENCES process_revisions(id) ON DELETE RESTRICT,
                FOREIGN KEY (production_gcode_release_id) REFERENCES gcode_releases(id) ON DELETE RESTRICT,
                FOREIGN KEY (production_tool_table_release_id) REFERENCES tool_table_releases(id) ON DELETE RESTRICT,
                CHECK (completed_cycle_count <= target_cycle_count),
                CHECK ((legacy_unmanaged = 1 AND process_revision_id IS NULL)
                    OR (legacy_unmanaged = 0 AND process_revision_id IS NOT NULL))
            );

            CREATE INDEX ix_production_run_programs_program
            ON production_run_programs(manufacturing_program_id, process_revision_id);

            CREATE TABLE production_run_outputs (
                id TEXT PRIMARY KEY,
                production_run_program_id TEXT NOT NULL,
                batch_operation_id TEXT NOT NULL,
                revision_output_id TEXT,
                quantity_per_cycle INTEGER NOT NULL CHECK (quantity_per_cycle > 0),
                target_quantity INTEGER NOT NULL CHECK (target_quantity > 0),
                produced_quantity INTEGER NOT NULL DEFAULT 0 CHECK (produced_quantity >= 0),
                status TEXT NOT NULL DEFAULT 'ALLOCATED'
                    CHECK (status IN ('ALLOCATED','IN_PRODUCTION','COMPLETED','RELEASED','ABORTED_REMAINDER_RELEASED')),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE (production_run_program_id, batch_operation_id),
                FOREIGN KEY (production_run_program_id) REFERENCES production_run_programs(id) ON DELETE RESTRICT,
                FOREIGN KEY (batch_operation_id) REFERENCES batch_operations(id) ON DELETE RESTRICT,
                FOREIGN KEY (revision_output_id) REFERENCES manufacturing_program_revision_outputs(id) ON DELETE RESTRICT,
                CHECK (produced_quantity <= target_quantity)
            );

            CREATE INDEX ix_production_run_outputs_batch_operation
            ON production_run_outputs(batch_operation_id, production_run_program_id);

            ALTER TABLE machine_assignments ADD COLUMN production_run_id TEXT
                REFERENCES production_runs(id) ON DELETE RESTRICT;
            CREATE UNIQUE INDEX ux_machine_assignments_production_run
            ON machine_assignments(production_run_id) WHERE production_run_id IS NOT NULL;

            INSERT INTO production_runs (
                id, status, shared_setup_seconds, setup_snapshot_json,
                structure_locked_at, legacy_batch_operation_id, version, created_at, updated_at)
            SELECT 'run:assignment:' || assignment.id,
                   CASE operation.status
                       WHEN 'in_progress' THEN 'IN_PROGRESS'
                       WHEN 'suspended' THEN 'SUSPENDED'
                       WHEN 'completed' THEN 'COMPLETED'
                       ELSE 'PLANNED'
                   END,
                   COALESCE(operation.setup_seconds, 0),
                   json_object(
                       'source', 'legacy_batch_operation',
                       'qaSeconds', operation.qa_seconds,
                       'loadUnloadSeconds', operation.load_unload_seconds,
                       'loadUnloadRequiresWorker', operation.load_unload_requires_worker,
                       'automaticLoading', operation.automatic_loading,
                       'loadUnloadEveryNParts', operation.load_unload_every_n_parts,
                       'dayShiftOnly', operation.day_shift_only),
                   CASE WHEN operation.status = 'not_started' THEN NULL
                        ELSE COALESCE(operation.actual_start, operation.updated_at) END,
                   operation.id, 1, assignment.created_at, assignment.updated_at
            FROM machine_assignments assignment
            JOIN batch_operations operation ON operation.id = assignment.batch_operation_id;

            INSERT INTO production_run_programs (
                id, production_run_id, manufacturing_program_id, process_revision_id,
                selected_gcode_release_id, sequence_position, target_cycle_count,
                completed_cycle_count, status, cycle_seconds_snapshot,
                production_process_revision_id, production_gcode_release_id,
                production_tool_table_release_id, production_gcode_file_hash,
                production_tool_table_file_hash, legacy_unmanaged, version,
                created_at, updated_at)
            SELECT 'run-program:assignment:' || assignment.id,
                   'run:assignment:' || assignment.id,
                   'case-operation:' || operation.source_case_operation_id,
                   active.id,
                   assignment.selected_gcode_release_id,
                   0, batch.planned_quantity,
                   CASE WHEN operation.status = 'completed' THEN batch.planned_quantity ELSE 0 END,
                   CASE operation.status
                       WHEN 'in_progress' THEN 'ACTIVE'
                       WHEN 'suspended' THEN 'SUSPENDED'
                       WHEN 'completed' THEN 'COMPLETED'
                       ELSE 'PLANNED'
                   END,
                   operation.cycle_seconds,
                   operation.production_process_revision_id,
                   operation.production_gcode_release_id,
                   operation.production_tool_table_release_id,
                   operation.production_gcode_file_hash,
                   operation.production_tool_table_file_hash,
                   CASE WHEN active.id IS NULL THEN 1 ELSE 0 END,
                   1, assignment.created_at, assignment.updated_at
            FROM machine_assignments assignment
            JOIN batch_operations operation ON operation.id = assignment.batch_operation_id
            JOIN production_batches batch ON batch.id = operation.production_batch_id
            LEFT JOIN process_revisions active
              ON active.manufacturing_program_id = 'case-operation:' || operation.source_case_operation_id
             AND active.is_active = 1;

            INSERT INTO production_run_outputs (
                id, production_run_program_id, batch_operation_id, revision_output_id,
                quantity_per_cycle, target_quantity, produced_quantity, status, version,
                created_at, updated_at)
            SELECT 'run-output:assignment:' || assignment.id,
                   'run-program:assignment:' || assignment.id,
                   operation.id, revision_output.id, 1, batch.planned_quantity,
                   CASE WHEN operation.status = 'completed' THEN batch.planned_quantity ELSE 0 END,
                   CASE operation.status WHEN 'completed' THEN 'COMPLETED'
                        WHEN 'in_progress' THEN 'IN_PRODUCTION'
                        WHEN 'suspended' THEN 'IN_PRODUCTION' ELSE 'ALLOCATED' END,
                   1, assignment.created_at, assignment.updated_at
            FROM machine_assignments assignment
            JOIN batch_operations operation ON operation.id = assignment.batch_operation_id
            JOIN production_batches batch ON batch.id = operation.production_batch_id
            LEFT JOIN process_revisions active
              ON active.manufacturing_program_id = 'case-operation:' || operation.source_case_operation_id
             AND active.is_active = 1
            LEFT JOIN manufacturing_program_revision_outputs revision_output
              ON revision_output.process_revision_id = active.id
             AND revision_output.case_operation_id = operation.source_case_operation_id;

            UPDATE machine_assignments
            SET production_run_id = (
                SELECT run.id FROM production_runs run
                WHERE run.legacy_batch_operation_id = machine_assignments.batch_operation_id);

            -- Rebuild assignment ownership: Production Run is authoritative and the legacy
            -- operation column is a non-unique compatibility projection for old clients.
            DROP TRIGGER machine_assignment_selected_release_matches_operation_insert;
            DROP TRIGGER machine_assignment_selected_release_matches_operation_update;
            DROP INDEX ix_machine_assignments_machine_backlog;
            DROP INDEX ix_machine_assignments_selected_gcode;
            DROP INDEX ux_machine_assignments_production_run;
            PRAGMA legacy_alter_table = ON;
            ALTER TABLE machine_assignments RENAME TO machine_assignments_v45;

            CREATE TABLE machine_assignments (
                id TEXT PRIMARY KEY,
                batch_operation_id TEXT NOT NULL,
                machine_id TEXT NOT NULL,
                backlog_position INTEGER NOT NULL CHECK (backlog_position >= 0),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                planning_mode TEXT NOT NULL DEFAULT 'manual'
                    CHECK (planning_mode IN ('forward','backward','manual')),
                selected_gcode_release_id TEXT,
                production_run_id TEXT,
                FOREIGN KEY (batch_operation_id) REFERENCES batch_operations(id) ON DELETE RESTRICT,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                FOREIGN KEY (selected_gcode_release_id) REFERENCES gcode_releases(id) ON DELETE RESTRICT,
                FOREIGN KEY (production_run_id) REFERENCES production_runs(id) ON DELETE RESTRICT,
                UNIQUE (machine_id, backlog_position),
                UNIQUE (production_run_id)
            );

            INSERT INTO machine_assignments (
                id, batch_operation_id, machine_id, backlog_position, version,
                created_at, updated_at, planning_mode, selected_gcode_release_id,
                production_run_id)
            SELECT id, batch_operation_id, machine_id, backlog_position, version,
                   created_at, updated_at, planning_mode, selected_gcode_release_id,
                   production_run_id
            FROM machine_assignments_v45;
            DROP TABLE machine_assignments_v45;
            PRAGMA legacy_alter_table = OFF;

            CREATE INDEX ix_machine_assignments_machine_backlog
            ON machine_assignments(machine_id, backlog_position);
            CREATE INDEX ix_machine_assignments_selected_gcode
            ON machine_assignments(selected_gcode_release_id);
            CREATE INDEX ix_machine_assignments_batch_operation_compatibility
            ON machine_assignments(batch_operation_id);

            CREATE TRIGGER machine_assignment_selected_release_matches_operation_insert
            BEFORE INSERT ON machine_assignments
            FOR EACH ROW
            WHEN NEW.selected_gcode_release_id IS NOT NULL
             AND NOT EXISTS (
                 SELECT 1 FROM gcode_releases release
                 JOIN production_run_programs program
                   ON program.production_run_id = NEW.production_run_id
                  AND program.process_revision_id = release.process_revision_id
                 WHERE release.id = NEW.selected_gcode_release_id)
            BEGIN
                SELECT RAISE(ABORT, 'selected G-code release must belong to the assigned Production Run');
            END;

            CREATE TRIGGER machine_assignment_selected_release_matches_operation_update
            BEFORE UPDATE OF selected_gcode_release_id, production_run_id ON machine_assignments
            FOR EACH ROW
            WHEN NEW.selected_gcode_release_id IS NOT NULL
             AND NOT EXISTS (
                 SELECT 1 FROM gcode_releases release
                 JOIN production_run_programs program
                   ON program.production_run_id = NEW.production_run_id
                  AND program.process_revision_id = release.process_revision_id
                 WHERE release.id = NEW.selected_gcode_release_id)
            BEGIN
                SELECT RAISE(ABORT, 'selected G-code release must belong to the assigned Production Run');
            END;

            CREATE TRIGGER production_run_output_target_matches_cycles_insert
            BEFORE INSERT ON production_run_outputs
            WHEN NOT EXISTS (
                SELECT 1 FROM production_run_programs program
                WHERE program.id = NEW.production_run_program_id
                  AND NEW.target_quantity = NEW.quantity_per_cycle * program.target_cycle_count)
            BEGIN
                SELECT RAISE(ABORT, 'run output target must equal quantity per cycle times target cycles');
            END;

            CREATE TRIGGER production_run_output_no_overallocation_insert
            BEFORE INSERT ON production_run_outputs
            WHEN NEW.target_quantity + COALESCE((
                SELECT SUM(existing.target_quantity)
                FROM production_run_outputs existing
                JOIN production_run_programs program ON program.id = existing.production_run_program_id
                JOIN production_runs run ON run.id = program.production_run_id
                WHERE existing.batch_operation_id = NEW.batch_operation_id
                  AND run.status NOT IN ('CANCELLED','ABORTED')), 0) > (
                SELECT batch.planned_quantity
                FROM batch_operations operation
                JOIN production_batches batch ON batch.id = operation.production_batch_id
                WHERE operation.id = NEW.batch_operation_id)
            BEGIN
                SELECT RAISE(ABORT, 'production run output would over-allocate the Batch Operation');
            END;

            CREATE TRIGGER production_run_structure_locked_program_insert
            BEFORE INSERT ON production_run_programs
            WHEN EXISTS (SELECT 1 FROM production_runs run
                         WHERE run.id = NEW.production_run_id AND run.structure_locked_at IS NOT NULL)
            BEGIN SELECT RAISE(ABORT, 'started production run structure is immutable'); END;

            CREATE TRIGGER production_run_structure_locked_program_update
            BEFORE UPDATE OF manufacturing_program_id, process_revision_id, sequence_position,
                             target_cycle_count ON production_run_programs
            WHEN EXISTS (SELECT 1 FROM production_runs run
                         WHERE run.id = OLD.production_run_id AND run.structure_locked_at IS NOT NULL)
            BEGIN SELECT RAISE(ABORT, 'started production run structure is immutable'); END;

            CREATE TRIGGER production_run_structure_locked_program_delete
            BEFORE DELETE ON production_run_programs
            WHEN EXISTS (SELECT 1 FROM production_runs run
                         WHERE run.id = OLD.production_run_id AND run.structure_locked_at IS NOT NULL)
            BEGIN SELECT RAISE(ABORT, 'started production run structure is immutable'); END;

            CREATE TRIGGER production_run_structure_locked_output_update
            BEFORE UPDATE OF batch_operation_id, revision_output_id, quantity_per_cycle,
                             target_quantity ON production_run_outputs
            WHEN EXISTS (
                SELECT 1 FROM production_run_programs program
                JOIN production_runs run ON run.id = program.production_run_id
                WHERE program.id = OLD.production_run_program_id AND run.structure_locked_at IS NOT NULL)
            BEGIN SELECT RAISE(ABORT, 'started production run structure is immutable'); END;

            CREATE TRIGGER production_run_structure_locked_output_delete
            BEFORE DELETE ON production_run_outputs
            WHEN EXISTS (
                SELECT 1 FROM production_run_programs program
                JOIN production_runs run ON run.id = program.production_run_id
                WHERE program.id = OLD.production_run_program_id AND run.structure_locked_at IS NOT NULL)
            BEGIN SELECT RAISE(ABORT, 'started production run structure is immutable'); END;

            -- Compatibility assignment: an old single-operation insert atomically reuses or
            -- creates its one-program/one-output Production Run and then binds the assignment.
            CREATE TRIGGER machine_assignment_wraps_legacy_operation
            AFTER INSERT ON machine_assignments
            WHEN NEW.production_run_id IS NULL
            BEGIN
                INSERT OR IGNORE INTO production_runs (
                    id, status, shared_setup_seconds, setup_snapshot_json,
                    structure_locked_at, legacy_batch_operation_id, version, created_at, updated_at)
                SELECT 'run:batch-operation:' || operation.id, 'PLANNED',
                       COALESCE(operation.setup_seconds, 0),
                       json_object('source','compatibility_assignment',
                                   'qaSeconds',operation.qa_seconds,
                                   'loadUnloadSeconds',operation.load_unload_seconds),
                       NULL, operation.id, 1, NEW.created_at, NEW.updated_at
                FROM batch_operations operation WHERE operation.id = NEW.batch_operation_id;

                INSERT OR IGNORE INTO production_run_programs (
                    id, production_run_id, manufacturing_program_id, process_revision_id,
                    selected_gcode_release_id, sequence_position, target_cycle_count,
                    completed_cycle_count, status, cycle_seconds_snapshot,
                    legacy_unmanaged, version, created_at, updated_at)
                SELECT 'run-program:batch-operation:' || operation.id,
                       (SELECT run.id FROM production_runs run
                        WHERE run.legacy_batch_operation_id = operation.id),
                       'case-operation:' || operation.source_case_operation_id,
                       active.id, NEW.selected_gcode_release_id, 0, batch.planned_quantity,
                       0, 'PLANNED', operation.cycle_seconds,
                       CASE WHEN active.id IS NULL THEN 1 ELSE 0 END,
                       1, NEW.created_at, NEW.updated_at
                FROM batch_operations operation
                JOIN production_batches batch ON batch.id = operation.production_batch_id
                LEFT JOIN process_revisions active
                  ON active.manufacturing_program_id = 'case-operation:' || operation.source_case_operation_id
                 AND active.is_active = 1
                WHERE operation.id = NEW.batch_operation_id;

                INSERT OR IGNORE INTO production_run_outputs (
                    id, production_run_program_id, batch_operation_id, revision_output_id,
                    quantity_per_cycle, target_quantity, produced_quantity, status, version,
                    created_at, updated_at)
                SELECT 'run-output:batch-operation:' || operation.id,
                       (SELECT program.id FROM production_run_programs program
                        JOIN production_runs run ON run.id = program.production_run_id
                        WHERE run.legacy_batch_operation_id = operation.id
                        ORDER BY program.sequence_position LIMIT 1),
                       operation.id, revision_output.id, 1, batch.planned_quantity,
                       0, 'ALLOCATED', 1, NEW.created_at, NEW.updated_at
                FROM batch_operations operation
                JOIN production_batches batch ON batch.id = operation.production_batch_id
                LEFT JOIN process_revisions active
                  ON active.manufacturing_program_id = 'case-operation:' || operation.source_case_operation_id
                 AND active.is_active = 1
                LEFT JOIN manufacturing_program_revision_outputs revision_output
                  ON revision_output.process_revision_id = active.id
                 AND revision_output.case_operation_id = operation.source_case_operation_id
                WHERE operation.id = NEW.batch_operation_id
                  AND NOT EXISTS (
                      SELECT 1 FROM production_run_outputs existing
                      JOIN production_run_programs existing_program
                        ON existing_program.id = existing.production_run_program_id
                      JOIN production_runs existing_run
                        ON existing_run.id = existing_program.production_run_id
                      WHERE existing.batch_operation_id = operation.id
                        AND existing_run.legacy_batch_operation_id = operation.id);

                UPDATE machine_assignments
                SET production_run_id = (
                    SELECT run.id FROM production_runs run
                    WHERE run.legacy_batch_operation_id = NEW.batch_operation_id)
                WHERE id = NEW.id;
            END;

            CREATE TRIGGER machine_assignment_run_required_update
            BEFORE UPDATE OF production_run_id ON machine_assignments
            WHEN NEW.production_run_id IS NULL
            BEGIN SELECT RAISE(ABORT, 'Machine Assignment requires a Production Run'); END;

            CREATE TRIGGER machine_assignment_run_contains_legacy_operation
            BEFORE UPDATE OF production_run_id ON machine_assignments
            WHEN NEW.production_run_id IS NOT NULL AND NOT EXISTS (
                SELECT 1 FROM production_run_outputs output
                JOIN production_run_programs program ON program.id = output.production_run_program_id
                WHERE program.production_run_id = NEW.production_run_id
                  AND output.batch_operation_id = NEW.batch_operation_id)
            BEGIN SELECT RAISE(ABORT, 'Machine Assignment Production Run does not contain its compatibility Batch Operation'); END;
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
