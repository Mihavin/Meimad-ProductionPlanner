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
            SELECT id, number, name, machine_type, axis_type,
                   capabilities_json, is_active
            FROM machines
            ORDER BY number COLLATE NOCASE, id;
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
                JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? [],
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
                   machine_assignments.backlog_position
            FROM batch_operations
            JOIN production_batches
              ON production_batches.id = batch_operations.production_batch_id
            JOIN cases
              ON cases.id = production_batches.case_id
            LEFT JOIN machine_assignments
              ON machine_assignments.batch_operation_id = batch_operations.id
            WHERE batch_operations.status <> 'completed';
            """;
        var operations = new List<PlanningBoardOperation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            operations.Add(new PlanningBoardOperation(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetString(6),
                GetNullableString(reader, 7),
                GetNullableInt32(reader, 8),
                GetNullableInt32(reader, 9),
                reader.GetString(10),
                GetNullableString(reader, 11),
                GetNullableInt32(reader, 12)));
        }

        return operations;
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? GetNullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
