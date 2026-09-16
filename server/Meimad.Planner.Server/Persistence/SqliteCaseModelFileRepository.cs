using System.Globalization;
using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Cases;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteCaseModelFileRepository(SqliteDatabase database) : ICaseModelFileRepository
{
    private const string SelectColumns = """
        SELECT id, case_id, case_operation_id, kind, format, file_path, label,
               is_primary, sort_order, version, created_at, updated_at
        FROM case_model_files
        """;

    public async Task<IReadOnlyList<CaseModelFile>> ListAsync(string caseId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE case_id = $caseId ORDER BY is_primary DESC, sort_order, created_at, id;";
        command.Parameters.AddWithValue("$caseId", caseId);
        var items = new List<CaseModelFile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Read(reader));
        }

        return items;
    }

    public async Task<CaseModelFile?> GetAsync(string caseId, string caseModelFileId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ReadOneAsync(connection, null, caseId, caseModelFileId, cancellationToken);
    }

    public async Task<CaseModelFile> CreateAsync(CaseModelFile file, EditAuthority editAuthority, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        await EnsureCaseAndOperationAsync(connection, transaction, file.CaseId, file.CaseOperationId, cancellationToken);

        // The first part model of a Case becomes the primary one automatically; an explicit
        // primary request always wins and demotes the current primary.
        var makePrimary = file.IsPrimary
            || (file.Kind == CaseModelFileKinds.Part
                && !await ExistsAsync(connection, transaction,
                    "SELECT EXISTS(SELECT 1 FROM case_model_files WHERE case_id = $caseId AND is_primary = 1);",
                    ("$caseId", file.CaseId), cancellationToken));
        if (makePrimary)
        {
            await DemotePrimaryAsync(connection, transaction, file.CaseId, file.UpdatedAt, cancellationToken);
        }

        var sortOrder = await NextSortOrderAsync(connection, transaction, file.CaseId, cancellationToken);
        var created = file with { IsPrimary = makePrimary, SortOrder = sortOrder };
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO case_model_files (
                    id, case_id, case_operation_id, kind, format, file_path, label,
                    is_primary, sort_order, version, created_at, updated_at)
                VALUES ($id, $caseId, $operationId, $kind, $format, $filePath, $label,
                    $isPrimary, $sortOrder, 1, $createdAt, $updatedAt);
                """;
            insert.Parameters.AddWithValue("$id", created.CaseModelFileId);
            insert.Parameters.AddWithValue("$caseId", created.CaseId);
            insert.Parameters.AddWithValue("$operationId", (object?)created.CaseOperationId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$kind", created.Kind);
            insert.Parameters.AddWithValue("$format", created.Format);
            insert.Parameters.AddWithValue("$filePath", created.FilePath);
            insert.Parameters.AddWithValue("$label", created.Label);
            insert.Parameters.AddWithValue("$isPrimary", created.IsPrimary ? 1 : 0);
            insert.Parameters.AddWithValue("$sortOrder", created.SortOrder);
            insert.Parameters.AddWithValue("$createdAt", Format(created.CreatedAt));
            insert.Parameters.AddWithValue("$updatedAt", Format(created.UpdatedAt));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await SqliteStructuredEventLogRepository.AppendAsync(
            connection,
            transaction,
            new(
                "case_model_file_added",
                created.CreatedAt,
                actor,
                RelatedIds(created),
                "planner_selected",
                null,
                null,
                new { created.Kind, created.Format, created.FilePath, created.Label, created.IsPrimary }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return created with { Version = 1 };
    }

    public async Task<CaseModelFile> UpdateAsync(
        string caseId,
        string caseModelFileId,
        int expectedVersion,
        CaseModelFileUpdate update,
        DateTimeOffset now,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        var current = await ReadOneAsync(connection, transaction, caseId, caseModelFileId, cancellationToken)
            ?? throw new CaseModelFileNotFoundException(caseModelFileId);
        if (current.Version != expectedVersion)
        {
            throw new CaseModelFileVersionConflictException(caseModelFileId, expectedVersion);
        }

        var operationId = update.ClearCaseOperation
            ? null
            : update.CaseOperationId ?? current.CaseOperationId;
        await EnsureCaseAndOperationAsync(connection, transaction, caseId, operationId, cancellationToken);
        var isPrimary = update.IsPrimary ?? current.IsPrimary;
        if (isPrimary && !current.IsPrimary)
        {
            await DemotePrimaryAsync(connection, transaction, caseId, now, cancellationToken);
        }

        var updated = current with
        {
            CaseOperationId = operationId,
            Kind = update.Kind ?? current.Kind,
            Label = update.Label ?? current.Label,
            IsPrimary = isPrimary,
            SortOrder = update.SortOrder ?? current.SortOrder,
            Version = current.Version + 1,
            UpdatedAt = now
        };
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE case_model_files
                SET case_operation_id = $operationId, kind = $kind, label = $label,
                    is_primary = $isPrimary, sort_order = $sortOrder,
                    version = version + 1, updated_at = $updatedAt
                WHERE id = $id AND case_id = $caseId AND version = $expectedVersion;
                """;
            command.Parameters.AddWithValue("$operationId", (object?)updated.CaseOperationId ?? DBNull.Value);
            command.Parameters.AddWithValue("$kind", updated.Kind);
            command.Parameters.AddWithValue("$label", updated.Label);
            command.Parameters.AddWithValue("$isPrimary", updated.IsPrimary ? 1 : 0);
            command.Parameters.AddWithValue("$sortOrder", updated.SortOrder);
            command.Parameters.AddWithValue("$updatedAt", Format(now));
            command.Parameters.AddWithValue("$id", caseModelFileId);
            command.Parameters.AddWithValue("$caseId", caseId);
            command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new CaseModelFileVersionConflictException(caseModelFileId, expectedVersion);
            }
        }

        await SqliteStructuredEventLogRepository.AppendAsync(
            connection,
            transaction,
            new(
                "case_model_file_updated",
                now,
                actor,
                RelatedIds(updated),
                "planner_selected",
                null,
                new { current.Kind, current.Label, current.IsPrimary, current.CaseOperationId, current.SortOrder },
                new { updated.Kind, updated.Label, updated.IsPrimary, updated.CaseOperationId, updated.SortOrder }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<bool> DeleteAsync(string caseId, string caseModelFileId, EditAuthority editAuthority, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        var current = await ReadOneAsync(connection, transaction, caseId, caseModelFileId, cancellationToken);
        if (current is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM case_model_files WHERE id = $id AND case_id = $caseId;";
            command.Parameters.AddWithValue("$id", caseModelFileId);
            command.Parameters.AddWithValue("$caseId", caseId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await SqliteStructuredEventLogRepository.AppendAsync(
            connection,
            transaction,
            new(
                "case_model_file_removed",
                DateTimeOffset.UtcNow,
                actor,
                RelatedIds(current),
                "planner_selected",
                null,
                new { current.Kind, current.Format, current.FilePath, current.Label, current.IsPrimary },
                null),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static Dictionary<string, string> RelatedIds(CaseModelFile file)
    {
        var ids = new Dictionary<string, string>
        {
            ["caseModelFileId"] = file.CaseModelFileId,
            ["caseId"] = file.CaseId
        };
        if (file.CaseOperationId is not null)
        {
            ids["caseOperationId"] = file.CaseOperationId;
        }

        return ids;
    }

    private static async Task EnsureCaseAndOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string caseId,
        string? caseOperationId,
        CancellationToken cancellationToken)
    {
        if (!await ExistsAsync(connection, transaction,
                "SELECT EXISTS(SELECT 1 FROM cases WHERE id = $caseId);", ("$caseId", caseId), cancellationToken))
        {
            throw new CaseModelFileValidationException("caseId", "case_not_found", "The Case was not found.");
        }

        if (caseOperationId is not null
            && !await ExistsAsync(connection, transaction,
                "SELECT EXISTS(SELECT 1 FROM case_operations WHERE id = $operationId AND case_id = $caseId);",
                ("$operationId", caseOperationId), cancellationToken, ("$caseId", caseId)))
        {
            throw new CaseModelFileValidationException(
                "caseOperationId", "operation_not_in_case", "The Operation does not belong to this Case.");
        }
    }

    private static async Task DemotePrimaryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string caseId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE case_model_files
            SET is_primary = 0, version = version + 1, updated_at = $updatedAt
            WHERE case_id = $caseId AND is_primary = 1;
            """;
        command.Parameters.AddWithValue("$updatedAt", Format(now));
        command.Parameters.AddWithValue("$caseId", caseId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> NextSortOrderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string caseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM case_model_files WHERE case_id = $caseId;";
        command.Parameters.AddWithValue("$caseId", caseId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        (string Name, object Value) parameter,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] more)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        foreach (var (name, value) in more)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<CaseModelFile?> ReadOneAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string caseId,
        string caseModelFileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectColumns + " WHERE id = $id AND case_id = $caseId;";
        command.Parameters.AddWithValue("$id", caseModelFileId);
        command.Parameters.AddWithValue("$caseId", caseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static CaseModelFile Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetInt32(7) == 1,
        reader.GetInt32(8),
        reader.GetInt32(9),
        Parse(reader.GetString(10)),
        Parse(reader.GetString(11)));

    private static async Task<string> EnsureEditAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EditAuthority authority,
        CancellationToken cancellationToken)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(connection, transaction, DateTimeOffset.UtcNow, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, holder_user_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            throw new EditModeMutationException("edit_mode_required", "No Windows client currently holds Edit Mode.");
        }

        if (!string.Equals(reader.GetString(0), authority.ClientId, StringComparison.Ordinal)
            || reader.GetInt64(2) != authority.Generation)
        {
            throw new EditModeMutationException("edit_generation_stale", "This client does not hold the active Edit Mode generation.");
        }

        return reader.IsDBNull(1) ? authority.ClientId : reader.GetString(1);
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
