using System.Text.Json;
using Meimad.Planner.Server.Application.PlanningBoard;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqlitePlanningBoardRepository : IPlanningBoardRepository
{
    private readonly SqliteDatabase database;

    public SqlitePlanningBoardRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<PlanningBoardSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var machines = await ReadMachinesAsync(connection, transaction, cancellationToken);
        var operations = await ReadOperationsAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var byMachine = operations
            .Where(operation => operation.MachineId is not null)
            .GroupBy(operation => operation.MachineId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PlanningBoardOperation>)group
                    .OrderBy(operation => operation.BacklogPosition)
                    .ToArray(),
                StringComparer.Ordinal);
        var projectedMachines = machines.Select(machine => machine with
        {
            Backlog = byMachine.GetValueOrDefault(machine.MachineId, [])
        }).ToArray();
        var pool = operations
            .Where(operation => operation.MachineId is null)
            .OrderBy(operation => operation.PartNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(operation => operation.BatchNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(operation => operation.OperationNumber)
            .ThenBy(operation => operation.BatchOperationId, StringComparer.Ordinal)
            .ToArray();

        return new PlanningBoardSnapshot(DateTimeOffset.UtcNow, pool, projectedMachines);
    }

    private static async Task<IReadOnlyList<PlanningBoardMachine>> ReadMachinesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT machines.id, machines.number, machines.name,
                   machines.machine_type, machines.axis_type,
                   machines.capabilities_json, machines.is_active,
                   machine_types.capabilities_json
            FROM machines
            LEFT JOIN machine_types
              ON machine_types.id = machines.machine_type_id
            ORDER BY machines.number COLLATE NOCASE, machines.id;
            """;
        var machines = new List<PlanningBoardMachine>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            machines.Add(new PlanningBoardMachine(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                GetNullableString(reader, 4),
                EffectiveCapabilities(
                    reader.GetString(5),
                    GetNullableString(reader, 7)),
                reader.GetBoolean(6),
                []));
        }

        return machines;
    }

    private static async Task<IReadOnlyList<PlanningBoardOperation>> ReadOperationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT batch_operations.id,
                   production_batches.id,
                   production_batches.batch_number,
                   cases.id,
                   cases.part_number,
                   batch_operations.operation_number,
                   batch_operations.name,
                   batch_operations.required_machine_type,
                   batch_operations.setup_seconds,
                   batch_operations.cycle_seconds,
                   batch_operations.status,
                   machine_assignments.machine_id,
                   machine_assignments.backlog_position,
                   production_batches.planned_quantity,
                   COALESCE((
                       SELECT json_group_array(order_reference)
                       FROM (
                           SELECT DISTINCT orders.order_reference AS order_reference
                           FROM batch_allocations
                           JOIN orders ON orders.id = batch_allocations.order_id
                           WHERE batch_allocations.production_batch_id = production_batches.id
                             AND batch_allocations.allocation_type = 'order'
                           ORDER BY orders.order_reference COLLATE NOCASE
                       ) AS order_reference_list
                   ), '[]') AS order_references_json
                   , batch_operations.qa_seconds
                   , batch_operations.load_unload_seconds
                   , batch_operations.load_unload_requires_worker
                   , batch_operations.automatic_loading
                   , batch_operations.load_unload_every_n_parts
                   , batch_operations.day_shift_only,
                   operation_pause_events.reason_type,
                   operation_pause_events.paused_by,
                   operation_pause_events.pause_started_at,
                   COALESCE(operation_pause_events.problem_description,
                            operation_pause_events.tooling_item_description,
                            operation_pause_events.request_description,
                            operation_pause_events.comment),
                   cases.name
            FROM batch_operations
            JOIN production_batches
              ON production_batches.id = batch_operations.production_batch_id
            JOIN cases
              ON cases.id = production_batches.case_id
            LEFT JOIN machine_assignments
              ON machine_assignments.batch_operation_id = batch_operations.id
            LEFT JOIN operation_pause_events
              ON operation_pause_events.batch_operation_id = batch_operations.id
             AND operation_pause_events.status = 'active'
            WHERE batch_operations.status <> 'completed';
            """;
        var operations = new List<PlanningBoardOperation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var setupSeconds = GetNullableInt32(reader, 8);
            var cycleSeconds = GetNullableInt32(reader, 9);
            var plannedQuantity = reader.GetInt32(13);
            var qaSeconds = reader.GetInt32(15);
            var loadUnloadSeconds = reader.GetInt32(16);
            var automaticLoading = reader.GetInt32(18) == 1;
            var everyNParts = GetNullableInt32(reader, 19);
            operations.Add(new PlanningBoardOperation(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetString(6),
                GetNullableString(reader, 7),
                setupSeconds,
                cycleSeconds,
                reader.GetString(10),
                GetNullableString(reader, 11),
                GetNullableInt32(reader, 12),
                plannedQuantity,
                JsonSerializer.Deserialize<string[]>(reader.GetString(14)) ?? [],
                EstimateSeconds(setupSeconds, cycleSeconds, plannedQuantity,
                    qaSeconds, loadUnloadSeconds, automaticLoading, everyNParts),
                qaSeconds, loadUnloadSeconds, reader.GetInt32(17) == 1,
                automaticLoading, everyNParts, reader.GetInt32(20) == 1,
                reader.IsDBNull(21) ? null : $"{reader.GetString(21).Replace('_', ' ')}: {reader.GetString(24)}",
                GetNullableString(reader, 22),
                GetNullableString(reader, 23) is { } pausedAt ? DateTimeOffset.Parse(pausedAt) : null,
                reader.GetString(25)));
        }

        return operations;
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? GetNullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static IReadOnlyList<string> EffectiveCapabilities(
        string machineCapabilitiesJson,
        string? machineTypeCapabilitiesJson)
    {
        var capabilities = JsonSerializer.Deserialize<string[]>(machineCapabilitiesJson) ?? [];
        var machineTypeCapabilities = machineTypeCapabilitiesJson is null
            ? []
            : JsonSerializer.Deserialize<string[]>(machineTypeCapabilitiesJson) ?? [];

        return capabilities
            .Concat(machineTypeCapabilities)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static long? EstimateSeconds(
        int? setupSeconds, int? cycleSeconds, int quantity,
        int qaSeconds, int loadUnloadSeconds, bool automaticLoading, int? everyNParts)
    {
        if (!setupSeconds.HasValue || !cycleSeconds.HasValue)
        {
            return null;
        }

        try
        {
            var loadOccurrences = automaticLoading
                ? everyNParts.HasValue ? (quantity + (long)everyNParts.Value - 1) / everyNParts.Value : 0
                : quantity;
            return checked((long)setupSeconds.Value + qaSeconds
                + (long)loadUnloadSeconds * loadOccurrences
                + (long)cycleSeconds.Value * quantity);
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
