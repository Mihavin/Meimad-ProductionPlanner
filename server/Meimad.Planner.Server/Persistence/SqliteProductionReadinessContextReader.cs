using Meimad.Planner.Server.Domain.Readiness;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal static class SqliteProductionReadinessContextReader
{
    private const int ChunkSize = 500;

    internal static async Task<ProductionReadinessContext?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string batchOperationId,
        CancellationToken token)
    {
        var contexts = await ReadManyAsync(connection, transaction, [batchOperationId], token);
        return contexts.GetValueOrDefault(batchOperationId);
    }

    internal static async Task<IReadOnlyDictionary<string, ProductionReadinessContext>> ReadManyAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyCollection<string> batchOperationIds,
        CancellationToken token)
    {
        var ids = batchOperationIds.Distinct(StringComparer.Ordinal).ToArray();
        var result = new Dictionary<string, ProductionReadinessContext>(StringComparer.Ordinal);
        if (ids.Length == 0) return result;

        var rows = await ReadOperationRowsAsync(connection, transaction, ids, token);
        if (rows.Count == 0) return result;

        var materials = await ReadMaterialsAsync(
            connection, transaction,
            rows.Select(row => row.BatchId).Distinct(StringComparer.Ordinal).ToArray(), token);
        var supported = await ReadSupportedPostprocessorsAsync(
            connection, transaction,
            rows.Where(row => row.MachineId is not null).Select(row => row.MachineId!)
                .Distinct(StringComparer.Ordinal).ToArray(), token);
        var releases = await ReadReleasesAsync(
            connection, transaction,
            rows.Select(row => row.SourceOperationId).Distinct(StringComparer.Ordinal).ToArray(), token);
        var offsetFacts = await ReadOffsetFactsAsync(
            connection, transaction, rows.Select(row => row.BatchOperationId).ToArray(), token);

        foreach (var row in rows)
        {
            var (materialStatus, materialComment) = materials[row.BatchId];
            result[row.BatchOperationId] = new ProductionReadinessContext(
                row.BatchOperationId,
                row.AssignmentId,
                row.MachineId,
                row.ExecutionMode,
                row.MachineId is null ? new HashSet<string>(StringComparer.Ordinal)
                    : supported.GetValueOrDefault(row.MachineId) ?? new HashSet<string>(StringComparer.Ordinal),
                row.UsablePositions,
                row.ProcessId,
                row.ToolTableId,
                row.RequiredToolCount,
                releases.GetValueOrDefault(row.SourceOperationId) ?? [],
                row.SelectedReleaseId,
                offsetFacts.GetValueOrDefault(row.BatchOperationId) ?? [],
                materialStatus,
                materialComment);
        }

        return result;
    }

    private sealed record OperationRow(
        string BatchOperationId,
        string SourceOperationId,
        string? AssignmentId,
        string? MachineId,
        string? ExecutionMode,
        int? UsablePositions,
        string? ProcessId,
        string? ToolTableId,
        int? RequiredToolCount,
        string? SelectedReleaseId,
        string BatchId);

    private static async Task<List<OperationRow>> ReadOperationRowsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<string> ids,
        CancellationToken token)
    {
        var rows = new List<OperationRow>();
        foreach (var chunk in ids.Chunk(ChunkSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT operation.id,
                       operation.source_case_operation_id,
                       assignment.id,
                       assignment.machine_id,
                       machine.execution_mode,
                       machine.usable_tool_positions,
                       CASE WHEN operation.status = 'not_started'
                            THEN active_process.id
                            ELSE operation.production_process_revision_id END,
                       CASE WHEN operation.status = 'not_started'
                            THEN active_process.tool_table_release_id
                            ELSE operation.production_tool_table_release_id END,
                       CASE WHEN operation.status = 'not_started'
                            THEN active_tools.required_tool_count
                            ELSE pinned_tools.required_tool_count END,
                       CASE WHEN operation.status = 'not_started'
                            THEN assignment.selected_gcode_release_id
                            ELSE operation.production_gcode_release_id END,
                       operation.production_batch_id
                FROM batch_operations operation
                LEFT JOIN machine_assignments assignment
                  ON assignment.batch_operation_id = operation.id
                 AND assignment.released_at IS NULL
                LEFT JOIN machines machine ON machine.id = assignment.machine_id
                LEFT JOIN process_revisions active_process
                  ON active_process.case_operation_id = operation.source_case_operation_id
                 AND active_process.is_active = 1
                LEFT JOIN tool_table_releases active_tools
                  ON active_tools.id = active_process.tool_table_release_id
                LEFT JOIN tool_table_releases pinned_tools
                  ON pinned_tools.id = operation.production_tool_table_release_id
                WHERE operation.id IN ({InList(command, chunk)});
                """;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                rows.Add(new OperationRow(
                    reader.GetString(0), reader.GetString(1), String(reader, 2), String(reader, 3),
                    String(reader, 4), Int(reader, 5), String(reader, 6), String(reader, 7),
                    Int(reader, 8), String(reader, 9), reader.GetString(10)));
            }
        }

        return rows;
    }

    private static async Task<Dictionary<string, (string Status, string Message)>> ReadMaterialsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<string> batchIds,
        CancellationToken token)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var chunk in batchIds.Chunk(ChunkSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT batch.id,
                       batch.planned_quantity,
                       COALESCE((SELECT SUM(quantity) FROM batch_material_reservations
                                 WHERE production_batch_id = batch.id), 0),
                       COALESCE((SELECT SUM(receipt.quantity)
                                 FROM verified_material_receipts receipt
                                 WHERE receipt.case_id = batch.case_id), 0)
                       - COALESCE((SELECT SUM(reservation.quantity)
                                   FROM batch_material_reservations reservation
                                   WHERE reservation.production_batch_id <> batch.id
                                     AND reservation.receipt_id IN (
                                         SELECT receipt.id
                                         FROM verified_material_receipts receipt
                                         WHERE receipt.case_id = batch.case_id)), 0)
                FROM production_batches batch
                WHERE batch.id IN ({InList(command, chunk)});
                """;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                result[reader.GetString(0)] = MaterialState(
                    reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
            }
        }

        return result;
    }

    private static (string Status, string Message) MaterialState(
        int plannedQuantity, int reserved, int availableToBatch)
    {
        if (reserved >= plannedQuantity)
            return ("READY",
                $"{reserved} of {plannedQuantity} verified material piece(s) are reserved for this Production Batch.");
        if (availableToBatch < plannedQuantity)
            return ("MISSING",
                $"Production Batch requires {plannedQuantity} material piece(s); {availableToBatch} verified piece(s) are available to it. Shortage: {plannedQuantity - availableToBatch}.");
        return ("UNVERIFIED",
            $"Production Batch requires {plannedQuantity} material piece(s); {availableToBatch} verified piece(s) are available, but only {reserved} are explicitly reserved.");
    }

    private static async Task<Dictionary<string, HashSet<string>>> ReadSupportedPostprocessorsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<string> machineIds,
        CancellationToken token)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var chunk in machineIds.Chunk(ChunkSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT machine_id, postprocessor_id
                FROM machine_supported_postprocessors
                WHERE machine_id IN ({InList(command, chunk)});
                """;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (!result.TryGetValue(reader.GetString(0), out var set))
                    result[reader.GetString(0)] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(reader.GetString(1));
            }
        }

        return result;
    }

    private static async Task<Dictionary<string, List<ReadinessRelease>>> ReadReleasesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<string> caseOperationIds,
        CancellationToken token)
    {
        var result = new Dictionary<string, List<ReadinessRelease>>(StringComparer.Ordinal);
        foreach (var chunk in caseOperationIds.Chunk(ChunkSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT release.case_operation_id, release.id, release.process_revision_id,
                       release.postprocessor_id, postprocessor.name,
                       release.original_file_name, release.post_specific_revision
                FROM gcode_releases release
                JOIN postprocessors postprocessor ON postprocessor.id = release.postprocessor_id
                WHERE release.case_operation_id IN ({InList(command, chunk)})
                  AND NOT EXISTS (
                      SELECT 1 FROM gcode_releases newer
                      WHERE newer.process_revision_id = release.process_revision_id
                        AND newer.postprocessor_id = release.postprocessor_id
                        AND newer.post_specific_revision > release.post_specific_revision)
                ORDER BY release.case_operation_id, release.released_at, release.id;
                """;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (!result.TryGetValue(reader.GetString(0), out var list))
                    result[reader.GetString(0)] = list = [];
                list.Add(new ReadinessRelease(
                    reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetInt32(6)));
            }
        }

        return result;
    }

    private static async Task<Dictionary<string, List<ToolOffsetReadinessFact>>> ReadOffsetFactsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<string> batchOperationIds,
        CancellationToken token)
    {
        var result = new Dictionary<string, List<ToolOffsetReadinessFact>>(StringComparer.Ordinal);
        foreach (var chunk in batchOperationIds.Chunk(ChunkSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT batch_operation_id, machine_id, process_revision_id, gcode_release_id,
                       status, comment, recorded_at
                FROM tool_offset_readiness_records
                WHERE batch_operation_id IN ({InList(command, chunk)})
                ORDER BY batch_operation_id, recorded_at DESC, id DESC;
                """;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (!result.TryGetValue(reader.GetString(0), out var list))
                    result[reader.GetString(0)] = list = [];
                list.Add(new ToolOffsetReadinessFact(
                    reader.GetString(1), reader.GetString(2), String(reader, 3),
                    reader.GetString(4), String(reader, 5),
                    DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        return result;
    }

    private static string InList(SqliteCommand command, IReadOnlyList<string> values)
    {
        var names = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            names[index] = $"$in{index}";
            command.Parameters.AddWithValue(names[index], values[index]);
        }
        return string.Join(',', names);
    }

    private static string? String(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? Int(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
