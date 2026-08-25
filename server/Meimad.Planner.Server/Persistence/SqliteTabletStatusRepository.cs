using System.Globalization;
using Meimad.Planner.Server.Application.EInk;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteTabletStatusRepository : ITabletStatusRepository
{
    private readonly SqliteDatabase database;

    public SqliteTabletStatusRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<TabletStatusSource?> ReadAsync(
        string tabletId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var device = await ReadDeviceAsync(connection, transaction, tabletId, cancellationToken);
        if (device is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var run = device.Machine is null
            ? null
            : await ReadRunAsync(
                connection, transaction, device.Machine.MachineId, cancellationToken);
        var outputs = run is null
            ? []
            : await ReadOutputsAsync(
                connection, transaction, run.ProgramId, cancellationToken);
        var workflow = run is null
            ? null
            : await ReadWorkflowAsync(
                connection, transaction, device.DeviceId, run.RunId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TabletStatusSource(
            device.DeviceId,
            device.TabletId,
            device.CredentialHash,
            device.IsEnabled,
            device.Machine,
            run,
            outputs,
            workflow);
    }

    private static async Task<DeviceRow?> ReadDeviceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tabletId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT device.id, device.tablet_id, device.credential_hash, device.is_enabled,
                   machine.id, machine.number, machine.name, machine.is_active
            FROM device_registry device
            LEFT JOIN machines machine ON machine.id = device.machine_id
            WHERE device.device_type = 'eink' AND device.tablet_id = $tabletId;
            """;
        command.Parameters.AddWithValue("$tabletId", tabletId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DeviceRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetBoolean(3),
            reader.IsDBNull(4)
                ? null
                : new TabletStatusMachineSource(
                    reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetBoolean(7)));
    }

    private static async Task<TabletStatusRunSource?> ReadRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string machineId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run.id, run.status, run.version,
                   program.id, program.status, program.completed_cycle_count
            FROM machine_assignments assignment
            JOIN production_runs run ON run.id = assignment.production_run_id
            JOIN production_run_programs program ON program.production_run_id = run.id
            WHERE assignment.machine_id = $machineId
              AND run.status NOT IN ('COMPLETED','CANCELLED','ABORTED')
            ORDER BY assignment.backlog_position,
                     CASE program.status
                         WHEN 'ACTIVE' THEN 0
                         WHEN 'SUSPENDED' THEN 1
                         WHEN 'PLANNED' THEN 2
                         ELSE 3
                     END,
                     program.sequence_position
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TabletStatusRunSource(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                reader.GetString(3), reader.GetString(4), reader.GetInt32(5))
            : null;
    }

    private static async Task<IReadOnlyList<TabletStatusOutputSource>> ReadOutputsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string programId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT cases.part_number, cases.name,
                   operation.operation_number, operation.name
            FROM production_run_outputs output
            JOIN batch_operations operation ON operation.id = output.batch_operation_id
            JOIN production_batches batch ON batch.id = operation.production_batch_id
            JOIN cases ON cases.id = batch.case_id
            WHERE output.production_run_program_id = $programId
            ORDER BY operation.operation_number, output.id;
            """;
        command.Parameters.AddWithValue("$programId", programId);
        var values = new List<TabletStatusOutputSource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new TabletStatusOutputSource(
                reader.GetString(0), reader.GetString(1),
                reader.GetInt32(2), reader.GetString(3)));
        }

        return values;
    }

    private static async Task<TabletStatusWorkflowSource?> ReadWorkflowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, resulting_state, occurred_at
            FROM tablet_workflow_events
            WHERE device_id = $deviceId AND production_run_id = $runId
            ORDER BY occurred_at DESC, id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$runId", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TabletStatusWorkflowSource(
                reader.GetString(0), reader.GetString(1), Parse(reader.GetString(2)))
            : null;
    }

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(
        value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private sealed record DeviceRow(
        string DeviceId,
        string TabletId,
        string? CredentialHash,
        bool IsEnabled,
        TabletStatusMachineSource? Machine);
}
