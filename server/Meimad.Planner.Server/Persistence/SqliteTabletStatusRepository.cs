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
                connection, transaction, run.RunId, cancellationToken);
        var verificationSession = run is null || device.Machine is null
            ? null
            : await ReadVerificationSessionAsync(
                connection, transaction, device.Machine.MachineId, run.RunId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TabletStatusSource(
            device.DeviceId,
            device.TabletId,
            device.IsEnabled,
            device.Machine,
            run,
            outputs,
            workflow,
            verificationSession);
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
            SELECT device.id, device.tablet_id, device.is_enabled,
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
            reader.GetBoolean(2),
            reader.IsDBNull(3)
                ? null
                : new TabletStatusMachineSource(
                    reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.GetBoolean(6)));
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
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, event_type, server_received_at
            FROM production_run_workflow_events
            WHERE production_run_id = $runId
            ORDER BY server_received_at DESC, id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TabletStatusWorkflowSource(
                reader.GetString(0), reader.GetString(1), Parse(reader.GetString(2)))
            : null;
    }

    private static async Task<TabletVerificationSessionSource?> ReadVerificationSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string machineId,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT session.id,session.state,session.nonce,
                   release.verification_release_token,hook.nc_identity_token,
                   session.macro_version,session.response_code_digits,session.expires_at,
                   settings.protected_secret,
                   CASE WHEN settings.enabled=1
                              AND settings.expected_macro_version=session.macro_version
                              AND settings.response_code_digits=session.response_code_digits
                              AND current.offset_loader_release_id=session.offset_loader_release_id
                              AND release.nc_release_id=session.nc_release_id
                              AND EXISTS (
                                  SELECT 1 FROM production_run_programs program
                                  WHERE program.production_run_id=session.production_run_id
                                    AND (program.selected_gcode_release_id=session.nc_release_id
                                         OR program.production_gcode_release_id=session.nc_release_id))
                              AND ((hook.invocation_kind='G65'
                                    AND hook.invocation_number=settings.verify_program_number)
                                   OR (hook.invocation_kind='CUSTOM_GCODE'
                                       AND hook.invocation_number=settings.custom_gcode_alias))
                        THEN 1 ELSE 0 END
            FROM cnc_setup_verification_sessions session
            JOIN offset_loader_releases release ON release.id=session.offset_loader_release_id
            JOIN gcode_release_verification_hooks hook ON hook.gcode_release_id=session.nc_release_id
            JOIN cnc_verification_settings settings ON settings.machine_id=session.machine_id
            LEFT JOIN production_run_current_offset_loaders current
              ON current.production_run_id=session.production_run_id
             AND current.machine_id=session.machine_id
            WHERE session.machine_id=$machineId
              AND session.production_run_id=$runId
            ORDER BY session.created_at DESC,session.id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        command.Parameters.AddWithValue("$runId", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TabletVerificationSessionSource(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5),
                reader.GetInt32(6), Parse(reader.GetString(7)), reader.GetString(8),
                reader.GetInt32(9) == 1)
            : null;
    }

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(
        value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private sealed record DeviceRow(
        string DeviceId,
        string TabletId,
        bool IsEnabled,
        TabletStatusMachineSource? Machine);
}
