using System.Globalization;
using Microsoft.Data.Sqlite;
using Meimad.Planner.Server.Application.Reports;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteWeeklyEmployeeEfficiencyRepository(SqliteDatabase database)
    : IWeeklyEmployeeEfficiencyRepository
{
    public async Task<EmployeeWorkMeasurement> CreateMeasurementAsync(EmployeeWorkMeasurement value, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO employee_work_measurements
                (id,employee_resource_id,work_date,planned_seconds,actual_seconds,source_reference,notes,recorded_by,recorded_at)
            SELECT $id,$resource,$date,$planned,$actual,$reference,$notes,$by,$at
            WHERE EXISTS (SELECT 1 FROM employee_resources WHERE id=$resource);
            """;
        command.Parameters.AddWithValue("$id", value.MeasurementId);
        command.Parameters.AddWithValue("$resource", value.EmployeeResourceId);
        command.Parameters.AddWithValue("$date", value.WorkDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$planned", value.PlannedSeconds);
        command.Parameters.AddWithValue("$actual", value.ActualSeconds);
        command.Parameters.AddWithValue("$reference", (object?)value.SourceReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", (object?)value.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$by", value.RecordedBy);
        command.Parameters.AddWithValue("$at", value.RecordedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new EmployeeWorkMeasurementValidationException("employeeResourceId", "Employee resource does not exist.");
        return value;
    }

    public async Task<IReadOnlyList<EmployeeEfficiencyAggregate>> ReadAsync(DateOnly from, DateOnly toExclusive, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.id,r.employee_number,r.first_name,r.last_name,r.resource_type,
                   SUM(m.planned_seconds),SUM(m.actual_seconds)
            FROM employee_work_measurements m
            JOIN employee_resources r ON r.id=m.employee_resource_id
            WHERE m.work_date >= $from AND m.work_date < $to
              AND r.resource_type IN ('setup_worker','qa_worker','regular_worker')
            GROUP BY r.id,r.employee_number,r.first_name,r.last_name,r.resource_type
            ORDER BY CASE r.resource_type WHEN 'setup_worker' THEN 1 WHEN 'qa_worker' THEN 2 ELSE 3 END,
                     r.last_name COLLATE NOCASE,r.first_name COLLATE NOCASE,r.employee_number COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", toExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var values = new List<EmployeeEfficiencyAggregate>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            values.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetInt64(5),reader.GetInt64(6)));
        return values;
    }

    public async Task<bool> WasAutomaticallySentAsync(string periodKey, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM weekly_employee_efficiency_deliveries WHERE period_key=$key);";
        command.Parameters.AddWithValue("$key", periodKey);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1;
    }

    public async Task MarkAutomaticallySentAsync(string periodKey, int recipientCount, DateTimeOffset sentAt, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO weekly_employee_efficiency_deliveries(period_key,sent_at,recipient_count) VALUES($key,$at,$count);";
        command.Parameters.AddWithValue("$key", periodKey);
        command.Parameters.AddWithValue("$at", sentAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$count", recipientCount);
        await command.ExecuteNonQueryAsync(token);
    }
}
