using System.Globalization;
using Meimad.Planner.Server.Application.Cases;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Stores the NC viewer work offsets of Case Operations (schema v120). Every write runs in one immediate transaction that
/// checks the Operation belongs to the Case, compares the version, writes the row and appends the audit event; the Case
/// Operation row itself is never touched.
/// </summary>
internal sealed class SqliteCaseOperationViewerWorkOffsetRepository(SqliteDatabase database) : ICaseOperationViewerWorkOffsetRepository
{
    private const string SelectColumns = """
        SELECT offsets.case_operation_id, operation.case_id, offsets.viewer_machine, offsets.offsets_json,
               offsets.version, offsets.created_at, offsets.updated_at, offsets.updated_by
        FROM case_operation_viewer_work_offsets offsets
        JOIN case_operations operation ON operation.id = offsets.case_operation_id
        """;

    public async Task<CaseOperationViewerWorkOffsetList> ListAsync(string caseId, string caseOperationId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        if (!await OperationExistsAsync(connection, transaction, caseId, caseOperationId, cancellationToken))
            return new(false, []);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectColumns + """
             WHERE offsets.case_operation_id = $id AND operation.case_id = $caseId ORDER BY offsets.viewer_machine;
            """;
        command.Parameters.AddWithValue("$id", caseOperationId);
        command.Parameters.AddWithValue("$caseId", caseId);
        var items = new List<CaseOperationViewerWorkOffsets>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        }
        await transaction.CommitAsync(cancellationToken);
        return new(true, items);
    }

    public async Task<CaseOperationViewerWorkOffsetWriteResult> SaveAsync(
        string caseId,
        string caseOperationId,
        string viewerMachine,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> offsets,
        int? expectedVersion,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await OperationExistsAsync(connection, transaction, caseId, caseOperationId, cancellationToken))
            return new(CaseOperationViewerWorkOffsetOutcome.OperationNotFound, null);
        var current = await FindAsync(connection, transaction, caseId, caseOperationId, viewerMachine, cancellationToken);
        if (expectedVersion is null && current is not null)
            return new(CaseOperationViewerWorkOffsetOutcome.AlreadyExists, current);
        if (expectedVersion is not null && current is null)
            return new(CaseOperationViewerWorkOffsetOutcome.OffsetsNotFound, null);
        if (expectedVersion is not null && current!.Version != expectedVersion.Value)
            return new(CaseOperationViewerWorkOffsetOutcome.Stale, current);

        var saved = new CaseOperationViewerWorkOffsets(
            caseOperationId, caseId, viewerMachine, offsets,
            (current?.Version ?? 0) + 1, current?.CreatedAt ?? now, now, actor);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = current is null
                ? """
                  INSERT INTO case_operation_viewer_work_offsets (
                      case_operation_id, viewer_machine, offsets_json, version, created_at, updated_at, updated_by)
                  VALUES ($id, $machine, $offsets, $version, $createdAt, $updatedAt, $updatedBy);
                  """
                : """
                  UPDATE case_operation_viewer_work_offsets
                  SET offsets_json = $offsets, version = $version, updated_at = $updatedAt, updated_by = $updatedBy
                  WHERE case_operation_id = $id AND viewer_machine = $machine AND version = $expectedVersion;
                  """;
            command.Parameters.AddWithValue("$id", caseOperationId);
            command.Parameters.AddWithValue("$machine", viewerMachine);
            command.Parameters.AddWithValue("$offsets", CaseOperationViewerWorkOffsetService.Serialize(offsets));
            command.Parameters.AddWithValue("$version", saved.Version);
            command.Parameters.AddWithValue("$createdAt", Format(saved.CreatedAt));
            command.Parameters.AddWithValue("$updatedAt", Format(saved.UpdatedAt));
            command.Parameters.AddWithValue("$updatedBy", actor);
            command.Parameters.AddWithValue("$expectedVersion", expectedVersion ?? 0);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                return new(CaseOperationViewerWorkOffsetOutcome.Stale, current);
        }

        await SqliteStructuredEventLogRepository.AppendAsync(
            connection,
            transaction,
            new(
                "case_operation_viewer_work_offsets_saved",
                now,
                actor,
                RelatedIds(caseId, caseOperationId, viewerMachine),
                "planner_entered",
                null,
                current is null ? null : Data(current),
                Data(saved)),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(current is null ? CaseOperationViewerWorkOffsetOutcome.Created : CaseOperationViewerWorkOffsetOutcome.Updated, saved);
    }

    public async Task<CaseOperationViewerWorkOffsetWriteResult> DeleteAsync(
        string caseId,
        string caseOperationId,
        string viewerMachine,
        int expectedVersion,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await OperationExistsAsync(connection, transaction, caseId, caseOperationId, cancellationToken))
            return new(CaseOperationViewerWorkOffsetOutcome.OperationNotFound, null);
        var current = await FindAsync(connection, transaction, caseId, caseOperationId, viewerMachine, cancellationToken);
        if (current is null)
            return new(CaseOperationViewerWorkOffsetOutcome.OffsetsNotFound, null);
        if (current.Version != expectedVersion)
            return new(CaseOperationViewerWorkOffsetOutcome.Stale, current);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM case_operation_viewer_work_offsets
                WHERE case_operation_id = $id AND viewer_machine = $machine AND version = $version;
                """;
            command.Parameters.AddWithValue("$id", caseOperationId);
            command.Parameters.AddWithValue("$machine", viewerMachine);
            command.Parameters.AddWithValue("$version", expectedVersion);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                return new(CaseOperationViewerWorkOffsetOutcome.Stale, current);
        }

        await SqliteStructuredEventLogRepository.AppendAsync(
            connection,
            transaction,
            new(
                "case_operation_viewer_work_offsets_removed",
                now,
                actor,
                RelatedIds(caseId, caseOperationId, viewerMachine),
                "planner_entered",
                null,
                Data(current),
                null),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(CaseOperationViewerWorkOffsetOutcome.Deleted, current);
    }

    private static async Task<bool> OperationExistsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string caseId, string caseOperationId, CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT EXISTS(SELECT 1 FROM case_operations WHERE id = $id AND case_id = $caseId);";
        exists.Parameters.AddWithValue("$id", caseOperationId);
        exists.Parameters.AddWithValue("$caseId", caseId);
        return Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<CaseOperationViewerWorkOffsets?> FindAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string caseId,
        string caseOperationId,
        string viewerMachine,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectColumns + """
             WHERE offsets.case_operation_id = $id AND operation.case_id = $caseId AND offsets.viewer_machine = $machine;
            """;
        command.Parameters.AddWithValue("$id", caseOperationId);
        command.Parameters.AddWithValue("$caseId", caseId);
        command.Parameters.AddWithValue("$machine", viewerMachine);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static CaseOperationViewerWorkOffsets Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        CaseOperationViewerWorkOffsetService.Deserialize(reader.GetString(3)),
        reader.GetInt32(4),
        Parse(reader.GetString(5)),
        Parse(reader.GetString(6)),
        reader.IsDBNull(7) ? null : reader.GetString(7));

    private static Dictionary<string, string> RelatedIds(string caseId, string caseOperationId, string viewerMachine) => new()
    {
        ["caseId"] = caseId,
        ["caseOperationId"] = caseOperationId,
        ["viewerMachine"] = viewerMachine
    };

    private static object Data(CaseOperationViewerWorkOffsets offsets) => new
    {
        offsets.ViewerMachine,
        offsets.Offsets,
        offsets.Version
    };

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
