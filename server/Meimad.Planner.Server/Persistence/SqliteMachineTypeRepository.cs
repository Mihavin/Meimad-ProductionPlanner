using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.MachineTypes;
using Meimad.Planner.Server.Domain.Machines;
using Meimad.Planner.Server.Domain.MachineTypes;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteMachineTypeRepository : IMachineTypeRepository
{
    private const string Projection = "id, name, capabilities_json, version, created_at, updated_at";
    private readonly SqliteDatabase database;

    public SqliteMachineTypeRepository(SqliteDatabase database) => this.database = database;

    public async Task<MachineType> CreateAsync(
        MachineType machineType,
        EditAuthority authority,
        CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        await EnsureNameAvailableAsync(connection, transaction, machineType.Name, null, token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO machine_types (
                id, name, capabilities_json, version, created_at, updated_at)
            VALUES ($id, $name, $capabilities, $version, $createdAt, $updatedAt);
            """;
        AddParameters(command, machineType);
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return machineType;
    }

    public async Task<MachineType?> GetByIdAsync(string machineTypeId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Projection} FROM machine_types WHERE id = $id;";
        command.Parameters.AddWithValue("$id", machineTypeId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<MachineType>> ListAsync(CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Projection} FROM machine_types ORDER BY name COLLATE NOCASE, id;";
        var values = new List<MachineType>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) values.Add(Read(reader));
        return values;
    }

    public async Task<MachineType?> UpdateAsync(
        MachineType machineType,
        int expectedVersion,
        EditAuthority authority,
        CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        await EnsureNameAvailableAsync(connection, transaction, machineType.Name, machineType.MachineTypeId, token);
        await EnsureRenameDoesNotStrandRequirementsAsync(connection, transaction, machineType, token);
        await EnsureLinkedAssignmentsRemainCompatibleAsync(connection, transaction, machineType, token);

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE machine_types
            SET name = $name,
                capabilities_json = $capabilities,
                version = $version,
                updated_at = $updatedAt
            WHERE id = $id AND version = $expectedVersion;
            """;
        AddParameters(update, machineType);
        update.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        if (await update.ExecuteNonQueryAsync(token) != 1)
        {
            await transaction.CommitAsync(token);
            return null;
        }

        await using var propagate = connection.CreateCommand();
        propagate.Transaction = transaction;
        propagate.CommandText = """
            UPDATE machines
            SET machine_type = $name,
                version = version + 1,
                updated_at = $updatedAt
            WHERE machine_type_id = $id;
            """;
        propagate.Parameters.AddWithValue("$name", machineType.Name);
        propagate.Parameters.AddWithValue("$updatedAt", Format(machineType.UpdatedAt));
        propagate.Parameters.AddWithValue("$id", machineType.MachineTypeId);
        await propagate.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return machineType;
    }

    private static async Task EnsureRenameDoesNotStrandRequirementsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MachineType candidate,
        CancellationToken token)
    {
        string? currentName;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT name FROM machine_types WHERE id = $id;";
            current.Parameters.AddWithValue("$id", candidate.MachineTypeId);
            currentName = await current.ExecuteScalarAsync(token) as string;
        }

        if (currentName is null
            || string.Equals(currentName, candidate.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await using var referenced = connection.CreateCommand();
        referenced.Transaction = transaction;
        referenced.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM case_operations
                WHERE required_machine_type = $currentName COLLATE NOCASE
                UNION ALL
                SELECT 1
                FROM batch_operations
                WHERE required_machine_type = $currentName COLLATE NOCASE
                  AND status <> 'completed');
            """;
        referenced.Parameters.AddWithValue("$currentName", currentName);
        if (Convert.ToInt32(await referenced.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1)
        {
            throw new MachineTypeNameInUseException(
                $"Machine Type '{currentName}' cannot be renamed while a current Case Operation or unfinished Batch Operation requires that name.");
        }
    }

    public async Task<bool> DeleteAsync(string machineTypeId, EditAuthority authority, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        await using (var used = connection.CreateCommand())
        {
            used.Transaction = transaction;
            used.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM machines WHERE machine_type_id = $id
                    UNION ALL
                    SELECT 1
                    FROM case_operations
                    WHERE required_machine_type = (
                        SELECT name FROM machine_types WHERE id = $id) COLLATE NOCASE
                    UNION ALL
                    SELECT 1
                    FROM batch_operations
                    WHERE required_machine_type = (
                        SELECT name FROM machine_types WHERE id = $id) COLLATE NOCASE);
                """;
            used.Parameters.AddWithValue("$id", machineTypeId);
            if (Convert.ToInt32(await used.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1)
            {
                throw new MachineTypeInUseException(machineTypeId);
            }
        }

        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM machine_types WHERE id = $id;";
        delete.Parameters.AddWithValue("$id", machineTypeId);
        var deleted = await delete.ExecuteNonQueryAsync(token) == 1;
        await transaction.CommitAsync(token);
        return deleted;
    }

    private static async Task EnsureLinkedAssignmentsRemainCompatibleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MachineType candidate,
        CancellationToken token)
    {
        var machines = new List<Machine>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id, number, name, axis_type, capabilities_json,
                       working_calendar_id, is_active, display_enabled,
                       version, created_at, updated_at
                FROM machines
                WHERE machine_type_id = $id;
                """;
            command.Parameters.AddWithValue("$id", candidate.MachineTypeId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                machines.Add(new Machine(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), candidate.Name,
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    JsonSerializer.Deserialize<string[]>(reader.GetString(4)) ?? [],
                    reader.GetString(5), reader.GetBoolean(6), reader.GetBoolean(7), null, 0,
                    reader.GetInt32(8), Parse(reader.GetString(9)), Parse(reader.GetString(10)),
                    null, candidate.MachineTypeId, candidate.Capabilities));
            }
        }

        foreach (var machine in machines)
        {
            await using var assigned = connection.CreateCommand();
            assigned.Transaction = transaction;
            assigned.CommandText = """
                SELECT batch_operations.id, batch_operations.required_machine_type
                FROM machine_assignments
                JOIN batch_operations ON batch_operations.id = machine_assignments.batch_operation_id
                WHERE machine_assignments.machine_id = $machineId;
                """;
            assigned.Parameters.AddWithValue("$machineId", machine.MachineId);
            await using var reader = await assigned.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var required = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (!MachineCompatibility.IsCompatible(machine, required))
                {
                    throw new MachineTypeCompatibilityException(
                        $"Machine Type update would make assigned Batch Operation '{reader.GetString(0)}' incompatible with Machine '{machine.Number}'.");
                }
            }
        }
    }

    private static async Task EnsureNameAvailableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string name,
        string? exceptId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM machine_types
                WHERE name = $name COLLATE NOCASE
                  AND ($exceptId IS NULL OR id <> $exceptId));
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$exceptId", exceptId is null ? DBNull.Value : exceptId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1)
        {
            throw new MachineTypeNameConflictException(name);
        }
    }

    private static async Task EnsureEditAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EditAuthority authority,
        CancellationToken token)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(connection, transaction, DateTimeOffset.UtcNow, token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0))
            throw new EditModeMutationException("edit_mode_required", "No Windows client currently holds Edit Mode.");
        if (!string.Equals(reader.GetString(0), authority.ClientId, StringComparison.Ordinal)
            || reader.GetInt64(1) != authority.Generation)
            throw new EditModeMutationException("edit_generation_stale", "This client does not hold the active Edit Mode generation.");
    }

    private static void AddParameters(SqliteCommand command, MachineType value)
    {
        command.Parameters.AddWithValue("$id", value.MachineTypeId);
        command.Parameters.AddWithValue("$name", value.Name);
        command.Parameters.AddWithValue("$capabilities", JsonSerializer.Serialize(value.Capabilities));
        command.Parameters.AddWithValue("$version", value.Version);
        command.Parameters.AddWithValue("$createdAt", Format(value.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", Format(value.UpdatedAt));
    }

    private static MachineType Read(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1),
        JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? [], reader.GetInt32(3),
        Parse(reader.GetString(4)), Parse(reader.GetString(5)));

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
