using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;

namespace Meimad.Planner.Server.Application.Maintenance;

internal sealed record OperationalEvidenceItem(string EventId, string EventType, DateTimeOffset Timestamp,
    string? ReasonCode, IReadOnlyDictionary<string, string> RelatedEntityIds);
internal sealed record OperationalEvidencePage(IReadOnlyList<OperationalEvidenceItem> Items, string? NextCursor);

internal sealed class OperationalEvidenceReader(SqliteDatabase database)
{
    internal static readonly HashSet<string> EntityKeys = new(StringComparer.Ordinal)
        { "machineId", "productionRunId", "batchOperationId", "productionPackageId", "caseId", "correlationId" };
    private sealed record Cursor(string At, string Id, string From, string To, string? EntityType, string? EntityId);

    internal async Task<OperationalEvidencePage> ReadAsync(DateTimeOffset from, DateTimeOffset to,
        string? entityType, string? entityId, string? cursor, int limit, CancellationToken token)
    {
        if (limit is < 1 or > 200 || to <= from || to - from > TimeSpan.FromDays(31)
            || (entityType is null) != (entityId is null) || entityType is not null && !EntityKeys.Contains(entityType)
            || entityId?.Length is > 256 or 0 || cursor?.Length > 2048)
            throw new ArgumentException("Use a range of at most 31 days, limit 1-200 and an allowed entity type with its ID.");
        var start = from.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var end = to.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        Cursor? after = null;
        if (cursor is not null)
        {
            try { after = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(cursor)); }
            catch (Exception exception) when (exception is FormatException or JsonException)
            { throw new ArgumentException("Invalid evidence cursor.", exception); }
            if (after is null || after.From != start || after.To != end || after.EntityType != entityType || after.EntityId != entityId
                || string.IsNullOrEmpty(after.Id) || after.Id.Length > 256 || string.IsNullOrEmpty(after.At) || after.At.Length > 64
                || !DateTimeOffset.TryParse(after.At, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) || at < from || at >= to)
                throw new ArgumentException("The evidence cursor does not match this query.");
        }
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,event_type,occurred_at,reason_code,related_entity_ids_json
            FROM structured_event_log
            WHERE occurred_at >= $from AND occurred_at < $to
              AND ($path IS NULL OR json_extract(related_entity_ids_json,$path)=$entity)
              AND ($at IS NULL OR occurred_at < $at OR (occurred_at=$at AND id<$id))
            ORDER BY occurred_at DESC,id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$from", start); command.Parameters.AddWithValue("$to", end);
        command.Parameters.AddWithValue("$path", entityType is null ? DBNull.Value : "$." + entityType);
        command.Parameters.AddWithValue("$entity", (object?)entityId ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", (object?)after?.At ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", (object?)after?.Id ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit + 1);
        var rows = new List<(OperationalEvidenceItem Item, string At)>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var entities = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4)) ?? [];
            rows.Add((new(reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? null : reader.GetString(3), entities.Where(pair => EntityKeys.Contains(pair.Key) && pair.Value is not null && pair.Value.Length <= 256)
                    .ToDictionary(pair => pair.Key, pair => pair.Value)), reader.GetString(2)));
        }
        var page = rows.Take(limit).ToArray();
        var next = rows.Count > limit ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new Cursor(page[^1].At, page[^1].Item.EventId, start, end, entityType, entityId))) : null;
        return new(page.Select(row => row.Item).ToArray(), next);
    }
}
