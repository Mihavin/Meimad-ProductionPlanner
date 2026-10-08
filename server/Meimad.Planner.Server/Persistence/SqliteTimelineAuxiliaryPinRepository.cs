using System.Globalization;
using System.Text.Json;
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
        if (pin.PlannedEndsAt < pin.PlannedStartsAt || (pin.PlannedEndsAt - pin.PlannedStartsAt).TotalSeconds > int.MaxValue)
            throw new TimelineAuxiliaryPinException("auxiliary_pin_invalid", "The planned end must not precede the planned start.");
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, cancellationToken);
        await CheckVersionAsync(connection, transaction, pin.BatchOperationId, pin.RequirementId, pin.ExpectedVersion, cancellationToken);
        await EnsureExistsAsync(connection, transaction, "batch_operations", pin.BatchOperationId, "The Batch Operation does not exist.", cancellationToken);
        await EnsureExistsAsync(connection, transaction, "operation_resource_requirements", pin.RequirementId, "The resource requirement does not exist.", cancellationToken);
        if (pin.WorkstationId is not null)
            await EnsureExistsAsync(connection, transaction, "workstations", pin.WorkstationId, "The Workstation does not exist.", cancellationToken);
        if (pin.EmployeeId is not null)
            await EnsureExistsAsync(connection, transaction, "employee_resources", pin.EmployeeId, "The Employee does not exist.", cancellationToken);
        await ValidateContextAsync(connection, transaction, pin, cancellationToken);
        await DeletePinAsync(connection, transaction, pin.BatchOperationId, pin.RequirementId, cancellationToken);

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await AdvanceVersionAsync(connection, transaction, pin.BatchOperationId, pin.RequirementId, authority, now, cancellationToken);
        var workId = Guid.NewGuid().ToString("N");
        var duration = (int)Math.Max(0, (pin.PlannedEndsAt - pin.PlannedStartsAt).TotalSeconds);
        await using (var work = connection.CreateCommand())
        {
            work.Transaction = transaction;
            work.CommandText = """
                INSERT INTO resource_schedule_work (id, batch_operation_id, requirement_id, requested_starts_at,
                    planned_duration_seconds, state, version, created_at, updated_at)
                VALUES ($id, $operation, $requirement, $requested, $duration, 'PINNED', $version, $now, $now);
                """;
            work.Parameters.AddWithValue("$id", workId);
            work.Parameters.AddWithValue("$operation", pin.BatchOperationId);
            work.Parameters.AddWithValue("$requirement", pin.RequirementId);
            work.Parameters.AddWithValue("$requested", pin.PinStart ? pin.PlannedStartsAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
            work.Parameters.AddWithValue("$duration", duration);
            work.Parameters.AddWithValue("$now", now);
            work.Parameters.AddWithValue("$version", pin.ExpectedVersion + 1);
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
            pin.PinStart ? pin.PlannedStartsAt.ToUniversalTime() : null, pin.ExpectedVersion + 1);
    }

    public async Task<bool> ClearAsync(string batchOperationId, string requirementId, long expectedVersion, EditAuthority authority, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, cancellationToken);
        await CheckVersionAsync(connection, transaction, batchOperationId, requirementId, expectedVersion, cancellationToken);
        var removed = await DeletePinAsync(connection, transaction, batchOperationId, requirementId, cancellationToken);
        if (removed > 0)
            await AdvanceVersionAsync(connection, transaction, batchOperationId, requirementId, authority,
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return removed > 0;
    }

    internal static async Task<IReadOnlyList<TimelineSourceAuxiliaryPin>> ReadPinsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT stamp.batch_operation_id, stamp.requirement_id, work.requested_starts_at,
                   (SELECT workstation_id FROM resource_schedule_assignments a WHERE a.schedule_work_id = work.id AND a.resource_class = 'WORKSTATION' AND a.is_pinned = 1 ORDER BY a.created_at DESC LIMIT 1),
                   (SELECT employee_resource_id FROM resource_schedule_assignments a WHERE a.schedule_work_id = work.id AND a.resource_class = 'EMPLOYEE' AND a.is_pinned = 1 ORDER BY a.created_at DESC LIMIT 1),
                   stamp.version, work.id IS NOT NULL
            FROM auxiliary_pin_versions stamp
            LEFT JOIN resource_schedule_work work ON work.batch_operation_id = stamp.batch_operation_id
                AND work.requirement_id = stamp.requirement_id AND work.state = 'PINNED'
            ORDER BY stamp.batch_operation_id, stamp.requirement_id;
            """;
        var values = new List<TimelineSourceAuxiliaryPin>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new TimelineSourceAuxiliaryPin(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(),
                reader.GetInt64(5), reader.GetBoolean(6)));
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

    private static async Task CheckVersionAsync(SqliteConnection connection, SqliteTransaction transaction,
        string operation, string requirement, long expectedVersion, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT version, updated_by, updated_at,
                EXISTS(SELECT 1 FROM resource_schedule_work WHERE batch_operation_id=$operation AND requirement_id=$requirement AND state='PINNED')
            FROM auxiliary_pin_versions WHERE batch_operation_id=$operation AND requirement_id=$requirement;
            """;
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$requirement", requirement);
        await using var reader = await command.ExecuteReaderAsync(token);
        var exists = await reader.ReadAsync(token);
        var current = exists ? reader.GetInt64(0) : 0;
        if (current != expectedVersion)
            throw new TimelineAuxiliaryPinConflictException(current,
                exists && !reader.IsDBNull(1) ? reader.GetString(1) : null, exists ? reader.GetString(2) : null,
                exists && reader.GetBoolean(3));
    }

    private static async Task AdvanceVersionAsync(SqliteConnection connection, SqliteTransaction transaction,
        string operation, string requirement, EditAuthority authority, string now, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO auxiliary_pin_versions(batch_operation_id, requirement_id, version, updated_by, updated_at)
            VALUES($operation, $requirement, 1, $actor, $now)
            ON CONFLICT(batch_operation_id, requirement_id) DO UPDATE SET
                version=version+1, updated_by=excluded.updated_by, updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$requirement", requirement);
        command.Parameters.AddWithValue("$actor", SignedInActor.Require(authority));
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task ValidateContextAsync(SqliteConnection connection, SqliteTransaction transaction,
        TimelineAuxiliaryPin pin, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT r.resource_class, r.required_skill_id,
                CASE WHEN $station IS NULL THEN 1 ELSE EXISTS(
                    SELECT 1 FROM workstations w JOIN workstation_types t ON t.id=w.workstation_type_id
                    WHERE w.id=$station AND w.is_active=1 AND t.is_active=1
                        AND w.workstation_type_id=r.workstation_type_id AND w.capacity>=r.capacity_required) END,
                CASE WHEN $employee IS NULL THEN 1 ELSE EXISTS(
                    SELECT 1 FROM employee_resources e JOIN employee_skills es ON es.employee_resource_id=e.id
                    JOIN skills s ON s.id=es.skill_id
                    WHERE e.id=$employee AND e.is_active=1 AND s.is_active=1 AND es.skill_id=r.required_skill_id) END,
                r.required_capability, (SELECT capabilities_json FROM workstations WHERE id=$station)
            FROM operation_resource_requirements r JOIN batch_operations op ON op.source_case_operation_id=r.case_operation_id
            WHERE r.id=$requirement AND op.id=$operation AND r.is_active=1;
            """;
        command.Parameters.AddWithValue("$station", (object?)pin.WorkstationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$employee", (object?)pin.EmployeeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$operation", pin.BatchOperationId);
        command.Parameters.AddWithValue("$requirement", pin.RequirementId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            throw new TimelineAuxiliaryPinException("auxiliary_pin_requirement_mismatch", "The active requirement must belong to this Batch Operation's Case Operation.");
        var resourceClass = reader.GetString(0);
        if (resourceClass == "MACHINE" || (pin.WorkstationId is not null && resourceClass != "WORKSTATION")
            || (pin.EmployeeId is not null && (resourceClass is not ("EMPLOYEE" or "WORKSTATION") || reader.IsDBNull(1))))
            throw new TimelineAuxiliaryPinException("auxiliary_pin_resource_class_mismatch", "The selected resource class is not required by this auxiliary step.");
        if (!reader.GetBoolean(2) || !reader.GetBoolean(3))
            throw new TimelineAuxiliaryPinException("auxiliary_pin_resource_ineligible", "The selected resource must be active and satisfy the step's type, capacity, capability and Skill requirements.");
        if (pin.WorkstationId is not null && !reader.IsDBNull(4))
        {
            // Match the allocator's Unicode-aware comparison, including Russian capability names.
            using var capabilities = JsonDocument.Parse(reader.GetString(5));
            if (capabilities.RootElement.ValueKind != JsonValueKind.Array
                || !capabilities.RootElement.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String
                    && string.Equals(value.GetString(), reader.GetString(4), StringComparison.OrdinalIgnoreCase)))
                throw new TimelineAuxiliaryPinException("auxiliary_pin_resource_ineligible", "The selected Workstation does not satisfy the required capability.");
        }
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
        // Single Edit Mode is retired: the API authorized the signed-in user for this change.
        SignedInActor.Require(authority);
        await Task.CompletedTask;
    }
}
