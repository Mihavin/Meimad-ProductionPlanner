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
        "production_batches",
        "batch_allocations",
        "batch_operations",
        "machines",
        "machine_assignments",
        "downtimes",
        "working_calendars",
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
        Assert.Equal(9L, (long)(await versionCommand.ExecuteScalarAsync())!);

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
        Assert.Equal(9L, (long)(await command.ExecuteScalarAsync())!);
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
                DELETE FROM schema_migrations WHERE version IN (5, 6, 7, 8, 9);
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
                DROP INDEX ix_batch_operations_predecessor_snapshot;
                ALTER TABLE batch_operations DROP COLUMN simultaneous_group_key;
                ALTER TABLE batch_operations DROP COLUMN predecessor_source_case_operation_id;
                ALTER TABLE batch_operations DROP COLUMN dependency_type;
                DELETE FROM schema_migrations WHERE version = 9;
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
    public async Task Newer_database_version_is_rejected()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version = 10;";
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
