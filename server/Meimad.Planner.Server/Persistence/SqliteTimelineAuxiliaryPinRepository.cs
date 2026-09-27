using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Timeline;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteTimelineAuxiliaryPinRepository(SqliteDatabase database) : ITimelineAuxiliaryPinRepository
{
    public async Task<TimelineSourceAuxiliaryPin> SetAsync(
        TimelineAuxiliaryPin pin, EditAuthority authority, string? userId, CancellationToken cancellationToken)
    {
        if (pin.WorkstationId is null && pin.EmployeeId is null && !pin.PinStart)
            throw new TimelineAuxiliaryPinException("auxiliary_pin_empty", "A pin fixes a Workstation, an Employee, or the start.");
        if (pin.PlannedEndsAt < pin.PlannedStartsAt)
            throw new TimelineAuxiliaryPinException("auxiliary_pin_invalid", "The planned end must not precede the planned start.");
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, cancellationToken);
        await EnsureExistsAsync(connection, transaction, "batch_operations", pin.BatchOperationId, "The Batch Operation does not exist.", cancellationToken);
        await EnsureExistsAsync(connection, transaction, "operation_resource_requirements", pin.RequirementId, "The resource requirement does not exist.", cancellationToken);
        if (pin.WorkstationId is not null)
            await EnsureExistsAsync(connection, transaction, "workstations", pin.WorkstationId, "The Workstation does not exist.", cancellationToken);
        if (pin.EmployeeId is not null)
            await EnsureExistsAsync(connection, transaction, "employee_resources", pin.EmployeeId, "The Employee does not exist.", cancellationToken);
        await DeletePinAsync(connection, transaction, pin.BatchOperationId, pin.RequirementId, cancellationToken);

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var workId = Guid.NewGuid().ToString("N");
        var duration = (int)Math.Max(0, (pin.PlannedEndsAt - pin.PlannedStartsAt).TotalSeconds);
        await using (var work = connection.CreateCommand())
        {
            work.Transaction = transaction;
            work.CommandText = """
                INSERT INTO resource_schedule_work (id, batch_operation_id, requirement_id, requested_starts_at,
                    planned_duration_seconds, state, version, created_at, updated_at)
                VALUES ($id, $operation, $requirement, $requested, $duration, 'PINNED', 1, $now, $now);
                """;
            work.Parameters.AddWithValue("$id", workId);
            work.Parameters.AddWithValue("$operation", pin.BatchOperationId);
            work.Parameters.AddWithValue("$requirement", pin.RequirementId);
            work.Parameters.AddWithValue("$requested", pin.PinStart ? pin.PlannedStartsAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
            work.Parameters.AddWithValue("$duration", duration);
            work.Parameters.AddWithValue("$now", now);
            await work.ExecuteNonQueryAsync(cancellationToken);
        }
        var reason = pin.Reason ?? "Planner pin";
        if (pin.WorkstationId is not null)
            await InsertAssignmentAsync(connection, transaction, workId, "WORKSTATION", "workstation_id", pin.WorkstationId, pin, duration, userId ?? authority.ClientId, reason, now, cancellationToken);
        if (pin.EmployeeId is not null)
            await InsertAssignmentAsync(connection, transaction, workId, "EMPLOYEE", "employee_resource_id", pin.EmployeeId, pin, duration, userId ?? authority.ClientId, reason, now, cancellationToken);
        if (pin.WorkstationId is null && pin.EmployeeId is null)
        {
            // A start-only pin still needs a row for the assignment ledger; the requirement's
            // class decides the placeholder resource column, so the work row alone carries it.
        }
        await transaction.CommitAsync(cancellationToken);
        return new TimelineSourceAuxiliaryPin(pin.BatchOperationId, pin.RequirementId, pin.WorkstationId, pin.EmployeeId,
            pin.PinStart ? pin.PlannedStartsAt.ToUniversalTime() : null);
    }

    public async Task<bool> ClearAsync(string batchOperationId, string requirementId, EditAuthority authority, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, cancellationToken);
        var removed = await DeletePinAsync(connection, transaction, batchOperationId, requirementId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return removed > 0;
    }

    internal static async Task<IReadOnlyList<TimelineSourceAuxiliaryPin>> ReadPinsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT work.batch_operation_id, work.requirement_id, work.requested_starts_at,
                   (SELECT workstation_id FROM resource_schedule_assignments a WHERE a.schedule_work_id = work.id AND a.resource_class = 'WORKSTATION' AND a.is_pinned = 1 ORDER BY a.created_at DESC LIMIT 1),
                   (SELECT employee_resource_id FROM resource_schedule_assignments a WHERE a.schedule_work_id = work.id AND a.resource_class = 'EMPLOYEE' AND a.is_pinned = 1 ORDER BY a.created_at DESC LIMIT 1)
            FROM resource_schedule_work work
            WHERE work.state = 'PINNED'
            ORDER BY work.batch_operation_id, work.requirement_id;
            """;
        var values = new List<TimelineSourceAuxiliaryPin>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new TimelineSourceAuxiliaryPin(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime()));
        }
        return values;
    }

    private static async Task InsertAssignmentAsync(
        SqliteConnection connection, SqliteTransaction transaction, string workId, string resourceClass, string column,
        string resourceId, TimelineAuxiliaryPin pin, int duration, string assignedBy, string reason, string now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO resource_schedule_assignments (id, schedule_work_id, resource_class, {column},
                planned_starts_at, planned_ends_at, planned_duration_seconds, is_pinned, assigned_by, assignment_reason, created_at)
            VALUES ($id, $work, $class, $resource, $starts, $ends, $duration, 1, $by, $reason, $now);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$work", workId);
        command.Parameters.AddWithValue("$class", resourceClass);
        command.Parameters.AddWithValue("$resource", resourceId);
        command.Parameters.AddWithValue("$starts", pin.PlannedStartsAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$ends", pin.PlannedEndsAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$duration", duration);
        command.Parameters.AddWithValue("$by", assignedBy);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> DeletePinAsync(
        SqliteConnection connection, SqliteTransaction transaction, string batchOperationId, string requirementId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM external_resource_executions WHERE schedule_work_id IN (
                SELECT id FROM resource_schedule_work WHERE batch_operation_id = $operation AND requirement_id = $requirement AND state = 'PINNED');
            DELETE FROM resource_schedule_assignments WHERE schedule_work_id IN (
                SELECT id FROM resource_schedule_work WHERE batch_operation_id = $operation AND requirement_id = $requirement AND state = 'PINNED');
            DELETE FROM resource_schedule_work WHERE batch_operation_id = $operation AND requirement_id = $requirement AND state = 'PINNED';
            """;
        command.Parameters.AddWithValue("$operation", batchOperationId);
        command.Parameters.AddWithValue("$requirement", requirementId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureExistsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string table, string id, string message,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table} WHERE id = $id);";
        command.Parameters.AddWithValue("$id", id);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
            throw new TimelineAuxiliaryPinException("auxiliary_pin_target_missing", message);
    }

    private static async Task EnsureEditAuthorityAsync(
        SqliteConnection connection, SqliteTransaction transaction, EditAuthority authority, CancellationToken cancellationToken)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(connection, transaction, DateTimeOffset.UtcNow, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
            throw new EditModeMutationException("edit_mode_required", "No Windows client currently holds Edit Mode.");
        if (!string.Equals(reader.GetString(0), authority.ClientId, StringComparison.Ordinal) || reader.GetInt64(1) != authority.Generation)
            throw new EditModeMutationException("edit_generation_stale", "This client does not hold the active Edit Mode generation.");
    }
}
