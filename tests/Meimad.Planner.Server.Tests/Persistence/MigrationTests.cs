using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class MigrationTests
{
    private static readonly string[] EntityTables =
    [
        "cases",
        "orders",
        "case_operations",
        "case_components",
        "production_batches",
        "batch_allocations",
        "batch_operations",
        "machines",
        "machine_assignments",
        "machine_assignment_overrides",
        "operation_pause_events",
        "downtimes",
        "working_calendars",
        "machine_types",
        "postprocessors",
        "machine_supported_postprocessors",
        "tool_table_releases",
        "tool_table_release_tools",
        "batch_operation_material_readiness",
        "tool_offset_readiness_records",
        "verified_material_receipts",
        "batch_material_reservations",
        "process_revisions",
        "manufacturing_programs",
        "production_runs",
        "production_run_programs",
        "production_run_outputs",
        "production_run_cycle_events",
        "gcode_releases",
        "gcode_release_verification_hooks",
        "setup_calendar_settings",
        "employee_resources",
        "employee_calendar_exceptions",
        "israeli_holidays",
        "report_email_settings",
        "kitaron_connection_settings",
        "kitaron_mapping_settings",
        "kitaron_sync_state",
        "edit_tokens",
        "edit_requests",
        "application_settings",
        "device_registry",
        "eink_package_revisions",
        "eink_package_files"
    ];

    [Fact]
    public async Task Fresh_database_applies_latest_schema()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using var connection = await fixture.Database.OpenConnectionAsync();

        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert.Equal(60L, (long)(await versionCommand.ExecuteScalarAsync())!);

        await using var migrationCommand = connection.CreateCommand();
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 1;";
        Assert.Equal("initial_planning_schema", await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 2;";
        Assert.Equal("case_details", await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 3;";
        Assert.Equal("order_notes", await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 4;";
        Assert.Equal("machine_master_fields", await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 5;";
        Assert.Equal("single_edit_mode_requests", await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 6;";
        Assert.Equal("eink_package_metadata", await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 7;";
        Assert.Equal("job_package_generation", await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 8;";
        Assert.Equal("machine_picture_path", await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 9;";
        Assert.Equal(
            "batch_lifecycle_and_dependency_snapshots",
            await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 10;";
        Assert.Equal(
            "setup_machine_types_and_order_lifecycle",
            await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 11;";
        Assert.Equal("administrative_setup", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 12;";
        Assert.Equal("employee_resource_details", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 13;";
        Assert.Equal("employee_calendar_exceptions", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 14;";
        Assert.Equal("israeli_holiday_cache", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 15;";
        Assert.Equal("machine_assignment_overrides", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 16;";
        Assert.Equal("operation_time_model", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 17;";
        Assert.Equal("machine downtime lifecycle", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 18;";
        Assert.Equal("structured operation pause events", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 19;";
        Assert.Equal("eink_setup_package_definition", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 20;";
        Assert.Equal("weekly_material_order_report", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 21;";
        Assert.Equal("weekly_employee_efficiency_report", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 22;";
        Assert.Equal("structured_event_log", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 23;";
        Assert.Equal("operation_actual_times", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 24;";
        Assert.Equal("machine_assignment_planning_mode", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 25;";
        Assert.Equal("legacy_working_plan_import_receipts", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 27;";
        Assert.Equal("incremental_case_order_import_receipts", await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 28;";
        Assert.Equal("kitaron_server_connection", await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 29;";
        Assert.Equal("kitaron_connector_mapping_draft", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 30;";
        Assert.Equal("kitaron_one_way_sync", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 31;";
        Assert.Equal("case_components_and_kitaron_bom_sync", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 33;";
        Assert.Equal("synchronize_not_started_batch_operation_times", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 34;";
        Assert.Equal("machine_execution_and_postprocessors", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 35;";
        Assert.Equal("gcode_process_revisions", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 36;";
        Assert.Equal("released_tool_capacity", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 37;";
        Assert.Equal("contextual_production_readiness", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 38;";
        Assert.Equal("nc_cycle_estimates", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 39;";
        Assert.Equal("verified_material_receipts_and_batch_reservations", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 40;";
        Assert.Equal("employee_setup_skills", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 41;";
        Assert.Equal("kitaron_material_orders_and_delivery_approvals", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 42;";
        Assert.Equal("haas_ngc_integration", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 43;";
        Assert.Equal("cnc_connection_platform", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 44;";
        Assert.Equal("haas_dprnt_part_port", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 45;";
        Assert.Equal("manufacturing_programs", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 46;";
        Assert.Equal("production_runs_and_assignment_ownership", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 47;";
        Assert.Equal("production_run_execution_events", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 48;";
        Assert.Equal("eink_physical_tablets_and_workflow", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 49;";
        Assert.Equal("operational_workflow_events_remove_cnc_mode_variable", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 50;";
        Assert.Equal("cnc_verification_foundation", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 51;";
        Assert.Equal("nc_verification_hook", await migrationCommand.ExecuteScalarAsync());
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 52;";
        Assert.Equal("setup_verification_sessions", await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 53;";
        Assert.Equal("tablet_terminal_health", await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 54;";
        Assert.Equal("tablet_send_to_qc_idempotency", await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 55;";
        Assert.Equal("qc_workflow_repeat_inspection", await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 56;";
        Assert.Equal("cycle_workflow_anomalies", await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 57;";
        Assert.Equal("production_session_closure", await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 58;";
        Assert.Equal("cycle_attempt_timing", await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 59;";
        Assert.Equal("operational_anomalies", await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE version = 60;";
        Assert.Equal("cnc_verification_bench_v6_mappings", await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = """
            SELECT COUNT(*) FROM pragma_table_info('cnc_verification_settings')
            WHERE (name='finalize_program_number' AND "notnull"=0)
               OR (name='event_sequence_variable' AND "notnull"=0);
            """;
        Assert.Equal(2L, (long)(await migrationCommand.ExecuteScalarAsync())!);

        migrationCommand.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE (type='table' AND name IN(
                       'production_run_cycle_attempts',
                       'production_run_cycle_attempt_outcomes'))
               OR (type='view' AND name='production_run_cycle_attempt_timing')
               OR (type='trigger' AND name IN(
                       'production_run_cycle_attempt_from_start',
                       'production_run_cycle_attempt_interrupted',
                       'production_run_cycle_attempt_completed'));
            """;
        Assert.Equal(6L, (long)(await migrationCommand.ExecuteScalarAsync())!);

        migrationCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ux_production_run_send_to_qc';";
        Assert.Equal(0L, (long)(await migrationCommand.ExecuteScalarAsync())!);

        migrationCommand.CommandText = """
            SELECT COUNT(*) FROM pragma_table_info('kitaron_material_orders')
            WHERE name IN ('source_key', 'purchase_order_number', 'material_number',
                           'requested_delivery_date', 'approved_delivery_date',
                           'approved_quantity', 'approval_note', 'active', 'source_hash');
            """;
        Assert.Equal(9L, (long)(await migrationCommand.ExecuteScalarAsync())!);

        migrationCommand.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE (type = 'table' AND name IN ('gcode_release_analyses', 'gcode_machine_cycle_estimates'))
               OR (type = 'view' AND name = 'effective_batch_operation_nc_estimates');
            """;
        Assert.Equal(3L, (long)(await migrationCommand.ExecuteScalarAsync())!);

        migrationCommand.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('machines')
            WHERE (name = 'execution_mode' AND "notnull" = 1 AND dflt_value = '''MANUAL''')
               OR (name = 'usable_tool_positions' AND "notnull" = 0)
               OR (name = 'rapid_rate_mm_per_min' AND "notnull" = 0)
               OR (name = 'tool_change_time_seconds' AND "notnull" = 0)
               OR (name = 'machine_time_factor' AND "notnull" = 1 AND dflt_value = '1.0');
            """;
        Assert.Equal(5L, (long)(await migrationCommand.ExecuteScalarAsync())!);

        migrationCommand.CommandText = """
            SELECT server_host || ':' || server_port || '/' || database_name || '/' || view_schema || '.' || view_name
            FROM kitaron_connection_settings WHERE id = 1;
            """;
        Assert.Equal(
            "192.168.0.240:1433/KitaronData229/dbo.VQWorkPlanningForStationF4",
            await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = """
            SELECT model_mode || '/' || mapping_status || '/' || json_array_length(mappings_json)
            FROM kitaron_mapping_settings WHERE id = 1;
            """;
        Assert.Equal("domain_aligned/draft/0", await migrationCommand.ExecuteScalarAsync());

        migrationCommand.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('legacy_working_plan_imports')
            WHERE name IN (
                'id', 'workbook_sha256', 'approved_request_sha256', 'response_json',
                'committed_by_client_id', 'committed_by_user_id', 'committed_at');
            """;
        Assert.Equal(7L, (long)(await migrationCommand.ExecuteScalarAsync())!);

        migrationCommand.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('machine_assignments')
            WHERE name = 'planning_mode'
              AND "notnull" = 1
              AND dflt_value = '''manual''';
            """;
        Assert.Equal(1L, (long)(await migrationCommand.ExecuteScalarAsync())!);

        migrationCommand.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('batch_operations')
            WHERE name IN ('actual_start', 'actual_end', 'actual_machine_id');
            """;
        Assert.Equal(3L, (long)(await migrationCommand.ExecuteScalarAsync())!);

        foreach (var table in EntityTables)
        {
            await AssertTimestampColumnsAsync(connection, table);
        }

        await using var integrityCommand = connection.CreateCommand();
        integrityCommand.CommandText = "PRAGMA foreign_key_check;";
        Assert.Null(await integrityCommand.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Reapplying_migrations_is_idempotent()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var migrator = new DatabaseMigrator(
            fixture.Database,
            NullLogger<DatabaseMigrator>.Instance);

        await migrator.MigrateAsync();

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
        Assert.Equal(60L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Version_56_preserves_sequence_anomalies_and_accepts_orphan_cycle_evidence()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            "Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE production_run_workflow_anomalies (
                    id TEXT PRIMARY KEY,production_run_id TEXT NOT NULL,machine_id TEXT NOT NULL,
                    source TEXT NOT NULL,source_event_id TEXT NOT NULL,
                    anomaly_type TEXT NOT NULL CHECK(anomaly_type IN(
                        'EVENT_SEQUENCE_GAP','EVENT_SEQUENCE_OUT_OF_ORDER')),
                    previous_sequence INTEGER NOT NULL,expected_sequence INTEGER NOT NULL,
                    received_sequence INTEGER NOT NULL,workflow_event_id TEXT NOT NULL,
                    detected_at TEXT NOT NULL,details_json TEXT NOT NULL DEFAULT '{}',
                    UNIQUE(source,source_event_id,anomaly_type));
                CREATE INDEX ix_production_run_workflow_anomalies_machine_time
                    ON production_run_workflow_anomalies(machine_id,detected_at DESC,id);
                CREATE TRIGGER production_run_workflow_anomalies_immutable_update
                    BEFORE UPDATE ON production_run_workflow_anomalies
                    BEGIN SELECT RAISE(ABORT,'Workflow anomalies are immutable'); END;
                CREATE TRIGGER production_run_workflow_anomalies_immutable_delete
                    BEFORE DELETE ON production_run_workflow_anomalies
                    BEGIN SELECT RAISE(ABORT,'Workflow anomalies are immutable'); END;
                INSERT INTO production_run_workflow_anomalies VALUES(
                    'old','run','machine','SOURCE','EVENT','EVENT_SEQUENCE_GAP',
                    10,11,12,'workflow','2026-08-26T00:00:00Z','{}');
                """;
            await setup.ExecuteNonQueryAsync();
        }

        await using (var transaction = connection.BeginTransaction())
        {
            await new SchemaV56CycleWorkflowAnomaliesMigration().ApplyAsync(
                connection, transaction, default);
            await transaction.CommitAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT anomaly_type || ':' || previous_sequence || ':' || received_sequence FROM production_run_workflow_anomalies WHERE id='old';";
        Assert.Equal("EVENT_SEQUENCE_GAP:10:12", await command.ExecuteScalarAsync());
        command.CommandText = """
            INSERT INTO production_run_workflow_anomalies(
                id,production_run_id,machine_id,source,source_event_id,anomaly_type,
                previous_sequence,expected_sequence,received_sequence,workflow_event_id,
                detected_at,details_json)
            VALUES('orphan','run','machine','SOURCE','END',
                   'CYCLE_END_WITHOUT_START',NULL,NULL,20,'workflow',
                   '2026-08-26T00:00:01Z','{}');
            """;
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        command.CommandText = "UPDATE production_run_workflow_anomalies SET received_sequence=21 WHERE id='orphan';";
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Version_45_preserves_multi_revision_release_history_and_adds_single_output_recipes()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        {
            await DowngradeToV45Async(connection);
            await using var setup = connection.CreateCommand();
            setup.CommandText = """
                DROP TRIGGER case_operations_create_default_manufacturing_program;
                DROP TRIGGER manufacturing_program_outputs_immutable_delete;
                DROP TRIGGER manufacturing_program_outputs_immutable_update;
                DROP TRIGGER process_revisions_program_immutable;
                DROP TABLE manufacturing_program_revision_outputs;
                DROP INDEX ix_process_revisions_program_history;
                DROP INDEX ux_process_revisions_active_program;
                CREATE UNIQUE INDEX ux_process_revisions_active_operation
                ON process_revisions (case_operation_id) WHERE is_active = 1;
                ALTER TABLE process_revisions DROP COLUMN manufacturing_program_id;
                DROP TABLE manufacturing_programs;
                DELETE FROM schema_migrations WHERE version = 45;
                PRAGMA user_version = 44;

                INSERT INTO cases (id, part_number, name, working_folder_path)
                VALUES ('v45-case', 'V45-PART', 'V45 Part', 'C:\Cases\V45');
                INSERT INTO case_operations (id, case_id, operation_number, route_position, name)
                VALUES ('v45-operation', 'v45-case', 10, 0, 'Mill');
                INSERT INTO postprocessors (id, name, is_active)
                VALUES ('v45-post', 'V45 post', 1);
                INSERT INTO tool_table_releases (
                    id, case_operation_id, revision_number, original_file_name,
                    stored_relative_path, file_size, file_hash, released_at,
                    released_by, release_comment, created_at, updated_at, required_tool_count)
                VALUES ('v45-tools', 'v45-operation', 1, 'tools.mht',
                    'operations/v45/tool/v45-tools/tools.mht', 15,
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                    '2026-08-20T00:00:00Z', 'engineer', 'Exact tools',
                    '2026-08-20T00:00:00Z', '2026-08-20T00:00:00Z', 1);
                INSERT INTO tool_table_release_tools (
                    id, tool_table_release_id, row_number, tool_identifier,
                    description, is_required, requires_magazine_position,
                    is_active, magazine_position, created_at, updated_at)
                VALUES ('v45-tool-row', 'v45-tools', 1, 'T1', 'Cutter', 1, 1, 1, '1',
                    '2026-08-20T00:00:00Z', '2026-08-20T00:00:00Z');
                INSERT INTO process_revisions (
                    id, case_operation_id, revision_number, is_active,
                    tool_table_release_id, created_at, created_by,
                    change_description, version, updated_at)
                VALUES
                    ('v45-process-1', 'v45-operation', 1, 0, 'v45-tools',
                     '2026-08-20T00:00:00Z', 'engineer', 'First', 2, '2026-08-21T00:00:00Z'),
                    ('v45-process-2', 'v45-operation', 2, 1, 'v45-tools',
                     '2026-08-21T00:00:00Z', 'engineer', 'Second', 1, '2026-08-21T00:00:00Z');
                INSERT INTO gcode_releases (
                    id, case_operation_id, process_revision_id, postprocessor_id,
                    post_specific_revision, original_file_name, stored_relative_path,
                    file_size, file_hash, released_at, released_by, change_scope,
                    release_comment, tool_table_release_id, created_at, updated_at)
                VALUES
                    ('v45-gcode-1', 'v45-operation', 'v45-process-1', 'v45-post', 1,
                     'old.nc', 'operations/v45/gcode/old.nc', 20,
                     'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                     '2026-08-20T00:00:00Z', 'engineer', 'NEW_PROCESS_REVISION',
                     'First release', 'v45-tools', '2026-08-20T00:00:00Z', '2026-08-20T00:00:00Z'),
                    ('v45-gcode-2', 'v45-operation', 'v45-process-2', 'v45-post', 1,
                     'current.nc', 'operations/v45/gcode/current.nc', 21,
                     'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc',
                     '2026-08-21T00:00:00Z', 'engineer', 'NEW_PROCESS_REVISION',
                     'Second release', 'v45-tools', '2026-08-21T00:00:00Z', '2026-08-21T00:00:00Z');
                """;
            await setup.ExecuteNonQueryAsync();
        }

        await new DatabaseMigrator(fixture.Database, NullLogger<DatabaseMigrator>.Instance).MigrateAsync();

        await using var reopened = await fixture.Database.OpenConnectionAsync();
        await using var assertion = reopened.CreateCommand();
        assertion.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM manufacturing_programs
                 WHERE id = 'case-operation:v45-operation' AND default_case_operation_id = 'v45-operation'),
                (SELECT COUNT(*) FROM process_revisions
                 WHERE id IN ('v45-process-1', 'v45-process-2')
                   AND manufacturing_program_id = 'case-operation:v45-operation'),
                (SELECT COUNT(*) FROM manufacturing_program_revision_outputs
                 WHERE process_revision_id IN ('v45-process-1', 'v45-process-2')
                   AND case_operation_id = 'v45-operation' AND quantity_per_cycle = 1 AND display_order = 0),
                (SELECT COUNT(*) FROM process_revisions
                 WHERE manufacturing_program_id = 'case-operation:v45-operation' AND is_active = 1),
                (SELECT group_concat(id || ':' || file_hash, '|') FROM gcode_releases
                 WHERE id IN ('v45-gcode-1', 'v45-gcode-2') ORDER BY id);
            """;
        await using var reader = await assertion.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(2, reader.GetInt32(1));
        Assert.Equal(2, reader.GetInt32(2));
        Assert.Equal(1, reader.GetInt32(3));
        Assert.Contains("v45-gcode-1:bbbb", reader.GetString(4), StringComparison.Ordinal);
        Assert.Contains("v45-gcode-2:cccc", reader.GetString(4), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Version_35_release_history_is_preserved_with_unknown_structured_tool_count()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        {
            await DowngradeToV45Async(connection);
            await DowngradeToV44Async(connection);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE machine_telemetry_raw;
                DROP TABLE machine_connection_events;
                DROP TABLE machine_state_history;
                DROP TABLE machine_current_state;
                DROP TABLE machine_connections;
                DELETE FROM schema_migrations WHERE version = 44;
                DELETE FROM schema_migrations WHERE version = 43;
                DROP TRIGGER nc_program_headers_immutable_update;
                DROP TRIGGER nc_program_headers_immutable_delete;
                DROP TRIGGER haas_events_immutable_update;
                DROP TRIGGER haas_events_immutable_delete;
                DROP TABLE IF EXISTS haas_macro_write_audits;
                DROP TABLE haas_events;
                DROP TABLE haas_bench_state_intervals;
                DROP TABLE haas_bench_sessions;
                DROP TABLE haas_machine_snapshots;
                DROP TABLE haas_connection_settings;
                DROP TABLE nc_program_headers;
                DELETE FROM schema_migrations WHERE version = 42;
                DROP TABLE kitaron_material_orders;
                DELETE FROM schema_migrations WHERE version = 41;
                ALTER TABLE employee_resources DROP COLUMN first_part_running_speed_percent;
                ALTER TABLE employee_resources DROP COLUMN fixture_assembly_seconds;
                ALTER TABLE employee_resources DROP COLUMN tool_load_seconds_per_tool;
                DELETE FROM schema_migrations WHERE version = 40;
                DROP TRIGGER batch_material_reservation_batch_capacity_insert;
                DROP TRIGGER batch_material_reservation_receipt_capacity_insert;
                DROP TRIGGER batch_material_reservation_case_match_insert;
                DROP TABLE batch_material_reservations;
                DROP TABLE verified_material_receipts;
                DELETE FROM schema_migrations WHERE version = 39;
                DROP VIEW effective_batch_operation_nc_estimates;
                DROP TRIGGER gcode_release_analyses_immutable_delete;
                DROP TRIGGER gcode_release_analyses_immutable_update;
                DROP TABLE gcode_machine_cycle_estimates;
                DROP TABLE gcode_release_analyses;
                DELETE FROM schema_migrations WHERE version = 38;
                DROP TRIGGER tool_offset_readiness_records_immutable_update;
                DROP TRIGGER tool_offset_readiness_context_consistent;
                DROP TRIGGER machine_assignment_selected_release_matches_operation_update;
                DROP TRIGGER machine_assignment_selected_release_matches_operation_insert;
                DROP TABLE tool_offset_readiness_records;
                DROP TABLE batch_operation_material_readiness;
                DROP INDEX ix_machine_assignments_selected_gcode;
                ALTER TABLE machine_assignments DROP COLUMN selected_gcode_release_id;
                DELETE FROM schema_migrations WHERE version = 37;
                DROP TRIGGER process_revisions_tool_count_consistent;
                DROP TABLE tool_table_release_tools;
                ALTER TABLE tool_table_releases DROP COLUMN required_tool_count;
                DELETE FROM schema_migrations WHERE version = 36;
                PRAGMA user_version = 35;

                INSERT INTO cases (id, part_number, name, working_folder_path)
                VALUES ('case-v35-tools', 'PN-V35-TOOLS', 'Legacy tools', 'C:\Cases\PN-V35-TOOLS');
                INSERT INTO case_operations (id, case_id, operation_number, route_position, name)
                VALUES ('operation-v35-tools', 'case-v35-tools', 10, 0, 'Mill');
                INSERT INTO tool_table_releases (
                    id, case_operation_id, revision_number, original_file_name,
                    stored_relative_path, file_size, file_hash, released_at,
                    released_by, release_comment, created_at, updated_at)
                VALUES (
                    'tools-v35', 'operation-v35-tools', 1, 'legacy.csv',
                    'operations/operation-v35-tools/tool-tables/tools-v35/legacy.csv',
                    10, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                    '2026-08-20T00:00:00Z', 'legacy-user', 'Legacy release',
                    '2026-08-20T00:00:00Z', '2026-08-20T00:00:00Z');
                INSERT INTO process_revisions (
                    id, case_operation_id, revision_number, is_active,
                    tool_table_release_id, created_at, created_by,
                    change_description, version, updated_at)
                VALUES (
                    'process-v35', 'operation-v35-tools', 1, 1, 'tools-v35',
                    '2026-08-20T00:00:00Z', 'legacy-user', 'Legacy process', 1,
                    '2026-08-20T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var migrator = new DatabaseMigrator(
            fixture.Database,
            NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync();

        await using var reopened = await fixture.Database.OpenConnectionAsync();
        await using var read = reopened.CreateCommand();
        read.CommandText = """
            SELECT tool_table_releases.required_tool_count,
                   process_revisions.tool_table_release_id,
                   (SELECT COUNT(*) FROM tool_table_release_tools)
            FROM process_revisions
            JOIN tool_table_releases
              ON tool_table_releases.id = process_revisions.tool_table_release_id
            WHERE process_revisions.id = 'process-v35';
            """;
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(0));
        Assert.Equal("tools-v35", reader.GetString(1));
        Assert.Equal(0, reader.GetInt32(2));
    }

    [Fact]
    public async Task Incremental_import_receipt_migration_preserves_v25_receipt_and_allows_distinct_request_hash()
    {
        await using var fixture = TemporaryDatabase.CreateUnmigrated();
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE legacy_working_plan_imports (
                    id TEXT PRIMARY KEY,
                    workbook_sha256 TEXT NOT NULL UNIQUE,
                    approved_request_sha256 TEXT NOT NULL,
                    response_json TEXT NOT NULL,
                    committed_by_client_id TEXT NOT NULL,
                    committed_by_user_id TEXT NOT NULL,
                    committed_at TEXT NOT NULL);
                CREATE INDEX ix_legacy_working_plan_imports_committed_at
                    ON legacy_working_plan_imports (committed_at);
                INSERT INTO legacy_working_plan_imports VALUES (
                    'receipt-1',
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                    'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                    '{}', 'client-1', 'user-1', '2026-08-16T10:00:00Z');
                """;
            await setup.ExecuteNonQueryAsync();
        }

        await using (var transaction = connection.BeginTransaction())
        {
            await new SchemaV27IncrementalCaseOrderImportReceiptsMigration().ApplyAsync(
                connection, transaction, CancellationToken.None);
            await transaction.CommitAsync();
        }

        await using var assertion = connection.CreateCommand();
        assertion.CommandText = "SELECT COUNT(*) FROM legacy_working_plan_imports WHERE id = 'receipt-1';";
        Assert.Equal(1L, (long)(await assertion.ExecuteScalarAsync())!);
        assertion.CommandText = """
            INSERT INTO legacy_working_plan_imports VALUES (
                'receipt-2',
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc',
                '{}', 'client-1', 'user-1', '2026-08-16T11:00:00Z');
            SELECT COUNT(*) FROM legacy_working_plan_imports;
            """;
        Assert.Equal(2L, (long)(await assertion.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Actual_time_migration_does_not_fabricate_legacy_history()
    {
        await using var fixture = TemporaryDatabase.CreateUnmigrated();
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE machines (id TEXT PRIMARY KEY);
                CREATE TABLE batch_operations (
                    id TEXT PRIMARY KEY,
                    status TEXT NOT NULL,
                    updated_at TEXT NOT NULL);
                INSERT INTO batch_operations (id, status, updated_at) VALUES
                    ('legacy-running', 'in_progress', '2026-08-13T08:00:00Z'),
                    ('legacy-complete', 'completed', '2026-08-13T09:00:00Z');
                """;
            await setup.ExecuteNonQueryAsync();
        }

        await using (var transaction = connection.BeginTransaction())
        {
            await new SchemaV23OperationActualTimesMigration().ApplyAsync(
                connection, transaction, CancellationToken.None);
            await transaction.CommitAsync();
        }

        await using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT actual_start, actual_end, actual_machine_id
            FROM batch_operations
            ORDER BY id;
            """;
        await using var reader = await read.ExecuteReaderAsync();
        var rowCount = 0;
        while (await reader.ReadAsync())
        {
            rowCount++;
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
            Assert.True(reader.IsDBNull(2));
        }
        Assert.Equal(2, rowCount);
    }

    [Fact]
    public async Task Version_one_database_upgrades_through_latest_schema()
    {
        await using var fixture = TemporaryDatabase.CreateUnmigrated();
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        {
            await using (var metadataCommand = connection.CreateCommand())
            {
                metadataCommand.CommandText = """
                    CREATE TABLE schema_migrations (
                        version INTEGER PRIMARY KEY,
                        name TEXT NOT NULL,
                        applied_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                    );
                    """;
                await metadataCommand.ExecuteNonQueryAsync();
            }

            await using var transaction =
                (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync();
            var versionOne = new SchemaV1Migration();
            await versionOne.ApplyAsync(connection, transaction, CancellationToken.None);

            await using (var seedCommand = connection.CreateCommand())
            {
                seedCommand.Transaction = transaction;
                seedCommand.CommandText = """
                    INSERT INTO cases (
                        id, part_number, name, working_folder_path, material, raw_stock)
                    VALUES (
                        'legacy-case', 'PN-LEGACY', 'Legacy', 'C:\Legacy',
                        'Al 7075-T6', 'Plate 20 x 100 x 100 mm');

                    INSERT INTO schema_migrations (version, name, applied_at)
                    VALUES (1, 'initial_planning_schema', '2026-08-11T00:00:00Z');

                    PRAGMA user_version = 1;
                    """;
                await seedCommand.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        var migrator = new DatabaseMigrator(
            fixture.Database,
            NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync();

        await using var reopened = await fixture.Database.OpenConnectionAsync();
        await using var command = reopened.CreateCommand();
        command.CommandText = """
            SELECT material_specification, raw_material_dimensions
            FROM cases
            WHERE id = 'legacy-case';
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Al 7075-T6", reader.GetString(0));
        Assert.Equal("Plate 20 x 100 x 100 mm", reader.GetString(1));
    }

    [Fact]
    public async Task Version_two_database_adds_order_notes_without_losing_demand()
    {
        await using var fixture = TemporaryDatabase.CreateUnmigrated();
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        {
            await using (var metadataCommand = connection.CreateCommand())
            {
                metadataCommand.CommandText = """
                    CREATE TABLE schema_migrations (
                        version INTEGER PRIMARY KEY,
                        name TEXT NOT NULL,
                        applied_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                    );
                    """;
                await metadataCommand.ExecuteNonQueryAsync();
            }

            await using var transaction =
                (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync();
            await new SchemaV1Migration().ApplyAsync(connection, transaction, CancellationToken.None);
            await new SchemaV2CaseDetailsMigration().ApplyAsync(
                connection,
                transaction,
                CancellationToken.None);

            await using (var seedCommand = connection.CreateCommand())
            {
                seedCommand.Transaction = transaction;
                seedCommand.CommandText = """
                    INSERT INTO cases (id, part_number, name, working_folder_path)
                    VALUES ('case-v2', 'PN-V2', 'V2 Case', 'C:\Cases\PN-V2');

                    INSERT INTO orders (
                        id, case_id, order_reference, quantity, work_finish_date, status)
                    VALUES (
                        'order-v2', 'case-v2', 'WO-V2', 12, '2026-08-20', 'active');

                    INSERT INTO schema_migrations (version, name, applied_at)
                    VALUES
                        (1, 'initial_planning_schema', '2026-08-11T00:00:00Z'),
                        (2, 'case_details', '2026-08-11T00:00:01Z');

                    PRAGMA user_version = 2;
                    """;
                await seedCommand.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        var migrator = new DatabaseMigrator(
            fixture.Database,
            NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync();

        await using var reopened = await fixture.Database.OpenConnectionAsync();
        await using var command = reopened.CreateCommand();
        command.CommandText = """
            SELECT order_reference, quantity, notes
            FROM orders
            WHERE id = 'order-v2';
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("WO-V2", reader.GetString(0));
        Assert.Equal(12, reader.GetInt32(1));
        Assert.True(reader.IsDBNull(2));
    }

    [Fact]
    public async Task Version_three_database_adds_machine_master_fields_without_losing_machine()
    {
        await using var fixture = TemporaryDatabase.CreateUnmigrated();
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        {
            await using (var metadataCommand = connection.CreateCommand())
            {
                metadataCommand.CommandText = """
                    CREATE TABLE schema_migrations (
                        version INTEGER PRIMARY KEY,
                        name TEXT NOT NULL,
                        applied_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                    );
                    """;
                await metadataCommand.ExecuteNonQueryAsync();
            }

            await using var transaction =
                (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync();
            await new SchemaV1Migration().ApplyAsync(connection, transaction, CancellationToken.None);
            await new SchemaV2CaseDetailsMigration().ApplyAsync(
                connection,
                transaction,
                CancellationToken.None);
            await new SchemaV3OrderNotesMigration().ApplyAsync(
                connection,
                transaction,
                CancellationToken.None);

            await using (var seedCommand = connection.CreateCommand())
            {
                seedCommand.Transaction = transaction;
                seedCommand.CommandText = """
                    INSERT INTO working_calendars (id, name, time_zone_id)
                    VALUES ('calendar-v3', 'Legacy Calendar', 'UTC');

                    INSERT INTO machines (
                        id, number, name, machine_type, capabilities_json,
                        working_calendar_id, status)
                    VALUES (
                        'machine-v3', 'M-V3', 'Legacy Machine', 'mill', '["probe"]',
                        'calendar-v3', 'inactive');

                    INSERT INTO schema_migrations (version, name, applied_at)
                    VALUES
                        (1, 'initial_planning_schema', '2026-08-11T00:00:00Z'),
                        (2, 'case_details', '2026-08-11T00:00:01Z'),
                        (3, 'order_notes', '2026-08-11T00:00:02Z');

                    PRAGMA user_version = 3;
                    """;
                await seedCommand.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        var migrator = new DatabaseMigrator(
            fixture.Database,
            NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync();

        await using var reopened = await fixture.Database.OpenConnectionAsync();
        await using var command = reopened.CreateCommand();
        command.CommandText = """
            SELECT number, machine_type, capabilities_json,
                   axis_type, is_active, display_enabled
            FROM machines
            WHERE id = 'machine-v3';
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("M-V3", reader.GetString(0));
        Assert.Equal("mill", reader.GetString(1));
        Assert.Equal("[\"probe\"]", reader.GetString(2));
        Assert.True(reader.IsDBNull(3));
        Assert.Equal(0, reader.GetInt32(4));
        Assert.Equal(0, reader.GetInt32(5));
    }

    [Fact]
    public async Task Version_four_database_adds_edit_requests_without_losing_active_token()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        {
            await DowngradeToV45Async(connection);
            await DowngradeToV44Async(connection);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE machine_telemetry_raw;
                DROP TABLE machine_connection_events;
                DROP TABLE machine_state_history;
                DROP TABLE machine_current_state;
                DROP TABLE machine_connections;
                DELETE FROM schema_migrations WHERE version = 44;
                DELETE FROM schema_migrations WHERE version = 43;
                DROP TRIGGER nc_program_headers_immutable_update;
                DROP TRIGGER nc_program_headers_immutable_delete;
                DROP TRIGGER haas_events_immutable_update;
                DROP TRIGGER haas_events_immutable_delete;
                DROP TABLE IF EXISTS haas_macro_write_audits;
                DROP TABLE haas_events;
                DROP TABLE haas_bench_state_intervals;
                DROP TABLE haas_bench_sessions;
                DROP TABLE haas_machine_snapshots;
                DROP TABLE haas_connection_settings;
                DROP TABLE nc_program_headers;
                DELETE FROM schema_migrations WHERE version = 42;
                DROP TABLE kitaron_material_orders;
                DELETE FROM schema_migrations WHERE version = 41;
                ALTER TABLE employee_resources DROP COLUMN first_part_running_speed_percent;
                ALTER TABLE employee_resources DROP COLUMN fixture_assembly_seconds;
                ALTER TABLE employee_resources DROP COLUMN tool_load_seconds_per_tool;
                DELETE FROM schema_migrations WHERE version = 40;
                DROP TRIGGER batch_material_reservation_batch_capacity_insert;
                DROP TRIGGER batch_material_reservation_receipt_capacity_insert;
                DROP TRIGGER batch_material_reservation_case_match_insert;
                DROP TABLE batch_material_reservations;
                DROP TABLE verified_material_receipts;
                DELETE FROM schema_migrations WHERE version = 39;
                DROP VIEW effective_batch_operation_nc_estimates;
                DROP TRIGGER gcode_release_analyses_immutable_delete;
                DROP TRIGGER gcode_release_analyses_immutable_update;
                DROP TABLE gcode_machine_cycle_estimates;
                DROP TABLE gcode_release_analyses;
                DELETE FROM schema_migrations WHERE version = 38;
                DROP TRIGGER tool_offset_readiness_records_immutable_update;
                DROP TRIGGER tool_offset_readiness_context_consistent;
                DROP TRIGGER machine_assignment_selected_release_matches_operation_update;
                DROP TRIGGER machine_assignment_selected_release_matches_operation_insert;
                DROP TABLE tool_offset_readiness_records;
                DROP TABLE batch_operation_material_readiness;
                DROP INDEX ix_machine_assignments_selected_gcode;
                ALTER TABLE machine_assignments DROP COLUMN selected_gcode_release_id;
                DELETE FROM schema_migrations WHERE version = 37;
                ALTER TABLE case_operations DROP COLUMN qa_seconds;
                ALTER TABLE case_operations DROP COLUMN load_unload_seconds;
                ALTER TABLE case_operations DROP COLUMN load_unload_requires_worker;
                ALTER TABLE case_operations DROP COLUMN automatic_loading;
                ALTER TABLE case_operations DROP COLUMN load_unload_every_n_parts;
                ALTER TABLE case_operations DROP COLUMN day_shift_only;
                ALTER TABLE batch_operations DROP COLUMN qa_seconds;
                ALTER TABLE batch_operations DROP COLUMN load_unload_seconds;
                ALTER TABLE batch_operations DROP COLUMN load_unload_requires_worker;
                ALTER TABLE batch_operations DROP COLUMN automatic_loading;
                ALTER TABLE batch_operations DROP COLUMN load_unload_every_n_parts;
                ALTER TABLE batch_operations DROP COLUMN day_shift_only;
                DROP TABLE operation_pause_events;
                DROP TABLE machine_assignment_overrides;
                DROP TABLE israeli_holiday_sync_state;
                DROP TABLE structured_event_log;
                DROP TABLE weekly_employee_efficiency_deliveries;
                DROP TABLE employee_work_measurements;
                DROP TABLE weekly_material_report_deliveries;
                DROP TABLE report_email_settings;
                DROP TABLE israeli_holidays;
                DROP TABLE employee_calendar_exceptions;
                DROP TABLE employee_resources;
                DROP TABLE setup_calendar_settings;
                DROP INDEX ix_batch_operations_production_release;
                ALTER TABLE batch_operations DROP COLUMN production_tool_table_file_hash;
                ALTER TABLE batch_operations DROP COLUMN production_gcode_file_hash;
                ALTER TABLE batch_operations DROP COLUMN production_tool_table_release_id;
                ALTER TABLE batch_operations DROP COLUMN production_gcode_release_id;
                ALTER TABLE batch_operations DROP COLUMN production_process_revision_id;
                DROP TRIGGER process_revisions_tool_count_consistent;
                DROP TABLE tool_table_release_tools;
                ALTER TABLE tool_table_releases DROP COLUMN required_tool_count;
                DROP TABLE gcode_releases;
                DROP TABLE process_revisions;
                DROP TABLE tool_table_releases;
                DROP TABLE machine_supported_postprocessors;
                DROP TABLE postprocessors;
                ALTER TABLE machines DROP COLUMN machine_time_factor;
                ALTER TABLE machines DROP COLUMN tool_change_time_seconds;
                ALTER TABLE machines DROP COLUMN rapid_rate_mm_per_min;
                ALTER TABLE machines DROP COLUMN usable_tool_positions;
                ALTER TABLE machines DROP COLUMN execution_mode;
                DROP INDEX ix_machines_machine_type_id;
                ALTER TABLE machines DROP COLUMN machine_type_id;
                DROP TABLE machine_types;
                DROP INDEX ix_batch_operations_predecessor_snapshot;
                ALTER TABLE batch_operations DROP COLUMN simultaneous_group_key;
                ALTER TABLE batch_operations DROP COLUMN predecessor_source_case_operation_id;
                ALTER TABLE batch_operations DROP COLUMN dependency_type;
                DROP TRIGGER eink_package_files_immutable_delete;
                DROP TRIGGER eink_package_files_immutable_update;
                DROP TRIGGER eink_package_revisions_immutable_delete;
                DROP TRIGGER eink_package_revisions_immutable_update;
                DROP TABLE eink_package_files;
                DROP TABLE eink_package_revisions;
                DROP TABLE edit_requests;
                ALTER TABLE machines DROP COLUMN picture_reference;
                DROP INDEX ix_batch_operations_actual_machine_time;
                ALTER TABLE batch_operations DROP COLUMN actual_machine_id;
                ALTER TABLE batch_operations DROP COLUMN actual_end;
                ALTER TABLE batch_operations DROP COLUMN actual_start;
                ALTER TABLE machine_assignments DROP COLUMN planning_mode;
                DROP TABLE legacy_working_plan_imports;
                DROP TABLE case_components;
                DROP TABLE kitaron_sync_links;
                DROP TABLE kitaron_sync_state;
                DROP TABLE kitaron_mapping_settings;
                DROP TABLE kitaron_connection_settings;
                DELETE FROM schema_migrations WHERE version IN (5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36);
                UPDATE edit_tokens
                SET holder_client_id = 'existing-client',
                    holder_user_id = 'existing-user',
                    generation = 17,
                    acquired_at = '2026-08-11T10:00:00Z',
                    updated_at = '2026-08-11T10:00:00Z'
                WHERE id = 1;
                PRAGMA user_version = 4;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var migrator = new DatabaseMigrator(
            fixture.Database,
            NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync();

        await using var reopened = await fixture.Database.OpenConnectionAsync();
        await using var tokenCommand = reopened.CreateCommand();
        tokenCommand.CommandText = """
            SELECT holder_client_id, holder_user_id, generation
            FROM edit_tokens
            WHERE id = 1;
            """;
        await using var reader = await tokenCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("existing-client", reader.GetString(0));
        Assert.Equal("existing-user", reader.GetString(1));
        Assert.Equal(17, reader.GetInt64(2));

        await reader.DisposeAsync();
        tokenCommand.CommandText = "SELECT COUNT(*) FROM edit_requests;";
        Assert.Equal(0L, (long)(await tokenCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Cases_table_contains_paths_and_no_blob_columns()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('cases');";

        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1), reader.GetString(2));
        }

        Assert.Equal("TEXT", columns["working_folder_path"]);
        Assert.Equal("TEXT", columns["preview_reference"]);
        Assert.DoesNotContain(columns.Values, type =>
            string.Equals(type, "BLOB", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Orders_are_case_children_without_machine_or_persisted_case_activity_columns()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using var connection = await fixture.Database.OpenConnectionAsync();

        var orderColumns = await ReadColumnNamesAsync(connection, "orders");
        Assert.Contains("case_id", orderColumns);
        Assert.Contains("notes", orderColumns);
        Assert.DoesNotContain("machine_id", orderColumns);
        Assert.DoesNotContain("machine_assignment_id", orderColumns);

        var caseColumns = await ReadColumnNamesAsync(connection, "cases");
        Assert.DoesNotContain("is_active", caseColumns);
        Assert.DoesNotContain("active", caseColumns);
    }

    [Fact]
    public async Task Machine_master_columns_are_explicit_booleans_and_axis_type()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using var connection = await fixture.Database.OpenConnectionAsync();
        var columns = await ReadColumnNamesAsync(connection, "machines");

        Assert.Contains("axis_type", columns);
        Assert.Contains("is_active", columns);
        Assert.Contains("display_enabled", columns);
        Assert.Contains("picture_reference", columns);
    }

    [Fact]
    public async Task Version_nine_snapshots_dependencies_and_backfills_batch_lifecycle()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        {
            await DowngradeToV45Async(connection);
            await DowngradeToV44Async(connection);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE machine_telemetry_raw;
                DROP TABLE machine_connection_events;
                DROP TABLE machine_state_history;
                DROP TABLE machine_current_state;
                DROP TABLE machine_connections;
                DELETE FROM schema_migrations WHERE version = 44;
                DELETE FROM schema_migrations WHERE version = 43;
                DROP TRIGGER nc_program_headers_immutable_update;
                DROP TRIGGER nc_program_headers_immutable_delete;
                DROP TRIGGER haas_events_immutable_update;
                DROP TRIGGER haas_events_immutable_delete;
                DROP TABLE IF EXISTS haas_macro_write_audits;
                DROP TABLE haas_events;
                DROP TABLE haas_bench_state_intervals;
                DROP TABLE haas_bench_sessions;
                DROP TABLE haas_machine_snapshots;
                DROP TABLE haas_connection_settings;
                DROP TABLE nc_program_headers;
                DELETE FROM schema_migrations WHERE version = 42;
                DROP TABLE kitaron_material_orders;
                DELETE FROM schema_migrations WHERE version = 41;
                ALTER TABLE employee_resources DROP COLUMN first_part_running_speed_percent;
                ALTER TABLE employee_resources DROP COLUMN fixture_assembly_seconds;
                ALTER TABLE employee_resources DROP COLUMN tool_load_seconds_per_tool;
                DELETE FROM schema_migrations WHERE version = 40;
                DROP TRIGGER batch_material_reservation_batch_capacity_insert;
                DROP TRIGGER batch_material_reservation_receipt_capacity_insert;
                DROP TRIGGER batch_material_reservation_case_match_insert;
                DROP TABLE batch_material_reservations;
                DROP TABLE verified_material_receipts;
                DELETE FROM schema_migrations WHERE version = 39;
                DROP VIEW effective_batch_operation_nc_estimates;
                DROP TRIGGER gcode_release_analyses_immutable_delete;
                DROP TRIGGER gcode_release_analyses_immutable_update;
                DROP TABLE gcode_machine_cycle_estimates;
                DROP TABLE gcode_release_analyses;
                DELETE FROM schema_migrations WHERE version = 38;
                DROP TRIGGER tool_offset_readiness_records_immutable_update;
                DROP TRIGGER tool_offset_readiness_context_consistent;
                DROP TRIGGER machine_assignment_selected_release_matches_operation_update;
                DROP TRIGGER machine_assignment_selected_release_matches_operation_insert;
                DROP TABLE tool_offset_readiness_records;
                DROP TABLE batch_operation_material_readiness;
                DROP INDEX ix_machine_assignments_selected_gcode;
                ALTER TABLE machine_assignments DROP COLUMN selected_gcode_release_id;
                DELETE FROM schema_migrations WHERE version = 37;
                ALTER TABLE case_operations DROP COLUMN qa_seconds;
                ALTER TABLE case_operations DROP COLUMN load_unload_seconds;
                ALTER TABLE case_operations DROP COLUMN load_unload_requires_worker;
                ALTER TABLE case_operations DROP COLUMN automatic_loading;
                ALTER TABLE case_operations DROP COLUMN load_unload_every_n_parts;
                ALTER TABLE case_operations DROP COLUMN day_shift_only;
                ALTER TABLE batch_operations DROP COLUMN qa_seconds;
                ALTER TABLE batch_operations DROP COLUMN load_unload_seconds;
                ALTER TABLE batch_operations DROP COLUMN load_unload_requires_worker;
                ALTER TABLE batch_operations DROP COLUMN automatic_loading;
                ALTER TABLE batch_operations DROP COLUMN load_unload_every_n_parts;
                ALTER TABLE batch_operations DROP COLUMN day_shift_only;
                DROP TABLE operation_pause_events;
                DROP TABLE machine_assignment_overrides;
                DROP TABLE israeli_holiday_sync_state;
                DROP TABLE structured_event_log;
                DROP TABLE weekly_employee_efficiency_deliveries;
                DROP TABLE employee_work_measurements;
                DROP TABLE weekly_material_report_deliveries;
                DROP TABLE report_email_settings;
                DROP TABLE israeli_holidays;
                DROP TABLE employee_calendar_exceptions;
                DROP TABLE employee_resources;
                DROP TABLE setup_calendar_settings;
                DROP INDEX ix_batch_operations_production_release;
                ALTER TABLE batch_operations DROP COLUMN production_tool_table_file_hash;
                ALTER TABLE batch_operations DROP COLUMN production_gcode_file_hash;
                ALTER TABLE batch_operations DROP COLUMN production_tool_table_release_id;
                ALTER TABLE batch_operations DROP COLUMN production_gcode_release_id;
                ALTER TABLE batch_operations DROP COLUMN production_process_revision_id;
                DROP TRIGGER process_revisions_tool_count_consistent;
                DROP TABLE tool_table_release_tools;
                ALTER TABLE tool_table_releases DROP COLUMN required_tool_count;
                DROP TABLE gcode_releases;
                DROP TABLE process_revisions;
                DROP TABLE tool_table_releases;
                DROP TABLE machine_supported_postprocessors;
                DROP TABLE postprocessors;
                ALTER TABLE machines DROP COLUMN machine_time_factor;
                ALTER TABLE machines DROP COLUMN tool_change_time_seconds;
                ALTER TABLE machines DROP COLUMN rapid_rate_mm_per_min;
                ALTER TABLE machines DROP COLUMN usable_tool_positions;
                ALTER TABLE machines DROP COLUMN execution_mode;
                DROP INDEX ix_machines_machine_type_id;
                ALTER TABLE machines DROP COLUMN machine_type_id;
                DROP TABLE machine_types;
                DROP INDEX ix_batch_operations_predecessor_snapshot;
                ALTER TABLE batch_operations DROP COLUMN simultaneous_group_key;
                ALTER TABLE batch_operations DROP COLUMN predecessor_source_case_operation_id;
                ALTER TABLE batch_operations DROP COLUMN dependency_type;
                ALTER TABLE eink_package_revisions DROP COLUMN setup_worker_id;
                ALTER TABLE eink_package_revisions DROP COLUMN setup_worker_first_name;
                ALTER TABLE eink_package_revisions DROP COLUMN setup_worker_last_name;
                ALTER TABLE eink_package_revisions DROP COLUMN setup_worker_photo_file_id;
                ALTER TABLE eink_package_revisions DROP COLUMN planned_setup_starts_at;
                ALTER TABLE eink_package_revisions DROP COLUMN planned_setup_ends_at;
                ALTER TABLE eink_package_revisions DROP COLUMN job_tools_json;
                ALTER TABLE eink_package_revisions DROP COLUMN expected_machine_tools_json;
                ALTER TABLE eink_package_revisions DROP COLUMN local_checklist_items_json;
                DROP INDEX ix_batch_operations_actual_machine_time;
                ALTER TABLE batch_operations DROP COLUMN actual_machine_id;
                ALTER TABLE batch_operations DROP COLUMN actual_end;
                ALTER TABLE batch_operations DROP COLUMN actual_start;
                ALTER TABLE machine_assignments DROP COLUMN planning_mode;
                DROP TABLE legacy_working_plan_imports;
                DROP TABLE case_components;
                DROP TABLE kitaron_sync_links;
                DROP TABLE kitaron_sync_state;
                DROP TABLE kitaron_mapping_settings;
                DROP TABLE kitaron_connection_settings;
                DELETE FROM schema_migrations WHERE version IN (9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36);
                PRAGMA user_version = 8;

                INSERT INTO cases (id, part_number, name, working_folder_path)
                VALUES ('case-v9', 'PN-V9', 'V9 Case', 'C:\Cases\PN-V9');

                INSERT INTO case_operations (
                    id, case_id, operation_number, route_position, name,
                    dependency_type, predecessor_case_operation_id,
                    simultaneous_group_key)
                VALUES
                    ('case-op-v9-1', 'case-v9', 10, 0, 'First',
                     'independent', NULL, NULL),
                    ('case-op-v9-2', 'case-v9', 20, 1, 'Second',
                     'sequential', 'case-op-v9-1', NULL);

                INSERT INTO production_batches (
                    id, case_id, batch_number, status, planned_quantity)
                VALUES
                    ('batch-v9-waiting', 'case-v9', 'B-V9-W', 'planned', 1),
                    ('batch-v9-running', 'case-v9', 'B-V9-R', 'planned', 1),
                    ('batch-v9-complete', 'case-v9', 'B-V9-C', 'planned', 1);

                INSERT INTO batch_operations (
                    id, production_batch_id, source_case_operation_id,
                    operation_number, route_position, name, status)
                VALUES
                    ('batch-op-v9-waiting', 'batch-v9-waiting', 'case-op-v9-1',
                     10, 0, 'First', 'not_started'),
                    ('batch-op-v9-running', 'batch-v9-running', 'case-op-v9-2',
                     20, 0, 'Second', 'in_progress'),
                    ('batch-op-v9-complete', 'batch-v9-complete', 'case-op-v9-2',
                     20, 0, 'Second', 'completed');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var migrator = new DatabaseMigrator(
            fixture.Database,
            NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync();

        await using var reopened = await fixture.Database.OpenConnectionAsync();
        await using var assertion = reopened.CreateCommand();
        assertion.CommandText = """
            SELECT dependency_type, predecessor_source_case_operation_id
            FROM batch_operations
            WHERE id = 'batch-op-v9-running';
            """;
        await using (var reader = await assertion.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("sequential", reader.GetString(0));
            Assert.Equal("case-op-v9-1", reader.GetString(1));
        }

        assertion.CommandText = """
            SELECT id, status
            FROM production_batches
            ORDER BY id;
            """;
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var reader = await assertion.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                statuses.Add(reader.GetString(0), reader.GetString(1));
            }
        }

        Assert.Equal("complete", statuses["batch-v9-complete"]);
        Assert.Equal("in_production", statuses["batch-v9-running"]);
        Assert.Equal("waiting", statuses["batch-v9-waiting"]);
    }

    [Fact]
    public async Task Every_server_connection_enables_foreign_keys()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();

        await using var firstConnection = await fixture.Database.OpenConnectionAsync();
        await using var secondConnection = await fixture.Database.OpenConnectionAsync();

        Assert.Equal(1L, await ReadForeignKeySettingAsync(firstConnection));
        Assert.Equal(1L, await ReadForeignKeySettingAsync(secondConnection));
    }

    [Fact]
    public async Task Version_ten_backfills_machine_types_setup_settings_and_order_lifecycle()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        {
            await DowngradeToV45Async(connection);
            await DowngradeToV44Async(connection);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE machine_telemetry_raw;
                DROP TABLE machine_connection_events;
                DROP TABLE machine_state_history;
                DROP TABLE machine_current_state;
                DROP TABLE machine_connections;
                DELETE FROM schema_migrations WHERE version = 44;
                DELETE FROM schema_migrations WHERE version = 43;
                DROP TRIGGER nc_program_headers_immutable_update;
                DROP TRIGGER nc_program_headers_immutable_delete;
                DROP TRIGGER haas_events_immutable_update;
                DROP TRIGGER haas_events_immutable_delete;
                DROP TABLE IF EXISTS haas_macro_write_audits;
                DROP TABLE haas_events;
                DROP TABLE haas_bench_state_intervals;
                DROP TABLE haas_bench_sessions;
                DROP TABLE haas_machine_snapshots;
                DROP TABLE haas_connection_settings;
                DROP TABLE nc_program_headers;
                DELETE FROM schema_migrations WHERE version = 42;
                DROP TABLE kitaron_material_orders;
                DELETE FROM schema_migrations WHERE version = 41;
                ALTER TABLE employee_resources DROP COLUMN first_part_running_speed_percent;
                ALTER TABLE employee_resources DROP COLUMN fixture_assembly_seconds;
                ALTER TABLE employee_resources DROP COLUMN tool_load_seconds_per_tool;
                DELETE FROM schema_migrations WHERE version = 40;
                DROP TRIGGER batch_material_reservation_batch_capacity_insert;
                DROP TRIGGER batch_material_reservation_receipt_capacity_insert;
                DROP TRIGGER batch_material_reservation_case_match_insert;
                DROP TABLE batch_material_reservations;
                DROP TABLE verified_material_receipts;
                DELETE FROM schema_migrations WHERE version = 39;
                DROP VIEW effective_batch_operation_nc_estimates;
                DROP TRIGGER gcode_release_analyses_immutable_delete;
                DROP TRIGGER gcode_release_analyses_immutable_update;
                DROP TABLE gcode_machine_cycle_estimates;
                DROP TABLE gcode_release_analyses;
                DELETE FROM schema_migrations WHERE version = 38;
                DROP TRIGGER tool_offset_readiness_records_immutable_update;
                DROP TRIGGER tool_offset_readiness_context_consistent;
                DROP TRIGGER machine_assignment_selected_release_matches_operation_update;
                DROP TRIGGER machine_assignment_selected_release_matches_operation_insert;
                DROP TABLE tool_offset_readiness_records;
                DROP TABLE batch_operation_material_readiness;
                DROP INDEX ix_machine_assignments_selected_gcode;
                ALTER TABLE machine_assignments DROP COLUMN selected_gcode_release_id;
                DELETE FROM schema_migrations WHERE version = 37;
                ALTER TABLE case_operations DROP COLUMN qa_seconds;
                ALTER TABLE case_operations DROP COLUMN load_unload_seconds;
                ALTER TABLE case_operations DROP COLUMN load_unload_requires_worker;
                ALTER TABLE case_operations DROP COLUMN automatic_loading;
                ALTER TABLE case_operations DROP COLUMN load_unload_every_n_parts;
                ALTER TABLE case_operations DROP COLUMN day_shift_only;
                ALTER TABLE batch_operations DROP COLUMN qa_seconds;
                ALTER TABLE batch_operations DROP COLUMN load_unload_seconds;
                ALTER TABLE batch_operations DROP COLUMN load_unload_requires_worker;
                ALTER TABLE batch_operations DROP COLUMN automatic_loading;
                ALTER TABLE batch_operations DROP COLUMN load_unload_every_n_parts;
                ALTER TABLE batch_operations DROP COLUMN day_shift_only;
                DROP TABLE operation_pause_events;
                DROP TABLE machine_assignment_overrides;
                DROP TABLE israeli_holiday_sync_state;
                DROP TABLE structured_event_log;
                DROP TABLE weekly_employee_efficiency_deliveries;
                DROP TABLE employee_work_measurements;
                DROP TABLE weekly_material_report_deliveries;
                DROP TABLE report_email_settings;
                DROP TABLE israeli_holidays;
                DROP TABLE employee_calendar_exceptions;
                DROP TABLE employee_resources;
                DROP TABLE setup_calendar_settings;
                DROP INDEX ix_batch_operations_production_release;
                ALTER TABLE batch_operations DROP COLUMN production_tool_table_file_hash;
                ALTER TABLE batch_operations DROP COLUMN production_gcode_file_hash;
                ALTER TABLE batch_operations DROP COLUMN production_tool_table_release_id;
                ALTER TABLE batch_operations DROP COLUMN production_gcode_release_id;
                ALTER TABLE batch_operations DROP COLUMN production_process_revision_id;
                DROP TRIGGER process_revisions_tool_count_consistent;
                DROP TABLE tool_table_release_tools;
                ALTER TABLE tool_table_releases DROP COLUMN required_tool_count;
                DROP TABLE gcode_releases;
                DROP TABLE process_revisions;
                DROP TABLE tool_table_releases;
                DROP TABLE machine_supported_postprocessors;
                DROP TABLE postprocessors;
                ALTER TABLE machines DROP COLUMN machine_time_factor;
                ALTER TABLE machines DROP COLUMN tool_change_time_seconds;
                ALTER TABLE machines DROP COLUMN rapid_rate_mm_per_min;
                ALTER TABLE machines DROP COLUMN usable_tool_positions;
                ALTER TABLE machines DROP COLUMN execution_mode;
                DROP INDEX ix_machines_machine_type_id;
                ALTER TABLE machines DROP COLUMN machine_type_id;
                DROP TABLE machine_types;
                ALTER TABLE eink_package_revisions DROP COLUMN setup_worker_id;
                ALTER TABLE eink_package_revisions DROP COLUMN setup_worker_first_name;
                ALTER TABLE eink_package_revisions DROP COLUMN setup_worker_last_name;
                ALTER TABLE eink_package_revisions DROP COLUMN setup_worker_photo_file_id;
                ALTER TABLE eink_package_revisions DROP COLUMN planned_setup_starts_at;
                ALTER TABLE eink_package_revisions DROP COLUMN planned_setup_ends_at;
                ALTER TABLE eink_package_revisions DROP COLUMN job_tools_json;
                ALTER TABLE eink_package_revisions DROP COLUMN expected_machine_tools_json;
                ALTER TABLE eink_package_revisions DROP COLUMN local_checklist_items_json;
                DROP INDEX ix_batch_operations_actual_machine_time;
                ALTER TABLE batch_operations DROP COLUMN actual_machine_id;
                ALTER TABLE batch_operations DROP COLUMN actual_end;
                ALTER TABLE batch_operations DROP COLUMN actual_start;
                ALTER TABLE machine_assignments DROP COLUMN planning_mode;
                DROP TABLE legacy_working_plan_imports;
                DROP TABLE case_components;
                DROP TABLE kitaron_sync_links;
                DROP TABLE kitaron_sync_state;
                DROP TABLE kitaron_mapping_settings;
                DROP TABLE kitaron_connection_settings;
                DELETE FROM schema_migrations WHERE version IN (10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36);
                PRAGMA user_version = 9;

                INSERT INTO working_calendars (id, name, time_zone_id)
                VALUES ('v10-calendar', 'V10 day', 'UTC');
                INSERT INTO machines (
                    id, number, name, machine_type, working_calendar_id, status, is_active)
                VALUES
                    ('v10-machine-1', 'V10-1', 'V10 One', 'Five axis', 'v10-calendar', 'active', 1),
                    ('v10-machine-2', 'V10-2', 'V10 Two', 'five AXIS', 'v10-calendar', 'active', 1);
                INSERT INTO cases (id, part_number, name, working_folder_path)
                VALUES ('v10-case', 'PN-V10', 'V10 case', 'C:\Cases\PN-V10');
                INSERT INTO case_operations (id, case_id, operation_number, route_position, name)
                VALUES
                    ('v10-case-op-running', 'v10-case', 10, 0, 'Running'),
                    ('v10-case-op-complete', 'v10-case', 20, 1, 'Complete');
                INSERT INTO orders (id, case_id, order_reference, quantity, work_finish_date, status)
                VALUES
                    ('v10-order-active', 'v10-case', 'SO-ACTIVE', 2, '2026-09-01', 'active'),
                    ('v10-order-running', 'v10-case', 'SO-RUNNING', 2, '2026-09-01', 'complete'),
                    ('v10-order-complete', 'v10-case', 'SO-COMPLETE', 2, '2026-09-01', 'active'),
                    ('v10-order-cancelled', 'v10-case', 'SO-CANCELLED', 1, '2026-09-01', 'cancelled');
                INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
                VALUES
                    ('v10-batch-running', 'v10-case', 'B-RUNNING', 'in_production', 2),
                    ('v10-batch-complete', 'v10-case', 'B-COMPLETE', 'complete', 2),
                    ('v10-batch-cancelled', 'v10-case', 'B-CANCELLED', 'complete', 1);
                INSERT INTO batch_allocations (
                    id, production_batch_id, allocation_type, order_id, quantity)
                VALUES
                    ('v10-allocation-running', 'v10-batch-running', 'order', 'v10-order-running', 2),
                    ('v10-allocation-complete', 'v10-batch-complete', 'order', 'v10-order-complete', 2),
                    ('v10-allocation-cancelled', 'v10-batch-cancelled', 'order', 'v10-order-cancelled', 1);
                INSERT INTO batch_operations (
                    id, production_batch_id, source_case_operation_id,
                    operation_number, route_position, name, status)
                VALUES
                    ('v10-op-running', 'v10-batch-running', 'v10-case-op-running', 10, 0, 'Running', 'suspended'),
                    ('v10-op-complete', 'v10-batch-complete', 'v10-case-op-complete', 20, 0, 'Complete', 'completed'),
                    ('v10-op-cancelled', 'v10-batch-cancelled', 'v10-case-op-complete', 20, 0, 'Complete', 'completed');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var migrator = new DatabaseMigrator(fixture.Database, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync();

        await using var reopened = await fixture.Database.OpenConnectionAsync();
        await using var assertion = reopened.CreateCommand();
        assertion.CommandText = """
            SELECT COUNT(DISTINCT machine_type_id)
            FROM machines
            WHERE id IN ('v10-machine-1', 'v10-machine-2');
            """;
        Assert.Equal(1L, (long)(await assertion.ExecuteScalarAsync())!);

        assertion.CommandText = "SELECT COUNT(*) FROM setup_calendar_settings WHERE id = 1 AND working_calendar_id IS NULL AND legacy_fallback_enabled = 1;";
        Assert.Equal(1L, (long)(await assertion.ExecuteScalarAsync())!);

        assertion.CommandText = """
            SELECT id, status
            FROM orders
            WHERE id LIKE 'v10-order-%'
            ORDER BY id;
            """;
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var reader = await assertion.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) statuses.Add(reader.GetString(0), reader.GetString(1));
        }
        Assert.Equal("active", statuses["v10-order-active"]);
        Assert.Equal("cancelled", statuses["v10-order-cancelled"]);
        Assert.Equal("complete", statuses["v10-order-complete"]);
        Assert.Equal("in_production", statuses["v10-order-running"]);

        assertion.CommandText = "PRAGMA foreign_key_check;";
        Assert.Null(await assertion.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Newer_database_version_is_rejected()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version = 61;";
            await command.ExecuteNonQueryAsync();
        }

        var migrator = new DatabaseMigrator(
            fixture.Database,
            NullLogger<DatabaseMigrator>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            migrator.MigrateAsync());

        Assert.Contains("newer than supported", exception.Message, StringComparison.Ordinal);
    }

    private static async Task DowngradeToV45Async(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER operational_anomaly_from_workflow_anomaly;
            DROP TRIGGER operational_anomaly_from_workflow_event;
            DROP TRIGGER operational_anomaly_from_expired_verification;
            DROP TRIGGER operational_anomaly_from_tablet_revoke;
            DROP TABLE operational_anomalies;
            DROP VIEW production_run_cycle_attempt_timing;
            DROP TRIGGER production_run_cycle_attempt_from_start;
            DROP TRIGGER production_run_cycle_attempt_interrupted;
            DROP TRIGGER production_run_cycle_attempt_completed;
            DROP TABLE production_run_cycle_attempt_outcomes;
            DROP TABLE production_run_cycle_attempts;
            DROP TABLE production_run_cycle_events;
            DELETE FROM schema_migrations WHERE version = 47;
            DROP TRIGGER machine_assignment_run_contains_legacy_operation;
            DROP TRIGGER machine_assignment_run_required_update;
            DROP TRIGGER machine_assignment_wraps_legacy_operation;
            DROP TRIGGER production_run_structure_locked_output_delete;
            DROP TRIGGER production_run_structure_locked_output_update;
            DROP TRIGGER production_run_structure_locked_program_delete;
            DROP TRIGGER production_run_structure_locked_program_update;
            DROP TRIGGER production_run_structure_locked_program_insert;
            DROP TRIGGER production_run_output_no_overallocation_insert;
            DROP TRIGGER production_run_output_target_matches_cycles_insert;
            DROP TRIGGER machine_assignment_selected_release_matches_operation_insert;
            DROP TRIGGER machine_assignment_selected_release_matches_operation_update;
            DROP INDEX ix_machine_assignments_machine_backlog;
            DROP INDEX ix_machine_assignments_selected_gcode;
            DROP INDEX ix_machine_assignments_batch_operation_compatibility;
            PRAGMA legacy_alter_table=ON;
            ALTER TABLE machine_assignments RENAME TO machine_assignments_v46;
            CREATE TABLE machine_assignments (
                id TEXT PRIMARY KEY,
                batch_operation_id TEXT NOT NULL UNIQUE,
                machine_id TEXT NOT NULL,
                backlog_position INTEGER NOT NULL CHECK(backlog_position>=0),
                version INTEGER NOT NULL DEFAULT 1 CHECK(version>0),
                created_at TEXT NOT NULL DEFAULT(strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                updated_at TEXT NOT NULL DEFAULT(strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                planning_mode TEXT NOT NULL DEFAULT 'manual' CHECK(planning_mode IN('forward','backward','manual')),
                selected_gcode_release_id TEXT REFERENCES gcode_releases(id) ON DELETE RESTRICT,
                FOREIGN KEY(batch_operation_id) REFERENCES batch_operations(id) ON DELETE RESTRICT,
                FOREIGN KEY(machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                UNIQUE(machine_id,backlog_position));
            INSERT INTO machine_assignments(id,batch_operation_id,machine_id,backlog_position,version,created_at,updated_at,planning_mode,selected_gcode_release_id)
            SELECT id,batch_operation_id,machine_id,backlog_position,version,created_at,updated_at,planning_mode,selected_gcode_release_id FROM machine_assignments_v46;
            DROP TABLE machine_assignments_v46;
            PRAGMA legacy_alter_table=OFF;
            CREATE INDEX ix_machine_assignments_machine_backlog ON machine_assignments(machine_id,backlog_position);
            CREATE INDEX ix_machine_assignments_selected_gcode ON machine_assignments(selected_gcode_release_id);
            CREATE TRIGGER machine_assignment_selected_release_matches_operation_insert
            BEFORE INSERT ON machine_assignments WHEN NEW.selected_gcode_release_id IS NOT NULL AND NOT EXISTS(
                SELECT 1 FROM gcode_releases release JOIN batch_operations operation ON operation.id=NEW.batch_operation_id AND operation.source_case_operation_id=release.case_operation_id WHERE release.id=NEW.selected_gcode_release_id)
            BEGIN SELECT RAISE(ABORT,'selected G-code release must belong to the assigned Batch Operation source');END;
            CREATE TRIGGER machine_assignment_selected_release_matches_operation_update
            BEFORE UPDATE OF selected_gcode_release_id,batch_operation_id ON machine_assignments WHEN NEW.selected_gcode_release_id IS NOT NULL AND NOT EXISTS(
                SELECT 1 FROM gcode_releases release JOIN batch_operations operation ON operation.id=NEW.batch_operation_id AND operation.source_case_operation_id=release.case_operation_id WHERE release.id=NEW.selected_gcode_release_id)
            BEGIN SELECT RAISE(ABORT,'selected G-code release must belong to the assigned Batch Operation source');END;
            DROP TABLE production_run_outputs;
            DROP TABLE production_run_programs;
            DROP TABLE production_runs;
            DELETE FROM schema_migrations WHERE version=46;
            PRAGMA user_version=45;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DowngradeToV44Async(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER case_operations_create_default_manufacturing_program;
            DROP TRIGGER manufacturing_program_outputs_immutable_delete;
            DROP TRIGGER manufacturing_program_outputs_immutable_update;
            DROP TRIGGER process_revisions_program_immutable;
            DROP TABLE manufacturing_program_revision_outputs;
            DROP INDEX ix_process_revisions_program_history;
            DROP INDEX ux_process_revisions_active_program;
            CREATE UNIQUE INDEX ux_process_revisions_active_operation
            ON process_revisions(case_operation_id) WHERE is_active=1;
            ALTER TABLE process_revisions DROP COLUMN manufacturing_program_id;
            DROP TABLE manufacturing_programs;
            DELETE FROM schema_migrations WHERE version=45;
            PRAGMA user_version=44;
            """;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public void Database_configuration_rejects_unc_network_share()
    {
        var values = new Dictionary<string, string?>
        {
            ["Database:Path"] = @"\\factory-share\planner\planner.db"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DatabaseOptions.FromConfiguration(configuration, AppContext.BaseDirectory));

        Assert.Contains("server-local", exception.Message, StringComparison.Ordinal);
    }

    private static async Task AssertTimestampColumnsAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}');";

        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Contains("created_at", columns);
        Assert.Contains("updated_at", columns);
    }

    private static async Task<HashSet<string>> ReadColumnNamesAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}');";

        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task<long> ReadForeignKeySettingAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
