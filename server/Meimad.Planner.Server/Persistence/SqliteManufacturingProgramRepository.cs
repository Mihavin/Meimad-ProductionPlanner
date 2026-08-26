using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.EventLogging;
using Meimad.Planner.Server.Application.GCode;
using Meimad.Planner.Server.Domain.GCode;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteManufacturingProgramRepository : IManufacturingProgramRepository
{
    private readonly SqliteDatabase database;
    private readonly TimeProvider timeProvider;

    public SqliteManufacturingProgramRepository(SqliteDatabase database, TimeProvider timeProvider)
    {
        this.database = database;
        this.timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<ManufacturingProgram>> ListAsync(CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        var ids = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM manufacturing_programs ORDER BY name COLLATE NOCASE, id;";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) ids.Add(reader.GetString(0));
        }

        var values = new List<ManufacturingProgram>(ids.Count);
        foreach (var id in ids)
        {
            var value = await ReadAsync(connection, null, id, token);
            if (value is not null) values.Add(value);
        }
        return values;
    }

    public async Task<ManufacturingProgram?> GetAsync(string programId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        return await ReadAsync(connection, null, programId, token);
    }

    public async Task<ManufacturingProgram> CreateAsync(
        CreateManufacturingProgramCommand command,
        EditAuthority authority,
        CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        var now = timeProvider.GetUtcNow();
        var programId = Guid.NewGuid().ToString("N");
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO manufacturing_programs
                    (id, name, default_case_operation_id, version, created_at, updated_at)
                VALUES ($id, $name, NULL, 1, $at, $at);
                """;
            insert.Parameters.AddWithValue("$id", programId);
            insert.Parameters.AddWithValue("$name", command.Name!);
            insert.Parameters.AddWithValue("$at", Format(now));
            await insert.ExecuteNonQueryAsync(token);
        }
        var revisionId = await InsertRevisionAsync(connection, transaction, programId,
            command.SourceProcessRevisionId!, command.ChangeDescription!, command.Outputs!, actor, now, token);
        await AppendAuditAsync(connection, transaction, "manufacturing_program_created",
            programId, revisionId, actor, command.ChangeDescription!, command.Outputs!, now, token);
        await transaction.CommitAsync(token);
        return (await GetAsync(programId, token))!;
    }

    public async Task<ManufacturingProgram> CreateRevisionAsync(
        string programId,
        int expectedVersion,
        CreateManufacturingProgramRevisionCommand command,
        EditAuthority authority,
        CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        if (!await ProgramExistsAsync(connection, transaction, programId, token))
            throw new ManufacturingProgramNotFoundException(programId);
        await using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "SELECT version FROM manufacturing_programs WHERE id = $id;";
            version.Parameters.AddWithValue("$id", programId);
            if (Convert.ToInt32(await version.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != expectedVersion)
                throw new ManufacturingProgramVersionConflictException(programId, expectedVersion);
        }
        var now = timeProvider.GetUtcNow();
        var revisionId = await InsertRevisionAsync(connection, transaction, programId,
            command.SourceProcessRevisionId!, command.ChangeDescription!, command.Outputs!, actor, now, token);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE manufacturing_programs SET version = version + 1, updated_at = $at WHERE id = $id;";
            update.Parameters.AddWithValue("$id", programId);
            update.Parameters.AddWithValue("$at", Format(now));
            await update.ExecuteNonQueryAsync(token);
        }
        await AppendAuditAsync(connection, transaction, "manufacturing_program_revision_created",
            programId, revisionId, actor, command.ChangeDescription!, command.Outputs!, now, token);
        await transaction.CommitAsync(token);
        return (await GetAsync(programId, token))!;
    }

    private static async Task<string> InsertRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string programId,
        string sourceRevisionId,
        string description,
        IReadOnlyList<ManufacturingProgramOutputInput> outputs,
        string actor,
        DateTimeOffset now,
        CancellationToken token)
    {
        string toolTableId;
        await using (var source = connection.CreateCommand())
        {
            source.Transaction = transaction;
            source.CommandText = "SELECT tool_table_release_id FROM process_revisions WHERE id = $id;";
            source.Parameters.AddWithValue("$id", sourceRevisionId);
            toolTableId = await source.ExecuteScalarAsync(token) as string
                ?? throw new ManufacturingProgramSourceRevisionNotFoundException(sourceRevisionId);
        }

        foreach (var output in outputs)
        {
            await using var exists = connection.CreateCommand();
            exists.Transaction = transaction;
            exists.CommandText = "SELECT EXISTS(SELECT 1 FROM case_operations WHERE id = $id);";
            exists.Parameters.AddWithValue("$id", output.CaseOperationId!);
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 0)
                throw new ManufacturingProgramOutputOperationNotFoundException(output.CaseOperationId!);
        }

        var ownerOperationId = outputs.OrderBy(value => value.DisplayOrder).First().CaseOperationId!;
        int revisionNumber;
        await using (var next = connection.CreateCommand())
        {
            next.Transaction = transaction;
            next.CommandText = """
                SELECT MAX(
                    COALESCE((SELECT MAX(revision_number) FROM process_revisions WHERE manufacturing_program_id = $programId), 0),
                    COALESCE((SELECT MAX(revision_number) FROM process_revisions WHERE case_operation_id = $operationId), 0)
                ) + 1;
                """;
            next.Parameters.AddWithValue("$programId", programId);
            next.Parameters.AddWithValue("$operationId", ownerOperationId);
            revisionNumber = Convert.ToInt32(await next.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
        }

        await using (var deactivate = connection.CreateCommand())
        {
            deactivate.Transaction = transaction;
            deactivate.CommandText = "UPDATE process_revisions SET is_active = 0, version = version + 1, updated_at = $at WHERE manufacturing_program_id = $id AND is_active = 1;";
            deactivate.Parameters.AddWithValue("$id", programId);
            deactivate.Parameters.AddWithValue("$at", Format(now));
            await deactivate.ExecuteNonQueryAsync(token);
        }

        var revisionId = Guid.NewGuid().ToString("N");
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO process_revisions (
                    id, case_operation_id, revision_number, is_active,
                    tool_table_release_id, created_at, created_by,
                    change_description, version, updated_at, manufacturing_program_id)
                VALUES ($id, $operationId, $number, 1, $toolTableId, $at,
                        $actor, $description, 1, $at, $programId);
                """;
            insert.Parameters.AddWithValue("$id", revisionId);
            insert.Parameters.AddWithValue("$operationId", ownerOperationId);
            insert.Parameters.AddWithValue("$number", revisionNumber);
            insert.Parameters.AddWithValue("$toolTableId", toolTableId);
            insert.Parameters.AddWithValue("$at", Format(now));
            insert.Parameters.AddWithValue("$actor", actor);
            insert.Parameters.AddWithValue("$description", description);
            insert.Parameters.AddWithValue("$programId", programId);
            await insert.ExecuteNonQueryAsync(token);
        }

        foreach (var output in outputs)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO manufacturing_program_revision_outputs (
                    id, process_revision_id, case_operation_id, quantity_per_cycle,
                    display_order, execution_metadata_json, created_at)
                VALUES ($id, $revisionId, $operationId, $quantity, $order, $metadata, $at);
                """;
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("$revisionId", revisionId);
            insert.Parameters.AddWithValue("$operationId", output.CaseOperationId!);
            insert.Parameters.AddWithValue("$quantity", output.QuantityPerCycle);
            insert.Parameters.AddWithValue("$order", output.DisplayOrder);
            insert.Parameters.AddWithValue("$metadata", output.ExecutionMetadataJson!);
            insert.Parameters.AddWithValue("$at", Format(now));
            await insert.ExecuteNonQueryAsync(token);
        }
        return revisionId;
    }

    private static Task AppendAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventType,
        string programId,
        string revisionId,
        string actor,
        string description,
        IReadOnlyList<ManufacturingProgramOutputInput> outputs,
        DateTimeOffset at,
        CancellationToken token) =>
        SqliteStructuredEventLogRepository.AppendAsync(connection, transaction,
            new StructuredEventWrite(eventType, at, actor,
                new Dictionary<string, string>
                {
                    ["manufacturingProgramId"] = programId,
                    ["processRevisionId"] = revisionId
                }, "MANUFACTURING_METHOD_CHANGE", description, null,
                new
                {
                    outputs = outputs.Select(value => new
                    {
                        value.CaseOperationId,
                        value.QuantityPerCycle,
                        value.DisplayOrder,
                        value.ExecutionMetadataJson
                    }).ToArray()
                }), token);

    private static async Task<ManufacturingProgram?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string programId,
        CancellationToken token)
    {
        string name;
        string? defaultOperation;
        int version;
        DateTimeOffset created;
        DateTimeOffset updated;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name, default_case_operation_id, version, created_at, updated_at FROM manufacturing_programs WHERE id = $id;";
            command.Parameters.AddWithValue("$id", programId);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return null;
            name = reader.GetString(0);
            defaultOperation = reader.IsDBNull(1) ? null : reader.GetString(1);
            version = reader.GetInt32(2);
            created = Parse(reader.GetString(3));
            updated = Parse(reader.GetString(4));
        }

        var outputs = new Dictionary<string, List<ManufacturingProgramRevisionOutput>>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT output.id, output.process_revision_id, output.case_operation_id,
                       output.quantity_per_cycle, output.display_order, output.execution_metadata_json
                FROM manufacturing_program_revision_outputs output
                JOIN process_revisions revision ON revision.id = output.process_revision_id
                WHERE revision.manufacturing_program_id = $id
                ORDER BY revision.revision_number DESC, output.display_order;
                """;
            command.Parameters.AddWithValue("$id", programId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var revisionId = reader.GetString(1);
                if (!outputs.TryGetValue(revisionId, out var list)) outputs[revisionId] = list = [];
                list.Add(new(reader.GetString(0), reader.GetString(2), reader.GetInt32(3),
                    reader.GetInt32(4), reader.GetString(5)));
            }
        }

        var toolRows = new Dictionary<string, List<ReleasedTool>>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT row.id, row.tool_table_release_id, row.row_number,
                       row.tool_identifier, row.description, row.is_required,
                       row.requires_magazine_position, row.is_active, row.magazine_position
                FROM tool_table_release_tools row
                JOIN process_revisions revision ON revision.tool_table_release_id = row.tool_table_release_id
                WHERE revision.manufacturing_program_id = $id
                ORDER BY row.tool_table_release_id, row.row_number;
                """;
            command.Parameters.AddWithValue("$id", programId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var releaseId = reader.GetString(1);
                if (!toolRows.TryGetValue(releaseId, out var rows)) toolRows[releaseId] = rows = [];
                rows.Add(new(reader.GetString(0), reader.GetInt32(2), reader.GetString(3),
                    reader.GetString(4), reader.GetBoolean(5), reader.GetBoolean(6),
                    reader.GetBoolean(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
        }

        var revisions = new List<ProcessRevision>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT revision.id, revision.case_operation_id, revision.revision_number,
                       revision.is_active, revision.tool_table_release_id, revision.created_at,
                       revision.created_by, revision.change_description, revision.version,
                       tool.revision_number, tool.original_file_name, tool.stored_relative_path,
                       tool.file_size, tool.file_hash, tool.released_at, tool.released_by,
                       tool.release_comment, tool.required_tool_count
                FROM process_revisions revision
                JOIN tool_table_releases tool ON tool.id = revision.tool_table_release_id
                WHERE revision.manufacturing_program_id = $id
                ORDER BY revision.revision_number DESC;
                """;
            command.Parameters.AddWithValue("$id", programId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var revisionId = reader.GetString(0);
                var tool = new ToolTableRelease(reader.GetString(4), reader.GetString(1), reader.GetInt32(9),
                    reader.GetString(10), reader.GetString(11), reader.GetInt64(12), reader.GetString(13),
                    Parse(reader.GetString(14)), reader.GetString(15), reader.GetString(16),
                    reader.IsDBNull(17) ? null : reader.GetInt32(17),
                    toolRows.GetValueOrDefault(reader.GetString(4), []));
                revisions.Add(new ProcessRevision(revisionId, reader.GetString(1), reader.GetInt32(2),
                    reader.GetBoolean(3), reader.GetString(4), Parse(reader.GetString(5)), reader.GetString(6),
                    reader.GetString(7), reader.GetInt32(8), tool, programId,
                    outputs.GetValueOrDefault(revisionId, [])));
            }
        }
        var releases = new List<ManufacturingProgramRelease>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT release.id, release.process_revision_id, release.postprocessor_id,
                       release.post_specific_revision, release.original_file_name,
                       release.file_size, release.file_hash, release.released_at,
                       release.released_by, release.change_scope, release.release_comment,
                       release.tool_table_release_id, hook.hook_version, hook.invocation_kind,
                       hook.invocation_number, hook.nc_identity_token, hook.line_number
                FROM gcode_releases release
                JOIN process_revisions revision ON revision.id = release.process_revision_id
                LEFT JOIN gcode_release_verification_hooks hook ON hook.gcode_release_id = release.id
                WHERE revision.manufacturing_program_id = $id
                ORDER BY revision.revision_number DESC, release.postprocessor_id,
                         release.post_specific_revision DESC;
                """;
            command.Parameters.AddWithValue("$id", programId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                releases.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetInt32(3), reader.GetString(4), reader.GetInt64(5), reader.GetString(6),
                    Parse(reader.GetString(7)), reader.GetString(8), reader.GetString(9),
                    reader.GetString(10), reader.GetString(11), reader.IsDBNull(12) ? null : new(
                        reader.GetInt32(12), reader.GetString(13), reader.GetInt32(14),
                        reader.GetInt32(15), reader.GetInt32(16))));
        }
        return new(programId, name, defaultOperation, version, created, updated,
            revisions.FirstOrDefault(value => value.IsActive), revisions, releases);
    }

    private static async Task<bool> ProgramExistsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string id, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM manufacturing_programs WHERE id = $id);";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<string> EnsureEditAuthorityAsync(
        SqliteConnection connection, SqliteTransaction transaction, EditAuthority authority, CancellationToken token)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(connection, transaction, DateTimeOffset.UtcNow, token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, holder_user_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0))
            throw new EditModeMutationException("edit_mode_required", "No Windows client currently holds Edit Mode.");
        if (!string.Equals(reader.GetString(0), authority.ClientId, StringComparison.Ordinal)
            || reader.GetInt64(2) != authority.Generation)
            throw new EditModeMutationException("edit_generation_stale", "This client does not hold the active Edit Mode generation.");
        return reader.IsDBNull(1) ? authority.ClientId : reader.GetString(1);
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
