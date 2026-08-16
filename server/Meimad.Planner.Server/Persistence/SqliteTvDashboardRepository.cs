using System.Globalization;
using Meimad.Planner.Server.Application.TvDashboard;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteTvDashboardRepository : ITvDashboardRepository
{
    private readonly SqliteDatabase database;

    public SqliteTvDashboardRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<TvDashboardSource> ReadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var machines = await ReadMachinesAsync(connection, transaction, cancellationToken);
        var operations = await ReadOperationsAsync(connection, transaction, cancellationToken);
        var downtimes = await ReadDowntimesAsync(connection, transaction, cancellationToken);
        var dueDates = await ReadBatchDueDatesAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var backlogs = operations
            .GroupBy(value => value.MachineId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<TvSourceOperation>)group
                    .OrderBy(value => value.Operation.BacklogPosition)
                    .Select(value => value.Operation)
                    .ToArray(),
                StringComparer.Ordinal);
        return new TvDashboardSource(
            machines.Select(machine => machine with
            {
                Backlog = backlogs.GetValueOrDefault(machine.MachineId, [])
            }).ToArray(),
            downtimes,
            dueDates);
    }

    private static async Task<IReadOnlyList<TvSourceMachine>> ReadMachinesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, number, name, machine_type
            FROM machines
            WHERE is_active = 1 AND display_enabled = 1
            ORDER BY number COLLATE NOCASE, id;
            """;
        var values = new List<TvSourceMachine>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new TvSourceMachine(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), []));
        }

        return values;
    }

    private static async Task<IReadOnlyList<AssignedOperation>> ReadOperationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT machine_assignments.machine_id,
                   batch_operations.id,
                   production_batches.id,
                   cases.id,
                   production_batches.batch_number,
                   cases.part_number,
                   batch_operations.operation_number,
                   batch_operations.name,
                   batch_operations.status,
                   machine_assignments.backlog_position
            FROM machine_assignments
            JOIN machines ON machines.id = machine_assignments.machine_id
            JOIN batch_operations
              ON batch_operations.id = machine_assignments.batch_operation_id
            JOIN production_batches
              ON production_batches.id = batch_operations.production_batch_id
            JOIN cases ON cases.id = production_batches.case_id
            WHERE machines.is_active = 1 AND machines.display_enabled = 1
            ORDER BY machine_assignments.machine_id, machine_assignments.backlog_position;
            """;
        var values = new List<AssignedOperation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new AssignedOperation(
                reader.GetString(0),
                new TvSourceOperation(
                    reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetString(7),
                    reader.GetString(8), reader.GetInt32(9))));
        }

        return values;
    }

    private static async Task<IReadOnlyList<TvSourceDowntime>> ReadDowntimesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT downtimes.id, downtimes.machine_id, downtimes.starts_at,
                   COALESCE(downtimes.ends_at, '9999-12-31T23:59:59.9999999+00:00'),
                   CASE downtimes.downtime_type
                     WHEN 'breakdown' THEN 'Breakdown: ' || downtimes.reason
                     ELSE 'Planned maintenance: ' || downtimes.reason
                   END
            FROM downtimes
            JOIN machines ON machines.id = downtimes.machine_id
            WHERE machines.is_active = 1
              AND machines.display_enabled = 1
              AND downtimes.status IN ('planned', 'active', 'restored')
            ORDER BY downtimes.starts_at, downtimes.id;
            """;
        var values = new List<TvSourceDowntime>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new TvSourceDowntime(
                reader.GetString(0), reader.GetString(1), ParseInstant(reader.GetString(2)),
                ParseInstant(reader.GetString(3)), reader.GetString(4)));
        }

        return values;
    }

    private static async Task<IReadOnlyList<TvSourceBatchDueDate>> ReadBatchDueDatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT production_batches.id, production_batches.batch_number,
                   cases.part_number, orders.work_finish_date
            FROM production_batches
            JOIN cases ON cases.id = production_batches.case_id
            JOIN batch_allocations
              ON batch_allocations.production_batch_id = production_batches.id
             AND batch_allocations.allocation_type = 'order'
            JOIN orders ON orders.id = batch_allocations.order_id
            WHERE orders.status IN ('active', 'in_production')
            ORDER BY orders.work_finish_date, production_batches.id;
            """;
        var values = new List<TvSourceBatchDueDate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new TvSourceBatchDueDate(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        return values;
    }

    private static DateTimeOffset ParseInstant(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();

    private sealed record AssignedOperation(string MachineId, TvSourceOperation Operation);
}
