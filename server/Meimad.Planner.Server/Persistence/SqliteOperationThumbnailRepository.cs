using System.Globalization;
using Meimad.Planner.Server.Application.TvDashboard;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal enum OperationThumbnailOutcome
{
    Created,
    Updated,
    Deleted,
    OperationNotFound,
    ThumbnailNotFound,
    AlreadyExists,
    Stale
}

internal sealed record OperationThumbnailResult(OperationThumbnailOutcome Outcome, OperationThumbnail? Thumbnail);

/// <summary>The thumbnails of one Case Operation and whether its Case has a picture stored on the Server.</summary>
internal sealed record OperationThumbnailSet(bool OperationExists, bool CaseHasPicture, IReadOnlyList<OperationThumbnail> Thumbnails);

/// <summary>
/// Case Operation thumbnails (schema v118). Each source slot has its own version: a create needs no current row, a replace
/// or removal needs the version the client read, and the check, the change and the audit event are one immediate
/// transaction. The TV reads a stored image only through <see cref="FindDisplayableAsync"/>.
/// </summary>
internal sealed class SqliteOperationThumbnailRepository(SqliteDatabase database)
{
    private const string Select = """
        SELECT case_operation_id, source, file_name, media_type, file_size, file_hash, stored_relative_path,
               source_reference, version, updated_at, updated_by
        FROM case_operation_thumbnails
        """;

