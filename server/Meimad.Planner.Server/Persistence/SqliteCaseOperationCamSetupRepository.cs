using System.Globalization;
using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Domain.Machines;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Stores Operation CAM setups (schema v113). Every write runs in one immediate transaction that
/// checks the Operation belongs to the Case, compares the version, writes the row and appends the
/// audit event; the Case Operation row itself is never touched.
/// </summary>
internal sealed class SqliteCaseOperationCamSetupRepository(SqliteDatabase database) : ICaseOperationCamSetupRepository
{
    private const string SelectColumns = """
        SELECT setup.case_operation_id, operation.case_id, setup.source, setup.work_offset,
               setup.origin_x_mm, setup.origin_y_mm, setup.origin_z_mm,
               setup.rotation_x_deg, setup.rotation_y_deg, setup.rotation_z_deg,
               setup.notes, setup.version, setup.created_at, setup.updated_at, setup.updated_by
        FROM case_operation_cam_setups setup
        JOIN case_operations operation ON operation.id = setup.case_operation_id
        """;

    public async Task<CaseOperationCamSetupRead> GetAsync(string caseId, string caseOperationId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var read = await ReadAsync(connection, transaction, caseId, caseOperationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return read;
    }

    public async Task<CaseOperationCamSetupWriteResult> SaveAsync(
        string caseId,
        string caseOperationId,
        CaseOperationCamSetupValues values,
        int? expectedVersion,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadAsync(connection, transaction, caseId, caseOperationId, cancellationToken);
        if (!current.OperationExists)
            return new(CaseOperationCamSetupOutcome.OperationNotFound, null);
        if (expectedVersion is null && current.Setup is not null)
            return new(CaseOperationCamSetupOutcome.AlreadyExists, current.Setup);
        if (expectedVersion is not null && current.Setup is null)
            return new(CaseOperationCamSetupOutcome.SetupNotFound, null);
        if (expectedVersion is not null && current.Setup!.Version != expectedVersion.Value)
            return new(CaseOperationCamSetupOutcome.Stale, current.Setup);

        var saved = new CaseOperationCamSetup(
            caseOperationId,
            caseId,
            values.Source,
            values.WorkOffset,
            values.OriginMm,
            values.RotationDeg,
            values.Notes,
            (current.Setup?.Version ?? 0) + 1,
            current.Setup?.CreatedAt ?? now,
            now,
            actor);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = current.Setup is null
                ? """
                  INSERT INTO case_operation_cam_setups (
                      case_operation_id, source, work_offset,
                      origin_x_mm, origin_y_mm, origin_z_mm,
                      rotation_x_deg, rotation_y_deg, rotation_z_deg,
                      notes, version, created_at, updated_at, updated_by)
                  VALUES ($id, $source, $workOffset, $ox, $oy, $oz, $rx, $ry, $rz,
                      $notes, $version, $createdAt, $updatedAt, $updatedBy);
                  """
                : """
                  UPDATE case_operation_cam_setups
                  SET source = $source, work_offset = $workOffset,
                      origin_x_mm = $ox, origin_y_mm = $oy, origin_z_mm = $oz,
                      rotation_x_deg = $rx, rotation_y_deg = $ry, rotation_z_deg = $rz,
                      notes = $notes, version = $version, updated_at = $updatedAt, updated_by = $updatedBy
                  WHERE case_operation_id = $id AND version = $expectedVersion;
                  """;
            command.Parameters.AddWithValue("$id", saved.CaseOperationId);
            command.Parameters.AddWithValue("$source", saved.Source);
            command.Parameters.AddWithValue("$workOffset", (object?)saved.WorkOffset ?? DBNull.Value);
            command.Parameters.AddWithValue("$ox", saved.OriginMm.X);
            command.Parameters.AddWithValue("$oy", saved.OriginMm.Y);
            command.Parameters.AddWithValue("$oz", saved.OriginMm.Z);
            command.Parameters.AddWithValue("$rx", saved.RotationDeg.X);
            command.Parameters.AddWithValue("$ry", saved.RotationDeg.Y);
            command.Parameters.AddWithValue("$rz", saved.RotationDeg.Z);
            command.Parameters.AddWithValue("$notes", (object?)saved.Notes ?? DBNull.Value);
            command.Parameters.AddWithValue("$version", saved.Version);
            command.Parameters.AddWithValue("$createdAt", Format(saved.CreatedAt));
            command.Parameters.AddWithValue("$updatedAt", Format(saved.UpdatedAt));
            command.Parameters.AddWithValue("$updatedBy", actor);
            command.Parameters.AddWithValue("$expectedVersion", expectedVersion ?? 0);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                return new(CaseOperationCamSetupOutcome.Stale, current.Setup);
        }

        await SqliteStructuredEventLogRepository.AppendAsync(
            connection,
            transaction,
            new(
                "case_operation_cam_setup_saved",
                now,
                actor,
                RelatedIds(caseId, caseOperationId),
                "planner_entered",
                null,
                current.Setup is null ? null : Data(current.Setup),
                Data(saved)),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(current.Setup is null ? CaseOperationCamSetupOutcome.Created : CaseOperationCamSetupOutcome.Updated, saved);
    }

    public async Task<CaseOperationCamSetupWriteResult> DeleteAsync(
        string caseId,
        string caseOperationId,
        int expectedVersion,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadAsync(connection, transaction, caseId, caseOperationId, cancellationToken);
        if (!current.OperationExists)
            return new(CaseOperationCamSetupOutcome.OperationNotFound, null);
        if (current.Setup is null)
            return new(CaseOperationCamSetupOutcome.SetupNotFound, null);
        if (current.Setup.Version != expectedVersion)
            return new(CaseOperationCamSetupOutcome.Stale, current.Setup);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM case_operation_cam_setups WHERE case_operation_id = $id AND version = $version;";
            command.Parameters.AddWithValue("$id", caseOperationId);
            command.Parameters.AddWithValue("$version", expectedVersion);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                return new(CaseOperationCamSetupOutcome.Stale, current.Setup);
        }

        await SqliteStructuredEventLogRepository.AppendAsync(
            connection,
            transaction,
            new(
                "case_operation_cam_setup_removed",
                now,
                actor,
                RelatedIds(caseId, caseOperationId),
                "planner_entered",
                null,
                Data(current.Setup),
                null),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(CaseOperationCamSetupOutcome.Deleted, current.Setup);
    }

    private static async Task<CaseOperationCamSetupRead> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string caseId,
        string caseOperationId,
        CancellationToken cancellationToken)
    {
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT EXISTS(SELECT 1 FROM case_operations WHERE id = $id AND case_id = $caseId);";
            exists.Parameters.AddWithValue("$id", caseOperationId);
            exists.Parameters.AddWithValue("$caseId", caseId);
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
                return new(false, null);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectColumns + " WHERE setup.case_operation_id = $id AND operation.case_id = $caseId;";
        command.Parameters.AddWithValue("$id", caseOperationId);
        command.Parameters.AddWithValue("$caseId", caseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new(true, null);
        return new(true, new CaseOperationCamSetup(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            new MachineVector3(reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6)),
            new MachineVector3(reader.GetDouble(7), reader.GetDouble(8), reader.GetDouble(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetInt32(11),
            Parse(reader.GetString(12)),
            Parse(reader.GetString(13)),
            reader.IsDBNull(14) ? null : reader.GetString(14)));
    }

    private static Dictionary<string, string> RelatedIds(string caseId, string caseOperationId) => new()
    {
        ["caseId"] = caseId,
        ["caseOperationId"] = caseOperationId
    };

    private static object Data(CaseOperationCamSetup setup) => new
    {
        setup.Source,
        setup.WorkOffset,
        setup.OriginMm,
        setup.RotationDeg,
        setup.Notes,
        setup.Version
    };

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
