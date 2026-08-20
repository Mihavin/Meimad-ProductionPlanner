using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV37ProductionReadinessMigration : IDatabaseMigration
{
    public int Version => 37;

    public string Name => "contextual_production_readiness";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE machine_assignments
                ADD COLUMN selected_gcode_release_id TEXT NULL
                REFERENCES gcode_releases(id) ON DELETE RESTRICT;

            CREATE INDEX ix_machine_assignments_selected_gcode
                ON machine_assignments(selected_gcode_release_id);

            CREATE TABLE batch_operation_material_readiness (
                batch_operation_id TEXT PRIMARY KEY
                    REFERENCES batch_operations(id) ON DELETE CASCADE,
                status TEXT NOT NULL
                    CHECK (status IN ('UNVERIFIED', 'MISSING', 'READY')),
                confirmed_at TEXT NULL,
                confirmed_by TEXT NULL,
                comment TEXT NULL CHECK (comment IS NULL OR length(comment) <= 2000),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL
            );

            CREATE TABLE tool_offset_readiness_records (
                id TEXT PRIMARY KEY,
                batch_operation_id TEXT NOT NULL
                    REFERENCES batch_operations(id) ON DELETE CASCADE,
                machine_id TEXT NOT NULL
                    REFERENCES machines(id) ON DELETE RESTRICT,
                process_revision_id TEXT NOT NULL
                    REFERENCES process_revisions(id) ON DELETE RESTRICT,
                gcode_release_id TEXT NULL
                    REFERENCES gcode_releases(id) ON DELETE RESTRICT,
                status TEXT NOT NULL
                    CHECK (status IN ('UNVERIFIED', 'MISSING', 'READY')),
                confirmed_at TEXT NULL,
                confirmed_by TEXT NULL,
                comment TEXT NULL CHECK (comment IS NULL OR length(comment) <= 2000),
                recorded_at TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                CHECK ((status = 'READY' AND confirmed_at IS NOT NULL AND confirmed_by IS NOT NULL)
                    OR status <> 'READY')
            );

            CREATE INDEX ix_tool_offset_readiness_context
                ON tool_offset_readiness_records(
                    batch_operation_id, machine_id, process_revision_id,
                    gcode_release_id, recorded_at DESC);

            CREATE TRIGGER machine_assignment_selected_release_matches_operation_insert
            BEFORE INSERT ON machine_assignments
            FOR EACH ROW
            WHEN NEW.selected_gcode_release_id IS NOT NULL
             AND NOT EXISTS (
                 SELECT 1
                 FROM gcode_releases release
                 JOIN batch_operations operation
                   ON operation.id = NEW.batch_operation_id
                  AND operation.source_case_operation_id = release.case_operation_id
                 WHERE release.id = NEW.selected_gcode_release_id)
            BEGIN
                SELECT RAISE(ABORT, 'selected G-code release must belong to the assigned Batch Operation source');
            END;

            CREATE TRIGGER machine_assignment_selected_release_matches_operation_update
            BEFORE UPDATE OF selected_gcode_release_id, batch_operation_id ON machine_assignments
            FOR EACH ROW
            WHEN NEW.selected_gcode_release_id IS NOT NULL
             AND NOT EXISTS (
                 SELECT 1
                 FROM gcode_releases release
                 JOIN batch_operations operation
                   ON operation.id = NEW.batch_operation_id
                  AND operation.source_case_operation_id = release.case_operation_id
                 WHERE release.id = NEW.selected_gcode_release_id)
            BEGIN
                SELECT RAISE(ABORT, 'selected G-code release must belong to the assigned Batch Operation source');
            END;

            CREATE TRIGGER tool_offset_readiness_context_consistent
            BEFORE INSERT ON tool_offset_readiness_records
            FOR EACH ROW
            WHEN NOT EXISTS (
                SELECT 1
                FROM batch_operations operation
                JOIN process_revisions process
                  ON process.id = NEW.process_revision_id
                 AND process.case_operation_id = operation.source_case_operation_id
                WHERE operation.id = NEW.batch_operation_id)
              OR (NEW.gcode_release_id IS NOT NULL AND NOT EXISTS (
                SELECT 1 FROM gcode_releases release
                WHERE release.id = NEW.gcode_release_id
                  AND release.process_revision_id = NEW.process_revision_id))
            BEGIN
                SELECT RAISE(ABORT, 'tool-offset readiness context is inconsistent');
            END;

            CREATE TRIGGER tool_offset_readiness_records_immutable_update
            BEFORE UPDATE ON tool_offset_readiness_records
            BEGIN
                SELECT RAISE(ABORT, 'tool-offset readiness records are immutable');
            END;

            CREATE TRIGGER tool_offset_readiness_records_immutable_delete
            BEFORE DELETE ON tool_offset_readiness_records
            BEGIN
                SELECT RAISE(ABORT, 'tool-offset readiness records are immutable');
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
