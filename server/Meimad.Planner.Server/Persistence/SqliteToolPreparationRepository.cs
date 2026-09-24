using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.ToolPreparations;
using Meimad.Planner.Server.Domain.ToolPreparations;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteToolPreparationRepository(SqliteDatabase database) : IToolPreparationRepository
{
    public async Task<ToolPreparationView?> ReadViewAsync(string batchOperationId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var readiness = await SqliteProductionReadinessContextReader.ReadAsync(
            connection, transaction, batchOperationId, cancellationToken);
        if (readiness?.MachineId is null) return null;
        if (readiness.ActiveToolTableReleaseId is null)
            throw new ToolPreparationValidationException(
                "tool_preparation_tool_table_missing",
                "The Operation has no released Tool Table yet; release the process and its tool table first.");

        await using var context = connection.CreateCommand();
        context.Transaction = transaction;
        context.CommandText = """
            SELECT machine.number, machine.name, machine.machine_type, machine.nc_dialect,
                   machine.tool_diameter_offset_kind, tool.revision_number, tool.original_file_name
            FROM machines machine
            JOIN tool_table_releases tool ON tool.id = $toolTableId
            WHERE machine.id = $machineId;
            """;
        context.Parameters.AddWithValue("$machineId", readiness.MachineId);
        context.Parameters.AddWithValue("$toolTableId", readiness.ActiveToolTableReleaseId);
        await using var reader = await context.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var machineNumber = reader.GetString(0);
        var machineName = reader.GetString(1);
        var processType = reader.GetString(2);
        var ncDialect = reader.GetString(3);
        var offsetKind = reader.GetString(4);
        var toolTableRevision = reader.GetInt32(5);
        var toolTableFileName = reader.GetString(6);
        await reader.DisposeAsync();

        var released = await ReadReleasedToolsAsync(connection, transaction, readiness.ActiveToolTableReleaseId, cancellationToken);
        var current = await ReadLatestAsync(connection, transaction, batchOperationId, readiness.MachineId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ToolPreparationView(
            batchOperationId, readiness.MachineId, machineNumber, machineName, processType, ncDialect, offsetKind,
            readiness.ActiveToolTableReleaseId, toolTableRevision, toolTableFileName, released, current);
    }

    public async Task<ToolPreparation> SaveAsync(
        ToolPreparation preparation,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = """
                SELECT COALESCE(MAX(version_number), 0) FROM tool_preparations
                WHERE batch_operation_id = $operationId AND machine_id = $machineId;
                """;
            check.Parameters.AddWithValue("$operationId", preparation.BatchOperationId);
            check.Parameters.AddWithValue("$machineId", preparation.MachineId);
            var current = Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (current != expectedVersion)
                throw new ToolPreparationConflictException(
                    "tool_preparation_version_conflict",
                    $"The tool preparation was saved by someone else (version {current}); reload it before saving.");
        }

        await ExecuteAsync(connection, transaction, """
            INSERT INTO tool_preparations (
                id, batch_operation_id, machine_id, tool_table_release_id, version_number,
                saved_at, saved_by, comment, content_hash)
            VALUES ($id, $operationId, $machineId, $toolTableId, $version, $savedAt, $savedBy, $comment, $hash);
            """, cancellationToken,
            ("$id", preparation.ToolPreparationId), ("$operationId", preparation.BatchOperationId),
            ("$machineId", preparation.MachineId), ("$toolTableId", preparation.ToolTableReleaseId),
            ("$version", preparation.VersionNumber), ("$savedAt", Format(preparation.SavedAt)),
            ("$savedBy", preparation.SavedBy), ("$comment", Db(preparation.Comment)),
            ("$hash", preparation.ContentHash));

        foreach (var tool in preparation.Tools)
        {
            var toolId = Guid.NewGuid().ToString("N");
            await ExecuteAsync(connection, transaction, """
                INSERT INTO tool_preparation_tools (
                    id, tool_preparation_id, row_number, tool_identifier, offset_number,
                    measured_length, measured_diameter, shape_type, shape_json, notes)
                VALUES ($id, $preparationId, $row, $identifier, $offset, $length, $diameter, $shape, $shapeJson, $notes);
                """, cancellationToken,
                ("$id", toolId), ("$preparationId", preparation.ToolPreparationId),
                ("$row", tool.RowNumber), ("$identifier", tool.ToolIdentifier),
                ("$offset", Db(tool.OffsetNumber)), ("$length", Db(tool.MeasuredLength)),
                ("$diameter", Db(tool.MeasuredDiameter)), ("$shape", tool.ShapeType),
                ("$shapeJson", JsonSerializer.Serialize(tool.Shape)), ("$notes", Db(tool.Notes)));
            foreach (var component in tool.Components)
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO tool_preparation_components (
                        id, tool_preparation_tool_id, sequence, component_type, name,
                        catalog_number, length, diameter, notes)
                    VALUES ($id, $toolId, $sequence, $type, $name, $catalog, $length, $diameter, $notes);
                    """, cancellationToken,
                    ("$id", Guid.NewGuid().ToString("N")), ("$toolId", toolId),
                    ("$sequence", component.Sequence), ("$type", component.ComponentType),
                    ("$name", component.Name), ("$catalog", Db(component.CatalogNumber)),
                    ("$length", Db(component.Length)), ("$diameter", Db(component.Diameter)),
                    ("$notes", Db(component.Notes)));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return preparation;
    }

    /// <summary>The latest saved version for the Operation on the Machine, with its tools and components.</summary>
    internal static async Task<ToolPreparation?> ReadLatestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchOperationId,
        string machineId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, tool_table_release_id, version_number, saved_at, saved_by, comment, content_hash
            FROM tool_preparations
            WHERE batch_operation_id = $operationId AND machine_id = $machineId
            ORDER BY version_number DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$operationId", batchOperationId);
        command.Parameters.AddWithValue("$machineId", machineId);
        ToolPreparation? header;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            header = new ToolPreparation(
                reader.GetString(0), batchOperationId, machineId, reader.GetString(1), reader.GetInt32(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), []);
        }
        return header with { Tools = await ReadToolsAsync(connection, transaction, header.ToolPreparationId, cancellationToken) };
    }

    private static async Task<IReadOnlyList<ToolPreparationTool>> ReadToolsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string preparationId,
        CancellationToken cancellationToken)
    {
        var components = new Dictionary<string, List<ToolPreparationComponent>>(StringComparer.Ordinal);
        await using (var componentCommand = connection.CreateCommand())
        {
            componentCommand.Transaction = transaction;
            componentCommand.CommandText = """
                SELECT component.tool_preparation_tool_id, component.sequence, component.component_type,
                       component.name, component.catalog_number, component.length, component.diameter, component.notes
                FROM tool_preparation_components component
                JOIN tool_preparation_tools tool ON tool.id = component.tool_preparation_tool_id
                WHERE tool.tool_preparation_id = $preparationId
                ORDER BY component.tool_preparation_tool_id, component.sequence;
                """;
            componentCommand.Parameters.AddWithValue("$preparationId", preparationId);
            await using var reader = await componentCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var toolId = reader.GetString(0);
                if (!components.TryGetValue(toolId, out var list)) components[toolId] = list = [];
                list.Add(new ToolPreparationComponent(
                    reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }

        var tools = new List<ToolPreparationTool>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, row_number, tool_identifier, offset_number, measured_length, measured_diameter,
                   shape_type, shape_json, notes
            FROM tool_preparation_tools
            WHERE tool_preparation_id = $preparationId
            ORDER BY row_number;
            """;
        command.Parameters.AddWithValue("$preparationId", preparationId);
        await using var toolReader = await command.ExecuteReaderAsync(cancellationToken);
        while (await toolReader.ReadAsync(cancellationToken))
        {
            var shape = JsonSerializer.Deserialize<Dictionary<string, double>>(toolReader.GetString(7)) ?? [];
            tools.Add(new ToolPreparationTool(
                toolReader.GetInt32(1), toolReader.GetString(2),
                toolReader.IsDBNull(3) ? null : toolReader.GetInt32(3),
                toolReader.IsDBNull(4) ? null : toolReader.GetDouble(4),
                toolReader.IsDBNull(5) ? null : toolReader.GetDouble(5),
                toolReader.GetString(6), new SortedDictionary<string, double>(shape, StringComparer.Ordinal),
                toolReader.IsDBNull(8) ? null : toolReader.GetString(8),
                components.TryGetValue(toolReader.GetString(0), out var list) ? list : []));
        }
        return tools;
    }

    internal static async Task<IReadOnlyList<ToolPreparationReleasedTool>> ReadReleasedToolsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string toolTableReleaseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT row_number, tool_identifier, description, is_required, magazine_position
            FROM tool_table_release_tools
            WHERE tool_table_release_id = $toolTableId AND is_active = 1
            ORDER BY row_number;
            """;
        command.Parameters.AddWithValue("$toolTableId", toolTableReleaseId);
        var rows = new List<ToolPreparationReleasedTool>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ToolPreparationReleasedTool(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return rows;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object Db(object? value) => value ?? DBNull.Value;

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
