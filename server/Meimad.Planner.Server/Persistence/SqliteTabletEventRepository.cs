using System.Globalization;
using Meimad.Planner.Server.Application.EInk;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteTabletEventRepository(SqliteDatabase database)
    : ITabletEventRepository
{
    public async Task<TabletEventResult> SubmitSendToQcAsync(
        SubmitTabletEventCommand command,
        string credentialHash,
        DateTimeOffset serverReceivedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var device = await ReadDeviceAsync(
            connection, transaction, command.TabletId, cancellationToken);
        if (device is null
            || !device.IsEnabled
            || string.IsNullOrWhiteSpace(device.CredentialHash)
            || !TabletEventService.FixedEquals(credentialHash, device.CredentialHash))
        {
            throw new TabletEventResourceNotFoundException();
        }

        if (device.MachineId is null)
        {
            throw new TabletEventStateException(
                "tablet_unassigned",
                "The tablet is not assigned to a Machine.");
        }

        await RecordContactAsync(
            connection, transaction, device.DeviceId, command, serverReceivedAt,
            cancellationToken);
        var run = await ReadCurrentRunAsync(
            connection, transaction, device.MachineId, cancellationToken)
            ?? throw new TabletEventStateException(
                "tablet_no_current_run",
                "No current Production Run is assigned to the tablet's Machine.");

        var existing = await ReadExistingAsync(
            connection, transaction, run.RunId, command.TabletId, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        var latestEvent = await ReadLatestEventTypeAsync(
            connection, transaction, run.RunId, cancellationToken);
        if (!run.MachineIsActive
            || string.Equals(run.RunStatus, "SUSPENDED", StringComparison.Ordinal)
            || latestEvent is not ("SETUP_VERIFICATION_SUCCEEDED" or "QC_FAIL"))
        {
            throw new TabletEventStateException(
                "tablet_event_not_allowed",
                "SEND_TO_QC is allowed only while the current Production Run is IN_SETUP_RUN.");
        }

        var timestamp = serverReceivedAt.ToUniversalTime();
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO production_run_workflow_events (
                    id, production_run_id, machine_id, event_type, source,
                    source_event_id, server_received_at, tablet_device_id,
                    metadata_json)
                VALUES ($id,$runId,$machineId,'SEND_TO_QC','TABLET',
                        $sourceEventId,$receivedAt,$deviceId,'{}');
                """;
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("$runId", run.RunId);
            insert.Parameters.AddWithValue("$machineId", device.MachineId);
            insert.Parameters.AddWithValue(
                "$sourceEventId", $"SEND_TO_QC:{device.DeviceId}:{run.RunId}");
            insert.Parameters.AddWithValue("$receivedAt", Format(timestamp));
            insert.Parameters.AddWithValue("$deviceId", device.DeviceId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new(command.TabletId, "SEND_TO_QC", timestamp, false);
    }

    private static async Task<DeviceRow?> ReadDeviceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tabletId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT id,credential_hash,is_enabled,machine_id
            FROM device_registry
            WHERE device_type='eink' AND tablet_id=$tabletId;
            """;
        query.Parameters.AddWithValue("$tabletId", tabletId.Trim());
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : reader.GetString(3))
            : null;
    }

    private static async Task<RunRow?> ReadCurrentRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string machineId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT run.id,run.status,machine.is_active
            FROM machine_assignments assignment
            JOIN production_runs run ON run.id=assignment.production_run_id
            JOIN machines machine ON machine.id=assignment.machine_id
            WHERE assignment.machine_id=$machineId
              AND run.status NOT IN ('COMPLETED','CANCELLED','ABORTED')
            ORDER BY assignment.backlog_position,run.id
            LIMIT 1;
            """;
        query.Parameters.AddWithValue("$machineId", machineId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2))
            : null;
    }

    private static async Task<TabletEventResult?> ReadExistingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string tabletId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT server_received_at
            FROM production_run_workflow_events
            WHERE production_run_id=$runId AND event_type='SEND_TO_QC'
            LIMIT 1;
            """;
        query.Parameters.AddWithValue("$runId", runId);
        var value = await query.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? null
            : new(tabletId, "SEND_TO_QC", Parse((string)value), true);
    }

    private static async Task<string?> ReadLatestEventTypeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT event_type
            FROM production_run_workflow_events
            WHERE production_run_id=$runId
            ORDER BY server_received_at DESC,id DESC
            LIMIT 1;
            """;
        query.Parameters.AddWithValue("$runId", runId);
        return await query.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task RecordContactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        SubmitTabletEventCommand command,
        DateTimeOffset contactedAt,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE device_registry
            SET last_seen_at=$at,last_server_contact_at=$at,
                battery_voltage=COALESCE($voltage,battery_voltage),
                battery_percent=COALESCE($percent,battery_percent),
                firmware_version=COALESCE($firmware,firmware_version),
                wifi_ip_address=COALESCE($wifiIp,wifi_ip_address),
                wifi_rssi=COALESCE($wifiRssi,wifi_rssi)
            WHERE id=$deviceId AND device_type='eink' AND is_enabled=1;
            """;
        update.Parameters.AddWithValue("$at", Format(contactedAt));
        update.Parameters.AddWithValue("$voltage", Db(command.BatteryVoltage));
        update.Parameters.AddWithValue("$percent", Db(command.BatteryPercent));
        update.Parameters.AddWithValue("$firmware", Db(command.FirmwareVersion));
        update.Parameters.AddWithValue("$wifiIp", Db(command.WifiIpAddress));
        update.Parameters.AddWithValue("$wifiRssi", Db(command.WifiRssi));
        update.Parameters.AddWithValue("$deviceId", deviceId);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object Db(object? value) => value ?? DBNull.Value;
    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(
        value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private sealed record DeviceRow(
        string DeviceId, string? CredentialHash, bool IsEnabled, string? MachineId);

    private sealed record RunRow(string RunId, string RunStatus, bool MachineIsActive);
}
