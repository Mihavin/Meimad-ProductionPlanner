using System.Globalization;
using Meimad.Planner.Server.Application.EInk;
using Meimad.Planner.Server.Application.EditMode;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteEInkDeviceRegistrationRepository : IEInkDeviceRegistrationRepository
{
    private readonly SqliteDatabase database;

    public SqliteEInkDeviceRegistrationRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<EInkDeviceRegistration> CreateAsync(
        EInkDeviceRegistration registration,
        string credentialHash,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await ValidateAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        await ValidateMachineAsync(connection, transaction, registration.MachineId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO device_registry (
                id, tablet_id, hardware_id, device_type, device_name, machine_id, credential_hash,
                access_mode, is_enabled, version, created_at, updated_at)
            VALUES (
                $id, $tabletId, $hardwareId, 'eink', $name, $machineId, $credentialHash,
                'read_only', 1, 1, $createdAt, $updatedAt);
            """;
        Bind(command, "$id", registration.DeviceId);
        Bind(command, "$tabletId", registration.TabletId);
        Bind(command, "$hardwareId", registration.HardwareId);
        Bind(command, "$name", registration.DeviceName);
        Bind(command, "$machineId", registration.MachineId);
        Bind(command, "$credentialHash", credentialHash);
        Bind(command, "$createdAt", Iso(registration.CreatedAt));
        Bind(command, "$updatedAt", Iso(registration.UpdatedAt));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw BindingConflict(exception);
        }

        await transaction.CommitAsync(cancellationToken);
        return registration;
    }

    public async Task<EInkDeviceRegistration?> UpdateAsync(
        string deviceId,
        string? machineId,
        bool isEnabled,
        string? credentialHash,
        DateTimeOffset updatedAt,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await ValidateAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        await ValidateMachineAsync(connection, transaction, machineId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE device_registry
            SET machine_id = $machineId,
                is_enabled = $isEnabled,
                credential_hash = COALESCE($credentialHash, credential_hash),
                version = version + 1,
                updated_at = $updatedAt
            WHERE id = $deviceId AND device_type = 'eink';
            """;
        Bind(command, "$machineId", machineId);
        Bind(command, "$isEnabled", isEnabled);
        Bind(command, "$credentialHash", credentialHash);
        Bind(command, "$updatedAt", Iso(updatedAt));
        Bind(command, "$deviceId", deviceId);
        int changed;
        try
        {
            changed = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw BindingConflict(exception);
        }

        if (changed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var value = await ReadOneAsync(connection, transaction, deviceId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return value;
    }

    public async Task<IReadOnlyList<EInkDeviceRegistration>> ListAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH current_runs AS (
                SELECT assignment.machine_id, run.id, run.status,
                       ROW_NUMBER() OVER (
                           PARTITION BY assignment.machine_id
                           ORDER BY assignment.backlog_position, run.id) AS row_number
                FROM machine_assignments assignment
                JOIN production_runs run ON run.id = assignment.production_run_id
                WHERE run.status NOT IN ('COMPLETED','CANCELLED','ABORTED')
            ),
            latest_workflow AS (
                SELECT production_run_id, event_type,
                       ROW_NUMBER() OVER (
                           PARTITION BY production_run_id
                           ORDER BY server_received_at DESC, id DESC) AS row_number
                FROM production_run_workflow_events
            ),
            latest_packages AS (
                SELECT batch_operation_id, revision,
                       ROW_NUMBER() OVER (
                           PARTITION BY batch_operation_id
                           ORDER BY published_at DESC, id DESC) AS row_number
                FROM eink_package_revisions
            ),
            run_packages AS (
                SELECT program.production_run_id,
                       CASE WHEN COUNT(DISTINCT package.revision) = 1
                            THEN MAX(package.revision)
                            ELSE 'MULTIPLE' END AS revision
                FROM production_run_programs program
                JOIN production_run_outputs output
                  ON output.production_run_program_id = program.id
                JOIN latest_packages package
                  ON package.batch_operation_id = output.batch_operation_id
                 AND package.row_number = 1
                GROUP BY program.production_run_id
            )
            SELECT device.id, device.tablet_id, device.hardware_id,
                   device.device_name, device.machine_id, device.is_enabled,
                   device.version, device.created_at, device.updated_at,
                   device.last_seen_at, device.last_server_contact_at,
                   device.firmware_version, device.battery_voltage,
                   device.battery_percent, device.wifi_ip_address,
                   device.wifi_rssi, machine.number, machine.name,
                   current.id,
                   CASE
                       WHEN current.id IS NULL THEN NULL
                       WHEN machine.is_active = 0 OR current.status = 'SUSPENDED'
                           THEN 'BLOCKED'
                       WHEN workflow.event_type IS NULL THEN 'READY_FOR_SETUP'
                       WHEN workflow.event_type IN (
                           'OFFSET_LOADER_COMPLETED', 'SETUP_VERIFICATION_REQUESTED',
                           'SETUP_VERIFICATION_FAILED') THEN 'IN_SETUP'
                       WHEN workflow.event_type IN (
                           'SETUP_VERIFICATION_SUCCEEDED', 'QC_FAIL') THEN 'IN_SETUP_RUN'
                       WHEN workflow.event_type = 'SEND_TO_QC' THEN 'IN_QC'
                       WHEN workflow.event_type = 'QC_PASS' THEN 'READY_FOR_PRODUCTION'
                       WHEN workflow.event_type IN (
                           'CYCLE_START', 'CYCLE_END', 'CYCLE_INTERRUPTED',
                           'PRODUCTION_SESSION_OPENED') THEN 'IN_PRODUCTION'
                       ELSE 'UNKNOWN'
                   END,
                   packages.revision
            FROM device_registry device
            LEFT JOIN machines machine ON machine.id = device.machine_id
            LEFT JOIN current_runs current
              ON current.machine_id = device.machine_id AND current.row_number = 1
            LEFT JOIN latest_workflow workflow
              ON workflow.production_run_id = current.id AND workflow.row_number = 1
            LEFT JOIN run_packages packages ON packages.production_run_id = current.id
            WHERE device.device_type = 'eink'
            ORDER BY device.device_name COLLATE NOCASE, device.id;
            """;
        var values = new List<EInkDeviceRegistration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(Map(reader));
        }

        return values;
    }

    public async Task<EInkDeviceRegistration?> FindEnabledByCredentialAndHardwareAsync(
        string credentialHash,
        string hardwareId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, tablet_id, hardware_id, device_name, machine_id, is_enabled, version, created_at, updated_at
            FROM device_registry
            WHERE device_type = 'eink' AND is_enabled = 1
              AND credential_hash = $credentialHash AND hardware_id = $hardwareId;
            """;
        Bind(command, "$credentialHash", credentialHash);
        Bind(command, "$hardwareId", hardwareId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task RecordContactAsync(
        string deviceId,
        DateTimeOffset contactedAt,
        decimal? batteryVoltage,
        int? batteryPercent,
        string? firmwareVersion,
        string? wifiIpAddress,
        int? wifiRssi,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE device_registry
            SET last_seen_at = $at, last_server_contact_at = $at,
                battery_voltage = COALESCE($voltage, battery_voltage),
                battery_percent = COALESCE($percent, battery_percent),
                firmware_version = COALESCE($firmware, firmware_version),
                wifi_ip_address = COALESCE($wifiIp, wifi_ip_address),
                wifi_rssi = COALESCE($wifiRssi, wifi_rssi)
            WHERE id = $deviceId AND device_type = 'eink' AND is_enabled = 1;
            """;
        Bind(command, "$at", Iso(contactedAt));
        Bind(command, "$voltage", batteryVoltage);
        Bind(command, "$percent", batteryPercent);
        Bind(command, "$firmware", firmwareVersion);
        Bind(command, "$wifiIp", wifiIpAddress);
        Bind(command, "$wifiRssi", wifiRssi);
        Bind(command, "$deviceId", deviceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<EInkDeviceRegistration?> ReadOneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, tablet_id, hardware_id, device_name, machine_id, is_enabled, version, created_at, updated_at
            FROM device_registry
            WHERE id = $deviceId AND device_type = 'eink';
            """;
        Bind(command, "$deviceId", deviceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    private static async Task ValidateMachineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? machineId,
        CancellationToken cancellationToken)
    {
        if (machineId is null)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM machines WHERE id = $machineId;";
        Bind(command, "$machineId", machineId);
        if ((long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L) == 0)
        {
            throw new EInkDeviceBindingException(
                "machine_not_found",
                $"Machine '{machineId}' was not found.");
        }
    }

    private static async Task ValidateAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EditAuthority authority,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || reader.IsDBNull(0)
            || !string.Equals(reader.GetString(0), authority.ClientId, StringComparison.Ordinal)
            || reader.GetInt64(1) != authority.Generation)
        {
            throw new EditModeMutationException(
                "edit_authority_required",
                "The active Server Edit Mode generation is required for device administration.");
        }
    }

    private static EInkDeviceRegistration Map(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetBoolean(5),
        reader.GetInt32(6),
        Parse(reader.GetString(7)),
        Parse(reader.GetString(8)),
        OptionalDate(reader, 9),
        OptionalDate(reader, 10),
        OptionalString(reader, 11),
        OptionalDecimal(reader, 12),
        OptionalInt(reader, 13),
        OptionalString(reader, 14),
        OptionalInt(reader, 15),
        OptionalString(reader, 16),
        OptionalString(reader, 17),
        OptionalString(reader, 18),
        OptionalString(reader, 19),
        OptionalString(reader, 20));

    private static string? OptionalString(SqliteDataReader reader, int ordinal) =>
        reader.FieldCount <= ordinal || reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? OptionalInt(SqliteDataReader reader, int ordinal) =>
        reader.FieldCount <= ordinal || reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static decimal? OptionalDecimal(SqliteDataReader reader, int ordinal) =>
        reader.FieldCount <= ordinal || reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

    private static DateTimeOffset? OptionalDate(SqliteDataReader reader, int ordinal) =>
        reader.FieldCount <= ordinal || reader.IsDBNull(ordinal)
            ? null
            : Parse(reader.GetString(ordinal));

    private static EInkDeviceBindingException BindingConflict(SqliteException exception) =>
        new(
            exception.Message.Contains("ux_device_registry_eink_hardware_id", StringComparison.Ordinal)
                ? "hardware_id_conflict"
                : exception.Message.Contains("ux_device_registry_eink_tablet_id", StringComparison.Ordinal)
                    ? "tablet_id_conflict"
                    : "device_binding_conflict",
            exception.Message.Contains("ux_device_registry_eink_hardware_id", StringComparison.Ordinal)
                ? "Another active E-Ink tablet already has that hardware ID."
                : exception.Message.Contains("ux_device_registry_eink_tablet_id", StringComparison.Ordinal)
                    ? "The generated tablet ID conflicts with an existing registration."
                    : exception.Message.Contains("ux_device_registry_eink_machine", StringComparison.Ordinal)
                ? "Another E-Ink device is already bound to that Machine."
                : "The device name or Machine binding conflicts with an existing registration.");

    private static void Bind(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();
}
