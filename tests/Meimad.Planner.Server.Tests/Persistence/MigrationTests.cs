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
        Assert.Equal(33L, (long)(await versionCommand.ExecuteScalarAsync())!);

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
        Assert.Equal(33L, (long)(await command.ExecuteScalarAsync())!);
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
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
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
                DELETE FROM schema_migrations WHERE version IN (5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33);
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
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
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
                DELETE FROM schema_migrations WHERE version IN (9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33);
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
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
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
                DELETE FROM schema_migrations WHERE version IN (10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33);
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
            command.CommandText = "PRAGMA user_version = 34;";
            await command.ExecuteNonQueryAsync();
        }

        var migrator = new DatabaseMigrator(
            fixture.Database,
            NullLogger<DatabaseMigrator>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            migrator.MigrateAsync());

        Assert.Contains("newer than supported", exception.Message, StringComparison.Ordinal);
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
