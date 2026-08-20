using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.GCode;
using Meimad.Planner.Server.Domain.GCode;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteGCodeRepository : IGCodeRepository
{
    private readonly SqliteDatabase database;

    public SqliteGCodeRepository(SqliteDatabase database) => this.database = database;

    public async Task<OperationGCodeCatalog?> ReadCatalogAsync(
        string caseId,
        string caseOperationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        if (!await OperationExistsAsync(connection, null, caseId, caseOperationId, cancellationToken))
        {
            return null;
        }

        var processes = await ReadProcessRevisionsAsync(connection, caseOperationId, cancellationToken);
        var releases = await ReadReleasesAsync(connection, caseOperationId, cancellationToken);
        var active = processes.FirstOrDefault(value => value.IsActive);
        var postprocessors = await ReadPostprocessorStatusesAsync(
            connection,
            active,
            releases,
            cancellationToken);
        return new OperationGCodeCatalog(caseOperationId, active, processes, postprocessors, releases);
    }

    public async Task<GCodeRelease> PublishAsync(
        PublishGCodeReleaseCommand command,
        EditAuthority authority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var releasedBy = await EnsureEditAuthorityAsync(
            connection, transaction, authority, cancellationToken);
        if (!await OperationExistsAsync(
                connection, transaction, command.CaseId, command.CaseOperationId, cancellationToken))
        {
            throw new GCodeOperationNotFoundException(command.CaseOperationId);
        }

        var postprocessorName = await ReadActivePostprocessorNameAsync(
            connection, transaction, command.PostprocessorId, cancellationToken)
            ?? throw new GCodePostprocessorNotFoundException(command.PostprocessorId);
        var active = await ReadActiveProcessAsync(
            connection, transaction, command.CaseOperationId, cancellationToken);

        ToolTableRelease toolTable;
        ProcessRevision process;
        var createsProcess = active is null
            || command.ChangeScope == GCodeChangeScopes.NewProcessRevision;
        if (createsProcess)
        {
            if (active is not null && !command.ConfirmNewProcessRevision)
            {
                throw new GCodeProcessStateException(
                    "process_revision_confirmation_required",
                    "Creating a new manufacturing-process revision requires confirmation.");
            }

            if (command.ToolTableFile is not null)
            {
                toolTable = await InsertToolTableAsync(
                    connection,
                    transaction,
                    command.CaseOperationId,
                    command.ToolTableFile,
                    command.ToolTableDefinition ?? throw new GCodeProcessStateException(
                        "tool_table_definition_required",
                        "The released tool table could not be interpreted as structured tool requirements."),
                    command.ReleaseComment,
                    command.ReleasedAt,
                    releasedBy,
                    cancellationToken);
            }
            else if (active is not null && command.ReuseActiveToolTable)
            {
                toolTable = active.ToolTable;
            }
            else
            {
                throw new GCodeProcessStateException(
                    "tool_table_release_required",
                    "The first or new process revision requires an uploaded tool table or confirmed reuse of the active tool table.");
            }

            var nextProcessNumber = await NextNumberAsync(
                connection,
                transaction,
                "process_revisions",
                "revision_number",
                "case_operation_id",
                command.CaseOperationId,
                cancellationToken);
            await using (var deactivate = connection.CreateCommand())
            {
                deactivate.Transaction = transaction;
                deactivate.CommandText = "UPDATE process_revisions SET is_active = 0, version = version + 1, updated_at = $at WHERE case_operation_id = $operationId AND is_active = 1;";
                deactivate.Parameters.AddWithValue("$operationId", command.CaseOperationId);
                deactivate.Parameters.AddWithValue("$at", Format(command.ReleasedAt));
                await deactivate.ExecuteNonQueryAsync(cancellationToken);
            }

            process = new ProcessRevision(
                command.CandidateProcessRevisionId,
                command.CaseOperationId,
                nextProcessNumber,
                true,
                toolTable.ToolTableReleaseId,
                command.ReleasedAt,
                releasedBy,
                command.ProcessChangeDescription,
                1,
                toolTable);
            await InsertProcessRevisionAsync(connection, transaction, process, cancellationToken);
        }
        else
        {
            if (command.ToolTableFile is not null)
            {
                throw new GCodeProcessStateException(
                    "new_process_revision_required",
                    "A physical tool-table change requires a new process revision.");
            }

            process = active!;
            toolTable = active!.ToolTable;
        }

        var postRevision = await NextPostRevisionAsync(
            connection,
            transaction,
            process.ProcessRevisionId,
            command.PostprocessorId,
            cancellationToken);
        var release = new GCodeRelease(
            command.GCodeFile.ArtifactId,
            command.CaseOperationId,
            process.ProcessRevisionId,
            process.ProcessRevisionNumber,
            command.PostprocessorId,
            postprocessorName,
            postRevision,
            command.GCodeFile.OriginalFileName,
            command.GCodeFile.StoredRelativePath,
            command.GCodeFile.FileSize,
            command.GCodeFile.FileHash,
            command.ReleasedAt,
            releasedBy,
            command.ChangeScope,
            command.ReleaseComment,
            toolTable.ToolTableReleaseId,
            true,
            process.IsActive);
        await InsertReleaseAsync(connection, transaction, release, cancellationToken);
        await SqliteStructuredEventLogRepository.AppendAsync(
            connection,
            transaction,
            new(
                createsProcess ? "process_revision_activated" : "gcode_release_published",
                command.ReleasedAt,
                releasedBy,
                new Dictionary<string, string>
                {
                    ["caseOperationId"] = command.CaseOperationId,
                    ["processRevisionId"] = process.ProcessRevisionId,
                    ["gcodeReleaseId"] = release.GCodeReleaseId,
                    ["postprocessorId"] = release.PostprocessorId,
                    ["toolTableReleaseId"] = toolTable.ToolTableReleaseId
                },
                command.ChangeScope,
                command.ReleaseComment,
                null,
                new
                {
                    processRevisionNumber = process.ProcessRevisionNumber,
                    postSpecificRevision = postRevision,
                    gcodeHash = release.FileHash,
                    toolTableHash = toolTable.FileHash
                }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return release;
    }

    public Task<StoredReleaseFile?> ReadGCodeFileAsync(
        string caseOperationId,
        string releaseId,
        CancellationToken cancellationToken) =>
        ReadStoredFileAsync(
            "gcode_releases", "id", releaseId, caseOperationId, cancellationToken);

    public Task<StoredReleaseFile?> ReadToolTableFileAsync(
        string caseOperationId,
        string toolTableReleaseId,
        CancellationToken cancellationToken) =>
        ReadStoredFileAsync(
            "tool_table_releases", "id", toolTableReleaseId, caseOperationId, cancellationToken);

    public async Task<IReadOnlySet<string>> ListStoredArtifactIdsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM gcode_releases UNION SELECT id FROM tool_table_releases;";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private async Task<StoredReleaseFile?> ReadStoredFileAsync(
        string table,
        string idColumn,
        string id,
        string operationId,
        CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, original_file_name, stored_relative_path, file_size, file_hash
            FROM {table}
            WHERE {idColumn} = $id AND case_operation_id = $operationId;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$operationId", operationId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? new StoredReleaseFile(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetString(4))
            : null;
    }

    private static async Task<IReadOnlyList<ProcessRevision>> ReadProcessRevisionsAsync(
        SqliteConnection connection,
        string operationId,
        CancellationToken token)
    {
        var toolRows = await ReadToolRowsAsync(connection, null, operationId, token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pr.id, pr.case_operation_id, pr.revision_number, pr.is_active,
                   pr.tool_table_release_id, pr.created_at, pr.created_by,
                   pr.change_description, pr.version,
                   tt.revision_number, tt.original_file_name, tt.stored_relative_path,
                   tt.file_size, tt.file_hash, tt.released_at, tt.released_by,
                   tt.release_comment, tt.required_tool_count
            FROM process_revisions pr
            JOIN tool_table_releases tt ON tt.id = pr.tool_table_release_id
            WHERE pr.case_operation_id = $operationId
            ORDER BY pr.revision_number DESC;
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        var values = new List<ProcessRevision>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var tool = new ToolTableRelease(
                reader.GetString(4), reader.GetString(1), reader.GetInt32(9),
                reader.GetString(10), reader.GetString(11), reader.GetInt64(12),
                reader.GetString(13), Parse(reader.GetString(14)), reader.GetString(15),
                reader.GetString(16), reader.IsDBNull(17) ? null : reader.GetInt32(17),
                toolRows.GetValueOrDefault(reader.GetString(4), []));
            values.Add(new ProcessRevision(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                reader.GetBoolean(3), reader.GetString(4), Parse(reader.GetString(5)),
                reader.GetString(6), reader.GetString(7), reader.GetInt32(8), tool));
        }

        return values;
    }

    private static async Task<IReadOnlyList<GCodeRelease>> ReadReleasesAsync(
        SqliteConnection connection,
        string operationId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT gr.id, gr.case_operation_id, gr.process_revision_id,
                   pr.revision_number, gr.postprocessor_id, pp.name,
                   gr.post_specific_revision, gr.original_file_name,
                   gr.stored_relative_path, gr.file_size, gr.file_hash,
                   gr.released_at, gr.released_by, gr.change_scope,
                   gr.release_comment, gr.tool_table_release_id,
                   NOT EXISTS (
                       SELECT 1 FROM gcode_releases newer
                       WHERE newer.process_revision_id = gr.process_revision_id
                         AND newer.postprocessor_id = gr.postprocessor_id
                         AND newer.post_specific_revision > gr.post_specific_revision),
                   pr.is_active
            FROM gcode_releases gr
            JOIN process_revisions pr ON pr.id = gr.process_revision_id
            JOIN postprocessors pp ON pp.id = gr.postprocessor_id
            WHERE gr.case_operation_id = $operationId
            ORDER BY pr.revision_number DESC, pp.name COLLATE NOCASE,
                     gr.post_specific_revision DESC, gr.id;
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        var values = new List<GCodeRelease>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            values.Add(ReadRelease(reader));
        }

        return values;
    }

    private static async Task<IReadOnlyList<PostprocessorReleaseStatus>> ReadPostprocessorStatusesAsync(
        SqliteConnection connection,
        ProcessRevision? active,
        IReadOnlyList<GCodeRelease> releases,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, is_active FROM postprocessors ORDER BY name COLLATE NOCASE, id;";
        var values = new List<PostprocessorReleaseStatus>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var id = reader.GetString(0);
            var current = active is null
                ? null
                : releases.FirstOrDefault(value =>
                    value.ProcessRevisionId == active.ProcessRevisionId
                    && value.PostprocessorId == id
                    && value.IsCurrentForProcessAndPost);
            var historical = releases.FirstOrDefault(value => value.PostprocessorId == id);
            var status = current is not null ? "current" : historical is not null ? "stale" : "missing";
            values.Add(new PostprocessorReleaseStatus(
                id, reader.GetString(1), reader.GetBoolean(2), status, current, historical));
        }

        return values;
    }

    private static async Task<ProcessRevision?> ReadActiveProcessAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        CancellationToken token)
    {
        var toolRows = await ReadToolRowsAsync(connection, transaction, operationId, token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT pr.id, pr.case_operation_id, pr.revision_number, pr.is_active,
                   pr.tool_table_release_id, pr.created_at, pr.created_by,
                   pr.change_description, pr.version,
                   tt.revision_number, tt.original_file_name, tt.stored_relative_path,
                   tt.file_size, tt.file_hash, tt.released_at, tt.released_by,
                   tt.release_comment, tt.required_tool_count
            FROM process_revisions pr
            JOIN tool_table_releases tt ON tt.id = pr.tool_table_release_id
            WHERE pr.case_operation_id = $operationId AND pr.is_active = 1;
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            return null;
        }

        var tool = new ToolTableRelease(
            reader.GetString(4), reader.GetString(1), reader.GetInt32(9),
            reader.GetString(10), reader.GetString(11), reader.GetInt64(12),
            reader.GetString(13), Parse(reader.GetString(14)), reader.GetString(15),
            reader.GetString(16), reader.IsDBNull(17) ? null : reader.GetInt32(17),
            toolRows.GetValueOrDefault(reader.GetString(4), []));
        return new ProcessRevision(
            reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
            true, reader.GetString(4), Parse(reader.GetString(5)), reader.GetString(6),
            reader.GetString(7), reader.GetInt32(8), tool);
    }

    private static async Task<ToolTableRelease> InsertToolTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        StoredReleaseFile file,
        ReleasedToolTableDefinition definition,
        string comment,
        DateTimeOffset at,
        string releasedBy,
        CancellationToken token)
    {
        var revision = await NextNumberAsync(
            connection, transaction, "tool_table_releases", "revision_number",
            "case_operation_id", operationId, token);
        var value = new ToolTableRelease(
            file.ArtifactId, operationId, revision, file.OriginalFileName,
            file.StoredRelativePath, file.FileSize, file.FileHash, at, releasedBy, comment,
            definition.RequiredToolCount, definition.Tools);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tool_table_releases (
                id, case_operation_id, revision_number, original_file_name,
                stored_relative_path, file_size, file_hash, released_at,
                released_by, release_comment, created_at, updated_at, required_tool_count)
            VALUES ($id, $operationId, $revision, $name, $path, $size, $hash,
                    $at, $by, $comment, $at, $at, $requiredToolCount);
            """;
        command.Parameters.AddWithValue("$id", value.ToolTableReleaseId);
        command.Parameters.AddWithValue("$operationId", operationId);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$name", value.OriginalFileName);
        command.Parameters.AddWithValue("$path", value.StoredRelativePath);
        command.Parameters.AddWithValue("$size", value.FileSize);
        command.Parameters.AddWithValue("$hash", value.FileHash);
        command.Parameters.AddWithValue("$at", Format(at));
        command.Parameters.AddWithValue("$by", releasedBy);
        command.Parameters.AddWithValue("$comment", comment);
        command.Parameters.AddWithValue("$requiredToolCount", definition.RequiredToolCount);
        await command.ExecuteNonQueryAsync(token);

        foreach (var tool in definition.Tools)
        {
            await using var row = connection.CreateCommand();
            row.Transaction = transaction;
            row.CommandText = """
                INSERT INTO tool_table_release_tools (
                    id, tool_table_release_id, row_number, tool_identifier,
                    description, is_required, requires_magazine_position,
                    is_active, magazine_position, created_at, updated_at)
                VALUES ($id, $releaseId, $rowNumber, $identifier, $description,
                        $required, $magazine, $active, $position, $at, $at);
                """;
            row.Parameters.AddWithValue("$id", tool.ReleasedToolId);
            row.Parameters.AddWithValue("$releaseId", value.ToolTableReleaseId);
            row.Parameters.AddWithValue("$rowNumber", tool.RowNumber);
            row.Parameters.AddWithValue("$identifier", tool.ToolIdentifier);
            row.Parameters.AddWithValue("$description", tool.Description);
            row.Parameters.AddWithValue("$required", tool.IsRequired ? 1 : 0);
            row.Parameters.AddWithValue("$magazine", tool.RequiresMagazinePosition ? 1 : 0);
            row.Parameters.AddWithValue("$active", tool.IsActive ? 1 : 0);
            row.Parameters.AddWithValue(
                "$position", tool.MagazinePosition is null ? DBNull.Value : tool.MagazinePosition);
            row.Parameters.AddWithValue("$at", Format(at));
            await row.ExecuteNonQueryAsync(token);
        }

        return value;
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<ReleasedTool>>> ReadToolRowsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string operationId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT rows.id, rows.tool_table_release_id, rows.row_number,
                   rows.tool_identifier, rows.description, rows.is_required,
                   rows.requires_magazine_position, rows.is_active,
                   rows.magazine_position
            FROM tool_table_release_tools rows
            JOIN tool_table_releases releases
              ON releases.id = rows.tool_table_release_id
            WHERE releases.case_operation_id = $operationId
            ORDER BY rows.tool_table_release_id, rows.row_number;
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        var values = new Dictionary<string, List<ReleasedTool>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var releaseId = reader.GetString(1);
            if (!values.TryGetValue(releaseId, out var rows))
            {
                rows = [];
                values.Add(releaseId, rows);
            }

            rows.Add(new ReleasedTool(
                reader.GetString(0), reader.GetInt32(2), reader.GetString(3),
                reader.GetString(4), reader.GetBoolean(5), reader.GetBoolean(6),
                reader.GetBoolean(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return values.ToDictionary(
            value => value.Key,
            value => (IReadOnlyList<ReleasedTool>)value.Value,
            StringComparer.Ordinal);
    }

    private static async Task InsertProcessRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProcessRevision value,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO process_revisions (
                id, case_operation_id, revision_number, is_active,
                tool_table_release_id, created_at, created_by,
                change_description, version, updated_at)
            VALUES ($id, $operationId, $revision, 1, $toolTableId, $at, $by,
                    $description, 1, $at);
            """;
        command.Parameters.AddWithValue("$id", value.ProcessRevisionId);
        command.Parameters.AddWithValue("$operationId", value.CaseOperationId);
        command.Parameters.AddWithValue("$revision", value.ProcessRevisionNumber);
        command.Parameters.AddWithValue("$toolTableId", value.ToolTableReleaseId);
        command.Parameters.AddWithValue("$at", Format(value.CreatedAt));
        command.Parameters.AddWithValue("$by", value.CreatedBy);
        command.Parameters.AddWithValue("$description", value.ChangeDescription);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task InsertReleaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GCodeRelease value,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO gcode_releases (
                id, case_operation_id, process_revision_id, postprocessor_id,
                post_specific_revision, original_file_name, stored_relative_path,
                file_size, file_hash, released_at, released_by, change_scope,
                release_comment, tool_table_release_id, created_at, updated_at)
            VALUES ($id, $operationId, $processId, $postprocessorId, $postRevision,
                    $name, $path, $size, $hash, $at, $by, $scope, $comment,
                    $toolTableId, $at, $at);
            """;
        command.Parameters.AddWithValue("$id", value.GCodeReleaseId);
        command.Parameters.AddWithValue("$operationId", value.CaseOperationId);
        command.Parameters.AddWithValue("$processId", value.ProcessRevisionId);
        command.Parameters.AddWithValue("$postprocessorId", value.PostprocessorId);
        command.Parameters.AddWithValue("$postRevision", value.PostSpecificRevision);
        command.Parameters.AddWithValue("$name", value.OriginalFileName);
        command.Parameters.AddWithValue("$path", value.StoredRelativePath);
        command.Parameters.AddWithValue("$size", value.FileSize);
        command.Parameters.AddWithValue("$hash", value.FileHash);
        command.Parameters.AddWithValue("$at", Format(value.ReleasedAt));
        command.Parameters.AddWithValue("$by", value.ReleasedBy);
        command.Parameters.AddWithValue("$scope", value.ChangeScope);
        command.Parameters.AddWithValue("$comment", value.ReleaseComment);
        command.Parameters.AddWithValue("$toolTableId", value.ToolTableReleaseId);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<int> NextPostRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string processId,
        string postprocessorId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(post_specific_revision), 0) + 1
            FROM gcode_releases
            WHERE process_revision_id = $processId AND postprocessor_id = $postprocessorId;
            """;
        command.Parameters.AddWithValue("$processId", processId);
        command.Parameters.AddWithValue("$postprocessorId", postprocessorId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }

    private static async Task<int> NextNumberAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string numberColumn,
        string ownerColumn,
        string ownerId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COALESCE(MAX({numberColumn}), 0) + 1 FROM {table} WHERE {ownerColumn} = $ownerId;";
        command.Parameters.AddWithValue("$ownerId", ownerId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ReadActivePostprocessorNameAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM postprocessors WHERE id = $id AND is_active = 1;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteScalarAsync(token) as string;
    }

    private static async Task<bool> OperationExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string caseId,
        string operationId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM case_operations WHERE id = $operationId AND case_id = $caseId);";
        command.Parameters.AddWithValue("$operationId", operationId);
        command.Parameters.AddWithValue("$caseId", caseId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1;
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
            throw new EditModeMutationException("edit_mode_required", "No Windows client currently holds Edit Mode.");
        }

        if (!string.Equals(reader.GetString(0), authority.ClientId, StringComparison.Ordinal)
            || reader.GetInt64(2) != authority.Generation)
        {
            throw new EditModeMutationException("edit_generation_stale", "This client does not hold the active Edit Mode generation.");
        }

        return reader.IsDBNull(1) ? authority.ClientId : reader.GetString(1);
    }

    private static GCodeRelease ReadRelease(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
        reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetString(7),
        reader.GetString(8), reader.GetInt64(9), reader.GetString(10), Parse(reader.GetString(11)),
        reader.GetString(12), reader.GetString(13), reader.GetString(14), reader.GetString(15),
        reader.GetBoolean(16), reader.GetBoolean(17));

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
