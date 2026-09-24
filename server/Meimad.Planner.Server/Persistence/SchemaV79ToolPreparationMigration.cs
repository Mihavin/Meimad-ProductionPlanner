using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Tool Room measurements. A tool preparation is one immutable version of the measured tools of
/// a Batch Operation on its assigned Machine for one released Tool Table: per released tool its
/// offset number, measured length and diameter, shape and dimensions, and the assembled
/// components (holder, extension, collet, shank, cutter, ...). Saving creates the next version;
/// a Production Package binds the exact version it was built from. The Machine records whether
/// its control keeps diameter offsets as radius or diameter values, which the generated offset
/// program must match.
/// </summary>
internal sealed class SchemaV79ToolPreparationMigration : IDatabaseMigration
{
    public int Version => 79;
    public string Name => "tool_preparation";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE machines ADD COLUMN tool_diameter_offset_kind TEXT NOT NULL DEFAULT 'RADIUS'
                CHECK (tool_diameter_offset_kind IN ('RADIUS', 'DIAMETER'));

            CREATE TABLE tool_preparations (
                id TEXT PRIMARY KEY,
                batch_operation_id TEXT NOT NULL
                    REFERENCES batch_operations(id) ON DELETE CASCADE,
                machine_id TEXT NOT NULL
                    REFERENCES machines(id) ON DELETE RESTRICT,
                tool_table_release_id TEXT NOT NULL
                    REFERENCES tool_table_releases(id) ON DELETE RESTRICT,
                version_number INTEGER NOT NULL CHECK (version_number > 0),
                saved_at TEXT NOT NULL,
                saved_by TEXT NOT NULL CHECK (length(trim(saved_by)) > 0),
                comment TEXT NULL CHECK (comment IS NULL OR length(comment) <= 2000),
                content_hash TEXT NOT NULL CHECK (length(content_hash) = 64),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                UNIQUE (batch_operation_id, machine_id, version_number)
            );

            CREATE INDEX ix_tool_preparations_context
                ON tool_preparations (batch_operation_id, machine_id, version_number DESC);

            CREATE TABLE tool_preparation_tools (
                id TEXT PRIMARY KEY,
                tool_preparation_id TEXT NOT NULL
                    REFERENCES tool_preparations(id) ON DELETE CASCADE,
                row_number INTEGER NOT NULL CHECK (row_number > 0),
                tool_identifier TEXT NOT NULL CHECK (length(trim(tool_identifier)) BETWEEN 1 AND 80),
                offset_number INTEGER NULL CHECK (offset_number IS NULL OR offset_number BETWEEN 1 AND 9999),
                measured_length REAL NULL CHECK (measured_length IS NULL OR measured_length >= 0),
                measured_diameter REAL NULL CHECK (measured_diameter IS NULL OR measured_diameter >= 0),
                shape_type TEXT NOT NULL CHECK (shape_type IN (
                    'END_MILL', 'BALL_END_MILL', 'BULL_NOSE_END_MILL', 'CHAMFER_MILL', 'FACE_MILL',
                    'DRILL', 'TAP', 'REAMER', 'BORING_BAR', 'TURNING_TOOL', 'PROBE', 'OTHER')),
                shape_json TEXT NOT NULL CHECK (json_valid(shape_json)),
                notes TEXT NULL CHECK (notes IS NULL OR length(notes) <= 1000),
                UNIQUE (tool_preparation_id, row_number),
                UNIQUE (tool_preparation_id, tool_identifier)
            );

            CREATE TABLE tool_preparation_components (
                id TEXT PRIMARY KEY,
                tool_preparation_tool_id TEXT NOT NULL
                    REFERENCES tool_preparation_tools(id) ON DELETE CASCADE,
                sequence INTEGER NOT NULL CHECK (sequence > 0),
                component_type TEXT NOT NULL CHECK (component_type IN (
                    'HOLDER', 'EXTENSION', 'COLLET', 'ARBOR', 'SHANK', 'CUTTER', 'INSERT', 'OTHER')),
                name TEXT NOT NULL CHECK (length(trim(name)) BETWEEN 1 AND 120),
                catalog_number TEXT NULL CHECK (catalog_number IS NULL OR length(catalog_number) <= 120),
                length REAL NULL CHECK (length IS NULL OR length >= 0),
                diameter REAL NULL CHECK (diameter IS NULL OR diameter >= 0),
                notes TEXT NULL CHECK (notes IS NULL OR length(notes) <= 500),
                UNIQUE (tool_preparation_tool_id, sequence)
            );

            CREATE TRIGGER tool_preparations_immutable_update
            BEFORE UPDATE ON tool_preparations
            BEGIN
                SELECT RAISE(ABORT, 'tool_preparations are immutable');
            END;

            CREATE TRIGGER tool_preparations_immutable_delete
            BEFORE DELETE ON tool_preparations
            BEGIN
                SELECT RAISE(ABORT, 'tool_preparations are immutable');
            END;

            CREATE TRIGGER tool_preparation_tools_immutable_update
            BEFORE UPDATE ON tool_preparation_tools
            BEGIN
                SELECT RAISE(ABORT, 'tool_preparation_tools are immutable');
            END;

            CREATE TRIGGER tool_preparation_components_immutable_update
            BEFORE UPDATE ON tool_preparation_components
            BEGIN
                SELECT RAISE(ABORT, 'tool_preparation_components are immutable');
            END;

            ALTER TABLE production_packages ADD COLUMN tool_preparation_id TEXT NULL
                REFERENCES tool_preparations(id) ON DELETE RESTRICT;

            -- The artifact type check of schema v64 is part of the table definition, so the table is
            -- rebuilt to admit the measured tool offsets (JSON) and the offset-input program.
            DROP TRIGGER production_package_artifacts_immutable_update;
            DROP TRIGGER production_package_artifacts_immutable_delete;
            CREATE TABLE production_package_artifacts_v79 (
                id TEXT PRIMARY KEY,
                production_package_id TEXT NOT NULL,
                artifact_type TEXT NOT NULL CHECK (artifact_type IN (
                    'RUNNABLE_NC','TOOL_TABLE','OFFSET_LOADER','MANUAL_SETUP','MANIFEST',
                    'TOOL_OFFSETS','TOOL_OFFSET_PROGRAM')),
                logical_path TEXT NOT NULL,
                stored_relative_path TEXT NOT NULL UNIQUE,
                file_size INTEGER NOT NULL CHECK (file_size>0),
                file_hash TEXT NOT NULL CHECK (length(file_hash)=64),
                source_release_id TEXT,
                UNIQUE(production_package_id,logical_path),
                FOREIGN KEY (production_package_id) REFERENCES production_packages(id) ON DELETE RESTRICT
            );
            INSERT INTO production_package_artifacts_v79 (
                id, production_package_id, artifact_type, logical_path, stored_relative_path,
                file_size, file_hash, source_release_id)
            SELECT id, production_package_id, artifact_type, logical_path, stored_relative_path,
                   file_size, file_hash, source_release_id
            FROM production_package_artifacts;
            DROP TABLE production_package_artifacts;
            ALTER TABLE production_package_artifacts_v79 RENAME TO production_package_artifacts;
            CREATE TRIGGER production_package_artifacts_immutable_update BEFORE UPDATE ON production_package_artifacts
            BEGIN SELECT RAISE(ABORT,'Production Package artifacts are immutable'); END;
            CREATE TRIGGER production_package_artifacts_immutable_delete BEFORE DELETE ON production_package_artifacts
            BEGIN SELECT RAISE(ABORT,'Production Package artifacts are immutable'); END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
