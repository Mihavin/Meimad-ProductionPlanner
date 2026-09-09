using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Domain.CaseOperations;
using Meimad.Planner.Server.Domain.Cases;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Cases;

public sealed class CaseServicePersistenceTests
{
    [Fact]
    public async Task Case_can_be_created_and_read_without_orders()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var editAuthority = await GrantEditModeAsync(fixture.Database);
        var service = CreateService(fixture.Database);
        var command = CompleteCaseCommand(Path.Combine(Path.GetTempPath(), "external-case-100"));

        var created = await service.CreateAsync(command, editAuthority);
        var read = await service.GetByIdAsync(created.CaseId);

        Assert.NotNull(read);
        Assert.Equal("PN-100", read.PartNumber);
        Assert.Equal("Bearing housing", read.Name);
        Assert.Equal("Customer A", read.Customer);
        Assert.Equal("PO-7721", read.CustomerReference);
        Assert.Equal("Aluminium", read.MaterialType);
        Assert.Equal("7075-T6", read.MaterialSpecification);
        Assert.Equal("Plate", read.RawMaterialForm);
        Assert.Equal("30 x 120 x 180 mm", read.RawMaterialDimensions);
        Assert.Equal(0, read.CurrentSetupTimeSeconds);
        Assert.Equal(0, read.CurrentCycleTimePerPartSeconds);
        Assert.Equal(1, read.Version);

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var orderCountCommand = connection.CreateCommand();
        orderCountCommand.CommandText = "SELECT COUNT(*) FROM orders;";
        Assert.Equal(0L, (long)(await orderCountCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Case_update_changes_only_supplied_fields_and_increments_version()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var editAuthority = await GrantEditModeAsync(fixture.Database);
        var service = CreateService(fixture.Database);
        var created = await service.CreateAsync(
            CompleteCaseCommand(Path.Combine(Path.GetTempPath(), "external-case-update")),
            editAuthority);

        var updated = await service.UpdateAsync(
            created.CaseId,
            created.Version,
            Patch(
                customer: OptionalField<string?>.Specified("Customer B"),
                previewPath: OptionalField<string?>.Specified(null),
                notes: OptionalField<string?>.Specified("Updated notes")),
            editAuthority);

        Assert.Equal(2, updated.Version);
        Assert.Equal("Customer B", updated.Customer);
        Assert.Null(updated.PreviewPath);
        Assert.Equal(0, updated.CurrentCycleTimePerPartSeconds);
        Assert.Equal("Updated notes", updated.Notes);
        Assert.Equal(created.PartNumber, updated.PartNumber);
        Assert.Equal(created.WorkingFolderPath, updated.WorkingFolderPath);

        var read = await service.GetByIdAsync(created.CaseId);
        Assert.Equal(updated, read);
    }

    [Fact]
    public async Task Case_timing_is_the_sum_of_operation_timings_with_null_as_zero()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var editAuthority = await GrantEditModeAsync(fixture.Database);
        var service = CreateService(fixture.Database);
        var created = await service.CreateAsync(
            CompleteCaseCommand(Path.Combine(Path.GetTempPath(), "external-case-timing")),
            editAuthority);

        var first = await service.CreateOperationAsync(
            created.CaseId,
            new CreateCaseOperationCommand(
                10,
                "Saw",
                "saw",
                60,
                30,
                "INDEPENDENT",
                null,
                null),
            editAuthority);
        await service.CreateOperationAsync(
            created.CaseId,
            new CreateCaseOperationCommand(
                20,
                "Mill",
                "mill",
                120,
                null,
                "SEQUENTIAL",
                first.CaseOperationId,
                null),
            editAuthority);

        var read = await service.GetByIdAsync(created.CaseId);

        Assert.NotNull(read);
        Assert.Equal(180, read.CurrentSetupTimeSeconds);
        Assert.Equal(30, read.CurrentCycleTimePerPartSeconds);
    }

