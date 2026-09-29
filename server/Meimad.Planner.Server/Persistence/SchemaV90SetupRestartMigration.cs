using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// A running operation goes back to setup for a newer G-code release (owner decisions 2026-09-29):
/// when a planner refreshes a Work Order from its Case, a started operation whose production
/// release has a newer local version for the same Postprocessor is set up again like a new one.
/// The workflow event `SETUP_RESTARTED` returns its Production Run to "ready for setup" (so cycles
/// count only after verification and first-part QC again), and `batch_operation_setup_restarts`
/// records the restart until a new Production Package pins the new release. The workflow event
/// table is rebuilt because its event-type check is part of the table definition.
/// </summary>
internal sealed class SchemaV90SetupRestartMigration : IDatabaseMigration
{
    public int Version => 90;

    public string Name => "setup_restart_for_new_nc_release";

    public bool DisablesForeignKeyEnforcement => true;

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        // The table's own indexes and triggers (v49, v54, v58, v59) are re-created exactly as
        // they are, after the rebuilt table takes the name back.
        var dependents = new List<string>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT sql FROM sqlite_master
                WHERE tbl_name = 'production_run_workflow_events'
                  AND type IN ('index', 'trigger') AND sql IS NOT NULL
                ORDER BY CASE type WHEN 'index' THEN 0 ELSE 1 END, name;
                """;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) dependents.Add(reader.GetString(0));
        }

        await using (var rebuild = connection.CreateCommand())
        {
            rebuild.Transaction = transaction;
            rebuild.CommandText = """
                CREATE TABLE production_run_workflow_events_v90 (
                    id TEXT PRIMARY KEY,
                    production_run_id TEXT NOT NULL,
                    machine_id TEXT NOT NULL,
                    event_type TEXT NOT NULL CHECK (event_type IN (
                        'OFFSET_LOADER_COMPLETED',
                        'SETUP_VERIFICATION_REQUESTED',
                        'SETUP_VERIFICATION_SUCCEEDED',
                        'SETUP_VERIFICATION_FAILED',
                        'SEND_TO_QC',
                        'QC_PASS',
                        'QC_FAIL',
                        'CYCLE_START',
                        'CYCLE_END',
                        'CYCLE_INTERRUPTED',
                        'PRODUCTION_SESSION_OPENED',
                        'PRODUCTION_SESSION_CLOSED',
                        'SETUP_RESTARTED')),
                    source TEXT NOT NULL,
                    source_event_id TEXT,
                    source_sequence INTEGER,
                    server_received_at TEXT NOT NULL,
                    machine_timestamp TEXT,
                    nc_release_id TEXT,
                    offset_loader_release_id TEXT,
                    tablet_device_id TEXT,
                    user_id TEXT,
                    metadata_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(metadata_json)),
                    FOREIGN KEY (production_run_id) REFERENCES production_runs(id) ON DELETE RESTRICT,
                    FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                    FOREIGN KEY (nc_release_id) REFERENCES gcode_releases(id) ON DELETE RESTRICT,
                    FOREIGN KEY (tablet_device_id) REFERENCES device_registry(id) ON DELETE RESTRICT
                );
                INSERT INTO production_run_workflow_events_v90 (
                    id, production_run_id, machine_id, event_type, source, source_event_id,
                    source_sequence, server_received_at, machine_timestamp, nc_release_id,
                    offset_loader_release_id, tablet_device_id, user_id, metadata_json)
                SELECT id, production_run_id, machine_id, event_type, source, source_event_id,
                       source_sequence, server_received_at, machine_timestamp, nc_release_id,
                       offset_loader_release_id, tablet_device_id, user_id, metadata_json
                FROM production_run_workflow_events;
                DROP TABLE production_run_workflow_events;
                -- Legacy renaming leaves the references of other tables and of the cycle-event
                -- trigger alone; they name the table, which exists again after the rename.
                PRAGMA legacy_alter_table = ON;
                ALTER TABLE production_run_workflow_events_v90 RENAME TO production_run_workflow_events;
                PRAGMA legacy_alter_table = OFF;
                """;
            await rebuild.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var sql in dependents)
        {
            await using var recreate = connection.CreateCommand();
            recreate.Transaction = transaction;
            recreate.CommandText = sql + ";";
            await recreate.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var restarts = connection.CreateCommand();
        restarts.Transaction = transaction;
        restarts.CommandText = """
            CREATE TABLE batch_operation_setup_restarts (
                id TEXT PRIMARY KEY,
                batch_operation_id TEXT NOT NULL,
                production_run_id TEXT NOT NULL,
                machine_id TEXT NOT NULL,
                replaced_gcode_release_id TEXT NOT NULL,
                workflow_event_id TEXT NOT NULL UNIQUE,
                invalidated_package_id TEXT,
                requested_at TEXT NOT NULL,
                requested_by TEXT NOT NULL,
                resolved_at TEXT,
                resolution TEXT CHECK (resolution IS NULL
                    OR resolution IN ('NEW_PACKAGE', 'OPERATION_FINISHED', 'OPERATION_RESET')),
                resolved_package_id TEXT,
                CHECK ((resolved_at IS NULL) = (resolution IS NULL)),
                CHECK (resolution = 'NEW_PACKAGE' OR resolved_package_id IS NULL),
                FOREIGN KEY (batch_operation_id) REFERENCES batch_operations(id) ON DELETE RESTRICT,
                FOREIGN KEY (production_run_id) REFERENCES production_runs(id) ON DELETE RESTRICT,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                FOREIGN KEY (replaced_gcode_release_id) REFERENCES gcode_releases(id) ON DELETE RESTRICT,
                FOREIGN KEY (workflow_event_id) REFERENCES production_run_workflow_events(id) ON DELETE RESTRICT,
                FOREIGN KEY (invalidated_package_id) REFERENCES production_packages(id) ON DELETE RESTRICT,
                FOREIGN KEY (resolved_package_id) REFERENCES production_packages(id) ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX ux_batch_operation_setup_restarts_open
                ON batch_operation_setup_restarts(batch_operation_id) WHERE resolved_at IS NULL;

            -- A restart is history: it is only ever resolved, once.
            CREATE TRIGGER batch_operation_setup_restarts_resolve_only
            BEFORE UPDATE ON batch_operation_setup_restarts
            WHEN OLD.resolved_at IS NOT NULL
              OR NEW.id IS NOT OLD.id OR NEW.batch_operation_id IS NOT OLD.batch_operation_id
              OR NEW.production_run_id IS NOT OLD.production_run_id OR NEW.machine_id IS NOT OLD.machine_id
              OR NEW.replaced_gcode_release_id IS NOT OLD.replaced_gcode_release_id
              OR NEW.workflow_event_id IS NOT OLD.workflow_event_id
              OR NEW.invalidated_package_id IS NOT OLD.invalidated_package_id
              OR NEW.requested_at IS NOT OLD.requested_at OR NEW.requested_by IS NOT OLD.requested_by
            BEGIN SELECT RAISE(ABORT, 'A setup restart can only be resolved, once'); END;
            CREATE TRIGGER batch_operation_setup_restarts_immutable_delete
            BEFORE DELETE ON batch_operation_setup_restarts
            BEGIN SELECT RAISE(ABORT, 'Setup restarts are history and cannot be deleted'); END;

            -- An operation that finishes or goes back to not started no longer waits for a package.
            CREATE TRIGGER batch_operation_setup_restarts_resolve_on_status
            AFTER UPDATE OF status ON batch_operations
            WHEN NEW.status IN ('completed', 'not_started') AND NEW.status IS NOT OLD.status
            BEGIN
                UPDATE batch_operation_setup_restarts
                SET resolved_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                    resolution = CASE NEW.status WHEN 'completed' THEN 'OPERATION_FINISHED' ELSE 'OPERATION_RESET' END
                WHERE batch_operation_id = NEW.id AND resolved_at IS NULL;
            END;
            """;
        await restarts.ExecuteNonQueryAsync(cancellationToken);
    }
}
