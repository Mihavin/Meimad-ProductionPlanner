using System.Globalization;
using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Domain.CaseOperations;
using Meimad.Planner.Server.Domain.ProductionBatches;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Shared writer for batch_operations snapshot rows. Production Batch creation snapshots the whole
/// Case route through <see cref="InsertAsync"/>, and Case Operation creation appends the new
/// operation to every open Batch through <see cref="AppendToOpenBatchesAsync"/>, so both paths
/// write exactly the same snapshot shape.
/// </summary>
internal static class SqliteBatchOperationRows
{
    internal static async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BatchOperation operation,
        string dependencyType,
        string? predecessorSourceCaseOperationId,
        string? simultaneousGroupKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO batch_operations (
                id, production_batch_id, source_case_operation_id,
                operation_number, route_position, name, required_machine_type,
                setup_seconds, cycle_seconds, status, version, created_at, updated_at,
                dependency_type, predecessor_source_case_operation_id,
                simultaneous_group_key,
                qa_seconds, load_unload_seconds, load_unload_requires_worker,
                automatic_loading, load_unload_every_n_parts, day_shift_only,
                has_external_delay, external_delay_description, external_delay_duration,
                external_delay_duration_unit, external_delay_calendar_id,
                external_delay_respect_master_calendar)
            VALUES (
                $id, $batchId, $sourceId,
                $operationNumber, $routePosition, $name, $requiredMachineType,
                $setupSeconds, $cycleSeconds, $status, $version, $createdAt, $updatedAt,
                $dependencyType, $predecessorSourceId, $simultaneousGroupKey,
                $qaSeconds, $loadUnloadSeconds, $loadUnloadRequiresWorker,
                $automaticLoading, $loadUnloadEveryNParts, $dayShiftOnly,
                $hasExternalDelay, $externalDelayDescription, $externalDelayDuration,
                $externalDelayDurationUnit, $externalDelayCalendarId,
                $externalDelayRespectMasterCalendar);
            """;
        command.Parameters.AddWithValue("$id", operation.BatchOperationId);
        command.Parameters.AddWithValue("$batchId", operation.BatchId);
        command.Parameters.AddWithValue("$sourceId", operation.SourceCaseOperationId);
        command.Parameters.AddWithValue("$operationNumber", operation.OperationNumber);
        command.Parameters.AddWithValue("$routePosition", operation.RoutePosition);
        command.Parameters.AddWithValue("$name", operation.Name);
        command.Parameters.AddWithValue(
            "$requiredMachineType",
            operation.RequiredMachineType is null ? DBNull.Value : operation.RequiredMachineType);
        command.Parameters.AddWithValue(
            "$setupSeconds",
            operation.SetupTimeSeconds.HasValue ? operation.SetupTimeSeconds.Value : DBNull.Value);
        command.Parameters.AddWithValue(
            "$cycleSeconds",
            operation.CycleTimePerPartSeconds.HasValue
                ? operation.CycleTimePerPartSeconds.Value
                : DBNull.Value);
        command.Parameters.AddWithValue("$status", operation.Status);
        command.Parameters.AddWithValue("$version", operation.Version);
        command.Parameters.AddWithValue("$createdAt", FormatInstant(operation.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatInstant(operation.UpdatedAt));
        command.Parameters.AddWithValue("$qaSeconds", operation.QaTimeAfterSetupSeconds);
        command.Parameters.AddWithValue("$loadUnloadSeconds", operation.LoadUnloadTimeSeconds);
        command.Parameters.AddWithValue("$loadUnloadRequiresWorker", operation.LoadUnloadRequiresWorker ? 1 : 0);
        command.Parameters.AddWithValue("$automaticLoading", operation.AutomaticLoading ? 1 : 0);
        command.Parameters.AddWithValue("$loadUnloadEveryNParts", operation.LoadUnloadEveryNParts.HasValue ? operation.LoadUnloadEveryNParts.Value : DBNull.Value);
        command.Parameters.AddWithValue("$dayShiftOnly", operation.DayShiftOnly ? 1 : 0);
        command.Parameters.AddWithValue("$hasExternalDelay", operation.HasExternalDelay ? 1 : 0);
        command.Parameters.AddWithValue("$externalDelayDescription", operation.ExternalDelayDescription is null ? DBNull.Value : operation.ExternalDelayDescription);
        command.Parameters.AddWithValue("$externalDelayDuration", operation.ExternalDelayDuration);
        command.Parameters.AddWithValue("$externalDelayDurationUnit", operation.ExternalDelayDurationUnit);
        command.Parameters.AddWithValue("$externalDelayCalendarId", operation.ExternalDelayCalendarId is null ? DBNull.Value : operation.ExternalDelayCalendarId);
        command.Parameters.AddWithValue("$externalDelayRespectMasterCalendar", operation.RespectMasterCalendar ? 1 : 0);
        command.Parameters.AddWithValue("$dependencyType", dependencyType);
        command.Parameters.AddWithValue(
            "$predecessorSourceId",
            predecessorSourceCaseOperationId is null ? DBNull.Value : predecessorSourceCaseOperationId);
        command.Parameters.AddWithValue(
            "$simultaneousGroupKey",
            simultaneousGroupKey is null ? DBNull.Value : simultaneousGroupKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Appends a newly created Case Operation as a not-started Batch Operation to every Production
    /// Batch of its Case whose status is neither complete nor cancelled, at that Batch's next route
    /// position, bumping each affected Batch version and recording one structured event per Batch.
    /// The append is rejected when an open Batch snapshot still uses the same operation number,
    /// because (production_batch_id, operation_number) is unique; the caller's transaction then rolls
    /// the Case Operation back too. Returns the affected Batch ids in creation order.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> AppendToOpenBatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CaseOperationDetails operation,
        string dependencyStorageToken,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var targets = new List<(string BatchId, string BatchNumber, int NextRoutePosition, bool NumberInUse)>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT batch.id, batch.batch_number,
                       COALESCE((SELECT MAX(route_position) + 1 FROM batch_operations
                                 WHERE production_batch_id = batch.id), 0),
                       EXISTS(SELECT 1 FROM batch_operations
                              WHERE production_batch_id = batch.id
                                AND operation_number = $operationNumber)
                FROM production_batches batch
                WHERE batch.case_id = $caseId
                  AND batch.status NOT IN ('complete', 'cancelled')
                ORDER BY batch.created_at, batch.id;
                """;
            read.Parameters.AddWithValue("$caseId", operation.CaseId);
            read.Parameters.AddWithValue("$operationNumber", operation.OperationNumber);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                targets.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3) == 1));
            }
        }

        var conflicts = targets
            .Where(target => target.NumberInUse)
            .Select(target => $"'{target.BatchNumber}'")
            .ToArray();
        if (conflicts.Length > 0)
        {
            throw new CaseOperationValidationException(
            [
                new(
                    "operationNumber",
                    "batch_operation_number_in_use",
                    $"Operation number {operation.OperationNumber} is still used by the route snapshot of open Production Batch {string.Join(", ", conflicts)}; choose another number.")
            ]);
        }

        var at = FormatInstant(now);
        var appended = new List<string>(targets.Count);
        foreach (var target in targets)
        {
            var snapshot = new BatchOperation(
                Guid.NewGuid().ToString("N"),
                target.BatchId,
                operation.CaseOperationId,
                operation.OperationNumber,
                target.NextRoutePosition,
                operation.Name,
                operation.RequiredMachineType,
                operation.SetupTimeSeconds,
                operation.CycleTimePerPartSeconds,
                ProductionBatchValidator.BatchOperationNotStartedStatus,
                1,
                now,
                now,
                operation.QaTimeAfterSetupSeconds,
                operation.LoadUnloadTimeSeconds,
                operation.LoadUnloadRequiresWorker,
                operation.AutomaticLoading,
                operation.LoadUnloadEveryNParts,
                operation.DayShiftOnly,
                HasExternalDelay: operation.HasExternalDelay,
                ExternalDelayDescription: operation.ExternalDelayDescription,
                ExternalDelayDuration: operation.ExternalDelayDuration,
                ExternalDelayDurationUnit: operation.ExternalDelayDurationUnit,
                ExternalDelayCalendarId: operation.ExternalDelayCalendarId,
                RespectMasterCalendar: operation.RespectMasterCalendar);
            await InsertAsync(
                connection,
                transaction,
                snapshot,
                dependencyStorageToken,
                operation.PredecessorCaseOperationId,
                operation.SimultaneousGroupKey,
                cancellationToken);

            await using (var bump = connection.CreateCommand())
            {
                bump.Transaction = transaction;
                bump.CommandText = """
                    UPDATE production_batches
                    SET version = version + 1, updated_at = $at
                    WHERE id = $batchId;
                    """;
                bump.Parameters.AddWithValue("$batchId", target.BatchId);
                bump.Parameters.AddWithValue("$at", at);
                await bump.ExecuteNonQueryAsync(cancellationToken);
            }

            await SqliteStructuredEventLogRepository.AppendAsync(
                connection,
                transaction,
                new(
                    "production_batch_route_appended",
                    now,
                    actor,
                    new Dictionary<string, string>
                    {
                        ["productionBatchId"] = target.BatchId,
                        ["batchOperationId"] = snapshot.BatchOperationId,
                        ["caseId"] = operation.CaseId,
                        ["caseOperationId"] = operation.CaseOperationId
                    },
                    "CASE_OPERATION_CREATED",
                    null,
                    new { routeOperationCount = target.NextRoutePosition },
                    new
                    {
                        routeOperationCount = target.NextRoutePosition + 1,
                        operationNumber = operation.OperationNumber,
                        routePosition = target.NextRoutePosition,
                        status = ProductionBatchValidator.BatchOperationNotStartedStatus
                    }),
                cancellationToken);
            appended.Add(target.BatchId);
        }

        return appended;
    }

    private static string FormatInstant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
