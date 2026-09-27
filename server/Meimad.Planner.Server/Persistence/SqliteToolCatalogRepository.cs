using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.ToolCatalog;
using Meimad.Planner.Server.Domain.ToolCatalog;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteToolCatalogRepository(SqliteDatabase database) : IToolCatalogRepository
{
    private const string Projection =
        "tool.id, tool.internal_number, tool.internal_code, tool.name, tool.tool_type, tool.hand, tool.description, " +
        "tool.shape_json, tool.attributes_json, tool.is_active, tool.version, tool.created_at, tool.updated_at, tool.updated_by";

    public async Task<IReadOnlyList<CatalogTool>> ListAsync(string? query, string? toolType, bool includeInactive, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {Projection}
            FROM catalog_tools tool
            WHERE ($includeInactive = 1 OR tool.is_active = 1)
              AND ($type IS NULL OR tool.tool_type = $type)
              AND ($like IS NULL
                   OR tool.internal_code LIKE $like ESCAPE '\'
                   OR tool.name LIKE $like ESCAPE '\'
                   OR tool.description LIKE $like ESCAPE '\'
                   OR EXISTS (SELECT 1 FROM catalog_tool_external_ids external
                              WHERE external.catalog_tool_id = tool.id AND external.value LIKE $like ESCAPE '\'))
            ORDER BY tool.internal_number;
            """;
        command.Parameters.AddWithValue("$includeInactive", includeInactive ? 1 : 0);
        command.Parameters.AddWithValue("$type", (object?)toolType ?? DBNull.Value);
        command.Parameters.AddWithValue("$like", query is null ? DBNull.Value : "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        var tools = new List<CatalogTool>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) tools.Add(Read(reader, []));
        }
        var externalIds = await ReadExternalIdsAsync(connection, transaction, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return tools.Select(tool => tool with { ExternalIds = externalIds.TryGetValue(tool.CatalogToolId, out var ids) ? ids : [] }).ToArray();
    }

    public async Task<CatalogTool?> GetAsync(string catalogToolId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var tool = await ReadOneAsync(connection, transaction, catalogToolId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return tool;
    }

    public async Task<CatalogTool> CreateAsync(ValidatedCatalogTool values, string updatedBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        int number;
        await using (var next = connection.CreateCommand())
        {
            next.Transaction = transaction;
            next.CommandText = "SELECT COALESCE(MAX(internal_number), 0) + 1 FROM catalog_tools;";
            number = Convert.ToInt32(await next.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        var tool = new CatalogTool(
            Guid.NewGuid().ToString("N"), number, CatalogTool.InternalCodeFor(number), values.Name, values.ToolType, values.Hand,
            values.Description, values.Shape, values.Attributes, values.ExternalIds, values.IsActive, 1, now, now, updatedBy);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO catalog_tools (
                id, internal_number, internal_code, name, tool_type, hand, description, shape_json, attributes_json,
                is_active, version, created_at, updated_at, updated_by)
            VALUES ($id, $number, $code, $name, $type, $hand, $description, $shape, $attributes, $active, 1, $now, $now, $by);
            """, cancellationToken,
            ("$id", tool.CatalogToolId), ("$number", number), ("$code", tool.InternalCode), ("$name", tool.Name),
            ("$type", tool.ToolType), ("$hand", Db(tool.Hand)), ("$description", Db(tool.Description)),
            ("$shape", JsonSerializer.Serialize(tool.Shape)), ("$attributes", JsonSerializer.Serialize(tool.Attributes)),
            ("$active", tool.IsActive ? 1 : 0), ("$now", Format(now)), ("$by", updatedBy));
        await WriteExternalIdsAsync(connection, transaction, tool.CatalogToolId, tool.ExternalIds, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return tool;
    }

    public async Task<CatalogTool> UpdateAsync(
        string catalogToolId, int expectedVersion, ValidatedCatalogTool values, string updatedBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadOneAsync(connection, transaction, catalogToolId, cancellationToken)
            ?? throw new ToolCatalogNotFoundException(catalogToolId);
        if (current.Version != expectedVersion)
            throw new ToolCatalogConflictException(
                "tool_catalog_version_conflict",
                $"Catalog tool {current.InternalCode} was changed by someone else (version {current.Version}); reload it before saving.");
        var tool = current with
        {
            Name = values.Name,
            ToolType = values.ToolType,
            Hand = values.Hand,
            Description = values.Description,
            Shape = values.Shape,
            Attributes = values.Attributes,
            ExternalIds = values.ExternalIds,
            IsActive = values.IsActive,
            Version = expectedVersion + 1,
            UpdatedAt = now,
            UpdatedBy = updatedBy
        };
        await ExecuteAsync(connection, transaction, """
            UPDATE catalog_tools
            SET name = $name, tool_type = $type, hand = $hand, description = $description, shape_json = $shape,
                attributes_json = $attributes, is_active = $active, version = $version, updated_at = $now, updated_by = $by
            WHERE id = $id AND version = $expected;
            """, cancellationToken,
            ("$id", tool.CatalogToolId), ("$name", tool.Name), ("$type", tool.ToolType), ("$hand", Db(tool.Hand)),
            ("$description", Db(tool.Description)), ("$shape", JsonSerializer.Serialize(tool.Shape)),
            ("$attributes", JsonSerializer.Serialize(tool.Attributes)), ("$active", tool.IsActive ? 1 : 0),
            ("$version", tool.Version), ("$now", Format(now)), ("$by", updatedBy), ("$expected", expectedVersion));
        await ExecuteAsync(connection, transaction, "DELETE FROM catalog_tool_external_ids WHERE catalog_tool_id = $id;", cancellationToken,
            ("$id", tool.CatalogToolId));
        await WriteExternalIdsAsync(connection, transaction, tool.CatalogToolId, tool.ExternalIds, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return tool;
    }

    public async Task<bool> DeleteAsync(string catalogToolId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadOneAsync(connection, transaction, catalogToolId, cancellationToken);
        if (current is null) return false;
        await using (var references = connection.CreateCommand())
        {
            references.Transaction = transaction;
            references.CommandText = "SELECT COUNT(*) FROM tool_preparation_tools WHERE catalog_tool_id = $id;";
            references.Parameters.AddWithValue("$id", catalogToolId);
            var count = Convert.ToInt64(await references.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (count > 0)
                throw new ToolCatalogConflictException(
                    "tool_catalog_in_use",
                    $"Catalog tool {current.InternalCode} is referenced by {count} prepared tool(s); deactivate it instead of deleting it.");
        }
        await ExecuteAsync(connection, transaction, "DELETE FROM catalog_tools WHERE id = $id;", cancellationToken, ("$id", catalogToolId));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>Whether every id names a catalog tool (the Tool Room's preparation link).</summary>
    internal static async Task<string?> FirstUnknownAsync(
        SqliteConnection connection, SqliteTransaction transaction, IEnumerable<string> catalogToolIds, CancellationToken cancellationToken)
    {
        foreach (var id in catalogToolIds.Distinct(StringComparer.Ordinal))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM catalog_tools WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0) return id;
        }
        return null;
    }

    private static async Task<CatalogTool?> ReadOneAsync(
        SqliteConnection connection, SqliteTransaction transaction, string catalogToolId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Projection} FROM catalog_tools tool WHERE tool.id = $id;";
        command.Parameters.AddWithValue("$id", catalogToolId);
        CatalogTool? tool;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            tool = Read(reader, []);
        }
        var externalIds = await ReadExternalIdsAsync(connection, transaction, catalogToolId, cancellationToken);
        return tool with { ExternalIds = externalIds.TryGetValue(catalogToolId, out var ids) ? ids : [] };
    }

    private static async Task<Dictionary<string, IReadOnlyList<CatalogToolExternalId>>> ReadExternalIdsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string? catalogToolId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT catalog_tool_id, system, value
            FROM catalog_tool_external_ids
            WHERE $id IS NULL OR catalog_tool_id = $id
            ORDER BY catalog_tool_id, system COLLATE NOCASE, value COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$id", (object?)catalogToolId ?? DBNull.Value);
        var result = new Dictionary<string, IReadOnlyList<CatalogToolExternalId>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            if (!result.TryGetValue(id, out var list))
            {
                list = new List<CatalogToolExternalId>();
                result[id] = list;
            }
            ((List<CatalogToolExternalId>)list).Add(new CatalogToolExternalId(reader.GetString(1), reader.GetString(2)));
        }
        return result;
    }

    private static async Task WriteExternalIdsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string catalogToolId,
        IReadOnlyList<CatalogToolExternalId> externalIds, CancellationToken cancellationToken)
    {
        foreach (var entry in externalIds)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO catalog_tool_external_ids (id, catalog_tool_id, system, value)
                VALUES ($id, $toolId, $system, $value);
                """, cancellationToken,
                ("$id", Guid.NewGuid().ToString("N")), ("$toolId", catalogToolId), ("$system", entry.System), ("$value", entry.Value));
        }
    }

    private static CatalogTool Read(SqliteDataReader reader, IReadOnlyList<CatalogToolExternalId> externalIds) => new(
        reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        new SortedDictionary<string, double>(JsonSerializer.Deserialize<Dictionary<string, double>>(reader.GetString(7)) ?? [], StringComparer.Ordinal),
        new SortedDictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(8)) ?? [], StringComparer.Ordinal),
        externalIds, reader.GetInt32(9) == 1, reader.GetInt32(10),
        DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.GetString(13));

    private static async Task ExecuteAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object Db(object? value) => value ?? DBNull.Value;

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
