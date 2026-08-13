using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.EventLogging;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteStructuredEventLogRepository(SqliteDatabase database) : IStructuredEventLogRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task AppendAsync(StructuredEventWrite value, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await AppendAsync(connection, transaction, value, token);
        await transaction.CommitAsync(token);
    }

    internal static async Task AppendAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        StructuredEventWrite value, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO structured_event_log
                (id,event_type,occurred_at,user_id,related_entity_ids_json,reason_code,
                 comment,before_data_json,after_data_json,event_key)
            VALUES ($id,$type,$at,$user,$entities,$reason,$comment,$before,$after,$key);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$type", value.EventType);
        command.Parameters.AddWithValue("$at", Format(value.Timestamp));
        command.Parameters.AddWithValue("$user", value.User);
        command.Parameters.AddWithValue("$entities", JsonSerializer.Serialize(value.RelatedEntityIds, JsonOptions));
        command.Parameters.AddWithValue("$reason", Db(value.ReasonCode));
        command.Parameters.AddWithValue("$comment", Db(value.Comment));
        command.Parameters.AddWithValue("$before", Db(value.BeforeData is null ? null : JsonSerializer.Serialize(value.BeforeData, JsonOptions)));
        command.Parameters.AddWithValue("$after", Db(value.AfterData is null ? null : JsonSerializer.Serialize(value.AfterData, JsonOptions)));
        command.Parameters.AddWithValue("$key", Db(value.EventKey));
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<IReadOnlyList<StructuredEvent>> ListAsync(
        DateTimeOffset? from, DateTimeOffset? to, string? eventType, int limit, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,event_type,occurred_at,user_id,related_entity_ids_json,reason_code,
                   comment,before_data_json,after_data_json
            FROM structured_event_log
            WHERE ($from IS NULL OR occurred_at >= $from)
              AND ($to IS NULL OR occurred_at < $to)
              AND ($type IS NULL OR event_type = $type)
            ORDER BY occurred_at DESC,id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$from", from.HasValue ? Format(from.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$to", to.HasValue ? Format(to.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$type", Db(string.IsNullOrWhiteSpace(eventType) ? null : eventType.Trim()));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        var result = new List<StructuredEvent>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new(reader.GetString(0),reader.GetString(1),Parse(reader.GetString(2)),reader.GetString(3),
                JsonSerializer.Deserialize<Dictionary<string,string>>(reader.GetString(4),JsonOptions) ?? [],
                reader.IsDBNull(5)?null:reader.GetString(5),reader.IsDBNull(6)?null:reader.GetString(6),
                reader.IsDBNull(7)?null:reader.GetString(7),reader.IsDBNull(8)?null:reader.GetString(8)));
        return result;
    }

    private static object Db(string? value) => value is null ? DBNull.Value : value;
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
}
