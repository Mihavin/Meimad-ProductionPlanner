using Meimad.Planner.Server.Domain.Readiness;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal static class SqliteProductionReadinessContextReader
{
    internal static async Task<ProductionReadinessContext?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string batchOperationId,
        CancellationToken token)
    {
        string sourceOperationId;
        string? assignmentId;
        string? machineId;
        string? executionMode;
        int? usablePositions;
        string? processId;
        string? toolTableId;
        int? requiredToolCount;
        string? selectedReleaseId;
        string materialStatus;
        string? materialComment;
        string batchId;
        int plannedQuantity;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT operation.source_case_operation_id,
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
                       operation.production_batch_id,
                       batch.planned_quantity
                FROM batch_operations operation
                JOIN production_batches batch ON batch.id = operation.production_batch_id
                LEFT JOIN machine_assignments assignment
                  ON assignment.batch_operation_id = operation.id
                LEFT JOIN machines machine ON machine.id = assignment.machine_id
                LEFT JOIN process_revisions active_process
                  ON active_process.case_operation_id = operation.source_case_operation_id
                 AND active_process.is_active = 1
                LEFT JOIN tool_table_releases active_tools
                  ON active_tools.id = active_process.tool_table_release_id
                LEFT JOIN tool_table_releases pinned_tools
                  ON pinned_tools.id = operation.production_tool_table_release_id
                WHERE operation.id = $id;
                """;
            command.Parameters.AddWithValue("$id", batchOperationId);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
            {
                return null;
            }

            sourceOperationId = reader.GetString(0);
            assignmentId = String(reader, 1);
            machineId = String(reader, 2);
            executionMode = String(reader, 3);
            usablePositions = Int(reader, 4);
            processId = String(reader, 5);
            toolTableId = String(reader, 6);
            requiredToolCount = Int(reader, 7);
            selectedReleaseId = String(reader, 8);
            batchId = reader.GetString(9);
            plannedQuantity = reader.GetInt32(10);
        }

        (materialStatus, materialComment) = await ReadMaterialAsync(
            connection, transaction, batchId, plannedQuantity, token);

        var supported = new HashSet<string>(StringComparer.Ordinal);
        if (machineId is not null)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT postprocessor_id
                FROM machine_supported_postprocessors
                WHERE machine_id = $machineId;
                """;
            command.Parameters.AddWithValue("$machineId", machineId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) supported.Add(reader.GetString(0));
        }

        var releases = new List<ReadinessRelease>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT release.id, release.process_revision_id,
                       release.postprocessor_id, postprocessor.name,
                       release.original_file_name, release.post_specific_revision
                FROM gcode_releases release
                JOIN postprocessors postprocessor ON postprocessor.id = release.postprocessor_id
                WHERE release.case_operation_id = $operationId
                  AND NOT EXISTS (
                      SELECT 1 FROM gcode_releases newer
                      WHERE newer.process_revision_id = release.process_revision_id
                        AND newer.postprocessor_id = release.postprocessor_id
                        AND newer.post_specific_revision > release.post_specific_revision)
                ORDER BY release.released_at, release.id;
                """;
            command.Parameters.AddWithValue("$operationId", sourceOperationId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                releases.Add(new ReadinessRelease(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetInt32(5)));
            }
        }

        var offsetFacts = new List<ToolOffsetReadinessFact>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT machine_id, process_revision_id, gcode_release_id,
                       status, comment, recorded_at
                FROM tool_offset_readiness_records
                WHERE batch_operation_id = $id
                ORDER BY recorded_at DESC, id DESC;
                """;
            command.Parameters.AddWithValue("$id", batchOperationId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                offsetFacts.Add(new ToolOffsetReadinessFact(
                    reader.GetString(0), reader.GetString(1), String(reader, 2),
                    reader.GetString(3), String(reader, 4),
                    DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        return new ProductionReadinessContext(
            batchOperationId, assignmentId, machineId, executionMode, supported,
            usablePositions, processId, toolTableId, requiredToolCount, releases,
            selectedReleaseId, offsetFacts, materialStatus, materialComment);
    }

    private static string? String(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? Int(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static async Task<(string Status, string Message)> ReadMaterialAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string batchId,
        int plannedQuantity,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                COALESCE((SELECT SUM(quantity) FROM batch_material_reservations
                          WHERE production_batch_id = $batchId), 0),
                COALESCE((SELECT SUM(receipt.quantity)
                          FROM verified_material_receipts receipt
                          JOIN production_batches batch ON batch.id = $batchId
                          WHERE receipt.case_id = batch.case_id), 0)
                - COALESCE((SELECT SUM(reservation.quantity)
                            FROM batch_material_reservations reservation
                            WHERE reservation.production_batch_id <> $batchId
                              AND reservation.receipt_id IN (
                                  SELECT receipt.id
                                  FROM verified_material_receipts receipt
                                  JOIN production_batches batch ON batch.id = $batchId
                                  WHERE receipt.case_id = batch.case_id)), 0);
            """;
        command.Parameters.AddWithValue("$batchId", batchId);
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var reserved = reader.GetInt32(0);
        var availableToBatch = reader.GetInt32(1);
        if (reserved >= plannedQuantity)
            return ("READY",
                $"{reserved} of {plannedQuantity} verified material piece(s) are reserved for this Production Batch.");
        if (availableToBatch < plannedQuantity)
            return ("MISSING",
                $"Production Batch requires {plannedQuantity} material piece(s); {availableToBatch} verified piece(s) are available to it. Shortage: {plannedQuantity - availableToBatch}.");
        return ("UNVERIFIED",
            $"Production Batch requires {plannedQuantity} material piece(s); {availableToBatch} verified piece(s) are available, but only {reserved} are explicitly reserved.");
    }
}
