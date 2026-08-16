using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Machines;
using Meimad.Planner.Server.Domain.Machines;
using Meimad.Planner.Server.Domain.WorkingCalendars;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteMachineRepository : IMachineRepository
{
    private const string Projection = """
        machines.id,
        machines.number,
        machines.name,
        machines.machine_type,
        machines.axis_type,
        machines.capabilities_json,
        machines.working_calendar_id,
        machines.is_active,
        machines.display_enabled,
        machines.picture_reference,
        (
            SELECT device_registry.id
            FROM device_registry
            WHERE device_registry.machine_id = machines.id
              AND device_registry.device_type = 'eink'
              AND device_registry.is_enabled = 1
            ORDER BY device_registry.id
            LIMIT 1
        ) AS display_device_id,
        (
            SELECT COUNT(*)
            FROM machine_assignments
            WHERE machine_assignments.machine_id = machines.id
        ) AS backlog_count,
        machines.version,
        machines.created_at,
        machines.updated_at,
        machines.machine_type_id,
        COALESCE((
            SELECT machine_types.capabilities_json
            FROM machine_types
            WHERE machine_types.id = machines.machine_type_id
        ), '[]') AS machine_type_capabilities_json,
        machines.respect_master_calendar
        """;

    private readonly SqliteDatabase database;

    public SqliteMachineRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<Machine> CreateAsync(
        Machine machine,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        machine = await ApplyMachineTypeAsync(connection, transaction, machine, cancellationToken);
        await EnsureCalendarExistsAsync(
            connection,
            transaction,
            machine.WorkingCalendarId,
            cancellationToken);
        await EnsureNumberAvailableAsync(
            connection,
            transaction,
            machine.Number,
            null,
            cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO machines (
                id, number, name, machine_type, axis_type, capabilities_json,
                working_calendar_id, display_configuration_json, status, picture_reference,
                is_active, display_enabled, version, created_at, updated_at, machine_type_id,
                respect_master_calendar)
            VALUES (
                $id, $number, $name, $processType, $axisType, $capabilities,
                $calendarId, '{}', $status, $picturePath,
                $isActive, $displayEnabled, $version, $createdAt, $updatedAt, $machineTypeId,
                $respectMasterCalendar);
            """;
        AddWriteParameters(command, machine);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return machine;
    }

    public async Task<Machine?> GetByIdAsync(
        string machineId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Projection} FROM machines WHERE machines.id = $id;";
        command.Parameters.AddWithValue("$id", machineId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMachine(reader) : null;
    }

    public async Task<IReadOnlyList<Machine>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Projection} FROM machines ORDER BY number, id;";
        var machines = new List<Machine>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            machines.Add(ReadMachine(reader));
        }

        return machines;
    }

    public async Task<Machine?> UpdateAsync(
        Machine machine,
        int expectedVersion,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        machine = await ApplyMachineTypeAsync(connection, transaction, machine, cancellationToken);
        await EnsureCalendarExistsAsync(
            connection,
            transaction,
            machine.WorkingCalendarId,
            cancellationToken);
        await EnsureNumberAvailableAsync(
            connection,
            transaction,
            machine.Number,
            machine.MachineId,
            cancellationToken);
        await EnsureBacklogRemainsCompatibleAsync(
            connection,
            transaction,
            machine,
            cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE machines
            SET number = $number,
                name = $name,
                machine_type = $processType,
                axis_type = $axisType,
                capabilities_json = $capabilities,
                working_calendar_id = $calendarId,
                status = $status,
                is_active = $isActive,
                display_enabled = $displayEnabled,
                picture_reference = $picturePath,
                machine_type_id = $machineTypeId,
                respect_master_calendar = $respectMasterCalendar,
                version = $version,
                updated_at = $updatedAt
            WHERE id = $id AND version = $expectedVersion;
            """;
        AddWriteParameters(command, machine);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return affected == 1 ? machine : null;
    }

    private static async Task EnsureBacklogRemainsCompatibleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Machine machine,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT batch_operations.id, batch_operations.required_machine_type
            FROM machine_assignments
            JOIN batch_operations
              ON batch_operations.id = machine_assignments.batch_operation_id
            WHERE machine_assignments.machine_id = $machineId;
            """;
        command.Parameters.AddWithValue("$machineId", machine.MachineId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var requiredType = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (!MachineCompatibility.IsCompatible(machine, requiredType))
            {
                throw new MachineBacklogCompatibilityException(
                    $"Machine update would make assigned Batch Operation '{reader.GetString(0)}' incompatible.");
            }
        }
    }

    private static async Task<Machine> ApplyMachineTypeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Machine machine,
        CancellationToken cancellationToken)
    {
        if (machine.MachineTypeId is null)
        {
            return machine with { MachineTypeCapabilities = null };
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name, capabilities_json FROM machine_types WHERE id = $id;";
        command.Parameters.AddWithValue("$id", machine.MachineTypeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new MachineTypeReferenceNotFoundException(machine.MachineTypeId);
        }

        var typeCapabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? [];
        return machine with
        {
            ProcessType = reader.GetString(0),
            MachineTypeCapabilities = typeCapabilities
        };
    }

    private static async Task EnsureCalendarExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string calendarId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT calendar_json FROM working_calendars WHERE id = $id;";
        command.Parameters.AddWithValue("$id", calendarId);
        var calendarJson = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (calendarJson is null)
        {
            throw new WorkingCalendarNotFoundException(calendarId);
        }

        using var document = JsonDocument.Parse(calendarJson);
        if (document.RootElement.TryGetProperty("usages", out var usages)
            && usages.ValueKind == JsonValueKind.Array
            && !usages.EnumerateArray().Any(value =>
                string.Equals(value.GetString(), WorkingCalendarUsage.Machine, StringComparison.Ordinal)))
            throw new WorkingCalendarUsageException(calendarId);
    }

    private static async Task EnsureNumberAvailableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string number,
        string? exceptMachineId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM machines
                WHERE number = $number
                  AND ($exceptId IS NULL OR id <> $exceptId));
            """;
        command.Parameters.AddWithValue("$number", number);
        command.Parameters.AddWithValue(
            "$exceptId",
            exceptMachineId is null ? DBNull.Value : exceptMachineId);
        if (Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture) == 1)
        {
            throw new MachineNumberConflictException(number);
        }
    }

    private static async Task EnsureEditAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(
            connection,
            transaction,
            DateTimeOffset.UtcNow,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            throw new EditModeMutationException(
                "edit_mode_required",
                "No Windows client currently holds Edit Mode.");
        }

        if (!string.Equals(reader.GetString(0), editAuthority.ClientId, StringComparison.Ordinal)
            || reader.GetInt64(1) != editAuthority.Generation)
        {
            throw new EditModeMutationException(
                "edit_generation_stale",
                "This client does not hold the active Edit Mode generation.");
        }
    }

    private static void AddWriteParameters(SqliteCommand command, Machine machine)
    {
        command.Parameters.AddWithValue("$id", machine.MachineId);
        command.Parameters.AddWithValue("$number", machine.Number);
        command.Parameters.AddWithValue("$name", machine.Name);
        command.Parameters.AddWithValue("$processType", machine.ProcessType);
        command.Parameters.AddWithValue(
            "$axisType",
            machine.AxisType is null ? DBNull.Value : machine.AxisType);
        command.Parameters.AddWithValue(
            "$capabilities",
            JsonSerializer.Serialize(machine.Capabilities));
        command.Parameters.AddWithValue("$calendarId", machine.WorkingCalendarId);
        command.Parameters.AddWithValue("$status", machine.IsActive ? "active" : "inactive");
        command.Parameters.AddWithValue("$isActive", machine.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$displayEnabled", machine.DisplayEnabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$picturePath",
            machine.PicturePath is null ? DBNull.Value : machine.PicturePath);
        command.Parameters.AddWithValue("$version", machine.Version);
        command.Parameters.AddWithValue("$createdAt", FormatInstant(machine.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatInstant(machine.UpdatedAt));
        command.Parameters.AddWithValue(
            "$machineTypeId",
            machine.MachineTypeId is null ? DBNull.Value : machine.MachineTypeId);
        command.Parameters.AddWithValue("$respectMasterCalendar", machine.RespectMasterCalendar ? 1 : 0);
    }

    private static Machine ReadMachine(SqliteDataReader reader)
    {
        var capabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(5))
            ?? throw new InvalidDataException("Stored Machine capabilities must be a JSON array.");
        var typeCapabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(16)) ?? [];
        return new Machine(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            capabilities,
            reader.GetString(6),
            reader.GetBoolean(7),
            reader.GetBoolean(8),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            ParseInstant(reader.GetString(13)),
            ParseInstant(reader.GetString(14)),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            typeCapabilities,
            reader.GetInt32(17) == 1);
    }

    private static string FormatInstant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseInstant(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
