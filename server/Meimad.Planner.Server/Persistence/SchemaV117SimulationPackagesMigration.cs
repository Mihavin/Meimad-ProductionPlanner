using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Cimatron simulation packages (Cimatron → Meimad exporter definition §8–§14).
/// <c>simulation_package_imports</c> records every imported package of a Case Operation: the manifest
/// SHA-256 (the idempotency key — one active import per Operation and manifest), the exporter and
/// Cimatron versions, the source, the operation/procedure, the target machine as exported and the
/// Meimad Machine it was mapped to (and how), the coordinate system, the stock the planner chose, the
/// NC and tool table hashes and the stored import report. <c>status</c> is <c>imported</c> for the
/// Operation's current package, <c>superseded</c> once a newer package replaced it, and <c>blocked</c>
/// for a watcher attempt that a blocking diagnostic stopped (its Case/Operation may be unknown).
/// <c>simulation_package_import_files</c> lists every manifest file with its role, relative path and
/// hash, and the Case model-file link the import created for it (stock, fixtures, finished part).
/// <c>simulation_package_machine_mappings</c> holds the Setup-managed mapping of Cimatron machine
/// identities to Meimad Machines: <c>external_id</c> rows are the configured external machine id (one
/// Machine each, mapping step 1), <c>postprocessor</c> and <c>machine_name</c> rows are the explicit
/// mapping table (step 3). The package files stay where the exporter wrote them; nothing is copied.
/// Package data is CAM context, not a planning input: the triggers advance the all-input and
/// non-planning revisions only.
/// </summary>
internal sealed class SchemaV117SimulationPackagesMigration : IDatabaseMigration
{
    public int Version => 117;
    public string Name => "simulation_packages";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE simulation_package_imports (
                id TEXT NOT NULL PRIMARY KEY,
                case_id TEXT NULL REFERENCES cases (id) ON DELETE CASCADE,
                case_operation_id TEXT NULL REFERENCES case_operations (id) ON DELETE CASCADE,
                status TEXT NOT NULL CHECK (status IN ('imported', 'superseded', 'blocked')),
                package_id TEXT NULL CHECK (package_id IS NULL OR length(package_id) <= 64),
                manifest_sha256 TEXT NOT NULL CHECK (length(manifest_sha256) = 64 AND manifest_sha256 NOT GLOB '*[^0-9a-f]*'),
                schema_version INTEGER NULL,
                package_path TEXT NOT NULL CHECK (length(package_path) BETWEEN 1 AND 1000),
                exporter_name TEXT NULL,
                exporter_version TEXT NULL,
                cimatron_version TEXT NULL,
                source_application TEXT NULL,
                source_project TEXT NULL,
                nc_file_name TEXT NULL,
                exported_at TEXT NULL,
                operation_number TEXT NULL,
                operation_name TEXT NULL,
                procedure_id INTEGER NULL,
                procedure_name TEXT NULL,
                machine_external_id TEXT NULL,
                machine_name TEXT NULL,
                postprocessor TEXT NULL,
                axis_configuration TEXT NULL,
                machine_id TEXT NULL REFERENCES machines (id) ON DELETE SET NULL,
                machine_matched_by TEXT NULL CHECK (machine_matched_by IS NULL OR machine_matched_by IN
                    ('external_id', 'postprocessor', 'mapping_table', 'compatible_pair', 'planner_selected')),
                units TEXT NULL,
                coordinate_system_json TEXT NULL CHECK (coordinate_system_json IS NULL OR json_valid(coordinate_system_json)),
                selected_stock_path TEXT NULL,
                nc_sha256 TEXT NULL CHECK (nc_sha256 IS NULL OR (length(nc_sha256) = 64 AND nc_sha256 NOT GLOB '*[^0-9a-f]*')),
                tool_table_sha256 TEXT NULL CHECK (tool_table_sha256 IS NULL OR (length(tool_table_sha256) = 64 AND tool_table_sha256 NOT GLOB '*[^0-9a-f]*')),
                tool_numbers_json TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(tool_numbers_json) AND json_type(tool_numbers_json) = 'array'),
                report_json TEXT NOT NULL CHECK (json_valid(report_json)),
                imported_at TEXT NOT NULL,
                imported_by TEXT NOT NULL,
                superseded_at TEXT NULL,
                superseded_by_import_id TEXT NULL,
                CHECK (status = 'blocked' OR (case_id IS NOT NULL AND case_operation_id IS NOT NULL AND nc_sha256 IS NOT NULL))
            );
            CREATE UNIQUE INDEX ux_simulation_package_imports_active
                ON simulation_package_imports (case_operation_id, manifest_sha256) WHERE status = 'imported';
            CREATE INDEX ix_simulation_package_imports_operation
                ON simulation_package_imports (case_operation_id, imported_at);
            CREATE INDEX ix_simulation_package_imports_manifest
                ON simulation_package_imports (manifest_sha256, status);

            CREATE TABLE simulation_package_import_files (
                import_id TEXT NOT NULL REFERENCES simulation_package_imports (id) ON DELETE CASCADE,
                role TEXT NOT NULL CHECK (role IN ('nc', 'initialStock', 'remainingStock', 'machinedStock', 'unknownStock',
                    'fixture', 'part', 'machineScene', 'toolTable', 'toolReport', 'cutters', 'holders', 'materials',
                    'jobsXml', 'coordinateSystems')),
                relative_path TEXT NOT NULL CHECK (length(relative_path) BETWEEN 1 AND 260),
                sha256 TEXT NOT NULL CHECK (length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'),
                size_bytes INTEGER NOT NULL CHECK (size_bytes >= 0),
                stock_kind TEXT NULL CHECK (stock_kind IS NULL OR stock_kind IN ('initial', 'remaining', 'machined')),
                source_operation TEXT NULL,
                name TEXT NULL,
                collision INTEGER NULL CHECK (collision IS NULL OR collision IN (0, 1)),
                selected INTEGER NOT NULL DEFAULT 0 CHECK (selected IN (0, 1)),
                case_model_file_id TEXT NULL REFERENCES case_model_files (id) ON DELETE SET NULL,
                PRIMARY KEY (import_id, relative_path)
            );
            CREATE INDEX ix_simulation_package_import_files_model_file
                ON simulation_package_import_files (case_model_file_id);

            CREATE TABLE simulation_package_machine_mappings (
                id TEXT NOT NULL PRIMARY KEY,
                match_field TEXT NOT NULL CHECK (match_field IN ('external_id', 'postprocessor', 'machine_name')),
                match_value TEXT NOT NULL COLLATE NOCASE CHECK (length(trim(match_value)) BETWEEN 1 AND 200),
                machine_id TEXT NOT NULL REFERENCES machines (id) ON DELETE CASCADE,
                created_at TEXT NOT NULL,
                created_by TEXT NOT NULL,
                UNIQUE (match_field, match_value, machine_id)
            );
            CREATE UNIQUE INDEX ux_simulation_package_machine_external_ids
                ON simulation_package_machine_mappings (match_value) WHERE match_field = 'external_id';
            """;
        await command.ExecuteNonQueryAsync(token);
        foreach (var table in new[] { "simulation_package_imports", "simulation_package_import_files", "simulation_package_machine_mappings" })
        {
            foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                command.CommandText = $"""
                    CREATE TRIGGER projection_revision_{table}_{action.ToLowerInvariant()}
                    AFTER {action} ON {table}
                    BEGIN UPDATE projection_revision
                          SET revision = revision + 1, non_planning_revision = non_planning_revision + 1
                          WHERE id = 1; END;
                    """;
                await command.ExecuteNonQueryAsync(token);
            }
        }
    }
}
