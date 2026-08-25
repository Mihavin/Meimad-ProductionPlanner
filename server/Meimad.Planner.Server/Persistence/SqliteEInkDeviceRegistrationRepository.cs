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
            SELECT id, tablet_id, hardware_id, device_name, machine_id, is_enabled, version, created_at, updated_at
            FROM device_registry
            WHERE device_type = 'eink'
            ORDER BY device_name COLLATE NOCASE, id;
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
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE device_registry
            SET last_seen_at = $at, last_server_contact_at = $at,
                battery_voltage = COALESCE($voltage, battery_voltage),
                battery_percent = COALESCE($percent, battery_percent)
            WHERE id = $deviceId AND device_type = 'eink' AND is_enabled = 1;
            """;
        Bind(command, "$at", Iso(contactedAt));
        Bind(command, "$voltage", batteryVoltage);
        Bind(command, "$percent", batteryPercent);
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
        Parse(reader.GetString(8)));

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
