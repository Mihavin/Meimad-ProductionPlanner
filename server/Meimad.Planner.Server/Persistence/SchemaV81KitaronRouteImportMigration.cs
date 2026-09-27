using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Kitaron route-master import (OD-038). `kitaron_stations` is the Setup-maintained lookup from a
/// Kitaron station (`TStation.StationID`) to what its route steps become in Meimad: a Case
/// Operation with a required Machine Type, an auxiliary Workstation requirement, an External
/// Resource requirement, or nothing. Stations are discovered by the synchronization and start
/// undecided; an undecided station never imports anything. `operation_resource_requirements`
/// gains the step name and number and a per-unit duration so an imported inspection or deburring
/// step can be timed per part, and `kitaron_sync_links` admits the `operation_requirement`
/// entity. The sync state gains the requirement and skipped-step counters.
/// </summary>
internal sealed class SchemaV81KitaronRouteImportMigration : IDatabaseMigration
{
    public int Version => 81;
    public string Name => "kitaron_route_import";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE kitaron_stations (
                kitaron_station_id INTEGER PRIMARY KEY,
                station_name TEXT NOT NULL,
                station_type TEXT,
                retired INTEGER NOT NULL DEFAULT 0 CHECK (retired IN (0, 1)),
                route_rows INTEGER NOT NULL DEFAULT 0 CHECK (route_rows >= 0),
                planned_rows INTEGER NOT NULL DEFAULT 0 CHECK (planned_rows >= 0),
                supplier_rows INTEGER NOT NULL DEFAULT 0 CHECK (supplier_rows >= 0),
                suggested_role TEXT NOT NULL DEFAULT 'WORKSTATION'
                    CHECK (suggested_role IN ('MACHINE', 'WORKSTATION', 'EXTERNAL', 'IGNORE')),
                import_role TEXT NOT NULL DEFAULT 'UNDECIDED'
                    CHECK (import_role IN ('UNDECIDED', 'MACHINE', 'WORKSTATION', 'EXTERNAL', 'IGNORE')),
                machine_type TEXT,
                workstation_type_id TEXT,
                external_resource_id TEXT,
                default_minutes_per_part REAL NOT NULL DEFAULT 0 CHECK (default_minutes_per_part >= 0),
                default_minutes_per_batch REAL NOT NULL DEFAULT 0 CHECK (default_minutes_per_batch >= 0),
                capacity_required INTEGER NOT NULL DEFAULT 1 CHECK (capacity_required > 0),
                notes TEXT,
                first_seen_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                decided_at TEXT,
                decided_by TEXT,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                updated_at TEXT NOT NULL,
                FOREIGN KEY (workstation_type_id) REFERENCES workstation_types (id) ON DELETE RESTRICT,
                FOREIGN KEY (external_resource_id) REFERENCES external_resources (id) ON DELETE RESTRICT,
                CHECK (import_role <> 'WORKSTATION' OR workstation_type_id IS NOT NULL),
                CHECK (import_role <> 'EXTERNAL' OR external_resource_id IS NOT NULL)
            );
            CREATE INDEX ix_kitaron_stations_role ON kitaron_stations (import_role, station_name);

            ALTER TABLE operation_resource_requirements
                ADD COLUMN name TEXT;
            ALTER TABLE operation_resource_requirements
                ADD COLUMN step_number INTEGER;
            ALTER TABLE operation_resource_requirements
                ADD COLUMN duration_per_unit_seconds INTEGER NOT NULL DEFAULT 0
                    CHECK (duration_per_unit_seconds >= 0);

            ALTER TABLE kitaron_sync_links RENAME TO kitaron_sync_links_v31;
            CREATE TABLE kitaron_sync_links (
                source_entity TEXT NOT NULL
                    CHECK (source_entity IN ('case', 'order', 'case_operation', 'case_component', 'operation_requirement')),
                source_key TEXT NOT NULL,
                target_id TEXT NOT NULL,
                owns_target INTEGER NOT NULL CHECK (owns_target IN (0, 1)),
                source_hash TEXT NOT NULL,
                first_seen_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                PRIMARY KEY (source_entity, source_key),
                UNIQUE (source_entity, target_id)
            );
            INSERT INTO kitaron_sync_links (
                source_entity, source_key, target_id, owns_target,
                source_hash, first_seen_at, last_seen_at)
            SELECT source_entity, source_key, target_id, owns_target,
                   source_hash, first_seen_at, last_seen_at
            FROM kitaron_sync_links_v31;
            DROP TABLE kitaron_sync_links_v31;
            CREATE INDEX ix_kitaron_sync_links_target
                ON kitaron_sync_links (source_entity, target_id);

            ALTER TABLE kitaron_sync_state
                ADD COLUMN requirements_created INTEGER NOT NULL DEFAULT 0 CHECK (requirements_created >= 0);
            ALTER TABLE kitaron_sync_state
                ADD COLUMN requirements_updated INTEGER NOT NULL DEFAULT 0 CHECK (requirements_updated >= 0);
            ALTER TABLE kitaron_sync_state
                ADD COLUMN requirements_matched INTEGER NOT NULL DEFAULT 0 CHECK (requirements_matched >= 0);
            ALTER TABLE kitaron_sync_state
                ADD COLUMN route_steps_skipped INTEGER NOT NULL DEFAULT 0 CHECK (route_steps_skipped >= 0);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
