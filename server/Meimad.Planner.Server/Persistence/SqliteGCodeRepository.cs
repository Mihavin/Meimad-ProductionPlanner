using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.GCode;
using Meimad.Planner.Server.Domain.GCode;
using Meimad.Planner.Server.Domain.Readiness;
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
        var nc = await SqliteNcCycleEstimateStore.ReadForOperationAsync(
            connection, caseOperationId, cancellationToken);
        var headers = await ReadHeadersAsync(connection, caseOperationId, cancellationToken);
        var releases = await ReadReleasesAsync(
            connection, caseOperationId, nc.Analyses, nc.Estimates, headers, cancellationToken);
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

        var programId = command.ManufacturingProgramId ?? DefaultProgramId(command.CaseOperationId);
        if (!await ProgramExistsAsync(connection, transaction, programId, cancellationToken))
        {
            throw new ManufacturingProgramNotFoundException(programId);
        }
        var postprocessorName = await ReadActivePostprocessorNameAsync(
            connection, transaction, command.PostprocessorId, cancellationToken)
            ?? throw new GCodePostprocessorNotFoundException(command.PostprocessorId);
        var active = await ReadActiveProcessAsync(
            connection, transaction, programId, command.CaseOperationId, cancellationToken);
        var readinessBefore = await ReadAffectedReadinessAsync(
            connection, transaction, command.CaseOperationId, cancellationToken);

        ToolTableRelease toolTable;
        ProcessRevision process;
        var createdToolTable = false;
        var createsProcess = active is null
            || command.ChangeScope == GCodeChangeScopes.NewProcessRevision;
        if (createsProcess)
        {
            var outputs = command.Outputs ??
                [new ManufacturingProgramRevisionOutput(
                    $"output:{command.CandidateProcessRevisionId}:{command.CaseOperationId}",
                    command.CaseOperationId, 1, 0,
                    $$"""{"caseOperationId":"{{command.CaseOperationId}}"}""")];
            if (command.ManufacturingProgramId is not null && command.Outputs is null)
            {
                throw new GCodeProcessStateException(
                    "program_outputs_required",
                    "A new combined Manufacturing Program revision requires its exact output recipe.");
            }
            if (active is not null && !command.ConfirmNewProcessRevision)
            {
                throw new GCodeProcessStateException(
                    "process_revision_confirmation_required",
                    "Creating a new manufacturing-process revision requires confirmation.");
            }

            if (command.ToolTableFile is not null)
            {
                createdToolTable = true;
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
                deactivate.CommandText = "UPDATE process_revisions SET is_active = 0, version = version + 1, updated_at = $at WHERE manufacturing_program_id = $programId AND is_active = 1;";
                deactivate.Parameters.AddWithValue("$programId", programId);
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
                toolTable,
                programId,
                outputs);
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
        await InsertHeaderAsync(connection, transaction, release.GCodeReleaseId,
            command.HeaderMetadata, command.ReleasedAt, cancellationToken);
        var ncEstimates = await SqliteNcCycleEstimateStore.InsertAnalysisAndEstimatesAsync(
            connection, transaction, release, command.NcAnalysis,
            releasedBy, command.ChangeScope, cancellationToken);
        release = release with
        {
            NcAnalysis = command.NcAnalysis,
            MachineCycleEstimates = ncEstimates,
            HeaderMetadata = command.HeaderMetadata
        };
        await AppendReleaseAuditAsync(
            connection, transaction, release, process, toolTable,
            active, createsProcess, createdToolTable, releasedBy,
            command.ReleaseComment, cancellationToken);
        var readinessAfter = await ReadAffectedReadinessAsync(
            connection, transaction, command.CaseOperationId, cancellationToken);
        foreach (var (operationId, current) in readinessAfter)
        {
            readinessBefore.TryGetValue(operationId, out var previous);
            await SqliteReadinessAudit.AppendEvaluationAsync(
                connection, transaction, current.Context, previous?.Result,
                current.Result, command.ReleasedAt, releasedBy,
                command.ChangeScope, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return release;
    }

    public Task<StoredReleaseFile?> ReadGCodeFileAsync(
        string caseOperationId,
        string releaseId,
        CancellationToken cancellationToken) =>
        ReadStoredFileAsync(
            "gcode_releases", "id", releaseId, caseOperationId, cancellationToken);

    public async Task<StoredReleaseFile?> ReadProgramGCodeFileAsync(
        string manufacturingProgramId,
        string releaseId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT release.id, release.original_file_name, release.stored_relative_path,
                   release.file_size, release.file_hash
            FROM gcode_releases release
            JOIN process_revisions revision ON revision.id = release.process_revision_id
            WHERE release.id = $releaseId AND revision.manufacturing_program_id = $programId;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$programId", manufacturingProgramId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredReleaseFile(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetString(4))
            : null;
    }

    public Task<StoredReleaseFile?> ReadToolTableFileAsync(
        string caseOperationId,
        string toolTableReleaseId,
        CancellationToken cancellationToken) =>
        ReadStoredFileAsync(
            "tool_table_releases", "id", toolTableReleaseId, caseOperationId, cancellationToken);

    public async Task<StoredReleaseFile?> ReadProgramToolTableFileAsync(
        string manufacturingProgramId,
        string toolTableReleaseId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tool.id, tool.original_file_name, tool.stored_relative_path,
                   tool.file_size, tool.file_hash
            FROM tool_table_releases tool
            JOIN process_revisions revision ON revision.tool_table_release_id = tool.id
            WHERE tool.id = $toolId AND revision.manufacturing_program_id = $programId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$toolId", toolTableReleaseId);
        command.Parameters.AddWithValue("$programId", manufacturingProgramId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredReleaseFile(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetString(4))
            : null;
    }

    public async Task<ProgramPublicationContext?> ResolveProgramPublicationContextAsync(
        string manufacturingProgramId,
        IReadOnlyList<ManufacturingProgramRevisionOutput>? outputs,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var operationId = outputs?.OrderBy(value => value.DisplayOrder).FirstOrDefault()?.CaseOperationId;
        await using var command = connection.CreateCommand();
        command.CommandText = operationId is null
            ? """
                SELECT operation.case_id, revision.case_operation_id
                FROM manufacturing_programs program
                JOIN process_revisions revision
                  ON revision.manufacturing_program_id = program.id AND revision.is_active = 1
                JOIN case_operations operation ON operation.id = revision.case_operation_id
                WHERE program.id = $programId;
                """
            : """
                SELECT operation.case_id, operation.id
                FROM manufacturing_programs program
                JOIN case_operations operation ON operation.id = $operationId
                WHERE program.id = $programId;
                """;
        command.Parameters.AddWithValue("$programId", manufacturingProgramId);
        if (operationId is not null) command.Parameters.AddWithValue("$operationId", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ProgramPublicationContext(reader.GetString(0), reader.GetString(1))
            : null;
    }

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
            WHERE pr.manufacturing_program_id = 'case-operation:' || $operationId
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
        IReadOnlyDictionary<string, NcProgramAnalysis> analyses,
        IReadOnlyDictionary<string, IReadOnlyList<NcMachineCycleEstimate>> estimates,
        IReadOnlyDictionary<string, Meimad.Planner.Server.Domain.Haas.NcHeaderMetadata> headers,
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
            var release = ReadRelease(reader);
            values.Add(release with
            {
                NcAnalysis = analyses.GetValueOrDefault(release.GCodeReleaseId),
                MachineCycleEstimates = estimates.GetValueOrDefault(release.GCodeReleaseId, []),
                HeaderMetadata = headers.GetValueOrDefault(release.GCodeReleaseId)
            });
        }

        return values;
    }

    private static async Task<IReadOnlyDictionary<string, Meimad.Planner.Server.Domain.Haas.NcHeaderMetadata>> ReadHeadersAsync(
        SqliteConnection connection, string operationId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT header.gcode_release_id, header.status, header.part_name,
                   header.case_number, header.operation, header.revision,
                   header.program_number, header.raw_header, header.parser_version
            FROM nc_program_headers header
            JOIN gcode_releases release ON release.id = header.gcode_release_id
            WHERE release.case_operation_id = $operationId;
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        var values = new Dictionary<string, Meimad.Planner.Server.Domain.Haas.NcHeaderMetadata>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            values[reader.GetString(0)] = new Meimad.Planner.Server.Domain.Haas.NcHeaderMetadata(
                reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7), reader.GetString(8));
        }
        return values;
    }

    private static async Task InsertHeaderAsync(
        SqliteConnection connection, SqliteTransaction transaction, string releaseId,
        Meimad.Planner.Server.Domain.Haas.NcHeaderMetadata value, DateTimeOffset at,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO nc_program_headers
                (gcode_release_id, status, part_name, case_number, operation,
                 revision, program_number, raw_header, parser_version, parsed_at)
            VALUES ($id, $status, $part, $case, $operation, $revision,
                    $program, $raw, $parser, $at);
            """;
        command.Parameters.AddWithValue("$id", releaseId);
        command.Parameters.AddWithValue("$status", value.Status);
        command.Parameters.AddWithValue("$part", (object?)value.PartName ?? DBNull.Value);
        command.Parameters.AddWithValue("$case", (object?)value.CaseNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("$operation", (object?)value.Operation ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", (object?)value.Revision ?? DBNull.Value);
        command.Parameters.AddWithValue("$program", (object?)value.ProgramNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("$raw", value.RawHeader);
        command.Parameters.AddWithValue("$parser", value.ParserVersion);
        command.Parameters.AddWithValue("$at", at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(token);
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

    private static Task<ProcessRevision?> ReadActiveProcessAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        CancellationToken token) =>
        ReadActiveProcessAsync(connection, transaction, DefaultProgramId(operationId), operationId, token);

    private static async Task<ProcessRevision?> ReadActiveProcessAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string programId,
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
            WHERE pr.manufacturing_program_id = $programId AND pr.is_active = 1;
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        command.Parameters.AddWithValue("$programId", programId);
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
                change_description, version, updated_at, manufacturing_program_id)
            VALUES ($id, $operationId, $revision, 1, $toolTableId, $at, $by,
                    $description, 1, $at, $programId);
            """;
        command.Parameters.AddWithValue("$id", value.ProcessRevisionId);
        command.Parameters.AddWithValue("$operationId", value.CaseOperationId);
        command.Parameters.AddWithValue("$revision", value.ProcessRevisionNumber);
        command.Parameters.AddWithValue("$toolTableId", value.ToolTableReleaseId);
        command.Parameters.AddWithValue("$at", Format(value.CreatedAt));
        command.Parameters.AddWithValue("$by", value.CreatedBy);
        command.Parameters.AddWithValue("$description", value.ChangeDescription);
        command.Parameters.AddWithValue("$programId", value.ManufacturingProgramId ?? DefaultProgramId(value.CaseOperationId));
        await command.ExecuteNonQueryAsync(token);

        foreach (var output in value.Outputs ??
                 [new ManufacturingProgramRevisionOutput(
                     $"output:{value.ProcessRevisionId}:{value.CaseOperationId}",
                     value.CaseOperationId, 1, 0,
                     $$"""{"caseOperationId":"{{value.CaseOperationId}}"}""")])
        {
            await using var outputCommand = connection.CreateCommand();
            outputCommand.Transaction = transaction;
            outputCommand.CommandText = """
                INSERT INTO manufacturing_program_revision_outputs (
                    id, process_revision_id, case_operation_id, quantity_per_cycle,
                    display_order, execution_metadata_json, created_at)
                VALUES ($id, $revisionId, $operationId, $quantity,
                        $displayOrder, $metadata, $at);
                """;
            outputCommand.Parameters.AddWithValue("$id", output.OutputId);
            outputCommand.Parameters.AddWithValue("$revisionId", value.ProcessRevisionId);
            outputCommand.Parameters.AddWithValue("$operationId", output.CaseOperationId);
            outputCommand.Parameters.AddWithValue("$quantity", output.QuantityPerCycle);
            outputCommand.Parameters.AddWithValue("$displayOrder", output.DisplayOrder);
            outputCommand.Parameters.AddWithValue("$metadata", output.ExecutionMetadataJson);
            outputCommand.Parameters.AddWithValue("$at", Format(value.CreatedAt));
            await outputCommand.ExecuteNonQueryAsync(token);
        }
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

    private static async Task AppendReleaseAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GCodeRelease release,
        ProcessRevision process,
        ToolTableRelease toolTable,
        ProcessRevision? previousActiveProcess,
        bool createsProcess,
        bool createdToolTable,
        string actor,
        string comment,
        CancellationToken token)
    {
        var entities = new Dictionary<string, string>
        {
            ["caseOperationId"] = release.CaseOperationId,
            ["processRevisionId"] = process.ProcessRevisionId,
            ["gcodeReleaseId"] = release.GCodeReleaseId,
            ["postprocessorId"] = release.PostprocessorId,
            ["toolTableReleaseId"] = toolTable.ToolTableReleaseId
        };
        var releaseData = new
        {
            processRevisionNumber = process.ProcessRevisionNumber,
            postSpecificRevision = release.PostSpecificRevision,
            release.ChangeScope,
            gcodeHash = release.FileHash,
            toolTableHash = toolTable.FileHash
        };

        if (createdToolTable)
        {
            await SqliteStructuredEventLogRepository.AppendAsync(
                connection, transaction,
                new(
                    "tool_table_release_published", release.ReleasedAt, actor, entities,
                    release.ChangeScope, comment, null,
                    new
                    {
                        toolTable.ToolTableReleaseId,
                        toolTable.RevisionNumber,
                        toolTable.RequiredToolCount,
                        toolTable.FileHash
                    }),
                token);
        }

        if (createsProcess)
        {
            await SqliteStructuredEventLogRepository.AppendAsync(
                connection, transaction,
                new(
                    "process_revision_created", release.ReleasedAt, actor, entities,
                    release.ChangeScope, process.ChangeDescription, null,
                    new
                    {
                        process.ProcessRevisionId,
                        process.ProcessRevisionNumber,
                        process.ToolTableReleaseId
                    }),
                token);
            await SqliteStructuredEventLogRepository.AppendAsync(
                connection, transaction,
                new(
                    "process_revision_activated", release.ReleasedAt, actor, entities,
                    release.ChangeScope, process.ChangeDescription,
                    previousActiveProcess is null ? null : new
                    {
                        previousActiveProcess.ProcessRevisionId,
                        previousActiveProcess.ProcessRevisionNumber
                    },
                    new
                    {
                        process.ProcessRevisionId,
                        process.ProcessRevisionNumber
                    }),
                token);
        }
        else
        {
            await SqliteStructuredEventLogRepository.AppendAsync(
                connection, transaction,
                new(
                    "local_post_revision_published", release.ReleasedAt, actor, entities,
                    release.ChangeScope, comment, null, releaseData),
                token);
        }

        await SqliteStructuredEventLogRepository.AppendAsync(
            connection, transaction,
            new(
                "gcode_release_published", release.ReleasedAt, actor, entities,
                release.ChangeScope, comment, null, releaseData),
            token);
    }

    private static async Task<IReadOnlyDictionary<string, ReadinessAuditSnapshot>>
        ReadAffectedReadinessAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string caseOperationId,
            CancellationToken token)
    {
        var operationIds = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id
                FROM batch_operations
                WHERE source_case_operation_id = $caseOperationId
                  AND status = 'not_started'
                ORDER BY id;
                """;
            command.Parameters.AddWithValue("$caseOperationId", caseOperationId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) operationIds.Add(reader.GetString(0));
        }

        var values = new Dictionary<string, ReadinessAuditSnapshot>(StringComparer.Ordinal);
        foreach (var operationId in operationIds)
        {
            var context = await SqliteProductionReadinessContextReader.ReadAsync(
                connection, transaction, operationId, token);
            if (context is null) continue;
            values.Add(operationId, new(
                context, ProductionReadinessEvaluator.Evaluate(context)));
        }
        return values;
    }

    private sealed record ReadinessAuditSnapshot(
        ProductionReadinessContext Context,
        ProductionReadinessResult Result);

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

    private static async Task<bool> ProgramExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string programId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM manufacturing_programs WHERE id = $id);";
        command.Parameters.AddWithValue("$id", programId);
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

    private static string DefaultProgramId(string operationId) => $"case-operation:{operationId}";
}
