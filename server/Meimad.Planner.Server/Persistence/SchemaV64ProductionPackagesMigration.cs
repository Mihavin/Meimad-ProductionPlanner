using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV64ProductionPackagesMigration : IDatabaseMigration
{
    public int Version => 64;
    public string Name => "server_owned_production_packages";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE production_packages (
                id TEXT PRIMARY KEY,
                batch_operation_id TEXT NOT NULL,
                production_run_id TEXT,
                machine_assignment_id TEXT NOT NULL,
                machine_id TEXT NOT NULL,
                gcode_release_id TEXT,
                tool_table_release_id TEXT NOT NULL,
                offset_loader_release_id TEXT,
                execution_mode TEXT NOT NULL CHECK (execution_mode IN ('CNC_GCODE','MANUAL')),
                verification_enabled INTEGER NOT NULL CHECK (verification_enabled IN (0,1)),
                verification_configuration_version INTEGER,
                verification_macro_version INTEGER,
                manifest_relative_path TEXT NOT NULL UNIQUE,
                manifest_hash TEXT NOT NULL CHECK (length(manifest_hash)=64),
                created_at TEXT NOT NULL,
                created_by TEXT NOT NULL CHECK (length(trim(created_by))>0),
                supersedes_package_id TEXT,
                FOREIGN KEY (batch_operation_id) REFERENCES batch_operations(id) ON DELETE RESTRICT,
                FOREIGN KEY (production_run_id) REFERENCES production_runs(id) ON DELETE RESTRICT,
                FOREIGN KEY (machine_assignment_id) REFERENCES machine_assignments(id) ON DELETE RESTRICT,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                FOREIGN KEY (gcode_release_id) REFERENCES gcode_releases(id) ON DELETE RESTRICT,
                FOREIGN KEY (tool_table_release_id) REFERENCES tool_table_releases(id) ON DELETE RESTRICT,
                FOREIGN KEY (offset_loader_release_id) REFERENCES offset_loader_releases(id) ON DELETE RESTRICT,
                FOREIGN KEY (supersedes_package_id) REFERENCES production_packages(id) ON DELETE RESTRICT,
                CHECK ((execution_mode='MANUAL' AND gcode_release_id IS NULL
                        AND offset_loader_release_id IS NULL AND verification_enabled=0)
                    OR execution_mode='CNC_GCODE'),
                CHECK ((verification_enabled=1 AND verification_configuration_version IS NOT NULL
                        AND verification_macro_version IS NOT NULL AND offset_loader_release_id IS NOT NULL)
                    OR (verification_enabled=0 AND verification_configuration_version IS NULL
                        AND verification_macro_version IS NULL AND offset_loader_release_id IS NULL))
            );
            CREATE INDEX ix_production_packages_operation_time
                ON production_packages(batch_operation_id,created_at DESC,id);

            CREATE TABLE production_package_artifacts (
                id TEXT PRIMARY KEY,
                production_package_id TEXT NOT NULL,
                artifact_type TEXT NOT NULL CHECK (artifact_type IN (
                    'RUNNABLE_NC','TOOL_TABLE','OFFSET_LOADER','MANUAL_SETUP','MANIFEST')),
                logical_path TEXT NOT NULL,
                stored_relative_path TEXT NOT NULL UNIQUE,
                file_size INTEGER NOT NULL CHECK (file_size>0),
                file_hash TEXT NOT NULL CHECK (length(file_hash)=64),
                source_release_id TEXT,
                UNIQUE(production_package_id,logical_path),
                FOREIGN KEY (production_package_id) REFERENCES production_packages(id) ON DELETE RESTRICT
            );

            CREATE TABLE production_package_current (
                batch_operation_id TEXT PRIMARY KEY,
                machine_id TEXT NOT NULL,
                production_package_id TEXT NOT NULL UNIQUE,
                activated_at TEXT NOT NULL,
                FOREIGN KEY (batch_operation_id) REFERENCES batch_operations(id) ON DELETE RESTRICT,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                FOREIGN KEY (production_package_id) REFERENCES production_packages(id) ON DELETE RESTRICT
            );

            CREATE TABLE production_package_invalidations (
                id TEXT PRIMARY KEY,
                production_package_id TEXT NOT NULL UNIQUE,
                replacement_package_id TEXT,
                reason TEXT NOT NULL,
                invalidated_at TEXT NOT NULL,
                FOREIGN KEY (production_package_id) REFERENCES production_packages(id) ON DELETE RESTRICT,
                FOREIGN KEY (replacement_package_id) REFERENCES production_packages(id) ON DELETE RESTRICT
            );

            CREATE TRIGGER production_packages_immutable_update BEFORE UPDATE ON production_packages
            BEGIN SELECT RAISE(ABORT,'Production Packages are immutable'); END;
            CREATE TRIGGER production_packages_immutable_delete BEFORE DELETE ON production_packages
            BEGIN SELECT RAISE(ABORT,'Production Packages are immutable'); END;
            CREATE TRIGGER production_package_artifacts_immutable_update BEFORE UPDATE ON production_package_artifacts
            BEGIN SELECT RAISE(ABORT,'Production Package artifacts are immutable'); END;
            CREATE TRIGGER production_package_artifacts_immutable_delete BEFORE DELETE ON production_package_artifacts
            BEGIN SELECT RAISE(ABORT,'Production Package artifacts are immutable'); END;
            CREATE TRIGGER production_package_invalidations_immutable_update BEFORE UPDATE ON production_package_invalidations
            BEGIN SELECT RAISE(ABORT,'Production Package invalidations are immutable'); END;
            CREATE TRIGGER production_package_invalidations_immutable_delete BEFORE DELETE ON production_package_invalidations
            BEGIN SELECT RAISE(ABORT,'Production Package invalidations are immutable'); END;

            CREATE TRIGGER production_package_current_consistent_insert
            BEFORE INSERT ON production_package_current
            WHEN NOT EXISTS (
                SELECT 1 FROM production_packages package
                WHERE package.id=NEW.production_package_id
                  AND package.batch_operation_id=NEW.batch_operation_id
                  AND package.machine_id=NEW.machine_id)
            BEGIN SELECT RAISE(ABORT,'Current Production Package context is inconsistent'); END;
            CREATE TRIGGER production_package_current_consistent_update
            BEFORE UPDATE ON production_package_current
            WHEN NOT EXISTS (
                SELECT 1 FROM production_packages package
                WHERE package.id=NEW.production_package_id
                  AND package.batch_operation_id=NEW.batch_operation_id
                  AND package.machine_id=NEW.machine_id)
            BEGIN SELECT RAISE(ABORT,'Current Production Package context is inconsistent'); END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
