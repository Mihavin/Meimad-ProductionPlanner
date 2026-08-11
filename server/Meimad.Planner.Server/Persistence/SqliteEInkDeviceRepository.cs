using System.Globalization;
using System.Text;
using Meimad.Planner.Server.Application.EInk;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteEInkDeviceRepository : IEInkDeviceRepository
{
    private readonly SqliteDatabase database;

    public SqliteEInkDeviceRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<EInkDeviceSource?> ReadAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var device = await ReadDeviceAsync(connection, transaction, deviceId, cancellationToken);
        if (device is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var machine = device.MachineId is null
            ? null
            : await ReadMachineAsync(
                connection,
                transaction,
                device.MachineId,
                cancellationToken);
        var operations = device.MachineId is null
            ? []
            : await ReadBacklogAsync(
                connection,
                transaction,
                device.MachineId,
                cancellationToken);
        var currentOperation = operations
            .Where(value => value.Operation.Status is not "complete" and not "cancelled")
            .OrderBy(value => value.Operation.BacklogPosition)
            .FirstOrDefault();
        var package = currentOperation is null
            ? null
            : await ReadPackageAsync(
                connection,
                transaction,
                currentOperation.Operation.OperationId,
                currentOperation.Operation.BatchId,
                device.MachineId!,
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var revisionSeed = new StringBuilder()
            .Append(device.UpdatedAt).Append('|')
            .Append(machine?.UpdatedAt).Append('|');
        foreach (var operation in operations)
        {
            revisionSeed.Append(operation.Operation.OperationId).Append(':')
                .Append(operation.Operation.BacklogPosition).Append(':')
                .Append(operation.Operation.Status).Append(':')
                .Append(operation.UpdatedAt).Append('|');
        }

        if (package is not null)
        {
            revisionSeed.Append(package.PackageId).Append(':')
                .Append(package.Revision).Append(':')
                .Append(package.PublishedAt.ToString("O", CultureInfo.InvariantCulture)).Append('|');
            foreach (var file in package.Files)
            {
                revisionSeed.Append(file.FileId).Append(':').Append(file.Sha256).Append('|');
            }
        }

        return new EInkDeviceSource(
            device.DeviceId,
            device.DeviceName,
            device.CredentialHash,
            device.IsEnabled,
            device.MachineId,
            machine?.Machine,
            operations.Select(value => value.Operation).ToArray(),
            package,
            revisionSeed.ToString());
    }

    private static async Task<DeviceRow?> ReadDeviceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, device_name, credential_hash, is_enabled, machine_id, updated_at
            FROM device_registry
            WHERE id = $deviceId AND device_type = 'eink';
            """;
        command.Parameters.AddWithValue("$deviceId", deviceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DeviceRow(
                reader.GetString(0),
                reader.GetString(1),
                NullableString(reader, 2),
                reader.GetBoolean(3),
                NullableString(reader, 4),
                reader.GetString(5))
            : null;
    }

    private static async Task<MachineRow?> ReadMachineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string machineId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, number, name, machine_type, is_active, updated_at
            FROM machines
            WHERE id = $machineId;
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new MachineRow(
                new EInkMachineSource(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetBoolean(4)),
                reader.GetString(5))
            : null;
    }

    private static async Task<IReadOnlyList<OperationRow>> ReadBacklogAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string machineId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT batch_operations.id, production_batches.id,
                   production_batches.batch_number, cases.part_number,
                   production_batches.planned_quantity,
                   batch_operations.operation_number, batch_operations.name,
                   batch_operations.status, machine_assignments.backlog_position,
                   batch_operations.updated_at, machine_assignments.updated_at,
                   production_batches.updated_at
            FROM machine_assignments
            JOIN batch_operations
              ON batch_operations.id = machine_assignments.batch_operation_id
            JOIN production_batches
              ON production_batches.id = batch_operations.production_batch_id
            JOIN cases ON cases.id = production_batches.case_id
            WHERE machine_assignments.machine_id = $machineId
            ORDER BY machine_assignments.backlog_position;
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        var values = new List<OperationRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new OperationRow(
                new EInkOperationSource(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5),
                    reader.GetString(6), reader.GetString(7), reader.GetInt32(8)),
                $"{reader.GetString(9)}:{reader.GetString(10)}:{reader.GetString(11)}"));
        }

        return values;
    }

    private static async Task<EInkPackageSource?> ReadPackageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        string batchId,
        string machineId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, revision, tool_cart_id, published_at,
                   machine_id, machine_number, machine_name,
                   case_id, part_number, part_name, part_revision, customer,
                   production_batch_id, batch_number, planned_quantity,
                   operation_number, operation_name
            FROM eink_package_revisions
            WHERE batch_operation_id = $operationId
              AND (machine_id IS NULL OR machine_id = $machineId)
            ORDER BY published_at DESC, id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        command.Parameters.AddWithValue("$machineId", machineId);
        string packageId;
        string revision;
        string? toolCartId;
        DateTimeOffset publishedAt;
        EInkPackageMetadataSource? metadata;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            packageId = reader.GetString(0);
            revision = reader.GetString(1);
            toolCartId = NullableString(reader, 2);
            publishedAt = ParseInstant(reader.GetString(3));
            metadata = reader.IsDBNull(4)
                ? null
                : new EInkPackageMetadataSource(
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    NullableString(reader, 10),
                    NullableString(reader, 11),
                    reader.GetString(12),
                    reader.GetString(13),
                    reader.GetInt32(14),
                    operationId,
                    reader.GetInt32(15),
                    reader.GetString(16));
        }

        var files = await ReadPackageFilesAsync(
            connection,
            transaction,
            packageId,
            cancellationToken);
        return new EInkPackageSource(
            packageId,
            revision,
            batchId,
            operationId,
            toolCartId,
            publishedAt,
            metadata,
            files);
    }

    private static async Task<IReadOnlyList<EInkPackageFileSource>> ReadPackageFilesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string packageId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, logical_path, storage_relative_path, media_type,
                   byte_length, sha256, modified_at, display_order, asset_type
            FROM eink_package_files
            WHERE package_revision_id = $packageId
            ORDER BY display_order, id;
            """;
        command.Parameters.AddWithValue("$packageId", packageId);
        var values = new List<EInkPackageFileSource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new EInkPackageFileSource(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4), reader.GetString(5),
                ParseInstant(reader.GetString(6)), reader.GetInt32(7), reader.GetString(8)));
        }

        return values;
    }

    private static DateTimeOffset ParseInstant(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private sealed record DeviceRow(
        string DeviceId,
        string DeviceName,
        string? CredentialHash,
        bool IsEnabled,
        string? MachineId,
        string UpdatedAt);

    private sealed record MachineRow(EInkMachineSource Machine, string UpdatedAt);

    private sealed record OperationRow(EInkOperationSource Operation, string UpdatedAt);
}
