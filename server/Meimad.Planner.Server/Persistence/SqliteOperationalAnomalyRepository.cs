using System.Globalization;
using Meimad.Planner.Server.Application.Anomalies;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteOperationalAnomalyRepository(SqliteDatabase database)
    : IOperationalAnomalyRepository
{
    public async Task AppendAsync(
        AppendOperationalAnomaly value,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO operational_anomalies(
                id,anomaly_type,machine_id,production_run_id,tablet_device_id,
                source,source_event_id,workflow_event_id,detected_at,details_json,dedupe_key)
            VALUES(
                $id,$type,
                CASE WHEN EXISTS(SELECT 1 FROM machines WHERE id=$machineId)
                     THEN $machineId END,
                CASE WHEN EXISTS(SELECT 1 FROM production_runs WHERE id=$runId)
                     THEN $runId END,
                CASE WHEN EXISTS(SELECT 1 FROM device_registry WHERE id=$tabletId)
                     THEN $tabletId END,
                $source,$sourceEventId,
                CASE WHEN EXISTS(SELECT 1 FROM production_run_workflow_events WHERE id=$workflowId)
                     THEN $workflowId END,
                $at,$details,$dedupeKey);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$type", value.AnomalyType);
        command.Parameters.AddWithValue("$machineId", Db(value.MachineId));
        command.Parameters.AddWithValue("$runId", Db(value.ProductionRunId));
        command.Parameters.AddWithValue("$tabletId", Db(value.TabletDeviceId));
        command.Parameters.AddWithValue("$source", value.Source.Trim());
        command.Parameters.AddWithValue("$sourceEventId", Db(value.SourceEventId));
        command.Parameters.AddWithValue("$workflowId", Db(value.WorkflowEventId));
        command.Parameters.AddWithValue("$at", Format(value.DetectedAt));
        command.Parameters.AddWithValue("$details", value.DetailsJson);
        command.Parameters.AddWithValue("$dedupeKey", value.DedupeKey.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OperationalAnomaly>> ListAsync(
        string? machineId,
        string? productionRunId,
        string? anomalyType,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,anomaly_type,machine_id,production_run_id,tablet_device_id,
                   source,source_event_id,workflow_event_id,detected_at,details_json
            FROM operational_anomalies
            WHERE ($machineId IS NULL OR machine_id=$machineId)
              AND ($runId IS NULL OR production_run_id=$runId)
              AND ($type IS NULL OR anomaly_type=$type)
            ORDER BY detected_at DESC,id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$machineId", Db(machineId));
        command.Parameters.AddWithValue("$runId", Db(productionRunId));
        command.Parameters.AddWithValue("$type", Db(anomalyType));
        command.Parameters.AddWithValue("$limit", limit);
        var values = new List<OperationalAnomaly>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var type = reader.GetString(1);
            values.Add(new(
                reader.GetString(0), type,
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                Parse(reader.GetString(8)),
                reader.GetString(9),
                OperationalAnomalyService.Message(type)));
        }
        return values;
    }

    private static object Db(string? value) => value is null ? DBNull.Value : value;
    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();
}
