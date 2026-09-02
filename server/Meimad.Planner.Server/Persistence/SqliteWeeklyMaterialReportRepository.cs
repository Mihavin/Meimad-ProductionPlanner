using System.Globalization;
using Meimad.Planner.Server.Application.Reports;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteWeeklyMaterialReportRepository(SqliteDatabase database)
    : IWeeklyMaterialReportRepository
{
    public async Task<IReadOnlyList<WeeklyMaterialReportItem>> ReadAsync(
        DateOnly weekStart, DateOnly weekEndExclusive, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH due_batches AS (
                SELECT DISTINCT production_batches.id,
                       production_batches.case_id,
                       production_batches.planned_quantity
                FROM production_batches
                JOIN batch_allocations
                  ON batch_allocations.production_batch_id = production_batches.id
                 AND batch_allocations.allocation_type = 'order'
                JOIN orders ON orders.id = batch_allocations.order_id
                WHERE orders.work_finish_date >= $weekStart
                  AND orders.work_finish_date < $weekEnd
                  AND orders.status IN ('active', 'in_production')
                  AND production_batches.status NOT IN ('complete', 'cancelled')
            )
            SELECT cases.id, cases.part_number, SUM(due_batches.planned_quantity)
            FROM due_batches
            JOIN cases ON cases.id = due_batches.case_id
            GROUP BY cases.id, cases.part_number
            ORDER BY cases.part_number COLLATE NOCASE, cases.id;
            """;
        command.Parameters.AddWithValue("$weekStart", weekStart.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$weekEnd", weekEndExclusive.ToString("yyyy-MM-dd"));
        var items = new List<WeeklyMaterialReportItem>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }
        return items;
    }

    public async Task<bool> WasAutomaticallySentAsync(string periodKey, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM weekly_material_report_deliveries WHERE period_key=$key);";
        command.Parameters.AddWithValue("$key", periodKey);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1;
    }

    public async Task MarkAutomaticallySentAsync(
        string periodKey, int recipientCount, DateTimeOffset sentAt, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO weekly_material_report_deliveries (period_key,sent_at,recipient_count) VALUES ($key,$sentAt,$count);";
        command.Parameters.AddWithValue("$key", periodKey);
        command.Parameters.AddWithValue("$sentAt", sentAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$count", recipientCount);
        await command.ExecuteNonQueryAsync(token);
    }
}