    [Fact]
    public async Task Updating_case_operation_times_updates_not_started_batch_snapshots_only()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var editAuthority = await GrantEditModeAsync(fixture.Database);
        var service = CreateService(fixture.Database);
        var created = await service.CreateAsync(
            CompleteCaseCommand(Path.Combine(Path.GetTempPath(), "external-case-operation-sync")),
            editAuthority);
        var operation = await service.CreateOperationAsync(
            created.CaseId,
            new CreateCaseOperationCommand(10, "Mill", "mill", 60, 30, "INDEPENDENT", null, null),
            editAuthority);

        await using (var connection = await fixture.Database.OpenConnectionAsync())
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
                VALUES ('batch-not-started', $caseId, 'B-NS', 'waiting', 1),
                       ('batch-in-progress', $caseId, 'B-IP', 'in_progress', 1);
                INSERT INTO batch_operations (
                    id, production_batch_id, source_case_operation_id, operation_number,
                    route_position, name, setup_seconds, cycle_seconds, status)
                VALUES
                    ('batch-operation-not-started', 'batch-not-started', $operationId, 10, 0, 'Mill', 60, 30, 'not_started'),
                    ('batch-operation-in-progress', 'batch-in-progress', $operationId, 10, 0, 'Mill', 60, 30, 'in_progress');
                """;
            insert.Parameters.AddWithValue("$caseId", created.CaseId);
            insert.Parameters.AddWithValue("$operationId", operation.CaseOperationId);
            await insert.ExecuteNonQueryAsync();
        }

        await service.UpdateOperationAsync(
            created.CaseId,
            operation.CaseOperationId,
            operation.Version,
            new UpdateCaseOperationCommand(
                OptionalField<int>.Unspecified,
                OptionalField<string?>.Unspecified,
                OptionalField<string?>.Unspecified,
                OptionalField<int?>.Specified(150),
                OptionalField<int?>.Specified(75),
                OptionalField<string?>.Unspecified,
                OptionalField<string?>.Unspecified,
                OptionalField<string?>.Unspecified),
            editAuthority);

        await using var assertionConnection = await fixture.Database.OpenConnectionAsync();
        await using var assertion = assertionConnection.CreateCommand();
        assertion.CommandText = """
            SELECT id, setup_seconds, cycle_seconds
            FROM batch_operations
            ORDER BY id;
            """;
        await using var reader = await assertion.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("batch-operation-in-progress", reader.GetString(0));
        Assert.Equal(60, reader.GetInt32(1));
        Assert.Equal(30, reader.GetInt32(2));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("batch-operation-not-started", reader.GetString(0));
        Assert.Equal(150, reader.GetInt32(1));
        Assert.Equal(75, reader.GetInt32(2));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Creating_case_operation_appends_it_to_open_batches_only()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var editAuthority = await GrantEditModeAsync(fixture.Database);
        var service = CreateService(fixture.Database);
        var created = await service.CreateAsync(
            CompleteCaseCommand(Path.Combine(Path.GetTempPath(), "external-case-route-append")),
            editAuthority);
        var first = await service.CreateOperationAsync(
            created.CaseId,
            new CreateCaseOperationCommand(10, "Mill", "mill", 60, 30, "INDEPENDENT", null, null),
            editAuthority);

        await using (var connection = await fixture.Database.OpenConnectionAsync())
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
                VALUES ('batch-waiting', $caseId, 'B-W', 'waiting', 4),
                       ('batch-in-production', $caseId, 'B-P', 'in_production', 4),
                       ('batch-complete', $caseId, 'B-C', 'complete', 4),
                       ('batch-cancelled', $caseId, 'B-X', 'cancelled', 4);
                INSERT INTO batch_operations (
                    id, production_batch_id, source_case_operation_id, operation_number,
                    route_position, name, setup_seconds, cycle_seconds, status)
                VALUES
                    ('op-waiting', 'batch-waiting', $operationId, 10, 0, 'Mill', 60, 30, 'not_started'),
                    ('op-in-production', 'batch-in-production', $operationId, 10, 0, 'Mill', 60, 30, 'in_progress'),
                    ('op-complete', 'batch-complete', $operationId, 10, 0, 'Mill', 60, 30, 'completed'),
                    ('op-cancelled', 'batch-cancelled', $operationId, 10, 0, 'Mill', 60, 30, 'cancelled');
                """;
            insert.Parameters.AddWithValue("$caseId", created.CaseId);
            insert.Parameters.AddWithValue("$operationId", first.CaseOperationId);
            await insert.ExecuteNonQueryAsync();
        }

        var second = await service.CreateOperationAsync(
            created.CaseId,
            new CreateCaseOperationCommand(
                20, "Deburr", "bench", 120, 45, "SEQUENTIAL", first.CaseOperationId, null,
                QaTimeAfterSetupSeconds: 15, LoadUnloadTimeSeconds: 20, LoadUnloadRequiresWorker: true),
            editAuthority);

        await using var assertionConnection = await fixture.Database.OpenConnectionAsync();
        await using (var appended = assertionConnection.CreateCommand())
        {
            appended.CommandText = """
                SELECT production_batch_id, operation_number, route_position, status, dependency_type,
                       predecessor_source_case_operation_id, name, required_machine_type,
                       setup_seconds, cycle_seconds, qa_seconds, load_unload_seconds,
                       load_unload_requires_worker
                FROM batch_operations
                WHERE source_case_operation_id = $secondId
                ORDER BY production_batch_id;
                """;
            appended.Parameters.AddWithValue("$secondId", second.CaseOperationId);
            await using var reader = await appended.ExecuteReaderAsync();
            foreach (var batchId in new[] { "batch-in-production", "batch-waiting" })
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(batchId, reader.GetString(0));
                Assert.Equal(20, reader.GetInt32(1));
                Assert.Equal(1, reader.GetInt32(2));
                Assert.Equal("not_started", reader.GetString(3));
                Assert.Equal("sequential", reader.GetString(4));
                Assert.Equal(first.CaseOperationId, reader.GetString(5));
                Assert.Equal("Deburr", reader.GetString(6));
                Assert.Equal("bench", reader.GetString(7));
                Assert.Equal(120, reader.GetInt32(8));
                Assert.Equal(45, reader.GetInt32(9));
                Assert.Equal(15, reader.GetInt32(10));
                Assert.Equal(20, reader.GetInt32(11));
                Assert.Equal(1, reader.GetInt32(12));
            }

            Assert.False(await reader.ReadAsync());
        }

        await using (var versions = assertionConnection.CreateCommand())
        {
            versions.CommandText = """
                SELECT group_concat(id || ':' || version, ',')
                FROM (SELECT id, version FROM production_batches ORDER BY id);
                """;
            Assert.Equal(
                "batch-cancelled:1,batch-complete:1,batch-in-production:2,batch-waiting:2",
                (string)(await versions.ExecuteScalarAsync())!);
        }

        await using (var events = assertionConnection.CreateCommand())
        {
            events.CommandText = """
                SELECT COUNT(*) FROM structured_event_log
                WHERE event_type = 'production_batch_route_appended';
                """;
            Assert.Equal(2L, (long)(await events.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task Creating_case_operation_rejects_a_number_still_used_by_an_open_batch_snapshot()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var editAuthority = await GrantEditModeAsync(fixture.Database);
        var service = CreateService(fixture.Database);
        var created = await service.CreateAsync(
            CompleteCaseCommand(Path.Combine(Path.GetTempPath(), "external-case-route-number-conflict")),
            editAuthority);
        var first = await service.CreateOperationAsync(
            created.CaseId,
            new CreateCaseOperationCommand(10, "Mill", "mill", 60, 30, "INDEPENDENT", null, null),
            editAuthority);

        await using (var connection = await fixture.Database.OpenConnectionAsync())
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
                VALUES ('batch-open', $caseId, 'B-OPEN', 'waiting', 4);
                INSERT INTO batch_operations (
                    id, production_batch_id, source_case_operation_id, operation_number,
                    route_position, name, setup_seconds, cycle_seconds, status)
                VALUES ('op-open', 'batch-open', $operationId, 10, 0, 'Mill', 60, 30, 'not_started');
                """;
            insert.Parameters.AddWithValue("$caseId", created.CaseId);
            insert.Parameters.AddWithValue("$operationId", first.CaseOperationId);
            await insert.ExecuteNonQueryAsync();
        }

        // Renumbering the source operation leaves the open Batch snapshot at number 10.
        await service.UpdateOperationAsync(
            created.CaseId,
            first.CaseOperationId,
            first.Version,
            new UpdateCaseOperationCommand(
                OptionalField<int>.Specified(15),
                OptionalField<string?>.Unspecified,
                OptionalField<string?>.Unspecified,
                OptionalField<int?>.Unspecified,
                OptionalField<int?>.Unspecified,
                OptionalField<string?>.Unspecified,
                OptionalField<string?>.Unspecified,
                OptionalField<string?>.Unspecified),
            editAuthority);

        var exception = await Assert.ThrowsAsync<CaseOperationValidationException>(() =>
            service.CreateOperationAsync(
                created.CaseId,
                new CreateCaseOperationCommand(10, "Deburr", "bench", 60, 30, "INDEPENDENT", null, null),
                editAuthority));
        Assert.Contains(exception.Issues, issue => issue.Code == "batch_operation_number_in_use");

        // The whole creation rolled back: one Case Operation and one Batch snapshot remain.
        await using var assertionConnection = await fixture.Database.OpenConnectionAsync();
        await using var counts = assertionConnection.CreateCommand();
        counts.CommandText = """
            SELECT (SELECT COUNT(*) FROM case_operations WHERE case_id = $caseId)
                   || '/' || (SELECT COUNT(*) FROM batch_operations);
            """;
        counts.Parameters.AddWithValue("$caseId", created.CaseId);
        Assert.Equal("1/1", (string)(await counts.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Case_survives_database_reopen()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var editAuthority = await GrantEditModeAsync(fixture.Database);
        var firstService = CreateService(fixture.Database);
        var created = await firstService.CreateAsync(
            CompleteCaseCommand(Path.Combine(Path.GetTempPath(), "external-case-reopen")),
            editAuthority);

        SqliteConnection.ClearAllPools();
        var reopenedDatabase = new SqliteDatabase(new DatabaseOptions(fixture.DatabasePath));
        var migrator = new DatabaseMigrator(
            reopenedDatabase,
            NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync();
        var reopenedService = CreateService(reopenedDatabase);

        var reopened = await reopenedService.GetByIdAsync(created.CaseId);

        Assert.Equal(created, reopened);
    }

    [Fact]
    public async Task Missing_working_folder_path_is_rejected()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var editAuthority = await GrantEditModeAsync(fixture.Database);
        var service = CreateService(fixture.Database);
        var command = CompleteCaseCommand(workingFolderPath: null);

        var exception = await Assert.ThrowsAsync<CaseValidationException>(() =>
            service.CreateAsync(command, editAuthority));

        Assert.Contains(exception.Issues, issue =>
            issue.Field == "workingFolderPath" && issue.Code == "required");
    }

    [Fact]
    public async Task Unavailable_external_paths_are_stored_without_creating_files_or_directories()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var editAuthority = await GrantEditModeAsync(fixture.Database);
        var service = CreateService(fixture.Database);
        var absentRoot = Path.Combine(
            Path.GetTempPath(),
            "MeimadPlanner.ExternalPath.Tests",
            Guid.NewGuid().ToString("N"));
        var workingFolder = Path.Combine(absentRoot, "case-folder");
        var previewPath = Path.Combine(absentRoot, "preview.png");
        Assert.False(Directory.Exists(absentRoot));

        var created = await service.CreateAsync(
            CompleteCaseCommand(workingFolder, previewPath),
            editAuthority);

        Assert.Equal(workingFolder, created.WorkingFolderPath);
        Assert.Equal(previewPath, created.PreviewPath);
        Assert.False(Directory.Exists(absentRoot));
        Assert.False(File.Exists(previewPath));
    }

    [Fact]
    public async Task Stale_edit_generation_is_rejected_before_case_write()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var activeAuthority = await GrantEditModeAsync(fixture.Database);
        var staleAuthority = activeAuthority with { Generation = activeAuthority.Generation - 1 };
        var service = CreateService(fixture.Database);

        var exception = await Assert.ThrowsAsync<EditModeMutationException>(() =>
            service.CreateAsync(
                CompleteCaseCommand(Path.Combine(Path.GetTempPath(), "stale-edit-case")),
                staleAuthority));

        Assert.Equal("edit_generation_stale", exception.Code);
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM cases;";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    private static CaseService CreateService(SqliteDatabase database) =>
        new(new SqliteCaseRepository(database), TimeProvider.System);

    private static async Task<EditAuthority> GrantEditModeAsync(SqliteDatabase database)
    {
        var editAuthority = new EditAuthority("case-service-test-client", 1);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE edit_tokens
            SET holder_client_id = $clientId,
                holder_user_id = 'case-service-test-user',
                generation = $generation,
                acquired_at = '2026-08-11T00:00:00Z',
                version = version + 1,
                updated_at = '2026-08-11T00:00:00Z'
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$clientId", editAuthority.ClientId);
        command.Parameters.AddWithValue("$generation", editAuthority.Generation);
        await command.ExecuteNonQueryAsync();
        return editAuthority;
    }

    private static CreateCaseCommand CompleteCaseCommand(
        string? workingFolderPath,
        string? previewPath = null) => new(
        "PN-100",
        "Bearing housing",
        "A",
        "Customer A",
        "PO-7721",
        previewPath ?? Path.Combine(Path.GetTempPath(), "external-preview-100.png"),
        workingFolderPath,
        "Aluminium",
        "7075-T6",
        "Plate",
        "30 x 120 x 180 mm",
        "Initial notes");

    private static UpdateCaseCommand Patch(
        OptionalField<string?>? partNumber = null,
        OptionalField<string?>? name = null,
        OptionalField<string?>? revision = null,
        OptionalField<string?>? customer = null,
        OptionalField<string?>? customerReference = null,
        OptionalField<string?>? previewPath = null,
        OptionalField<string?>? workingFolderPath = null,
        OptionalField<string?>? materialType = null,
        OptionalField<string?>? materialSpecification = null,
        OptionalField<string?>? rawMaterialForm = null,
        OptionalField<string?>? rawMaterialDimensions = null,
        OptionalField<string?>? notes = null) => new(
        partNumber ?? OptionalField<string?>.Unspecified,
        name ?? OptionalField<string?>.Unspecified,
        revision ?? OptionalField<string?>.Unspecified,
        customer ?? OptionalField<string?>.Unspecified,
        customerReference ?? OptionalField<string?>.Unspecified,
        previewPath ?? OptionalField<string?>.Unspecified,
        workingFolderPath ?? OptionalField<string?>.Unspecified,
        materialType ?? OptionalField<string?>.Unspecified,
        materialSpecification ?? OptionalField<string?>.Unspecified,
        rawMaterialForm ?? OptionalField<string?>.Unspecified,
        rawMaterialDimensions ?? OptionalField<string?>.Unspecified,
        notes ?? OptionalField<string?>.Unspecified);
}
