using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Adds reusable Manufacturing Programs while retaining every existing release and artifact ID.
/// </summary>
internal sealed class SchemaV45ManufacturingProgramsMigration : IDatabaseMigration
{
    public int Version => 45;
    public string Name => "manufacturing_programs";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE manufacturing_programs (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL CHECK (length(trim(name)) > 0),
                default_case_operation_id TEXT UNIQUE,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (default_case_operation_id) REFERENCES case_operations (id) ON DELETE CASCADE
            );

            INSERT INTO manufacturing_programs (
                id, name, default_case_operation_id, version, created_at, updated_at)
            SELECT 'case-operation:' || operation.id,
                   cases.part_number || ' OP' || printf('%02d', operation.operation_number) || ' ' || operation.name,
                   operation.id, 1, operation.created_at, operation.updated_at
            FROM case_operations operation
            JOIN cases ON cases.id = operation.case_id;

            ALTER TABLE process_revisions ADD COLUMN manufacturing_program_id TEXT
                REFERENCES manufacturing_programs (id) ON DELETE RESTRICT;

            UPDATE process_revisions
            SET manufacturing_program_id = 'case-operation:' || case_operation_id;

            DROP INDEX ux_process_revisions_active_operation;
            CREATE UNIQUE INDEX ux_process_revisions_active_program
            ON process_revisions (manufacturing_program_id)
            WHERE is_active = 1;
            CREATE INDEX ix_process_revisions_program_history
            ON process_revisions (manufacturing_program_id, revision_number DESC);

            CREATE TABLE manufacturing_program_revision_outputs (
                id TEXT PRIMARY KEY,
                process_revision_id TEXT NOT NULL,
                case_operation_id TEXT NOT NULL,
                quantity_per_cycle INTEGER NOT NULL CHECK (quantity_per_cycle > 0),
                display_order INTEGER NOT NULL CHECK (display_order >= 0),
                execution_metadata_json TEXT NOT NULL DEFAULT '{}'
                    CHECK (json_valid(execution_metadata_json)),
                created_at TEXT NOT NULL,
                UNIQUE (process_revision_id, case_operation_id),
                UNIQUE (process_revision_id, display_order),
                FOREIGN KEY (process_revision_id) REFERENCES process_revisions (id) ON DELETE RESTRICT,
                FOREIGN KEY (case_operation_id) REFERENCES case_operations (id) ON DELETE RESTRICT
            );

            INSERT INTO manufacturing_program_revision_outputs (
                id, process_revision_id, case_operation_id, quantity_per_cycle,
                display_order, execution_metadata_json, created_at)
            SELECT 'output:' || revision.id || ':' || revision.case_operation_id,
                   revision.id, revision.case_operation_id, 1, 0,
                   json_object('caseOperationId', revision.case_operation_id,
                               'migratedFromOperationProcess', 1),
                   revision.created_at
            FROM process_revisions revision;

            CREATE INDEX ix_manufacturing_program_outputs_operation
            ON manufacturing_program_revision_outputs (case_operation_id, process_revision_id);

            CREATE TRIGGER process_revisions_program_immutable
            BEFORE UPDATE OF manufacturing_program_id ON process_revisions
            WHEN NEW.manufacturing_program_id IS NOT OLD.manufacturing_program_id
            BEGIN
                SELECT RAISE(ABORT, 'manufacturing program revision ownership is immutable');
            END;

            CREATE TRIGGER manufacturing_program_outputs_immutable_update
            BEFORE UPDATE ON manufacturing_program_revision_outputs
            BEGIN
                SELECT RAISE(ABORT, 'manufacturing program revision output is immutable');
            END;

            CREATE TRIGGER manufacturing_program_outputs_immutable_delete
            BEFORE DELETE ON manufacturing_program_revision_outputs
            BEGIN
                SELECT RAISE(ABORT, 'manufacturing program revision output is immutable');
            END;

            CREATE TRIGGER case_operations_create_default_manufacturing_program
            AFTER INSERT ON case_operations
            BEGIN
                INSERT INTO manufacturing_programs (
                    id, name, default_case_operation_id, version, created_at, updated_at)
                SELECT 'case-operation:' || NEW.id,
                       cases.part_number || ' OP' || printf('%02d', NEW.operation_number) || ' ' || NEW.name,
                       NEW.id, 1, NEW.created_at, NEW.updated_at
                FROM cases WHERE cases.id = NEW.case_id;
            END;
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
