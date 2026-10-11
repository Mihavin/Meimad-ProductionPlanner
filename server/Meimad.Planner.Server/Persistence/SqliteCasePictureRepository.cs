using System.Globalization;
using Meimad.Planner.Server.Application.Cases;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal enum CasePictureOutcome
{
    Saved,
    Deleted,
    CaseNotFound,
    PictureNotFound,
    Stale
}

/// <summary>
/// Case pictures stored on the Server (schema v115). Uploading or removing a picture changes the Case (its version and
/// update time) in one immediate transaction with the version check, clears the Case's picture link (the stored picture
/// replaces it) and appends the audit event. Copying a linked picture to the Server changes no Case data.
/// </summary>
internal sealed class SqliteCasePictureRepository(SqliteDatabase database)
{
    private const string Select = """
        SELECT case_id, file_name, media_type, file_size, file_hash, stored_relative_path, source_reference, created_at, created_by
        FROM case_pictures
        """;

    public async Task<CasePicture?> GetAsync(string caseId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, caseId, cancellationToken);
    }

    /// <summary>Saves <paramref name="picture"/> as the Case's picture when the Case is still at <paramref name="expectedVersion"/>.</summary>
    public async Task<CasePictureOutcome> SaveAsync(
        CasePicture picture, int expectedVersion, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var outcome = await BumpCaseAsync(connection, transaction, picture.CaseId, expectedVersion, now, cancellationToken);
        if (outcome is not null) return outcome.Value;
        var before = await ReadAsync(connection, transaction, picture.CaseId, cancellationToken);
        await UpsertAsync(connection, transaction, picture, cancellationToken);
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction, new(
            "case_picture_saved", now, actor,
            new Dictionary<string, string> { ["caseId"] = picture.CaseId },
            "planner_entered", null,
            before is null ? null : new { before.FileName, before.Sha256 },
            new { picture.FileName, picture.MediaType, picture.SizeBytes, picture.Sha256 }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CasePictureOutcome.Saved;
    }

    public async Task<CasePictureOutcome> DeleteAsync(
        string caseId, int expectedVersion, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var before = await ReadAsync(connection, transaction, caseId, cancellationToken);
        if (before is null)
        {
            return await CaseExistsAsync(connection, transaction, caseId, cancellationToken)
                ? CasePictureOutcome.PictureNotFound
                : CasePictureOutcome.CaseNotFound;
        }
        var outcome = await BumpCaseAsync(connection, transaction, caseId, expectedVersion, now, cancellationToken);
        if (outcome is not null) return outcome.Value;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM case_pictures WHERE case_id = $id;";
            command.Parameters.AddWithValue("$id", caseId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction, new(
            "case_picture_removed", now, actor,
            new Dictionary<string, string> { ["caseId"] = caseId },
            "planner_entered", null,
            new { before.FileName, before.Sha256 }, null),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CasePictureOutcome.Deleted;
    }

    /// <summary>Cases whose picture is a link and that have no stored picture yet: (case id, stored link, working folder).</summary>
    public async Task<IReadOnlyList<(string CaseId, string Link, string? WorkingFolder)>> ListLinkedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var paths = await SqliteNetworkFolderSettings.ReadAsync(connection, null, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cases.id, cases.preview_reference, cases.working_folder_path
            FROM cases
            WHERE cases.preview_reference IS NOT NULL AND trim(cases.preview_reference) <> ''
              AND NOT EXISTS (SELECT 1 FROM case_pictures WHERE case_pictures.case_id = cases.id)
            ORDER BY cases.id;
            """;
        var result = new List<(string, string, string?)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add((reader.GetString(0), paths.ToAbsolute(reader.GetString(1))!,
                reader.IsDBNull(2) ? null : paths.ToAbsolute(reader.GetString(2))));
        }
        return result;
    }

    /// <summary>
    /// Records a copy of the Case's linked picture when the link is unchanged and no picture is stored yet; the Case itself
    /// is not changed. Returns false when the Case changed meanwhile.
    /// </summary>
    public async Task<bool> AddCopyAsync(CasePicture picture, string link, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var paths = await SqliteNetworkFolderSettings.ReadAsync(connection, transaction, cancellationToken);
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = """
                SELECT preview_reference FROM cases
                WHERE id = $id AND NOT EXISTS (SELECT 1 FROM case_pictures WHERE case_id = $id);
                """;
            check.Parameters.AddWithValue("$id", picture.CaseId);
            var current = await check.ExecuteScalarAsync(cancellationToken) as string;
            if (current is null || !string.Equals(paths.ToAbsolute(current), link, StringComparison.OrdinalIgnoreCase)) return false;
        }
        await UpsertAsync(connection, transaction, picture, cancellationToken);
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction, new(
            "case_picture_copied", now, "system",
            new Dictionary<string, string> { ["caseId"] = picture.CaseId },
            "picture_link_copied", null, null,
            new { picture.FileName, picture.MediaType, picture.SizeBytes, picture.Sha256, source = link }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    // Null when the Case was bumped; otherwise why not.
    private static async Task<CasePictureOutcome?> BumpCaseAsync(
        SqliteConnection connection, SqliteTransaction transaction, string caseId, int expectedVersion,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE cases SET preview_reference = NULL, version = version + 1, updated_at = $now
            WHERE id = $id AND version = $expected;
            """;
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$id", caseId);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1) return null;
        return await CaseExistsAsync(connection, transaction, caseId, cancellationToken)
            ? CasePictureOutcome.Stale
            : CasePictureOutcome.CaseNotFound;
    }

    private static async Task UpsertAsync(
        SqliteConnection connection, SqliteTransaction transaction, CasePicture picture, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO case_pictures (case_id, file_name, media_type, file_size, file_hash, stored_relative_path,
                source_reference, created_at, created_by)
            VALUES ($id, $name, $type, $size, $hash, $path, $source, $now, $actor)
            ON CONFLICT (case_id) DO UPDATE SET file_name = excluded.file_name, media_type = excluded.media_type,
                file_size = excluded.file_size, file_hash = excluded.file_hash,
                stored_relative_path = excluded.stored_relative_path, source_reference = excluded.source_reference,
                created_at = excluded.created_at, created_by = excluded.created_by;
            """;
        command.Parameters.AddWithValue("$id", picture.CaseId);
        command.Parameters.AddWithValue("$name", picture.FileName);
        command.Parameters.AddWithValue("$type", picture.MediaType);
        command.Parameters.AddWithValue("$size", picture.SizeBytes);
        command.Parameters.AddWithValue("$hash", picture.Sha256);
        command.Parameters.AddWithValue("$path", picture.StoredRelativePath);
        command.Parameters.AddWithValue("$source", (object?)picture.SourceReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Format(picture.CreatedAt));
        command.Parameters.AddWithValue("$actor", (object?)picture.CreatedBy ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<CasePicture?> ReadAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string caseId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Select + " WHERE case_id = $id;";
        command.Parameters.AddWithValue("$id", caseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new CasePicture(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4),
            reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    private static async Task<bool> CaseExistsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string caseId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM cases WHERE id = $id);";
        command.Parameters.AddWithValue("$id", caseId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
