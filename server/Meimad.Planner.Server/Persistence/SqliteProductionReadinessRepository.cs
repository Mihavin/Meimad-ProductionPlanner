using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.MachineAssignments;
using Meimad.Planner.Server.Application.Readiness;
using Meimad.Planner.Server.Domain.Readiness;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteProductionReadinessRepository(SqliteDatabase database)
    : IProductionReadinessRepository
{
    public async Task<ProductionReadinessResult> ReadAsync(
        string batchOperationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var context = await SqliteProductionReadinessContextReader.ReadAsync(
            connection, transaction, batchOperationId, cancellationToken)
            ?? throw new BatchOperationNotFoundException(batchOperationId);
        await transaction.CommitAsync(cancellationToken);
        return ProductionReadinessEvaluator.Evaluate(context);
    }

    public async Task<ProductionReadinessResult> UpdateInputsAsync(
        string batchOperationId,
        ProductionReadinessInputUpdate update,
        DateTimeOffset now,
        EditAuthority authority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAuthorityAsync(
            connection, transaction, authority, cancellationToken);
        var context = await SqliteProductionReadinessContextReader.ReadAsync(
            connection, transaction, batchOperationId, cancellationToken)
            ?? throw new BatchOperationNotFoundException(batchOperationId);
        var beforeReadiness = ProductionReadinessEvaluator.Evaluate(context);

        if (context.MachineAssignmentId is null || context.MachineId is null)
        {
            if (update.SelectedGCodeReleaseId is not null
                || update.ToolOffsetStatus != ReadinessStates.Unverified)
            {
                throw new ProductionReadinessValidationException(
                    "machineAssignment", "assignment_required",
                    "G-code selection and tool-offset status require a Machine assignment.");
            }
        }

        if (update.SelectedGCodeReleaseId is not null)
        {
            var selected = context.Releases.FirstOrDefault(
                release => release.GCodeReleaseId == update.SelectedGCodeReleaseId);
            if (selected is null || selected.ProcessRevisionId != context.ActiveProcessRevisionId)
            {
                throw new ProductionReadinessValidationException(
                    "selectedGCodeReleaseId", "release_not_current",
                    "The selected G-code release must belong to the active process revision.");
            }
            if (!context.SupportedPostprocessorIds.Contains(selected.PostprocessorId))
            {
                throw new ProductionReadinessValidationException(
                    "selectedGCodeReleaseId", "release_incompatible",
                    "The selected G-code release Postprocessor is not supported by the assigned Machine.");
            }
        }

        await UpdateSelectionAsync(connection, transaction, context,
            update.SelectedGCodeReleaseId, now, cancellationToken);
        await UpsertMaterialAsync(connection, transaction, batchOperationId,
            update.MaterialStatus, update.MaterialComment, actor, now, cancellationToken);

        var updatedContext = context with
        {
            SelectedGCodeReleaseId = update.SelectedGCodeReleaseId,
            MaterialStatus = update.MaterialStatus,
            MaterialComment = update.MaterialComment
        };
        var offsetRecorded = update.ToolOffsetStatus != ReadinessStates.Unverified
            || !string.IsNullOrWhiteSpace(update.ToolOffsetComment);
        if (offsetRecorded)
        {
            await InsertOffsetRecordAsync(connection, transaction, updatedContext,
                update.ToolOffsetStatus, update.ToolOffsetComment, actor, now, cancellationToken);
        }

        if (context.MaterialStatus != update.MaterialStatus
            || !string.Equals(context.MaterialComment, update.MaterialComment, StringComparison.Ordinal))
        {
            await SqliteStructuredEventLogRepository.AppendAsync(
                connection,
                transaction,
                new(
                    "material_readiness_changed",
                    now,
                    actor,
                    new Dictionary<string, string> { ["batchOperationId"] = batchOperationId },
                    "physical_verification",
                    update.MaterialComment,
                    new { status = context.MaterialStatus, comment = context.MaterialComment },
                    new { status = update.MaterialStatus, comment = update.MaterialComment }),
                cancellationToken);
        }

        if (offsetRecorded)
        {
            await SqliteStructuredEventLogRepository.AppendAsync(
                connection,
                transaction,
                new(
                    "tool_offsets_confirmation_recorded",
                    now,
                    actor,
                    ReadinessEntities(updatedContext),
                    "physical_verification",
                    update.ToolOffsetComment,
                    null,
                    new
                    {
                        status = update.ToolOffsetStatus,
                        gcodeReleaseId = ProductionReadinessEvaluator.Evaluate(updatedContext)
                            .EffectiveGCodeReleaseId
                    }),
                cancellationToken);
        }

        var finalContext = await SqliteProductionReadinessContextReader.ReadAsync(
            connection, transaction, batchOperationId, cancellationToken)
            ?? throw new BatchOperationNotFoundException(batchOperationId);
        var finalReadiness = ProductionReadinessEvaluator.Evaluate(finalContext);
        await SqliteReadinessAudit.AppendEvaluationAsync(
            connection, transaction, finalContext, beforeReadiness, finalReadiness,
            now, actor, "readiness_inputs_changed", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return finalReadiness;
    }

    private static async Task UpdateSelectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionReadinessContext context,
        string? selectedReleaseId,
        DateTimeOffset now,
        CancellationToken token)
    {
        if (context.MachineAssignmentId is null) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE machine_assignments
            SET selected_gcode_release_id = $releaseId,
                version = version + CASE
                    WHEN selected_gcode_release_id IS $releaseId THEN 0 ELSE 1 END,
                updated_at = CASE
                    WHEN selected_gcode_release_id IS $releaseId THEN updated_at ELSE $updatedAt END
            WHERE id = $assignmentId;
            """;
        command.Parameters.AddWithValue("$releaseId", (object?)selectedReleaseId ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", Iso(now));
        command.Parameters.AddWithValue("$assignmentId", context.MachineAssignmentId);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task UpsertMaterialAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchOperationId,
        string status,
        string? comment,
        string actor,
        DateTimeOffset now,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO batch_operation_material_readiness (
                batch_operation_id, status, confirmed_at, confirmed_by,
                comment, version, updated_at)
            VALUES ($id, $status, $confirmedAt, $confirmedBy, $comment, 1, $updatedAt)
            ON CONFLICT(batch_operation_id) DO UPDATE SET
                status = excluded.status,
                confirmed_at = excluded.confirmed_at,
                confirmed_by = excluded.confirmed_by,
                comment = excluded.comment,
                version = batch_operation_material_readiness.version + 1,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", batchOperationId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$confirmedAt",
            status == ReadinessStates.Ready ? Iso(now) : DBNull.Value);
        command.Parameters.AddWithValue("$confirmedBy",
            status == ReadinessStates.Ready ? actor : DBNull.Value);
        command.Parameters.AddWithValue("$comment", (object?)comment ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", Iso(now));
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task InsertOffsetRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionReadinessContext context,
        string status,
        string? comment,
        string actor,
        DateTimeOffset now,
        CancellationToken token)
    {
        if (context.MachineId is null || context.ActiveProcessRevisionId is null)
        {
            throw new ProductionReadinessValidationException(
                "toolOffsetStatus", "production_context_required",
                "Tool-offset status requires an assigned Machine and active process revision.");
        }

        var evaluation = ProductionReadinessEvaluator.Evaluate(context);
        var gcodeId = string.Equals(context.ExecutionMode, "MANUAL", StringComparison.Ordinal)
            ? null
            : evaluation.EffectiveGCodeReleaseId;
        if (!string.Equals(context.ExecutionMode, "MANUAL", StringComparison.Ordinal)
            && gcodeId is null)
        {
            throw new ProductionReadinessValidationException(
                "toolOffsetStatus", "gcode_selection_required",
                "Select one current compatible G-code release before recording tool-offset readiness.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tool_offset_readiness_records (
                id, batch_operation_id, machine_id, process_revision_id,
                gcode_release_id, status, confirmed_at, confirmed_by,
                comment, recorded_at)
            VALUES ($id, $batchOperationId, $machineId, $processId,
                    $gcodeId, $status, $confirmedAt, $confirmedBy,
                    $comment, $recordedAt);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$batchOperationId", context.BatchOperationId);
        command.Parameters.AddWithValue("$machineId", context.MachineId);
        command.Parameters.AddWithValue("$processId", context.ActiveProcessRevisionId);
        command.Parameters.AddWithValue("$gcodeId", (object?)gcodeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$confirmedAt",
            status == ReadinessStates.Ready ? Iso(now) : DBNull.Value);
        command.Parameters.AddWithValue("$confirmedBy",
            status == ReadinessStates.Ready ? actor : DBNull.Value);
        command.Parameters.AddWithValue("$comment", (object?)comment ?? DBNull.Value);
        command.Parameters.AddWithValue("$recordedAt", Iso(now));
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<string> EnsureEditAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EditAuthority authority,
        CancellationToken token)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(
            connection, transaction, DateTimeOffset.UtcNow, token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, holder_user_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0))
        {
            throw new EditModeMutationException(
                "edit_mode_required", "No Windows client currently holds Edit Mode.");
        }
        if (!string.Equals(reader.GetString(0), authority.ClientId, StringComparison.Ordinal)
            || reader.GetInt64(2) != authority.Generation)
        {
            throw new EditModeMutationException(
                "edit_generation_stale", "This client does not hold the active Edit Mode generation.");
        }
        return reader.IsDBNull(1) ? authority.ClientId : reader.GetString(1);
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static Dictionary<string, string> ReadinessEntities(
        ProductionReadinessContext context)
    {
        var values = new Dictionary<string, string>
        {
            ["batchOperationId"] = context.BatchOperationId
        };
        if (context.MachineId is not null) values["machineId"] = context.MachineId;
        if (context.ActiveProcessRevisionId is not null)
            values["processRevisionId"] = context.ActiveProcessRevisionId;
        return values;
    }
}
