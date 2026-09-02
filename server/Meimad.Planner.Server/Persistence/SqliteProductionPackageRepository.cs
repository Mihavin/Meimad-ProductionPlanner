using System.Globalization;
using Meimad.Planner.Server.Application.ProductionPackages;
using Meimad.Planner.Server.Domain.Readiness;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteProductionPackageRepository(SqliteDatabase database)
    : IProductionPackageRepository
{
    public async Task<ProductionPackageBuildContext?> ReadBuildContextAsync(
        string batchOperationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var readiness = await SqliteProductionReadinessContextReader.ReadAsync(
            connection, transaction, batchOperationId, cancellationToken);
        if (readiness is null) return null;
        var evaluated = ProductionReadinessEvaluator.Evaluate(readiness);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT assignment.id,machine.id,machine.number,machine.name,machine.execution_mode,
                   case_record.name,source_operation.name,
                   (SELECT program.production_run_id
                    FROM production_run_programs program
                    JOIN production_run_outputs output
                      ON output.production_run_program_id=program.id
                    WHERE output.batch_operation_id=operation.id
                    ORDER BY program.sequence_position,program.id LIMIT 1),
                   settings.enabled,settings.version,settings.challenge_program_number,
                   settings.verify_program_number,settings.expected_macro_version,
                   settings.event_sequence_variable,
                   connection.enabled,connection.allow_write,connection.connection_status,
                   current.production_package_id,
                   COALESCE(package_capability.allow_manual_dummy_tool_offsets,0)
            FROM batch_operations operation
            JOIN case_operations source_operation ON source_operation.id=operation.source_case_operation_id
            JOIN cases case_record ON case_record.id=source_operation.case_id
            JOIN machine_assignments assignment ON assignment.batch_operation_id=operation.id
            JOIN machines machine ON machine.id=assignment.machine_id
            LEFT JOIN cnc_verification_settings settings ON settings.machine_id=machine.id
            LEFT JOIN machine_connections connection ON connection.machine_id=machine.id
            LEFT JOIN production_package_current current ON current.batch_operation_id=operation.id
            LEFT JOIN machine_package_capabilities package_capability ON package_capability.machine_id=machine.id
            WHERE operation.id=$operationId
            ORDER BY assignment.id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$operationId", batchOperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var assignmentId = reader.GetString(0);
        var machineId = reader.GetString(1);
        var machineNumber = reader.GetString(2);
        var machineName = reader.GetString(3);
        var executionMode = reader.GetString(4);
        var partName = reader.GetString(5);
        var operationName = reader.GetString(6);
        var runId = Nullable(reader, 7);
        ProductionPackageVerificationConfiguration? verification = null;
        if (executionMode == "CNC_GCODE" && !reader.IsDBNull(8) && reader.GetBoolean(8))
        {
            if (reader.IsDBNull(13))
                throw new ProductionPackageBuildException(
                    "production_package_verification_configuration_incomplete",
                    "Server Verification is enabled, but its event-sequence variable is not configured.");
            verification = new(reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11),
                reader.GetInt32(12), reader.GetInt32(13));
        }
        var directConfigured = !reader.IsDBNull(14) && reader.GetBoolean(14)
            && !reader.IsDBNull(15) && reader.GetBoolean(15);
        var directOnline = directConfigured && !reader.IsDBNull(16)
            && reader.GetString(16) == "ONLINE";
        var currentPackageId = Nullable(reader, 17);
        var manualDummyAllowed = reader.GetBoolean(18);
        await reader.DisposeAsync();

        if (readiness.ActiveToolTableReleaseId is null)
            throw new ProductionPackageBuildException(
                "production_package_tool_table_missing",
                "No current Tool Table release exists for the active process.");

        var gcodeId = executionMode == "MANUAL" ? null : evaluated.EffectiveGCodeReleaseId;
        var gcode = gcodeId is null ? null : await ReadReleaseFileAsync(
            connection, transaction, "gcode_releases", gcodeId, cancellationToken);
        var ncIdentityToken = gcodeId is null ? null : await ReadNcIdentityTokenAsync(
            connection, transaction, gcodeId, cancellationToken);
        var tool = await ReadReleaseFileAsync(connection, transaction, "tool_table_releases",
            readiness.ActiveToolTableReleaseId, cancellationToken)
            ?? throw new ProductionPackageBuildException(
                "production_package_tool_table_missing",
                "The current Tool Table release artifact could not be resolved.");
        await transaction.CommitAsync(cancellationToken);
        return new(
            batchOperationId, runId, assignmentId, machineId, machineNumber, machineName,
            executionMode, partName, operationName,
            gcodeId, gcode?.OriginalName, gcode?.StoredPath, gcode?.Hash, ncIdentityToken,
            readiness.ActiveToolTableReleaseId, tool.OriginalName, tool.StoredPath, tool.Hash,
            verification, directConfigured, directOnline, manualDummyAllowed, currentPackageId, readiness);
    }

    public async Task ActivateAsync(
        ProductionPackageRecord package,
        OffsetLoaderPublication? offsetLoader,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (!await ContextStillMatchesAsync(connection, transaction, package, cancellationToken))
            throw new ProductionPackageBuildException(
                "production_package_context_changed",
                "The assigned Machine, current release, or verification configuration changed during package build; no package was activated.");
        if (offsetLoader is not null)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO offset_loader_releases (
                    id,production_run_id,machine_id,nc_release_id,tool_table_release_id,
                    verification_release_token,artifact_hash,created_at,created_by,metadata_json)
                VALUES ($id,$runId,$machineId,$ncId,$toolId,$token,$hash,$at,$by,
                        json_object('productionPackageId',$packageId));
                """, cancellationToken,
                ("$id", offsetLoader.ReleaseId), ("$runId", package.ProductionRunId!),
                ("$machineId", package.MachineId), ("$ncId", package.GCodeReleaseId!),
                ("$toolId", package.ToolTableReleaseId), ("$token", offsetLoader.ReleaseToken),
                ("$hash", offsetLoader.ArtifactHash), ("$at", Format(package.CreatedAt)),
                ("$by", package.CreatedBy), ("$packageId", package.ProductionPackageId));
        }

        await ExecuteAsync(connection, transaction, """
            INSERT INTO production_packages (
                id,batch_operation_id,production_run_id,machine_assignment_id,machine_id,
                gcode_release_id,tool_table_release_id,offset_loader_release_id,execution_mode,
                verification_enabled,verification_configuration_version,verification_macro_version,tool_offset_mode,
                manifest_relative_path,manifest_hash,created_at,created_by,supersedes_package_id)
            VALUES ($id,$operationId,$runId,$assignmentId,$machineId,$gcodeId,$toolId,$loaderId,
                    $mode,$verification,$configVersion,$macroVersion,$offsetMode,$manifestPath,$manifestHash,
                    $at,$by,$supersedes);
            """, cancellationToken,
            ("$id", package.ProductionPackageId), ("$operationId", package.BatchOperationId),
            ("$runId", Db(package.ProductionRunId)), ("$assignmentId", package.MachineAssignmentId),
            ("$machineId", package.MachineId), ("$gcodeId", Db(package.GCodeReleaseId)),
            ("$toolId", package.ToolTableReleaseId), ("$loaderId", Db(package.OffsetLoaderReleaseId)),
            ("$mode", package.ExecutionMode), ("$verification", package.VerificationEnabled ? 1 : 0),
            ("$configVersion", Db(package.VerificationConfigurationVersion)),
            ("$macroVersion", Db(package.VerificationMacroVersion)),
            ("$offsetMode", package.ToolOffsetMode),
            ("$manifestPath", package.ManifestRelativePath), ("$manifestHash", package.ManifestHash),
            ("$at", Format(package.CreatedAt)), ("$by", package.CreatedBy),
            ("$supersedes", Db(package.SupersedesPackageId)));

        foreach (var artifact in package.Artifacts)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO production_package_artifacts (
                    id,production_package_id,artifact_type,logical_path,stored_relative_path,
                    file_size,file_hash,source_release_id)
                VALUES ($id,$packageId,$type,$logical,$stored,$size,$hash,$source);
                """, cancellationToken,
                ("$id", artifact.ArtifactId), ("$packageId", package.ProductionPackageId),
                ("$type", artifact.ArtifactType), ("$logical", artifact.LogicalPath),
                ("$stored", artifact.StoredRelativePath), ("$size", artifact.FileSize),
                ("$hash", artifact.FileHash), ("$source", Db(artifact.SourceReleaseId)));
        }

        if (package.SupersedesPackageId is not null)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT OR IGNORE INTO production_package_invalidations (
                    id,production_package_id,replacement_package_id,reason,invalidated_at)
                VALUES ($id,$oldId,$newId,'SUPERSEDED_BY_NEW_PACKAGE',$at);
                """, cancellationToken,
                ("$id", Guid.NewGuid().ToString("N")), ("$oldId", package.SupersedesPackageId),
                ("$newId", package.ProductionPackageId), ("$at", Format(package.CreatedAt)));
        }

        await ExecuteAsync(connection, transaction, """
            INSERT INTO production_package_current (
                batch_operation_id,machine_id,production_package_id,activated_at)
            VALUES ($operationId,$machineId,$packageId,$at)
            ON CONFLICT(batch_operation_id) DO UPDATE SET
                machine_id=excluded.machine_id,
                production_package_id=excluded.production_package_id,
                activated_at=excluded.activated_at;
            """, cancellationToken,
            ("$operationId", package.BatchOperationId), ("$machineId", package.MachineId),
            ("$packageId", package.ProductionPackageId), ("$at", Format(package.CreatedAt)));

        if (offsetLoader is not null)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO production_run_current_offset_loaders (
                    production_run_id,machine_id,offset_loader_release_id,selected_at,selected_by,version)
                VALUES ($runId,$machineId,$loaderId,$at,$by,1)
                ON CONFLICT(production_run_id) DO UPDATE SET
                    machine_id=excluded.machine_id,
                    offset_loader_release_id=excluded.offset_loader_release_id,
                    selected_at=excluded.selected_at,
                    selected_by=excluded.selected_by,
                    version=production_run_current_offset_loaders.version+1;
                """, cancellationToken,
                ("$runId", package.ProductionRunId!), ("$machineId", package.MachineId),
                ("$loaderId", offsetLoader.ReleaseId), ("$at", Format(package.CreatedAt)),
                ("$by", package.CreatedBy));
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ProductionPackageRecord?> ReadCurrentAsync(
        string batchOperationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT package.id,package.batch_operation_id,package.production_run_id,
                   package.machine_assignment_id,package.machine_id,package.gcode_release_id,
                   package.tool_table_release_id,package.offset_loader_release_id,
                   package.execution_mode,package.verification_enabled,
                   package.verification_configuration_version,package.verification_macro_version,
                   package.manifest_relative_path,package.manifest_hash,package.created_at,
                   package.created_by,package.supersedes_package_id,package.tool_offset_mode,
                   connection.enabled,connection.allow_write,connection.connection_status
            FROM production_package_current current
            JOIN production_packages package ON package.id=current.production_package_id
            JOIN machine_assignments assignment
              ON assignment.id=package.machine_assignment_id
             AND assignment.batch_operation_id=package.batch_operation_id
             AND assignment.machine_id=package.machine_id
            JOIN batch_operations operation ON operation.id=package.batch_operation_id
            JOIN machines machine ON machine.id=package.machine_id
            JOIN process_revisions process
              ON process.case_operation_id=operation.source_case_operation_id AND process.is_active=1
             AND process.tool_table_release_id=package.tool_table_release_id
            LEFT JOIN cnc_verification_settings settings ON settings.machine_id=package.machine_id
            LEFT JOIN machine_connections connection ON connection.machine_id=package.machine_id
            LEFT JOIN machine_package_capabilities package_capability ON package_capability.machine_id=package.machine_id
            WHERE current.batch_operation_id=$operationId
              AND machine.execution_mode=package.execution_mode
              AND (package.tool_offset_mode='MEASURED'
                   OR COALESCE(package_capability.allow_manual_dummy_tool_offsets,0)=1)
              AND ((package.execution_mode='MANUAL' AND package.gcode_release_id IS NULL)
                   OR (package.execution_mode='CNC_GCODE'
                       AND package.gcode_release_id=COALESCE(
                           assignment.selected_gcode_release_id,
                           (SELECT release.id FROM gcode_releases release
                            JOIN machine_supported_postprocessors supported
                              ON supported.machine_id=package.machine_id
                             AND supported.postprocessor_id=release.postprocessor_id
                            WHERE release.process_revision_id=process.id
                              AND release.post_specific_revision=(
                                  SELECT MAX(latest.post_specific_revision)
                                  FROM gcode_releases latest
                                  WHERE latest.process_revision_id=release.process_revision_id
                                    AND latest.postprocessor_id=release.postprocessor_id)
                            ORDER BY release.id LIMIT 1))))
              AND (package.execution_mode='MANUAL'
                   OR (package.verification_enabled=0 AND COALESCE(settings.enabled,0)=0)
                   OR (package.verification_enabled=1 AND settings.enabled=1
                       AND settings.version=package.verification_configuration_version
                       AND settings.expected_macro_version=package.verification_macro_version));
            """;
        command.Parameters.AddWithValue("$operationId", batchOperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var record = ReadPackage(reader, []);
        await reader.DisposeAsync();
        var artifacts = await ReadArtifactsAsync(connection, record.ProductionPackageId, cancellationToken);
        return record with { Artifacts = artifacts };
    }

    private static async Task<bool> ContextStillMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionPackageRecord package,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM batch_operations operation
                JOIN machine_assignments assignment
                  ON assignment.batch_operation_id=operation.id
                 AND assignment.id=$assignmentId
                 AND assignment.machine_id=$machineId
                JOIN machines machine
                  ON machine.id=assignment.machine_id
                 AND machine.execution_mode=$mode
                JOIN process_revisions process
                  ON process.case_operation_id=operation.source_case_operation_id
                 AND process.is_active=1
                 AND process.tool_table_release_id=$toolId
                LEFT JOIN cnc_verification_settings settings ON settings.machine_id=machine.id
                LEFT JOIN machine_package_capabilities package_capability ON package_capability.machine_id=machine.id
                WHERE operation.id=$operationId
                  AND (($mode='MANUAL' AND $gcodeId IS NULL)
                       OR ($mode='CNC_GCODE' AND $gcodeId=COALESCE(
                           assignment.selected_gcode_release_id,
                           (SELECT release.id FROM gcode_releases release
                            JOIN machine_supported_postprocessors supported
                              ON supported.machine_id=machine.id
                             AND supported.postprocessor_id=release.postprocessor_id
                            WHERE release.process_revision_id=process.id
                              AND release.post_specific_revision=(
                                  SELECT MAX(latest.post_specific_revision)
                                  FROM gcode_releases latest
                                  WHERE latest.process_revision_id=release.process_revision_id
                                    AND latest.postprocessor_id=release.postprocessor_id)
                            ORDER BY release.id LIMIT 1))))
                  AND ($offsetMode='MEASURED'
                       OR COALESCE(package_capability.allow_manual_dummy_tool_offsets,0)=1)
                  AND ($mode='MANUAL'
                       OR ($verification=0 AND COALESCE(settings.enabled,0)=0)
                       OR ($verification=1 AND settings.enabled=1
                           AND settings.version=$configVersion
                           AND settings.expected_macro_version=$macroVersion))
                  AND ($runId IS NULL OR EXISTS (
                      SELECT 1 FROM production_run_programs program
                      JOIN production_run_outputs output
                        ON output.production_run_program_id=program.id
                      WHERE program.production_run_id=$runId
                        AND output.batch_operation_id=operation.id))
            );
            """;
        command.Parameters.AddWithValue("$operationId", package.BatchOperationId);
        command.Parameters.AddWithValue("$assignmentId", package.MachineAssignmentId);
        command.Parameters.AddWithValue("$machineId", package.MachineId);
        command.Parameters.AddWithValue("$mode", package.ExecutionMode);
        command.Parameters.AddWithValue("$offsetMode", package.ToolOffsetMode);
        command.Parameters.AddWithValue("$gcodeId", Db(package.GCodeReleaseId));
        command.Parameters.AddWithValue("$toolId", package.ToolTableReleaseId);
        command.Parameters.AddWithValue("$verification", package.VerificationEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$configVersion", Db(package.VerificationConfigurationVersion));
        command.Parameters.AddWithValue("$macroVersion", Db(package.VerificationMacroVersion));
        command.Parameters.AddWithValue("$runId", Db(package.ProductionRunId));
        return Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1;
    }

    private static ProductionPackageRecord ReadPackage(
        SqliteDataReader reader,
        IReadOnlyList<ProductionPackageArtifact> artifacts)
    {
        var configured = !reader.IsDBNull(18) && reader.GetBoolean(18)
            && !reader.IsDBNull(19) && reader.GetBoolean(19);
        return new(
            reader.GetString(0), reader.GetString(1), Nullable(reader, 2), reader.GetString(3),
            reader.GetString(4), Nullable(reader, 5), reader.GetString(6), Nullable(reader, 7),
            reader.GetString(8), reader.GetString(17), reader.GetBoolean(9), NullableInt(reader, 10), NullableInt(reader, 11),
            reader.GetString(12), reader.GetString(13), Parse(reader.GetString(14)), reader.GetString(15),
            Nullable(reader, 16), configured,
            configured && !reader.IsDBNull(20) && reader.GetString(20) == "ONLINE", artifacts);
    }

    private static async Task<IReadOnlyList<ProductionPackageArtifact>> ReadArtifactsAsync(
        SqliteConnection connection, string packageId, CancellationToken token)
    {
        var values = new List<ProductionPackageArtifact>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,artifact_type,logical_path,stored_relative_path,file_size,file_hash,source_release_id
            FROM production_package_artifacts WHERE production_package_id=$id ORDER BY logical_path;
            """;
        command.Parameters.AddWithValue("$id", packageId);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) values.Add(new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt64(4), reader.GetString(5), Nullable(reader, 6)));
        return values;
    }

    private static async Task<ReleaseFile?> ReadReleaseFileAsync(
        SqliteConnection connection, SqliteTransaction transaction, string table, string id,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT original_file_name,stored_relative_path,file_hash FROM {table} WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    private static async Task<int?> ReadNcIdentityTokenAsync(
        SqliteConnection connection, SqliteTransaction transaction, string releaseId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT nc_identity_token FROM gcode_release_verification_hooks WHERE gcode_release_id=$id;";
        command.Parameters.AddWithValue("$id", releaseId);
        var result = await command.ExecuteScalarAsync(token);
        return result is null or DBNull ? null : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql,
        CancellationToken token, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token);
    }

    private static object Db(object? value) => value ?? DBNull.Value;
    private static string? Nullable(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static int? NullableInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private sealed record ReleaseFile(string OriginalName, string StoredPath, string Hash);
}