    public async Task<OperationThumbnailSet> ListAsync(string caseId, string caseOperationId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        if (!await OperationExistsAsync(connection, transaction, caseId, caseOperationId, cancellationToken))
            return new OperationThumbnailSet(false, false, []);
        bool caseHasPicture;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM case_pictures WHERE case_id = $case);";
            command.Parameters.AddWithValue("$case", caseId);
            caseHasPicture = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
        }
        var items = new List<OperationThumbnail>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = Select + " WHERE case_operation_id = $op ORDER BY CASE source WHEN 'package' THEN 0 ELSE 1 END;";
            command.Parameters.AddWithValue("$op", caseOperationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        }
        await transaction.CommitAsync(cancellationToken);
        return new OperationThumbnailSet(true, caseHasPicture, items);
    }

    /// <summary>
    /// Creates (<paramref name="expectedVersion"/> null) or replaces the slot of <paramref name="thumbnail"/>'s source when
    /// the slot is still at <paramref name="expectedVersion"/>.
    /// </summary>
    public async Task<OperationThumbnailResult> SaveAsync(
        string caseId, OperationThumbnail thumbnail, int? expectedVersion, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await OperationExistsAsync(connection, transaction, caseId, thumbnail.CaseOperationId, cancellationToken))
            return new(OperationThumbnailOutcome.OperationNotFound, null);
        var current = await ReadAsync(connection, transaction, thumbnail.CaseOperationId, thumbnail.Source, cancellationToken);
        if (expectedVersion is null && current is not null) return new(OperationThumbnailOutcome.AlreadyExists, current);
        if (expectedVersion is not null && current is null) return new(OperationThumbnailOutcome.ThumbnailNotFound, null);
        if (expectedVersion is not null && current!.Version != expectedVersion) return new(OperationThumbnailOutcome.Stale, current);

        var saved = thumbnail with { Version = (current?.Version ?? 0) + 1 };
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO case_operation_thumbnails (case_operation_id, source, file_name, media_type, file_size, file_hash,
                    stored_relative_path, source_reference, version, updated_at, updated_by)
                VALUES ($op, $source, $name, $type, $size, $hash, $path, $reference, $version, $now, $actor)
                ON CONFLICT (case_operation_id, source) DO UPDATE SET file_name = excluded.file_name,
                    media_type = excluded.media_type, file_size = excluded.file_size, file_hash = excluded.file_hash,
                    stored_relative_path = excluded.stored_relative_path, source_reference = excluded.source_reference,
                    version = excluded.version, updated_at = excluded.updated_at, updated_by = excluded.updated_by;
                """;
            command.Parameters.AddWithValue("$op", saved.CaseOperationId);
            command.Parameters.AddWithValue("$source", saved.Source);
            command.Parameters.AddWithValue("$name", saved.FileName);
            command.Parameters.AddWithValue("$type", saved.MediaType);
            command.Parameters.AddWithValue("$size", saved.SizeBytes);
            command.Parameters.AddWithValue("$hash", saved.Sha256);
            command.Parameters.AddWithValue("$path", saved.StoredRelativePath);
            command.Parameters.AddWithValue("$reference", (object?)saved.SourceReference ?? DBNull.Value);
            command.Parameters.AddWithValue("$version", saved.Version);
            command.Parameters.AddWithValue("$now", Format(saved.UpdatedAt));
            command.Parameters.AddWithValue("$actor", (object?)saved.UpdatedBy ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction, new(
            "case_operation_thumbnail_saved", saved.UpdatedAt, saved.UpdatedBy ?? "system",
            new Dictionary<string, string> { ["caseId"] = caseId, ["caseOperationId"] = saved.CaseOperationId },
            "planner_entered", null,
            current is null ? null : new { current.Source, current.FileName, current.Sha256, current.Version },
            new { saved.Source, saved.FileName, saved.MediaType, saved.SizeBytes, saved.Sha256, saved.Version }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(current is null ? OperationThumbnailOutcome.Created : OperationThumbnailOutcome.Updated, saved);
    }

    public async Task<OperationThumbnailResult> DeleteAsync(
        string caseId, string caseOperationId, string source, int expectedVersion, string actor, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await OperationExistsAsync(connection, transaction, caseId, caseOperationId, cancellationToken))
            return new(OperationThumbnailOutcome.OperationNotFound, null);
        var current = await ReadAsync(connection, transaction, caseOperationId, source, cancellationToken);
        if (current is null) return new(OperationThumbnailOutcome.ThumbnailNotFound, null);
        if (current.Version != expectedVersion) return new(OperationThumbnailOutcome.Stale, current);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM case_operation_thumbnails WHERE case_operation_id = $op AND source = $source;";
            command.Parameters.AddWithValue("$op", caseOperationId);
            command.Parameters.AddWithValue("$source", source);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction, new(
            "case_operation_thumbnail_removed", now, actor,
            new Dictionary<string, string> { ["caseId"] = caseId, ["caseOperationId"] = caseOperationId },
            "planner_entered", null,
            new { current.Source, current.FileName, current.Sha256, current.Version }, null),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(OperationThumbnailOutcome.Deleted, current);
    }

    public async Task<OperationThumbnail?> GetAsync(
        string caseId, string caseOperationId, string source, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var value = await OperationExistsAsync(connection, transaction, caseId, caseOperationId, cancellationToken)
            ? await ReadAsync(connection, transaction, caseOperationId, source, cancellationToken)
            : null;
        await transaction.CommitAsync(cancellationToken);
        return value;
    }

    /// <summary>
    /// The stored file (relative to the release root) of a Case picture or Operation thumbnail with content
    /// <paramref name="sha256"/> that a TV may show: its Case or Operation is assigned to, or was last worked on, an active,
    /// display-enabled Machine. Anything else is not served to the TV.
    /// </summary>
    public async Task<string?> FindDisplayableAsync(string sha256, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT picture.stored_relative_path
            FROM case_pictures picture
            WHERE picture.file_hash = $hash
              AND EXISTS (
                  SELECT 1
                  FROM batch_operations operation
                  JOIN production_batches batch ON batch.id = operation.production_batch_id
                  WHERE batch.case_id = picture.case_id
                    AND (EXISTS (SELECT 1 FROM machine_assignments assignment
                                 JOIN machines machine ON machine.id = assignment.machine_id
                                 WHERE assignment.batch_operation_id = operation.id
                                   AND machine.is_active = 1 AND machine.display_enabled = 1)
                         OR EXISTS (SELECT 1 FROM machines machine
                                    WHERE machine.id = operation.actual_machine_id
                                      AND machine.is_active = 1 AND machine.display_enabled = 1)))
            UNION ALL
            SELECT thumbnail.stored_relative_path
            FROM case_operation_thumbnails thumbnail
            WHERE thumbnail.file_hash = $hash
              AND EXISTS (
                  SELECT 1
                  FROM batch_operations operation
                  WHERE operation.source_case_operation_id = thumbnail.case_operation_id
                    AND (EXISTS (SELECT 1 FROM machine_assignments assignment
                                 JOIN machines machine ON machine.id = assignment.machine_id
                                 WHERE assignment.batch_operation_id = operation.id
                                   AND machine.is_active = 1 AND machine.display_enabled = 1)
                         OR EXISTS (SELECT 1 FROM machines machine
                                    WHERE machine.id = operation.actual_machine_id
                                      AND machine.is_active = 1 AND machine.display_enabled = 1)))
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$hash", sha256);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<OperationThumbnail?> ReadAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string caseOperationId, string source,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Select + " WHERE case_operation_id = $op AND source = $source;";
        command.Parameters.AddWithValue("$op", caseOperationId);
        command.Parameters.AddWithValue("$source", source);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static OperationThumbnail Read(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
        reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt32(8),
        DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.IsDBNull(10) ? null : reader.GetString(10));

    private static async Task<bool> OperationExistsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string caseId, string caseOperationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM case_operations WHERE id = $op AND case_id = $case);";
        command.Parameters.AddWithValue("$op", caseOperationId);
        command.Parameters.AddWithValue("$case", caseId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
