using System.Text.Json;
using Meimad.Planner.Server.Application.ToolRequirements;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteToolRequirementSourceRepository(SqliteDatabase database) : IToolRequirementSourceRepository
{
    /// <summary>
    /// The planned operations with the tool table each would use: a not-started operation runs its
    /// Case Operation's active process revision, a started one the tool table it was pinned to. The
    /// material is the Case material and the raw material Kitaron names for the Work Order.
    /// </summary>
    public async Task<IReadOnlyList<PlannedOperationSource>> ReadOperationsAsync(
        IReadOnlyCollection<string> operationIds, CancellationToken cancellationToken)
    {
        if (operationIds.Count == 0) return [];
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH planned AS (
                SELECT operation.id, operation.status, operation.production_tool_table_release_id,
                       operation.production_process_revision_id, operation.source_case_operation_id,
                       operation.operation_number, operation.name, operation.production_batch_id
                FROM batch_operations operation
                WHERE operation.id IN (SELECT value FROM json_each($ids))
            ),
            effective AS (
                SELECT planned.*,
                       CASE WHEN planned.status = 'not_started' THEN active.tool_table_release_id
                            ELSE COALESCE(planned.production_tool_table_release_id, pinned.tool_table_release_id)
                       END AS release_id
                FROM planned
                LEFT JOIN process_revisions active
                  ON active.case_operation_id = planned.source_case_operation_id AND active.is_active = 1
                LEFT JOIN process_revisions pinned ON pinned.id = planned.production_process_revision_id
            )
            SELECT effective.id, batch.batch_number, part.part_number, effective.operation_number, effective.name,
                   release.id, release.stored_relative_path, release.original_file_name,
                   TRIM(COALESCE(part.material_type, '') || ' ' || COALESCE(part.material_specification, '')),
                   (SELECT material.description
                    FROM kitaron_work_orders work_order
                    JOIN kitaron_material_orders material ON material.material_number = work_order.raw_material_id
                    WHERE work_order.work_order_number = batch.batch_number
                      AND work_order.part_number = part.part_number
                    ORDER BY material.last_imported_at DESC
                    LIMIT 1)
            FROM effective
            JOIN production_batches batch ON batch.id = effective.production_batch_id
            JOIN cases part ON part.id = batch.case_id
            LEFT JOIN tool_table_releases release ON release.id = effective.release_id;
            """;
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(operationIds));
        var result = new List<PlannedOperationSource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PlannedOperationSource(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                Text(reader, 5),
                Text(reader, 6),
                Text(reader, 7),
                Text(reader, 8),
                Text(reader, 9)));
        }
        return result;
    }

    public async Task<IReadOnlyList<ReleasedToolRow>> ReadToolRowsAsync(
        IReadOnlyCollection<string> toolTableReleaseIds, CancellationToken cancellationToken)
    {
        if (toolTableReleaseIds.Count == 0) return [];
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tool_table_release_id, tool_identifier, description
            FROM tool_table_release_tools
            WHERE tool_table_release_id IN (SELECT value FROM json_each($ids)) AND is_active = 1
            ORDER BY tool_table_release_id, row_number;
            """;
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(toolTableReleaseIds));
        var result = new List<ReleasedToolRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ReleasedToolRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return result;
    }

    private static string? Text(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) || string.IsNullOrWhiteSpace(reader.GetString(ordinal)) ? null : reader.GetString(ordinal).Trim();
}
