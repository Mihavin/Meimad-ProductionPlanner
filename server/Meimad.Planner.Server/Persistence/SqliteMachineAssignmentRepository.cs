using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.EventLogging;
using Meimad.Planner.Server.Application.MachineAssignments;
using Meimad.Planner.Server.Domain.GCode;
using Meimad.Planner.Server.Domain.Machines;
using Meimad.Planner.Server.Domain.ProductionBatches;
using Meimad.Planner.Server.Domain.Readiness;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteMachineAssignmentRepository : IMachineAssignmentRepository
{
    private readonly SqliteDatabase database;

    public SqliteMachineAssignmentRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<AssignmentMutationResult> AssignOrMoveAsync(
        string batchOperationId,
        string machineId,
        int backlogPosition,
        MachineAssignmentOverrideConfirmation? overrideConfirmation,
        DateTimeOffset now,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var confirmedByUserId = await EnsureEditAuthorityAsync(
            connection, transaction, editAuthority, cancellationToken);
        var requiredMachineType = await ReadRequiredMachineTypeAsync(
            connection,
            transaction,
            batchOperationId,
            cancellationToken);
        var targetMachine = await ReadMachineAsync(
            connection,
            transaction,
            machineId,
            cancellationToken)
            ?? throw new AssignmentMachineNotFoundException(machineId);
        var requiresOverride = targetMachine.IsActive
            && !string.IsNullOrWhiteSpace(requiredMachineType)
            && !string.Equals(
                targetMachine.ProcessType,
                requiredMachineType.Trim(),
                StringComparison.OrdinalIgnoreCase);
        if (!targetMachine.IsActive)
        {
            throw new IncompatibleMachineException(batchOperationId, machineId);
        }

        if (requiresOverride && overrideConfirmation is null)
        {
            throw new MachineAssignmentOverrideRequiredException(
                batchOperationId,
                machineId,
                requiredMachineType!,
                targetMachine.ProcessType);
        }

        var current = await ReadAssignmentForOperationAsync(
            connection,
            transaction,
            batchOperationId,
            cancellationToken);
        var targetOriginal = await ReadAssignmentsForMachineAsync(
            connection,
            transaction,
            machineId,
            cancellationToken);
        var sameMachine = current is not null
            && string.Equals(current.MachineId, machineId, StringComparison.Ordinal);
        var maximumPosition = sameMachine ? targetOriginal.Count - 1 : targetOriginal.Count;
        if (backlogPosition > maximumPosition)
        {
            throw new BacklogPositionOutOfRangeException(backlogPosition, maximumPosition);
        }

        IReadOnlyList<MachineAssignment> sourceOriginal = [];
        if (current is not null && !sameMachine)
        {
            sourceOriginal = await ReadAssignmentsForMachineAsync(
                connection,
                transaction,
                current.MachineId,
                cancellationToken);
        }

        var targetFinal = targetOriginal
            .Where(assignment => assignment.MachineAssignmentId != current?.MachineAssignmentId)
            .ToList();
        var selected = current ?? new MachineAssignment(
            Guid.NewGuid().ToString("N"),
            batchOperationId,
            machineId,
            backlogPosition,
            MachineAssignmentPlanningMode.Manual,
            1,
            now,
            now);
        targetFinal.Insert(backlogPosition, selected with { MachineId = machineId });
        var sourceFinal = sourceOriginal
            .Where(assignment => assignment.MachineAssignmentId != current?.MachineAssignmentId)
            .ToList();

        await EnsureRunningOperationRemainsFirstAsync(
            connection,
            transaction,
            machineId,
            targetFinal[0].BatchOperationId,
            cancellationToken);

        var originalAssignments = targetOriginal
            .Concat(sourceOriginal)
            .ToDictionary(assignment => assignment.MachineAssignmentId, StringComparer.Ordinal);
        await StageBacklogAsync(connection, transaction, targetOriginal, cancellationToken);
        if (sourceOriginal.Count > 0)
        {
            await StageBacklogAsync(connection, transaction, sourceOriginal, cancellationToken);
        }

        var persisted = await WriteFinalBacklogsAsync(
            connection,
            transaction,
            sourceFinal,
            targetFinal,
            originalAssignments,
            selected.MachineAssignmentId,
            now,
            cancellationToken);
        if (requiresOverride)
        {
            await InsertOverrideLogAsync(
                connection,
                transaction,
                batchOperationId,
                targetMachine,
                requiredMachineType!,
                overrideConfirmation!,
                editAuthority,
                confirmedByUserId,
                now,
                cancellationToken);
            await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction, new(
                "cross_machine_type_override", now, confirmedByUserId,
                new Dictionary<string,string> { ["batchOperationId"]=batchOperationId,["machineId"]=machineId },
                "machine_type_incompatible", overrideConfirmation!.Reason,
                new { requiredMachineType }, new { selectedMachineType=targetMachine.ProcessType }), cancellationToken);
        }
        if (current is not null && (current.MachineId != machineId || current.BacklogPosition != backlogPosition))
            await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction, new(
                "manual_backlog_reorder", now, confirmedByUserId,
                new Dictionary<string,string> { ["batchOperationId"]=batchOperationId,["machineId"]=machineId },
                null, null,
                new { machineId=current.MachineId,backlogPosition=current.BacklogPosition },
                new { machineId,backlogPosition }), cancellationToken);
        if (current is null || !string.Equals(current.MachineId, machineId, StringComparison.Ordinal))
        {
            await SqliteNcCycleEstimateStore.RecalculateForMachineAsync(
                connection, transaction, machineId, now,
                confirmedByUserId, "machine_assignment_changed", cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new AssignmentMutationResult(persisted, current is null);
    }

    public async Task<IReadOnlyList<MachineAssignmentOverrideLog>> ListOverridesAsync(
        string batchOperationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, batch_operation_id, machine_id, required_machine_type,
                   selected_machine_type, reason, confirmed_by_client_id,
                   confirmed_by_user_id, confirmed_at
            FROM machine_assignment_overrides
            WHERE batch_operation_id = $batchOperationId
            ORDER BY confirmed_at, id;
            """;
        command.Parameters.AddWithValue("$batchOperationId", batchOperationId);
        var values = new List<MachineAssignmentOverrideLog>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new MachineAssignmentOverrideLog(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                ParseInstant(reader.GetString(8))));
        }

        return values;
    }

    public async Task<bool> UnassignAsync(
        string batchOperationId,
        DateTimeOffset now,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        var current = await ReadAssignmentForOperationAsync(
            connection,
            transaction,
            batchOperationId,
            cancellationToken);
        if (current is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        await EnsureAssignmentMayChangeAsync(
            connection, transaction, batchOperationId, cancellationToken);

        var original = await ReadAssignmentsForMachineAsync(
            connection,
            transaction,
            current.MachineId,
            cancellationToken);
        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM machine_assignments WHERE id = $id;";
            deleteCommand.Parameters.AddWithValue("$id", current.MachineAssignmentId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var remaining = original
            .Where(assignment => assignment.MachineAssignmentId != current.MachineAssignmentId)
            .ToList();
        await StageBacklogAsync(connection, transaction, remaining, cancellationToken);
        var originalById = remaining.ToDictionary(
            assignment => assignment.MachineAssignmentId,
            StringComparer.Ordinal);
        await WriteFinalBacklogsAsync(
            connection,
            transaction,
            [],
            remaining,
            originalById,
            string.Empty,
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<MachineBacklogItem>> GetBacklogAsync(
        string machineId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT machine_assignments.id,
                   machine_assignments.batch_operation_id,
                   machine_assignments.machine_id,
                   machine_assignments.backlog_position,
                   machine_assignments.version,
                   machine_assignments.created_at,
                   machine_assignments.updated_at,
                   machine_assignments.planning_mode,
                   batch_operations.production_batch_id,
                   batch_operations.operation_number,
                   batch_operations.name,
                   batch_operations.required_machine_type,
                   batch_operations.actual_start,
                   batch_operations.actual_end,
                   batch_operations.actual_machine_id
            FROM machine_assignments
            JOIN batch_operations
              ON batch_operations.id = machine_assignments.batch_operation_id
            WHERE machine_assignments.machine_id = $machineId
            ORDER BY machine_assignments.backlog_position;
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        var items = new List<MachineBacklogItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new MachineBacklogItem(
                ReadAssignment(reader),
                reader.GetString(8),
                reader.GetInt32(9),
                reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : ParseInstant(reader.GetString(12)),
                reader.IsDBNull(13) ? null : ParseInstant(reader.GetString(13)),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
        }

        return items;
    }

    public async Task<MachineAssignmentPlanningModeMutationResult> ChangePlanningModeAsync(
        string machineAssignmentId,
        int expectedVersion,
        MachineAssignmentPlanningMode planningMode,
        DateTimeOffset now,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAuthorityAsync(
            connection, transaction, editAuthority, cancellationToken);

        MachineAssignment assignment;
        string operationStatus;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT machine_assignments.id,
                       machine_assignments.batch_operation_id,
                       machine_assignments.machine_id,
                       machine_assignments.backlog_position,
                       machine_assignments.version,
                       machine_assignments.created_at,
                       machine_assignments.updated_at,
                       machine_assignments.planning_mode,
                       batch_operations.status
                FROM machine_assignments
                JOIN batch_operations
                  ON batch_operations.id = machine_assignments.batch_operation_id
                WHERE machine_assignments.id = $assignmentId;
                """;
            read.Parameters.AddWithValue("$assignmentId", machineAssignmentId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new MachineAssignmentNotFoundException(machineAssignmentId);
            }

            assignment = ReadAssignment(reader);
            operationStatus = reader.GetString(8);
        }

        if (assignment.Version != expectedVersion)
        {
            throw new MachineAssignmentVersionConflictException(
                machineAssignmentId, expectedVersion);
        }

        if (assignment.PlanningMode == planningMode)
        {
            await transaction.CommitAsync(cancellationToken);
            return new MachineAssignmentPlanningModeMutationResult(assignment, Changed: false);
        }

        if (operationStatus == "in_progress")
        {
            throw new RunningMachineAssignmentPlanningModeException(machineAssignmentId);
        }

        var updated = assignment with
        {
            PlanningMode = planningMode,
            Version = assignment.Version + 1,
            UpdatedAt = now
        };
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE machine_assignments
                SET planning_mode = $planningMode,
                    version = version + 1,
                    updated_at = $updatedAt
                WHERE id = $assignmentId AND version = $expectedVersion;
                """;
            update.Parameters.AddWithValue("$planningMode", planningMode.ToToken());
            update.Parameters.AddWithValue("$updatedAt", FormatInstant(now));
            update.Parameters.AddWithValue("$assignmentId", machineAssignmentId);
            update.Parameters.AddWithValue("$expectedVersion", expectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new MachineAssignmentVersionConflictException(
                    machineAssignmentId, expectedVersion);
            }
        }

        await SqliteStructuredEventLogRepository.AppendAsync(
            connection,
            transaction,
            new(
                "machine_assignment_planning_mode_changed",
                now,
                actor,
                new Dictionary<string, string>
                {
                    ["machineAssignmentId"] = assignment.MachineAssignmentId,
                    ["batchOperationId"] = assignment.BatchOperationId,
                    ["machineId"] = assignment.MachineId
                },
                "planner_selected",
                null,
                new { planningMode = assignment.PlanningMode.ToToken() },
                new { planningMode = updated.PlanningMode.ToToken() }),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new MachineAssignmentPlanningModeMutationResult(updated, Changed: true);
    }

    public async Task<BatchOperationExecutionResult> ChangeExecutionStatusAsync(
        string batchOperationId,
        BatchOperationExecutionAction action,
        OperationPauseReason? pauseReason,
        DateTimeOffset now,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var pausedBy = await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        var execution = await ReadExecutionStateAsync(
            connection, transaction, batchOperationId, cancellationToken)
            ?? throw new BatchOperationNotFoundException(batchOperationId);
        if (execution.AssignmentId is null || execution.MachineId is null
            || !execution.BacklogPosition.HasValue)
        {
            throw new BatchOperationNotAssignedException(batchOperationId);
        }

        var targetStatus = action switch
        {
            BatchOperationExecutionAction.Start
                when execution.Status is "not_started" or "suspended" => "in_progress",
            BatchOperationExecutionAction.Suspend
                when execution.Status == "in_progress" => "suspended",
            BatchOperationExecutionAction.Finish
                when execution.Status == "in_progress" => "completed",
            BatchOperationExecutionAction.Reset
                when execution.Status == "suspended" => "not_started",
            _ => throw new BatchOperationTransitionException(execution.Status, action)
        };

        var actualStart = action switch
        {
            BatchOperationExecutionAction.Start => execution.ActualStart ?? now,
            BatchOperationExecutionAction.Reset => null,
            _ => execution.ActualStart
        };
        var actualEnd = action switch
        {
            BatchOperationExecutionAction.Finish => now,
            BatchOperationExecutionAction.Reset => null,
            _ => execution.ActualEnd
        };
        var actualMachineId = action switch
        {
            BatchOperationExecutionAction.Start => execution.ActualMachineId ?? execution.MachineId,
            BatchOperationExecutionAction.Reset => null,
            _ => execution.ActualMachineId
        };

        if (action == BatchOperationExecutionAction.Start)
        {
            if (execution.BacklogPosition.Value != 0)
            {
                throw new BatchOperationNotFirstException(batchOperationId);
            }

            await EnsureMachineHasNoRunningOperationAsync(
                connection, transaction, execution.MachineId, batchOperationId, cancellationToken);
        }

        var productionPin = action switch
        {
            BatchOperationExecutionAction.Start when execution.Status == "not_started" =>
                await ResolveProductionPinAsync(
                    connection,
                    transaction,
                    batchOperationId,
                    now,
                    cancellationToken),
            BatchOperationExecutionAction.Reset => null,
            _ => execution.ProductionPin
        };

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE batch_operations
                SET status = $status,
                    actual_start = $actualStart,
                    actual_end = $actualEnd,
                    actual_machine_id = $actualMachineId,
                    production_process_revision_id = $processRevisionId,
                    production_gcode_release_id = $gcodeReleaseId,
                    production_tool_table_release_id = $toolTableReleaseId,
                    production_gcode_file_hash = $gcodeFileHash,
                    production_tool_table_file_hash = $toolTableFileHash,
                    version = version + 1,
                    updated_at = $updatedAt
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$status", targetStatus);
            update.Parameters.Add("$actualStart", SqliteType.Text).Value =
                actualStart.HasValue ? FormatInstant(actualStart.Value) : DBNull.Value;
            update.Parameters.Add("$actualEnd", SqliteType.Text).Value =
                actualEnd.HasValue ? FormatInstant(actualEnd.Value) : DBNull.Value;
            update.Parameters.Add("$actualMachineId", SqliteType.Text).Value =
                actualMachineId is null ? DBNull.Value : actualMachineId;
            update.Parameters.Add("$processRevisionId", SqliteType.Text).Value =
                productionPin?.ProcessRevisionId is null ? DBNull.Value : productionPin.ProcessRevisionId;
            update.Parameters.Add("$gcodeReleaseId", SqliteType.Text).Value =
                productionPin?.GCodeReleaseId is null ? DBNull.Value : productionPin.GCodeReleaseId;
            update.Parameters.Add("$toolTableReleaseId", SqliteType.Text).Value =
                productionPin?.ToolTableReleaseId is null ? DBNull.Value : productionPin.ToolTableReleaseId;
            update.Parameters.Add("$gcodeFileHash", SqliteType.Text).Value =
                productionPin?.GCodeFileHash is null ? DBNull.Value : productionPin.GCodeFileHash;
            update.Parameters.Add("$toolTableFileHash", SqliteType.Text).Value =
                productionPin?.ToolTableFileHash is null ? DBNull.Value : productionPin.ToolTableFileHash;
            update.Parameters.AddWithValue("$updatedAt", FormatInstant(now));
            update.Parameters.AddWithValue("$id", batchOperationId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        if (action == BatchOperationExecutionAction.Suspend)
        {
            await InsertPauseEventAsync(connection, transaction, batchOperationId,
                pauseReason!, pausedBy, now, cancellationToken);
        }
        else if ((action == BatchOperationExecutionAction.Start && execution.Status == "suspended")
                 || action == BatchOperationExecutionAction.Reset)
        {
            await ClosePauseEventAsync(
                connection, transaction, batchOperationId, action, now, cancellationToken);
        }

        if (action == BatchOperationExecutionAction.Finish)
        {
            var original = await ReadAssignmentsForMachineAsync(
                connection, transaction, execution.MachineId, cancellationToken);
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM machine_assignments WHERE id = $id;";
                delete.Parameters.AddWithValue("$id", execution.AssignmentId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            var remaining = original
                .Where(assignment => assignment.MachineAssignmentId != execution.AssignmentId)
                .ToList();
            await StageBacklogAsync(connection, transaction, remaining, cancellationToken);
            await WriteFinalBacklogsAsync(
                connection,
                transaction,
                [],
                remaining,
                remaining.ToDictionary(value => value.MachineAssignmentId, StringComparer.Ordinal),
                string.Empty,
                now,
                cancellationToken);
        }

        await UpdateProductionBatchStatusAsync(
            connection,
            transaction,
            execution.BatchId,
            now,
            cancellationToken);

        await SqliteOrderLifecycle.RecomputeForBatchAsync(
            connection,
            transaction,
            execution.BatchId,
            now,
            cancellationToken);

        var eventType = action switch
        {
            BatchOperationExecutionAction.Start when execution.Status == "suspended" => "operation_resumed",
            BatchOperationExecutionAction.Start => "operation_started",
            BatchOperationExecutionAction.Suspend => "operation_paused",
            BatchOperationExecutionAction.Finish => "operation_finished",
            BatchOperationExecutionAction.Reset => "operation_reset",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction, new(
            eventType, now, pausedBy,
            new Dictionary<string,string> {
                ["batchOperationId"]=batchOperationId,["productionBatchId"]=execution.BatchId,
                ["machineId"]=execution.MachineId },
            pauseReason?.ReasonType, pauseReason?.Comment,
            new
            {
                status = execution.Status,
                actualStart = execution.ActualStart,
                actualEnd = execution.ActualEnd,
                actualMachineId = execution.ActualMachineId,
                productionProcessRevisionId = execution.ProductionPin?.ProcessRevisionId,
                productionGCodeReleaseId = execution.ProductionPin?.GCodeReleaseId,
                productionToolTableReleaseId = execution.ProductionPin?.ToolTableReleaseId,
                productionGCodeFileHash = execution.ProductionPin?.GCodeFileHash,
                productionToolTableFileHash = execution.ProductionPin?.ToolTableFileHash
            },
            new
            {
                status=targetStatus,actualStart,actualEnd,actualMachineId,pauseReason,
                productionProcessRevisionId = productionPin?.ProcessRevisionId,
                productionGCodeReleaseId = productionPin?.GCodeReleaseId,
                productionToolTableReleaseId = productionPin?.ToolTableReleaseId,
                productionGCodeFileHash = productionPin?.GCodeFileHash,
                productionToolTableFileHash = productionPin?.ToolTableFileHash
            }), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new BatchOperationExecutionResult(
            batchOperationId,
            execution.MachineId,
            targetStatus,
            execution.Version + 1,
            actualStart,
            actualEnd,
            actualMachineId);
    }

    private static async Task InsertPauseEventAsync(
        SqliteConnection connection, SqliteTransaction transaction, string operationId,
        OperationPauseReason reason, string pausedBy, DateTimeOffset now, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO operation_pause_events
                (id, batch_operation_id, reason_type, problem_description, tooling_item_description,
                 customer_contact_name, request_description, comment, paused_by,
                 pause_started_at, status, version, created_at, updated_at)
            VALUES ($id, $operationId, $type, $problem, $tooling, $contact, $request,
                    $comment, $pausedBy, $at, 'active', 1, $at, $at);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$operationId", operationId);
        command.Parameters.AddWithValue("$type", reason.ReasonType);
        command.Parameters.AddWithValue("$problem", (object?)reason.ProblemDescription ?? DBNull.Value);
        command.Parameters.AddWithValue("$tooling", (object?)reason.ToolingItemDescription ?? DBNull.Value);
        command.Parameters.AddWithValue("$contact", (object?)reason.CustomerContactName ?? DBNull.Value);
        command.Parameters.AddWithValue("$request", (object?)reason.RequestDescription ?? DBNull.Value);
        command.Parameters.AddWithValue("$comment", (object?)reason.Comment ?? DBNull.Value);
        command.Parameters.AddWithValue("$pausedBy", pausedBy);
        command.Parameters.AddWithValue("$at", FormatInstant(now));
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task ClosePauseEventAsync(
        SqliteConnection connection, SqliteTransaction transaction, string operationId,
        BatchOperationExecutionAction action, DateTimeOffset now, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE operation_pause_events
            SET status = 'closed', pause_ended_at = $at, updated_at = $at, version = version + 1
            WHERE batch_operation_id = $operationId AND status = 'active';
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        command.Parameters.AddWithValue("$at", FormatInstant(now));
        if (await command.ExecuteNonQueryAsync(token) != 1)
        {
            throw new BatchOperationTransitionException("suspended_without_active_pause", action);
        }
    }

    private static async Task<ExecutionState?> ReadExecutionStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchOperationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT batch_operations.status, batch_operations.version,
                   batch_operations.production_batch_id,
                   machine_assignments.id, machine_assignments.machine_id,
                   machine_assignments.backlog_position,
                   batch_operations.actual_start,
                   batch_operations.actual_end,
                   batch_operations.actual_machine_id,
                   batch_operations.source_case_operation_id,
                   batch_operations.production_process_revision_id,
                   batch_operations.production_gcode_release_id,
                   batch_operations.production_tool_table_release_id,
                   batch_operations.production_gcode_file_hash,
                   batch_operations.production_tool_table_file_hash
            FROM batch_operations
            LEFT JOIN machine_assignments
              ON machine_assignments.batch_operation_id = batch_operations.id
            WHERE batch_operations.id = $id;
            """;
        command.Parameters.AddWithValue("$id", batchOperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExecutionState(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.IsDBNull(6) ? null : ParseInstant(reader.GetString(6)),
            reader.IsDBNull(7) ? null : ParseInstant(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9),
            new ProductionPin(
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
    }

    private static async Task<ProductionPin?> ResolveProductionPinAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchOperationId,
        DateTimeOffset now,
        CancellationToken token)
    {
        var context = await SqliteProductionReadinessContextReader.ReadAsync(
            connection, transaction, batchOperationId, token)
            ?? throw new BatchOperationNotFoundException(batchOperationId);
        if (context.ActiveProcessRevisionId is null)
        {
            // Existing Operations without managed release history retain their pre-v35 execution behavior.
            return null;
        }

        var readiness = ProductionReadinessEvaluator.Evaluate(context);
        if (!readiness.IsReadyForProduction)
        {
            var blocker = readiness.Components.First(component => component.IsBlocking);
            throw new ProductionReadinessException(ReadinessErrorCode(context, blocker), blocker.Message);
        }

        if (context.MachineAssignmentId is not null
            && context.SelectedGCodeReleaseId is null
            && readiness.EffectiveGCodeReleaseId is not null)
        {
            await using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = """
                UPDATE machine_assignments
                SET selected_gcode_release_id = $releaseId,
                    version = version + 1,
                    updated_at = $updatedAt
                WHERE id = $assignmentId AND selected_gcode_release_id IS NULL;
                """;
            select.Parameters.AddWithValue("$releaseId", readiness.EffectiveGCodeReleaseId);
            select.Parameters.AddWithValue("$updatedAt", FormatInstant(now));
            select.Parameters.AddWithValue("$assignmentId", context.MachineAssignmentId);
            await select.ExecuteNonQueryAsync(token);
        }

        string toolTableHash;
        await using (var tool = connection.CreateCommand())
        {
            tool.Transaction = transaction;
            tool.CommandText = "SELECT file_hash FROM tool_table_releases WHERE id = $id;";
            tool.Parameters.AddWithValue("$id", context.ActiveToolTableReleaseId!);
            toolTableHash = (string)(await tool.ExecuteScalarAsync(token))!;
        }

        string? gcodeHash = null;
        if (readiness.EffectiveGCodeReleaseId is not null)
        {
            await using var gcode = connection.CreateCommand();
            gcode.Transaction = transaction;
            gcode.CommandText = "SELECT file_hash FROM gcode_releases WHERE id = $id;";
            gcode.Parameters.AddWithValue("$id", readiness.EffectiveGCodeReleaseId);
            gcodeHash = (string)(await gcode.ExecuteScalarAsync(token))!;
        }

        return new ProductionPin(
            context.ActiveProcessRevisionId,
            readiness.EffectiveGCodeReleaseId,
            context.ActiveToolTableReleaseId,
            gcodeHash,
            toolTableHash);
    }

    private static string ReadinessErrorCode(
        ProductionReadinessContext context,
        ReadinessComponent component) => component.Key switch
    {
        ReadinessComponentKeys.GCode when component.State == ReadinessStates.Outdated => "gcode_release_outdated",
        ReadinessComponentKeys.GCode when component.State == ReadinessStates.Incompatible => "gcode_release_incompatible",
        ReadinessComponentKeys.GCode when component.State == ReadinessStates.Blocked => "gcode_release_selection_required",
        ReadinessComponentKeys.GCode => "gcode_release_missing",
        ReadinessComponentKeys.ToolTable when component.State == ReadinessStates.Outdated => "tool_table_outdated",
        ReadinessComponentKeys.ToolTable => "tool_table_missing",
        ReadinessComponentKeys.ToolOffsets when component.State == ReadinessStates.Outdated => "tool_offsets_outdated",
        ReadinessComponentKeys.ToolOffsets when component.State == ReadinessStates.Missing => "tool_offsets_missing",
        ReadinessComponentKeys.ToolOffsets => "tool_offsets_unverified",
        ReadinessComponentKeys.Material when component.State == ReadinessStates.Missing => "material_missing",
        ReadinessComponentKeys.Material => "material_unverified",
        ReadinessComponentKeys.MachinePostprocessorCompatibility => "postprocessor_incompatible",
        ReadinessComponentKeys.ToolCapacity when !context.RequiredToolCount.HasValue => "tool_requirements_unavailable",
        ReadinessComponentKeys.ToolCapacity when !context.UsableToolPositions.HasValue => "machine_capacity_unavailable",
        ReadinessComponentKeys.ToolCapacity => "tool_capacity_mismatch",
        _ => "production_not_ready"
    };

    private static async Task UpdateProductionBatchStatusAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        long total;
        long completed;
        long started;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT COUNT(*),
                       COALESCE(SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END), 0),
                       COALESCE(SUM(CASE WHEN status <> 'not_started' THEN 1 ELSE 0 END), 0)
                FROM batch_operations
                WHERE production_batch_id = $batchId;
                """;
            read.Parameters.AddWithValue("$batchId", batchId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            total = reader.GetInt64(0);
            completed = reader.GetInt64(1);
            started = reader.GetInt64(2);
        }

        var targetStatus = total > 0 && completed == total
            ? ProductionBatchValidator.CompleteStatus
            : started > 0
                ? ProductionBatchValidator.InProductionStatus
                : ProductionBatchValidator.WaitingStatus;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE production_batches
            SET status = $status,
                version = version + 1,
                updated_at = $updatedAt
            WHERE id = $batchId AND status <> $status;
            """;
        update.Parameters.AddWithValue("$status", targetStatus);
        update.Parameters.AddWithValue("$updatedAt", FormatInstant(now));
        update.Parameters.AddWithValue("$batchId", batchId);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureMachineHasNoRunningOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string machineId,
        string exceptBatchOperationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM machine_assignments
                JOIN batch_operations
                  ON batch_operations.id = machine_assignments.batch_operation_id
                WHERE machine_assignments.machine_id = $machineId
                  AND batch_operations.id <> $exceptOperationId
                  AND batch_operations.status = 'in_progress');
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        command.Parameters.AddWithValue("$exceptOperationId", exceptBatchOperationId);
        if (Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture) == 1)
        {
            throw new MachineAlreadyRunningOperationException(machineId);
        }
    }

    private static async Task<MachineAssignment> WriteFinalBacklogsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<MachineAssignment> sourceFinal,
        IReadOnlyList<MachineAssignment> targetFinal,
        IReadOnlyDictionary<string, MachineAssignment> originalAssignments,
        string selectedAssignmentId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        MachineAssignment? selected = null;
        foreach (var entry in sourceFinal.Select((assignment, position) => (assignment, position))
                     .Concat(targetFinal.Select((assignment, position) => (assignment, position))))
        {
            var assignment = entry.assignment with { BacklogPosition = entry.position };
            if (originalAssignments.TryGetValue(assignment.MachineAssignmentId, out var original))
            {
                var changed = !string.Equals(
                        original.MachineId,
                        assignment.MachineId,
                        StringComparison.Ordinal)
                    || original.BacklogPosition != assignment.BacklogPosition;
                assignment = assignment with
                {
                    Version = changed ? original.Version + 1 : original.Version,
                    UpdatedAt = changed ? now : original.UpdatedAt
                };
                await UpdateAssignmentAsync(connection, transaction, assignment, cancellationToken);
            }
            else
            {
                await InsertAssignmentAsync(connection, transaction, assignment, cancellationToken);
            }

            if (assignment.MachineAssignmentId == selectedAssignmentId)
            {
                selected = assignment;
            }
        }

        return selected ?? new MachineAssignment(
            string.Empty,
            string.Empty,
            string.Empty,
            0,
            MachineAssignmentPlanningMode.Manual,
            0,
            default,
            default);
    }

    private static async Task StageBacklogAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<MachineAssignment> assignments,
        CancellationToken cancellationToken)
    {
        if (assignments.Count == 0)
        {
            return;
        }

        var start = assignments.Max(assignment => (long)assignment.BacklogPosition)
            + assignments.Count
            + 1L;
        for (var index = 0; index < assignments.Count; index++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE machine_assignments
                SET backlog_position = $temporaryPosition
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$temporaryPosition", start + index);
            command.Parameters.AddWithValue("$id", assignments[index].MachineAssignmentId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpdateAssignmentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MachineAssignment assignment,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE machine_assignments
            SET machine_id = $machineId,
                backlog_position = $position,
                planning_mode = $planningMode,
                version = $version,
                updated_at = $updatedAt
            WHERE id = $id;
            """;
        AddAssignmentParameters(command, assignment);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAssignmentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MachineAssignment assignment,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO machine_assignments (
                id, batch_operation_id, machine_id, backlog_position,
                planning_mode, version, created_at, updated_at)
            VALUES (
                $id, $operationId, $machineId, $position,
                $planningMode, $version, $createdAt, $updatedAt);
            """;
        AddAssignmentParameters(command, assignment);
        command.Parameters.AddWithValue("$operationId", assignment.BatchOperationId);
        command.Parameters.AddWithValue("$createdAt", FormatInstant(assignment.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddAssignmentParameters(
        SqliteCommand command,
        MachineAssignment assignment)
    {
        command.Parameters.AddWithValue("$id", assignment.MachineAssignmentId);
        command.Parameters.AddWithValue("$machineId", assignment.MachineId);
        command.Parameters.AddWithValue("$position", assignment.BacklogPosition);
        command.Parameters.AddWithValue("$planningMode", assignment.PlanningMode.ToToken());
        command.Parameters.AddWithValue("$version", assignment.Version);
        command.Parameters.AddWithValue("$updatedAt", FormatInstant(assignment.UpdatedAt));
    }

    private static async Task<string?> ReadRequiredMachineTypeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchOperationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT required_machine_type, status FROM batch_operations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", batchOperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new BatchOperationNotFoundException(batchOperationId);
        }

        if (reader.GetString(1) == "completed")
        {
            throw new CompletedBatchOperationCannotBeAssignedException(batchOperationId);
        }

        if (reader.GetString(1) == "in_progress")
        {
            throw new RunningBatchOperationCannotMoveException(batchOperationId);
        }

        return reader.IsDBNull(0) ? null : reader.GetString(0);
    }

    private static async Task EnsureAssignmentMayChangeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchOperationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT status FROM batch_operations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", batchOperationId);
        var status = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (status == "in_progress")
        {
            throw new RunningBatchOperationCannotMoveException(batchOperationId);
        }
    }

    private static async Task EnsureRunningOperationRemainsFirstAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string machineId,
        string firstBatchOperationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT batch_operations.id
            FROM machine_assignments
            INNER JOIN batch_operations
                ON batch_operations.id = machine_assignments.batch_operation_id
            WHERE machine_assignments.machine_id = $machineId
              AND batch_operations.status = 'in_progress'
              AND batch_operations.id <> $firstBatchOperationId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        command.Parameters.AddWithValue("$firstBatchOperationId", firstBatchOperationId);
        var displacedOperationId = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (displacedOperationId is not null)
        {
            throw new RunningBatchOperationCannotMoveException(displacedOperationId);
        }
    }

    private static async Task<Machine?> ReadMachineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string machineId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT machines.id, machines.number, machines.name, machines.machine_type,
                   machines.axis_type, machines.capabilities_json,
                   machines.working_calendar_id, machines.is_active, machines.display_enabled,
                   machines.version, machines.created_at, machines.updated_at,
                   machines.machine_type_id,
                   COALESCE(machine_types.capabilities_json, '[]')
            FROM machines
            LEFT JOIN machine_types ON machine_types.id = machines.machine_type_id
            WHERE machines.id = $id;
            """;
        command.Parameters.AddWithValue("$id", machineId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var capabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? [];
        var typeCapabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(13)) ?? [];
        return new Machine(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            capabilities,
            reader.GetString(6),
            reader.GetBoolean(7),
            reader.GetBoolean(8),
            null,
            0,
            reader.GetInt32(9),
            ParseInstant(reader.GetString(10)),
            ParseInstant(reader.GetString(11)),
            null,
            reader.IsDBNull(12) ? null : reader.GetString(12),
            typeCapabilities);
    }

    private static async Task<MachineAssignment?> ReadAssignmentForOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchOperationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, batch_operation_id, machine_id, backlog_position,
                   version, created_at, updated_at, planning_mode
            FROM machine_assignments
            WHERE batch_operation_id = $operationId;
            """;
        command.Parameters.AddWithValue("$operationId", batchOperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAssignment(reader) : null;
    }

    private static async Task<IReadOnlyList<MachineAssignment>> ReadAssignmentsForMachineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string machineId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, batch_operation_id, machine_id, backlog_position,
                   version, created_at, updated_at, planning_mode
            FROM machine_assignments
            WHERE machine_id = $machineId
            ORDER BY backlog_position;
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        var assignments = new List<MachineAssignment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            assignments.Add(ReadAssignment(reader));
        }

        return assignments;
    }

    private static MachineAssignment ReadAssignment(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt32(3),
        ReadPlanningMode(reader.GetString(7)),
        reader.GetInt32(4),
        ParseInstant(reader.GetString(5)),
        ParseInstant(reader.GetString(6)));

    private static MachineAssignmentPlanningMode ReadPlanningMode(string value) =>
        MachineAssignmentPlanningModes.TryParse(value, out var mode)
            ? mode
            : throw new InvalidDataException(
                $"Stored Machine Assignment planning mode '{value}' is invalid.");

    private static async Task InsertOverrideLogAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchOperationId,
        Machine targetMachine,
        string requiredMachineType,
        MachineAssignmentOverrideConfirmation confirmation,
        EditAuthority editAuthority,
        string confirmedByUserId,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken)
    {
        var instant = FormatInstant(confirmedAt);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO machine_assignment_overrides (
                id, batch_operation_id, machine_id, required_machine_type,
                selected_machine_type, reason, confirmed_by_client_id,
                confirmed_by_user_id, confirmed_at, version, created_at, updated_at)
            VALUES (
                $id, $batchOperationId, $machineId, $requiredMachineType,
                $selectedMachineType, $reason, $confirmedByClientId,
                $confirmedByUserId, $confirmedAt, 1, $confirmedAt, $confirmedAt);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$batchOperationId", batchOperationId);
        command.Parameters.AddWithValue("$machineId", targetMachine.MachineId);
        command.Parameters.AddWithValue("$requiredMachineType", requiredMachineType);
        command.Parameters.AddWithValue("$selectedMachineType", targetMachine.ProcessType);
        command.Parameters.AddWithValue("$reason", confirmation.Reason);
        command.Parameters.AddWithValue("$confirmedByClientId", editAuthority.ClientId);
        command.Parameters.AddWithValue("$confirmedByUserId", confirmedByUserId);
        command.Parameters.AddWithValue("$confirmedAt", instant);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> EnsureEditAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(
            connection,
            transaction,
            DateTimeOffset.UtcNow,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, holder_user_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            throw new EditModeMutationException(
                "edit_mode_required",
                "No Windows client currently holds Edit Mode.");
        }

        if (!string.Equals(reader.GetString(0), editAuthority.ClientId, StringComparison.Ordinal)
            || reader.GetInt64(2) != editAuthority.Generation)
        {
            throw new EditModeMutationException(
                "edit_generation_stale",
                "This client does not hold the active Edit Mode generation.");
        }

        return reader.GetString(1);
    }

    private static string FormatInstant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseInstant(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record ExecutionState(
        string Status,
        int Version,
        string BatchId,
        string? AssignmentId,
        string? MachineId,
        int? BacklogPosition,
        DateTimeOffset? ActualStart,
        DateTimeOffset? ActualEnd,
        string? ActualMachineId,
        string SourceCaseOperationId,
        ProductionPin ProductionPin);

    private sealed record ProductionPin(
        string? ProcessRevisionId,
        string? GCodeReleaseId,
        string? ToolTableReleaseId,
        string? GCodeFileHash,
        string? ToolTableFileHash);
}
