using System.Globalization;
using Meimad.Planner.Server.Domain.ProductionBatches;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Shared writer for batch_operations snapshot rows. Production Batch creation snapshots the whole
/// Case route through <see cref="InsertAsync"/>, and a pending Work Order that takes a new Case
/// Operation copies it through <see cref="InsertFromCaseOperationAsync"/>, so both paths write the
/// same snapshot shape.
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
    /// Copies a Case Operation into a Work Order as a not-started operation at the given number and
    /// route position: how a pending Work Order takes an operation its Case gained.
    /// </summary>
    internal static async Task InsertFromCaseOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchId,
        string caseOperationId,
        int operationNumber,
        int routePosition,
        DateTimeOffset now,
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
            SELECT $id, $batchId, id,
                   $operationNumber, $routePosition, name, required_machine_type,
                   setup_seconds, cycle_seconds, $status, 1, $now, $now,
                   dependency_type, predecessor_case_operation_id,
                   simultaneous_group_key,
                   qa_seconds, load_unload_seconds, load_unload_requires_worker,
                   automatic_loading, load_unload_every_n_parts, day_shift_only,
                   has_external_delay, external_delay_description, external_delay_duration,
                   external_delay_duration_unit, external_delay_calendar_id,
                   external_delay_respect_master_calendar
            FROM case_operations
            WHERE id = $caseOperationId;
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$batchId", batchId);
        command.Parameters.AddWithValue("$caseOperationId", caseOperationId);
        command.Parameters.AddWithValue("$operationNumber", operationNumber);
        command.Parameters.AddWithValue("$routePosition", routePosition);
        command.Parameters.AddWithValue("$status", ProductionBatchValidator.BatchOperationNotStartedStatus);
        command.Parameters.AddWithValue("$now", FormatInstant(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string FormatInstant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
