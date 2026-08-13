using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.JobPackages;
using Meimad.Planner.Server.Domain.JobPackages;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteJobPackageRepository : IJobPackageRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteDatabase database;

    public SqliteJobPackageRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<JobPackageGenerationContext?> ReadGenerationContextAsync(
        string batchOperationId,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        var context = await ReadContextAsync(
            connection,
            transaction,
            batchOperationId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return context;
    }

    public async Task PublishAsync(
        JobPackage package,
        JobPackageContextStamp expectedContext,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        var current = await ReadContextAsync(
            connection,
            transaction,
            package.Snapshot.BatchOperationId,
            cancellationToken);
        if (current?.Snapshot is null || current.Stamp != expectedContext)
        {
            throw new JobPackageContextChangedException();
        }

        try
        {
            await InsertRevisionAsync(connection, transaction, package, cancellationToken);
            foreach (var asset in package.Assets)
            {
                await InsertAssetAsync(
                    connection,
                    transaction,
                    package.PackageId,
                    asset,
                    package.PublishedAt,
                    cancellationToken);
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new JobPackageRevisionConflictException(package.Revision);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<JobPackageSetupWorker?> ReadSetupWorkerAsync(
        string resourceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, first_name, last_name, photo_path
            FROM employee_resources
            WHERE id = $resourceId
              AND resource_type = 'setup_worker'
              AND is_active = 1;
            """;
        command.Parameters.AddWithValue("$resourceId", resourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new JobPackageSetupWorker(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                NullableString(reader, 3))
            : null;
    }

    private static async Task<JobPackageGenerationContext?> ReadContextAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string batchOperationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT cases.id, cases.version, cases.part_number, cases.name,
                   cases.revision, cases.customer, cases.working_folder_path,
                   cases.preview_reference,
                   production_batches.id, production_batches.version,
                   production_batches.batch_number, production_batches.planned_quantity,
                   batch_operations.id, batch_operations.version,
                   batch_operations.operation_number, batch_operations.name,
                   machine_assignments.id, machine_assignments.version,
                   machines.id, machines.version, machines.number, machines.name
            FROM batch_operations
            JOIN production_batches
              ON production_batches.id = batch_operations.production_batch_id
            JOIN cases ON cases.id = production_batches.case_id
            LEFT JOIN machine_assignments
              ON machine_assignments.batch_operation_id = batch_operations.id
            LEFT JOIN machines ON machines.id = machine_assignments.machine_id
            WHERE batch_operations.id = $batchOperationId;
            """;
        command.Parameters.AddWithValue("$batchOperationId", batchOperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var stamp = new JobPackageContextStamp(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(8),
            reader.GetInt32(9),
            reader.GetString(12),
            reader.GetInt32(13),
            NullableString(reader, 16),
            NullableInt32(reader, 17),
            NullableString(reader, 18),
            NullableInt32(reader, 19));
        JobPackageSnapshot? snapshot = null;
        if (!reader.IsDBNull(18))
        {
            snapshot = new JobPackageSnapshot(
                reader.GetString(18),
                reader.GetString(20),
                reader.GetString(21),
                reader.GetString(0),
                reader.GetString(2),
                reader.GetString(3),
                NullableString(reader, 4),
                NullableString(reader, 5),
                reader.GetString(8),
                reader.GetString(10),
                reader.GetInt32(11),
                reader.GetString(12),
                reader.GetInt32(14),
                reader.GetString(15));
        }

        return new JobPackageGenerationContext(
            snapshot,
            reader.GetString(6),
            NullableString(reader, 7),
            stamp);
    }

    private static async Task InsertRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobPackage package,
        CancellationToken cancellationToken)
    {
        var snapshot = package.Snapshot;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO eink_package_revisions (
                id, batch_operation_id, revision, tool_cart_id, published_at,
                machine_id, machine_number, machine_name,
                case_id, part_number, part_name, part_revision, customer,
                production_batch_id, batch_number, planned_quantity,
                operation_number, operation_name,
                setup_worker_id, setup_worker_first_name, setup_worker_last_name,
                setup_worker_photo_file_id, planned_setup_starts_at, planned_setup_ends_at,
                job_tools_json, expected_machine_tools_json, local_checklist_items_json,
                created_at, updated_at)
            VALUES (
                $id, $batchOperationId, $revision, $toolCartId, $publishedAt,
                $machineId, $machineNumber, $machineName,
                $caseId, $partNumber, $partName, $partRevision, $customer,
                $batchId, $batchNumber, $plannedQuantity,
                $operationNumber, $operationName,
                $setupWorkerId, $setupWorkerFirstName, $setupWorkerLastName,
                $setupWorkerPhotoFileId, $plannedSetupStartsAt, $plannedSetupEndsAt,
                $jobToolsJson, $expectedMachineToolsJson, $localChecklistItemsJson,
                $publishedAt, $publishedAt);
            """;
        Bind(command, "$id", package.PackageId);
        Bind(command, "$batchOperationId", snapshot.BatchOperationId);
        Bind(command, "$revision", package.Revision);
        Bind(command, "$toolCartId", package.ToolCartId);
        Bind(command, "$publishedAt", Iso(package.PublishedAt));
        Bind(command, "$machineId", snapshot.MachineId);
        Bind(command, "$machineNumber", snapshot.MachineNumber);
        Bind(command, "$machineName", snapshot.MachineName);
        Bind(command, "$caseId", snapshot.CaseId);
        Bind(command, "$partNumber", snapshot.PartNumber);
        Bind(command, "$partName", snapshot.PartName);
        Bind(command, "$partRevision", snapshot.PartRevision);
        Bind(command, "$customer", snapshot.Customer);
        Bind(command, "$batchId", snapshot.BatchId);
        Bind(command, "$batchNumber", snapshot.BatchNumber);
        Bind(command, "$plannedQuantity", snapshot.PlannedQuantity);
        Bind(command, "$operationNumber", snapshot.OperationNumber);
        Bind(command, "$operationName", snapshot.OperationName);
        Bind(command, "$setupWorkerId", snapshot.SetupWorker?.ResourceId);
        Bind(command, "$setupWorkerFirstName", snapshot.SetupWorker?.FirstName);
        Bind(command, "$setupWorkerLastName", snapshot.SetupWorker?.LastName);
        Bind(command, "$setupWorkerPhotoFileId", snapshot.SetupWorker?.PhotoFileId);
        Bind(command, "$plannedSetupStartsAt", snapshot.PlannedSetupStartsAt is null
            ? null
            : Iso(snapshot.PlannedSetupStartsAt.Value));
        Bind(command, "$plannedSetupEndsAt", snapshot.PlannedSetupEndsAt is null
            ? null
            : Iso(snapshot.PlannedSetupEndsAt.Value));
        Bind(command, "$jobToolsJson", JsonSerializer.Serialize(snapshot.JobTools ?? [], JsonOptions));
        Bind(command, "$expectedMachineToolsJson", JsonSerializer.Serialize(
            snapshot.ExpectedMachineTools ?? [], JsonOptions));
        Bind(command, "$localChecklistItemsJson", JsonSerializer.Serialize(
            snapshot.LocalChecklistItems ?? [], JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAssetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string packageId,
        JobPackageAsset asset,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO eink_package_files (
                id, package_revision_id, logical_path, storage_relative_path,
                media_type, byte_length, sha256, modified_at, display_order,
                asset_type, created_at, updated_at)
            VALUES (
                $id, $packageId, $logicalPath, $storageRelativePath,
                $mediaType, $byteLength, $sha256, $modifiedAt, $displayOrder,
                $assetType, $createdAt, $updatedAt);
            """;
        Bind(command, "$id", asset.FileId);
        Bind(command, "$packageId", packageId);
        Bind(command, "$logicalPath", asset.LogicalPath);
        Bind(command, "$storageRelativePath", asset.StorageRelativePath);
        Bind(command, "$mediaType", asset.MediaType);
        Bind(command, "$byteLength", asset.ByteLength);
        Bind(command, "$sha256", asset.Sha256);
        Bind(command, "$modifiedAt", Iso(asset.ModifiedAt));
        Bind(command, "$displayOrder", asset.DisplayOrder);
        Bind(command, "$assetType", asset.AssetType.ToStorageToken());
        Bind(command, "$createdAt", Iso(publishedAt));
        Bind(command, "$updatedAt", Iso(publishedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureEditAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || reader.IsDBNull(0)
            || !string.Equals(reader.GetString(0), editAuthority.ClientId, StringComparison.Ordinal)
            || reader.GetInt64(1) != editAuthority.Generation)
        {
            throw new EditModeMutationException(
                "edit_authority_required",
                "The active Server Edit Mode generation is required to publish a job package.");
        }
    }

    private static void Bind(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? NullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
