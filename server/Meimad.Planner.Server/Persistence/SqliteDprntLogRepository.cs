using System.Globalization;
using Meimad.Planner.Server.Application.Cnc;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteDprntLogRepository(SqliteDatabase database) : IDprntLogRepository
{
    public async Task<IReadOnlyList<DprntLogLine>?> ReadAsync(DprntLogQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM machines WHERE id = $machineId;";
            exists.Parameters.AddWithValue("$machineId", query.MachineId);
            if (await exists.ExecuteScalarAsync(cancellationToken) is null) return null;
        }

        await using var command = connection.CreateCommand();
        // received_at is always written as UTC round-trip text, so text order is time order; id is
        // the arrival order, also within one poll.
        command.CommandText = """
            SELECT id, received_at, line
            FROM machine_dprnt_lines
            WHERE machine_id = $machineId
              AND id > $afterId
              AND ($from IS NULL OR received_at >= $from)
              AND ($to IS NULL OR received_at < $to)
              AND ($search IS NULL OR line LIKE '%' || $search || '%' ESCAPE '\')
            ORDER BY id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$machineId", query.MachineId);
        command.Parameters.AddWithValue("$afterId", query.AfterId);
        command.Parameters.AddWithValue("$from", query.From is { } from ? Format(from) : DBNull.Value);
        command.Parameters.AddWithValue("$to", query.To is { } to ? Format(to) : DBNull.Value);
        command.Parameters.AddWithValue("$search", query.Search is { } search ? EscapeLike(search) : DBNull.Value);
        command.Parameters.AddWithValue("$limit", query.Limit + 1);
        var lines = new List<DprntLogLine>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new DprntLogLine(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(2)));
        }
        return lines;
    }

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
