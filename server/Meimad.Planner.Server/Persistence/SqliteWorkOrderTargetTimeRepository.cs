using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.ProductionBatches;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Work Order target times (schema v122). The context is read from the Work Order itself: its planned quantity, the
/// quantity-weighted price of the Orders it is allocated to, and the resources of its route that are not cancelled — the
/// Machine each operation is assigned to (or ran on), else the Machine type it needs, and the Workstation Type of each
/// active station step of its Case Operation. A save recalculates from that context inside the same immediate
/// transaction, so the stored snapshot matches the prices and route at that moment.
/// </summary>
internal sealed class SqliteWorkOrderTargetTimeRepository(SqliteDatabase database) : IWorkOrderTargetTimeRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<WorkOrderTargetTimeContext?> GetContextAsync(string productionBatchId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var context = await ReadContextAsync(connection, transaction, productionBatchId, token);
        await transaction.CommitAsync(token);
        return context;
    }

    public async Task<WorkOrderTargetTime?> GetAsync(string productionBatchId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        return await ReadAsync(connection, null, productionBatchId, token);
    }

    public async Task<WorkOrderTargetTimeWriteResult> SaveAsync(
        string productionBatchId, Func<WorkOrderTargetTimeContext, TargetTimeCalculation> calculate, string? notes,
        int? expectedVersion, string actor, DateTimeOffset now, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var context = await ReadContextAsync(connection, transaction, productionBatchId, token);
        if (context is null) return new(WorkOrderTargetTimeOutcome.NotFound, null);
        var current = await ReadAsync(connection, transaction, productionBatchId, token);
        if (expectedVersion is null && current is not null) return new(WorkOrderTargetTimeOutcome.AlreadyExists, current);
        if (expectedVersion is not null && current is null) return new(WorkOrderTargetTimeOutcome.TargetTimeNotFound, null);
        if (expectedVersion is not null && current!.Version != expectedVersion.Value) return new(WorkOrderTargetTimeOutcome.Stale, current);

        var saved = new WorkOrderTargetTime(productionBatchId, calculate(context), notes, (current?.Version ?? 0) + 1,
            current?.CreatedAt ?? now, now, actor);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = current is null
                ? """
                  INSERT INTO work_order_target_times (
                      production_batch_id, part_price_nis, material_total_nis, quantity, margin_nis, resources_json, notes,
                      version, created_at, updated_at, updated_by)
                  VALUES ($id, $price, $material, $quantity, $margin, $resources, $notes, $version, $createdAt, $updatedAt, $updatedBy);
                  """
                : """
                  UPDATE work_order_target_times
                  SET part_price_nis = $price, material_total_nis = $material, quantity = $quantity, margin_nis = $margin,
                      resources_json = $resources, notes = $notes, version = $version, updated_at = $updatedAt, updated_by = $updatedBy
                  WHERE production_batch_id = $id AND version = $expectedVersion;
                  """;
            var calculation = saved.Calculation;
            command.Parameters.AddWithValue("$id", productionBatchId);
            command.Parameters.AddWithValue("$price", calculation.PartPriceNis);
            command.Parameters.AddWithValue("$material", calculation.MaterialTotalNis);
            command.Parameters.AddWithValue("$quantity", calculation.Quantity);
            command.Parameters.AddWithValue("$margin", calculation.MarginNis);
            command.Parameters.AddWithValue("$resources", JsonSerializer.Serialize(calculation.Resources, Json));
            command.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value);
            command.Parameters.AddWithValue("$version", saved.Version);
            command.Parameters.AddWithValue("$createdAt", Format(saved.CreatedAt));
            command.Parameters.AddWithValue("$updatedAt", Format(saved.UpdatedAt));
            command.Parameters.AddWithValue("$updatedBy", actor);
            command.Parameters.AddWithValue("$expectedVersion", expectedVersion ?? 0);
            if (await command.ExecuteNonQueryAsync(token) != 1) return new(WorkOrderTargetTimeOutcome.Stale, current);
        }

        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction,
            new("work_order_target_time_saved", now, actor, RelatedIds(productionBatchId), "planner_entered", null,
                current is null ? null : Data(current), Data(saved)), token);
        await transaction.CommitAsync(token);
        return new(current is null ? WorkOrderTargetTimeOutcome.Created : WorkOrderTargetTimeOutcome.Updated, saved);
    }

    public async Task<WorkOrderTargetTimeWriteResult> DeleteAsync(string productionBatchId, int expectedVersion, string actor, DateTimeOffset now, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await BatchExistsAsync(connection, transaction, productionBatchId, token)) return new(WorkOrderTargetTimeOutcome.NotFound, null);
        var current = await ReadAsync(connection, transaction, productionBatchId, token);
        if (current is null) return new(WorkOrderTargetTimeOutcome.TargetTimeNotFound, null);
        if (current.Version != expectedVersion) return new(WorkOrderTargetTimeOutcome.Stale, current);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM work_order_target_times WHERE production_batch_id = $id AND version = $version;";
            command.Parameters.AddWithValue("$id", productionBatchId);
            command.Parameters.AddWithValue("$version", expectedVersion);
            if (await command.ExecuteNonQueryAsync(token) != 1) return new(WorkOrderTargetTimeOutcome.Stale, current);
        }
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction,
            new("work_order_target_time_removed", now, actor, RelatedIds(productionBatchId), "planner_entered", null, Data(current), null), token);
        await transaction.CommitAsync(token);
        return new(WorkOrderTargetTimeOutcome.Deleted, current);
    }

    private static async Task<WorkOrderTargetTimeContext?> ReadContextAsync(
        SqliteConnection connection, SqliteTransaction transaction, string productionBatchId, CancellationToken token)
    {
        string batchNumber;
        int quantity;
        await using (var batch = connection.CreateCommand())
        {
            batch.Transaction = transaction;
            batch.CommandText = "SELECT batch_number, planned_quantity FROM production_batches WHERE id = $id;";
            batch.Parameters.AddWithValue("$id", productionBatchId);
            await using var reader = await batch.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return null;
            batchNumber = reader.GetString(0);
            quantity = reader.GetInt32(1);
        }

        double? suggestedPrice = null;
        await using (var price = connection.CreateCommand())
        {
            price.Transaction = transaction;
            // The unit price of the Orders the Work Order is allocated to, weighted by the allocated quantity.
            price.CommandText = """
                SELECT SUM(allocation.quantity * orders.price) / SUM(allocation.quantity)
                FROM batch_allocations allocation
                JOIN orders ON orders.id = allocation.order_id
                WHERE allocation.production_batch_id = $id AND allocation.allocation_type = 'order' AND orders.price IS NOT NULL;
                """;
            price.Parameters.AddWithValue("$id", productionBatchId);
            var value = await price.ExecuteScalarAsync(token);
            if (value is not null and not DBNull) suggestedPrice = Math.Round(Convert.ToDouble(value, CultureInfo.InvariantCulture), 2);
        }

        var resources = new List<TargetTimeResourceInput>();
        await using (var machines = connection.CreateCommand())
        {
            machines.Transaction = transaction;
            machines.CommandText = """
                SELECT operation.operation_number, operation.required_machine_type, operation.setup_seconds, operation.cycle_seconds,
                       machine.id, machine.number, machine.name, machine.hourly_rate_nis
                FROM batch_operations operation
                LEFT JOIN machine_assignments assignment ON assignment.batch_operation_id = operation.id
                LEFT JOIN machines machine ON machine.id = COALESCE(operation.actual_machine_id, assignment.machine_id)
                WHERE operation.production_batch_id = $id AND operation.status <> 'cancelled'
                ORDER BY operation.route_position;
                """;
            machines.Parameters.AddWithValue("$id", productionBatchId);
            await using var reader = await machines.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var operationNumber = reader.GetInt32(0);
                var machineType = reader.IsDBNull(1) ? null : reader.GetString(1);
                var planned = (reader.IsDBNull(2) ? 0d : reader.GetInt64(2)) + (reader.IsDBNull(3) ? 0d : reader.GetInt64(3)) * quantity;
                if (!reader.IsDBNull(4))
                {
                    resources.Add(new(TargetTimeResourceKinds.Machine, reader.GetString(4), $"{reader.GetString(5)} {reader.GetString(6)}".Trim(),
                        reader.IsDBNull(7) ? null : reader.GetDouble(7), operationNumber, planned));
                }
                else if (!string.IsNullOrWhiteSpace(machineType))
                {
                    resources.Add(new(TargetTimeResourceKinds.MachineType, null, machineType.Trim(), null, operationNumber, planned));
                }
            }
        }

        await using (var stations = connection.CreateCommand())
        {
            stations.Transaction = transaction;
            stations.CommandText = """
                SELECT operation.operation_number, type.id, type.name, type.hourly_rate_nis,
                       requirement.estimated_duration_seconds, requirement.duration_per_unit_seconds
                FROM batch_operations operation
                JOIN operation_resource_requirements requirement ON requirement.case_operation_id = operation.source_case_operation_id
                JOIN workstation_types type ON type.id = requirement.workstation_type_id
                WHERE operation.production_batch_id = $id AND operation.status <> 'cancelled'
                  AND requirement.resource_class = 'WORKSTATION' AND requirement.is_active = 1
                ORDER BY operation.route_position, requirement.sequence_position;
                """;
            stations.Parameters.AddWithValue("$id", productionBatchId);
            await using var reader = await stations.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var planned = reader.GetInt64(4) + (reader.IsDBNull(5) ? 0d : reader.GetInt64(5)) * quantity;
                resources.Add(new(TargetTimeResourceKinds.WorkstationType, reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetDouble(3), reader.GetInt32(0), planned));
            }
        }
        return new WorkOrderTargetTimeContext(productionBatchId, batchNumber, quantity, suggestedPrice, resources);
    }

    private static async Task<bool> BatchExistsAsync(SqliteConnection connection, SqliteTransaction transaction, string productionBatchId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM production_batches WHERE id = $id);";
        command.Parameters.AddWithValue("$id", productionBatchId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<WorkOrderTargetTime?> ReadAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string productionBatchId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT part_price_nis, material_total_nis, quantity, margin_nis, resources_json, notes, version, created_at, updated_at, updated_by
            FROM work_order_target_times WHERE production_batch_id = $id;
            """;
        command.Parameters.AddWithValue("$id", productionBatchId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var resources = JsonSerializer.Deserialize<TargetTimeResource[]>(reader.GetString(4), Json) ?? [];
        return new WorkOrderTargetTime(
            productionBatchId,
            new TargetTimeCalculation(reader.GetDouble(0), reader.GetDouble(1), reader.GetInt32(2), reader.GetDouble(3), resources),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt32(6),
            Parse(reader.GetString(7)),
            Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    private static Dictionary<string, string> RelatedIds(string productionBatchId) => new() { ["productionBatchId"] = productionBatchId };

    private static object Data(WorkOrderTargetTime value) => new
    {
        value.Calculation.PartPriceNis,
        value.Calculation.MaterialTotalNis,
        value.Calculation.Quantity,
        value.Calculation.MarginNis,
        Resources = value.Calculation.Resources.Select(resource => new { resource.Kind, resource.Name, resource.HourlyRateNis, resource.TargetHours }),
        value.Notes,
        value.Version
    };

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
