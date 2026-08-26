using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Qc;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteQcWorkflowRepository(SqliteDatabase database)
    : IQcWorkflowRepository
{
    public async Task<IReadOnlyList<QcQueueItem>> ListQueueAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = new List<QcQueueItem>();
        await using var query = connection.CreateCommand();
        query.CommandText = """
            WITH latest AS (
                SELECT event.production_run_id,event.machine_id,event.event_type,
                       event.server_received_at,event.id,
                       ROW_NUMBER() OVER (
                           PARTITION BY event.production_run_id
                           ORDER BY event.server_received_at DESC,event.id DESC) AS position
                FROM production_run_workflow_events event)
            SELECT latest.production_run_id,latest.machine_id,
                   machine.number,machine.name,latest.server_received_at
            FROM latest
            JOIN machines machine ON machine.id=latest.machine_id
            WHERE latest.position=1 AND latest.event_type='SEND_TO_QC'
            ORDER BY latest.server_received_at,latest.production_run_id;
            """;
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        var pending = new List<QueueRow>();
        while (await reader.ReadAsync(cancellationToken))
            pending.Add(new(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), Parse(reader.GetString(4))));
        await reader.DisposeAsync();

        foreach (var row in pending)
        {
            var outputs = await ReadOutputsAsync(connection, row.ProductionRunId, cancellationToken);
            var setupist = await ReadSetupistAsync(connection, row.ProductionRunId, cancellationToken);
            rows.Add(new(
                row.ProductionRunId,
                row.MachineId,
                row.MachineNumber,
                row.MachineName,
                outputs.Count == 0
                    ? "Output unavailable"
                    : string.Join(" + ", outputs.Select(value => value.Part).Distinct()),
                outputs.Count == 0
                    ? "Operation unavailable"
                    : string.Join("; ", outputs.Select(value => value.Operation).Distinct()),
                row.ReceivedAt,
                setupist?.Id,
                setupist?.Name));
        }
        return rows;
    }

    public async Task<QcDecisionResult> DecideAsync(
        QcDecisionCommand command,
        string metadataJson,
        EditAuthority authority,
        DateTimeOffset serverReceivedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await ValidateAuthorityAsync(
            connection, transaction, authority, command.UserId, cancellationToken);
        var target = await ReadDecisionTargetAsync(
            connection, transaction, command.ProductionRunId, cancellationToken)
            ?? throw new QcWorkflowNotFoundException(command.ProductionRunId);
        if (target.LatestEventType != "SEND_TO_QC")
            throw new QcWorkflowStateException(
                "PASS or FAIL is allowed only while the Production Run is IN_QC.");

        var eventType = command.Decision == "PASS" ? "QC_PASS" : "QC_FAIL";
        var eventId = Guid.NewGuid().ToString("N");
        var timestamp = serverReceivedAt.ToUniversalTime() > target.LatestEventAt
            ? serverReceivedAt.ToUniversalTime()
            : target.LatestEventAt.AddTicks(1);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO production_run_workflow_events (
                id,production_run_id,machine_id,event_type,source,
                source_event_id,server_received_at,user_id,metadata_json)
            VALUES ($id,$runId,$machineId,$eventType,'WINDOWS_QC',
                    $sourceEventId,$receivedAt,$userId,$metadata);
            """;
        insert.Parameters.AddWithValue("$id", eventId);
        insert.Parameters.AddWithValue("$runId", command.ProductionRunId);
        insert.Parameters.AddWithValue("$machineId", target.MachineId);
        insert.Parameters.AddWithValue("$eventType", eventType);
        insert.Parameters.AddWithValue("$sourceEventId", $"QC:{eventId}");
        insert.Parameters.AddWithValue("$receivedAt", Format(timestamp));
        insert.Parameters.AddWithValue("$userId", command.UserId);
        insert.Parameters.AddWithValue("$metadata", metadataJson);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(
            eventId,
            command.ProductionRunId,
            command.Decision,
            command.Decision == "PASS" ? "READY_FOR_PRODUCTION" : "IN_SETUP_RUN",
            command.UserId,
            command.Reason,
            timestamp,
            command.Decision == "PASS" ? timestamp : null);
    }

    private static async Task<IReadOnlyList<OutputRow>> ReadOutputsAsync(
        SqliteConnection connection,
        string productionRunId,
        CancellationToken cancellationToken)
    {
        var values = new List<OutputRow>();
        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT cases.part_number,
                   printf('OP%02d %s',operation.operation_number,operation.name)
            FROM production_run_programs program
            JOIN production_run_outputs output ON output.production_run_program_id=program.id
            JOIN batch_operations operation ON operation.id=output.batch_operation_id
            JOIN production_batches batch ON batch.id=operation.production_batch_id
            JOIN cases ON cases.id=batch.case_id
            WHERE program.production_run_id=$runId
            ORDER BY program.sequence_position,operation.operation_number,output.id;
            """;
        query.Parameters.AddWithValue("$runId", productionRunId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            values.Add(new(reader.GetString(0), reader.GetString(1)));
        return values;
    }

    private static async Task<SetupistRow?> ReadSetupistAsync(
        SqliteConnection connection,
        string productionRunId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT package.setup_worker_id,
                   NULLIF(trim(COALESCE(package.setup_worker_first_name,'') || ' ' ||
                               COALESCE(package.setup_worker_last_name,'')),'')
            FROM eink_package_revisions package
            WHERE package.batch_operation_id IN (
                SELECT output.batch_operation_id
                FROM production_run_programs program
                JOIN production_run_outputs output
                  ON output.production_run_program_id=program.id
                WHERE program.production_run_id=$runId)
              AND package.setup_worker_id IS NOT NULL
            ORDER BY package.published_at DESC,package.id DESC
            LIMIT 1;
            """;
        query.Parameters.AddWithValue("$runId", productionRunId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1))
            : null;
    }

    private static async Task<DecisionTargetRow?> ReadDecisionTargetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string productionRunId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT event.machine_id,event.event_type,event.server_received_at
            FROM production_runs run
            LEFT JOIN production_run_workflow_events event ON event.id=(
                SELECT latest.id
                FROM production_run_workflow_events latest
                WHERE latest.production_run_id=run.id
                ORDER BY latest.server_received_at DESC,latest.id DESC
                LIMIT 1)
            WHERE run.id=$runId;
            """;
        query.Parameters.AddWithValue("$runId", productionRunId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? DateTimeOffset.MinValue : Parse(reader.GetString(2)))
            : null;
    }

    private static async Task ValidateAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EditAuthority authority,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT holder_client_id,holder_user_id,generation FROM edit_tokens WHERE id=1;";
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || reader.IsDBNull(0)
            || reader.IsDBNull(1)
            || reader.GetString(0) != authority.ClientId
            || reader.GetString(1) != userId
            || reader.GetInt64(2) != authority.Generation)
            throw new EditModeMutationException(
                "edit_authority_required",
                "The active Server Edit Mode generation is required for QC decisions.");
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(
        value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private sealed record QueueRow(
        string ProductionRunId,
        string MachineId,
        string MachineNumber,
        string MachineName,
        DateTimeOffset ReceivedAt);

    private sealed record OutputRow(string Part, string Operation);
    private sealed record SetupistRow(string Id, string? Name);
    private sealed record DecisionTargetRow(
        string MachineId, string? LatestEventType, DateTimeOffset LatestEventAt);
}
