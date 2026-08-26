using System.Globalization;
using Meimad.Planner.Server.Application.ProductionRuns;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteProductionRunDebugTimelineRepository(SqliteDatabase database)
    : IProductionRunDebugTimelineRepository
{
    public async Task<ProductionRunDebugTimelineSource?> ReadAsync(
        string machineId,
        string productionRunId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        string machineNumber;
        string machineName;
        string runStatus;
        await using (var target = connection.CreateCommand())
        {
            target.CommandText = """
                SELECT machine.number,machine.name,run.status
                FROM machines machine
                CROSS JOIN production_runs run
                WHERE machine.id=$machineId AND run.id=$runId
                  AND (EXISTS(
                          SELECT 1 FROM machine_assignments assignment
                          WHERE assignment.machine_id=machine.id
                            AND assignment.production_run_id=run.id)
                       OR EXISTS(
                          SELECT 1 FROM production_run_workflow_events event
                          WHERE event.machine_id=machine.id
                            AND event.production_run_id=run.id));
                """;
            target.Parameters.AddWithValue("$machineId", machineId);
            target.Parameters.AddWithValue("$runId", productionRunId);
            await using var reader = await target.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            machineNumber = reader.GetString(0);
            machineName = reader.GetString(1);
            runStatus = reader.GetString(2);
        }

        var workflowEvents = new List<ProductionRunDebugWorkflowEvidence>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT event.id,event.event_type,event.source,event.source_event_id,
                       event.source_sequence,event.server_received_at,event.machine_timestamp,
                       event.offset_loader_release_id,event.user_id,event.metadata_json,
                       COALESCE(start_outcome.completion_state,boundary_outcome.completion_state,
                           CASE WHEN attempt.id IS NOT NULL THEN 'OPEN' END),
                       CASE WHEN boundary_outcome.completion_state='COMPLETED' THEN 1 ELSE 0 END
                FROM production_run_workflow_events event
                LEFT JOIN production_run_cycle_attempts attempt
                  ON attempt.start_workflow_event_id=event.id
                LEFT JOIN production_run_cycle_attempt_outcomes start_outcome
                  ON start_outcome.attempt_id=attempt.id
                LEFT JOIN production_run_cycle_attempt_outcomes boundary_outcome
                  ON boundary_outcome.outcome_workflow_event_id=event.id
                WHERE event.machine_id=$machineId AND event.production_run_id=$runId
                ORDER BY event.server_received_at DESC,event.id DESC
                LIMIT $limit;
                """;
            query.Parameters.AddWithValue("$machineId", machineId);
            query.Parameters.AddWithValue("$runId", productionRunId);
            query.Parameters.AddWithValue("$limit", limit);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                workflowEvents.Add(new(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    Parse(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : Parse(reader.GetString(6)),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.GetInt32(11) == 1));
            }
        }

        var anomalies = new List<ProductionRunDebugAnomalyEvidence>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT id,anomaly_type,source_event_id,previous_sequence,
                       expected_sequence,received_sequence,detected_at
                FROM production_run_workflow_anomalies
                WHERE machine_id=$machineId AND production_run_id=$runId
                ORDER BY detected_at DESC,id DESC
                LIMIT $limit;
                """;
            query.Parameters.AddWithValue("$machineId", machineId);
            query.Parameters.AddWithValue("$runId", productionRunId);
            query.Parameters.AddWithValue("$limit", limit);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                anomalies.Add(new(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    reader.GetInt64(5),
                    Parse(reader.GetString(6))));
            }
        }

        return new(
            machineId,
            machineNumber,
            machineName,
            productionRunId,
            runStatus,
            workflowEvents,
            anomalies);
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();
}
