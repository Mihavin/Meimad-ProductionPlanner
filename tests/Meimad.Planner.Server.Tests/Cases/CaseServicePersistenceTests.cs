using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Configuration;
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
        Assert.Equal(1800, read.CurrentSetupTimeSeconds);
        Assert.Equal(240, read.CurrentCycleTimePerPartSeconds);
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
                currentCycleTimePerPartSeconds: OptionalField<int?>.Specified(300),
                notes: OptionalField<string?>.Specified("Updated notes")),
            editAuthority);

        Assert.Equal(2, updated.Version);
        Assert.Equal("Customer B", updated.Customer);
        Assert.Null(updated.PreviewPath);
        Assert.Equal(300, updated.CurrentCycleTimePerPartSeconds);
        Assert.Equal("Updated notes", updated.Notes);
        Assert.Equal(created.PartNumber, updated.PartNumber);
        Assert.Equal(created.WorkingFolderPath, updated.WorkingFolderPath);

        var read = await service.GetByIdAsync(created.CaseId);
        Assert.Equal(updated, read);
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
        1800,
        240,
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
        OptionalField<int?>? currentSetupTimeSeconds = null,
        OptionalField<int?>? currentCycleTimePerPartSeconds = null,
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
        currentSetupTimeSeconds ?? OptionalField<int?>.Unspecified,
        currentCycleTimePerPartSeconds ?? OptionalField<int?>.Unspecified,
        notes ?? OptionalField<string?>.Unspecified);
}
