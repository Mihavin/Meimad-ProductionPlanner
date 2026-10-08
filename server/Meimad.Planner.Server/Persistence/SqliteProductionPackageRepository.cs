using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Meimad.Planner.Server.Application.GCode;
using Meimad.Planner.Server.Application.ProductionPackages;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Domain.Readiness;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteProductionPackageRepository(SqliteDatabase database)
    : IProductionPackageRepository
{
    public async Task<ProductionPackageBuildContext?> ReadBuildContextAsync(
        string batchOperationId,
        CancellationToken cancellationToken)
        => await ReadBuildContextAsync(batchOperationId, null, cancellationToken);

    public async Task<ProductionPackageBuildContext?> ReadBuildContextAsync(
        string batchOperationId, ProductionPackageSelection? selection, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var result = await ReadBuildContextAsync(connection, transaction, batchOperationId, selection, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<ProductionPackageBuildContext?> ReadBuildContextAsync(
        SqliteConnection connection, SqliteTransaction transaction, string batchOperationId,
        ProductionPackageSelection? selection, CancellationToken cancellationToken)
    {
        var context = await SqliteProductionPackageContext.ResolveAsync(connection, transaction, batchOperationId, selection, cancellationToken);
        if (context is null) return null;
        var readiness = await SqliteProductionReadinessContextReader.ReadForPackageAsync(connection, transaction, context, cancellationToken);
        context = context with { ProcessRevisionId = readiness.ActiveProcessRevisionId };
        var evaluated = ProductionReadinessEvaluator.Evaluate(readiness);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT assignment.id,machine.id,machine.number,machine.name,machine.execution_mode,
                   case_record.name,source_operation.name,
                   assignment.production_run_id,
                   settings.enabled,settings.version,settings.challenge_program_number,
                   settings.verify_program_number,settings.expected_macro_version,
                   settings.event_sequence_variable,
                   connection.enabled,connection.allow_write,connection.connection_status,
                   current.production_package_id,
                   COALESCE(package_capability.allow_manual_dummy_tool_offsets,0),
                   machine.nc_dialect,machine.machine_type,machine.tool_diameter_offset_kind,
                   connection.configuration_json
            FROM batch_operations operation
            JOIN case_operations source_operation ON source_operation.id=COALESCE(
                (SELECT case_operation_id FROM process_revisions WHERE id=$processId),operation.source_case_operation_id)
            JOIN cases case_record ON case_record.id=source_operation.case_id
            JOIN machine_assignments assignment ON assignment.id=$assignmentId
             AND assignment.released_at IS NULL
            JOIN machines machine ON machine.id=assignment.machine_id
            LEFT JOIN cnc_verification_settings settings ON settings.machine_id=machine.id
            LEFT JOIN machine_connections connection ON connection.machine_id=machine.id
            LEFT JOIN production_package_context_current current ON current.machine_assignment_id=assignment.id
             AND current.production_run_program_id=$programId AND current.production_run_output_id=$outputId
            LEFT JOIN machine_package_capabilities package_capability ON package_capability.machine_id=machine.id
            WHERE operation.id=$operationId
            ;
            """;
        command.Parameters.AddWithValue("$operationId", batchOperationId);
        command.Parameters.AddWithValue("$assignmentId", context.MachineAssignmentId);
        command.Parameters.AddWithValue("$programId", context.ProductionRunProgramId);
        command.Parameters.AddWithValue("$outputId", context.ProductionRunOutputId);
        command.Parameters.AddWithValue("$processId", Db(context.ProcessRevisionId));
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
        var ncDialect = reader.GetString(19);
        var processType = reader.GetString(20);
        var toolDiameterOffsetKind = reader.GetString(21);
        // Part counting follows the DPRNT connection, not the verification switch: a configured
        // sequence variable is used even while verification is disabled, else the dialect default.
        ProductionPackagePartCounting? partCounting = null;
        if (executionMode == "CNC_GCODE" && !reader.IsDBNull(14) && reader.GetBoolean(14)
            && DprntSource(Nullable(reader, 22)) is { } dprntSource && dprntSource != CncDprntSources.None)
        {
            var dialectProfile = NcDialects.Profile(ncDialect);
            var configured = !reader.IsDBNull(13);
            partCounting = new(
                dprntSource,
                configured ? reader.GetInt32(13) : dialectProfile.DefaultEventSequenceVariable,
                !reader.IsDBNull(12) ? reader.GetInt32(12) : NcVerificationMacroGenerator.DefaultSettings(dialectProfile).MacroVersion,
                configured);
        }
        await reader.DisposeAsync();

        var runNumber = runId is null ? null : (int?)await EnsureRunNumberAsync(
            connection, transaction, runId, cancellationToken);

        if (readiness.ActiveToolTableReleaseId is null)
            throw new ProductionPackageBuildException(
                "production_package_tool_table_missing",
                "No current Tool Table release exists for the active process.");

        var gcodeId = executionMode == "MANUAL" ? null : evaluated.EffectiveGCodeReleaseId;
        var gcode = gcodeId is null ? null : await ReadReleaseFileAsync(
            connection, transaction, "gcode_releases", gcodeId, cancellationToken);
        var ncIdentityToken = gcodeId is null ? null : await ReadNcIdentityTokenAsync(
            connection, transaction, gcodeId, cancellationToken);
        var subprograms = gcodeId is null ? [] : await ReadSubprogramsAsync(
            connection, transaction, gcodeId, cancellationToken);
        var tool = await ReadReleaseFileAsync(connection, transaction, "tool_table_releases",
            readiness.ActiveToolTableReleaseId, cancellationToken)
            ?? throw new ProductionPackageBuildException(
                "production_package_tool_table_missing",
                "The current Tool Table release artifact could not be resolved.");
        // The Tool Room's measurements for this Machine: the package binds the exact version.
        var releasedTools = await SqliteToolPreparationRepository.ReadReleasedToolsAsync(
            connection, transaction, readiness.ActiveToolTableReleaseId, cancellationToken);
        var preparation = await SqliteToolPreparationRepository.ReadLatestAsync(
            connection, transaction, batchOperationId, machineId, cancellationToken);
        var publicationVersion = await ReadPublicationVersionAsync(connection, transaction, context, cancellationToken);
        await using var loader = connection.CreateCommand();
        loader.Transaction = transaction;
        loader.CommandText = "SELECT offset_loader_release_id || ':' || version || ':' || machine_id FROM production_run_current_offset_loaders WHERE production_run_id=$run;";
        loader.Parameters.AddWithValue("$run", Db(runId));
        var loaderStamp = await loader.ExecuteScalarAsync(cancellationToken) as string;
        return new(
            batchOperationId, runId, runNumber, assignmentId, machineId, machineNumber, machineName,
            executionMode, partName, operationName,
            gcodeId, gcode?.OriginalName, gcode?.StoredPath, gcode?.Hash, ncIdentityToken,
            readiness.ActiveToolTableReleaseId, tool.OriginalName, tool.StoredPath, tool.Hash,
            verification, directConfigured, directOnline, manualDummyAllowed, currentPackageId, readiness,
            ncDialect, processType, toolDiameterOffsetKind, releasedTools, preparation, partCounting,
            subprograms, context, publicationVersion, loaderStamp);
    }

    private static async Task<IReadOnlyList<ProductionPackageSubprogramSource>> ReadSubprogramsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string releaseId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, original_file_name, stored_relative_path, file_hash
            FROM gcode_release_subprograms
            WHERE gcode_release_id = $id
            ORDER BY position;
            """;
        command.Parameters.AddWithValue("$id", releaseId);
        var values = new List<ProductionPackageSubprogramSource>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            values.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return values;
    }

    /// <summary>
    /// The connection's DPRNT source (<c>dprnt.source</c> in its configuration JSON); TCP when the
    /// JSON predates the field, and NONE when the DPRNT output is switched off (<c>dprnt.enabled</c>).
    /// </summary>
    internal static string? DprntSource(string? configurationJson)
    {
        if (configurationJson is null) return null;
        try
        {
            using var document = JsonDocument.Parse(configurationJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return CncDprntSources.Tcp;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("dprnt", StringComparison.OrdinalIgnoreCase)
                    || property.Value.ValueKind != JsonValueKind.Object) continue;
                if (property.Value.EnumerateObject().Any(field =>
                        field.Name.Equals("enabled", StringComparison.OrdinalIgnoreCase) && field.Value.ValueKind == JsonValueKind.False))
                    return CncDprntSources.None;
                foreach (var field in property.Value.EnumerateObject())
                {
                    if (field.Name.Equals("source", StringComparison.OrdinalIgnoreCase) && field.Value.ValueKind == JsonValueKind.String)
                    {
                        var source = field.Value.GetString()?.Trim().ToUpperInvariant();
                        return string.IsNullOrEmpty(source) ? CncDprntSources.Tcp : source;
                    }
                }
            }
            return CncDprntSources.Tcp;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<int> AllocatePackageNumberAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        for (var attempt = 0; attempt < 25; attempt++)
        {
            var candidate = RandomNumberGenerator.GetInt32(100000, 1_000_000);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT NOT EXISTS(SELECT 1 FROM production_packages WHERE package_number=$number);";
            command.Parameters.AddWithValue("$number", candidate);
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture) == 1)
                return candidate;
        }
        throw new ProductionPackageBuildException(
            "production_package_number_exhausted",
            "Could not allocate a unique Production Package number.");
    }

    private static async Task<int> EnsureRunNumberAsync(
        SqliteConnection connection, SqliteTransaction transaction, string runId,
        CancellationToken token)
    {
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT run_number FROM production_runs WHERE id=$id;";
            select.Parameters.AddWithValue("$id", runId);
            var existing = await select.ExecuteScalarAsync(token);
            if (existing is int existingNumber) return existingNumber;
            if (existing is long existingLong) return (int)existingLong;
        }

        for (var attempt = 0; attempt < 25; attempt++)
        {
            var candidate = RandomNumberGenerator.GetInt32(100000, 1_000_000);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE production_runs SET run_number=$number
                WHERE id=$id AND run_number IS NULL
                  AND NOT EXISTS (SELECT 1 FROM production_runs WHERE run_number=$number);
                """;
            update.Parameters.AddWithValue("$number", candidate);
            update.Parameters.AddWithValue("$id", runId);
            if (await update.ExecuteNonQueryAsync(token) == 1) return candidate;

            await using var recheck = connection.CreateCommand();
            recheck.Transaction = transaction;
            recheck.CommandText = "SELECT run_number FROM production_runs WHERE id=$id;";
            recheck.Parameters.AddWithValue("$id", runId);
            var value = await recheck.ExecuteScalarAsync(token);
            if (value is int number) return number;
            if (value is long numberLong) return (int)numberLong;
        }
        throw new ProductionPackageBuildException(
            "production_package_run_number_exhausted",
            "Could not allocate a unique Production Run number.");
    }

    public async Task<ProductionPackageRecord?> ReadRequestAsync(ProductionPackageRequest request, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        return await ReadRequestAsync(connection, null, request, token);
    }

    private static async Task<ProductionPackageRecord?> ReadRequestAsync(SqliteConnection connection,
        SqliteTransaction? transaction, ProductionPackageRequest request, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT request_hash,result_json FROM production_package_requests WHERE actor_id=$actor AND request_id=$request;";
        command.Parameters.AddWithValue("$actor", request.ActorId);
        command.Parameters.AddWithValue("$request", request.RequestId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        if (reader.GetString(0) != request.RequestHash)
            throw new ProductionPackageBuildException("production_package_request_conflict",
                "This request key was already used with different package inputs. Review the request; use a new key only for deliberate regeneration.");
        return JsonSerializer.Deserialize<ProductionPackageRecord>(reader.GetString(1))
            ?? throw new InvalidDataException("The package request receipt is corrupt.");
    }

    public async Task<bool> IsReferencedAsync(string packageId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM production_packages WHERE id=$id);";
        command.Parameters.AddWithValue("$id", packageId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<long> ReadPublicationVersionAsync(SqliteConnection connection,
        SqliteTransaction transaction, ProductionPackageContext context, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT version FROM production_package_publication_versions
            WHERE machine_assignment_id=$assignment AND production_run_program_id=$program AND production_run_output_id=$output;
            """;
        command.Parameters.AddWithValue("$assignment", context.MachineAssignmentId);
        command.Parameters.AddWithValue("$program", context.ProductionRunProgramId);
        command.Parameters.AddWithValue("$output", context.ProductionRunOutputId);
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public async Task<ProductionPackageRecord> PublishAsync(ProductionPackageRecord package,
        OffsetLoaderPublication? loader, ProductionPackageBuildContext observed,
        ProductionPackageRequest? request, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        // Take the write reservation before reading any precondition. Competing publishers
        // serialize here; no generation or file hashing takes place under this reservation.
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (request is not null && await ReadRequestAsync(connection, transaction, request, token) is { } receipt)
            return receipt;
        if (package.Context is not { } exact)
            throw new ProductionPackageBuildException("production_package_context_required", "An exact context is required.");
        var current = await ReadBuildContextAsync(connection, transaction, package.BatchOperationId, exact.Selection, token)
            ?? throw new ProductionPackageBuildException("production_package_context_changed", "The assignment is no longer current.");
        if (current.PublicationVersion != observed.PublicationVersion || current.CurrentPackageId != observed.CurrentPackageId
            || package.SupersedesPackageId != current.CurrentPackageId)
            throw new ProductionPackageBuildException("production_package_publication_conflict",
                "Another package was published or retired during this build. Refresh and review the current package before regenerating.");
        if (ProductionPackagePublication.Fingerprint(current, package.ToolOffsetMode)
            != ProductionPackagePublication.Fingerprint(observed, package.ToolOffsetMode))
            throw new ProductionPackageBuildException("production_package_context_changed",
                "Package inputs, measurements, Machine configuration or the Run's Offset Loader changed during generation. Refresh before rebuilding.");
        await WriteAsync(connection, transaction, package, loader, token);
        if (request is not null)
            await ExecuteAsync(connection, transaction, """
                INSERT INTO production_package_requests(actor_id,request_id,request_hash,production_package_id,result_json,completed_at)
                VALUES($actor,$request,$hash,$package,$result,$at);
                """, token, ("$actor",request.ActorId),("$request",request.RequestId),("$hash",request.RequestHash),
                ("$package",package.ProductionPackageId),("$result",JsonSerializer.Serialize(package)),("$at",Format(package.CreatedAt)));
        await transaction.CommitAsync(token);
        return package;
    }

    private static async Task WriteAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        ProductionPackageRecord package,
        OffsetLoaderPublication? offsetLoader,
        CancellationToken cancellationToken)
    {
        if (package.Context is not { } exact)
            throw new ProductionPackageBuildException("production_package_context_required", "An exact Run/program/output context is required.");
        await SqliteProductionPackageContext.ResolveAsync(connection, transaction, package.BatchOperationId, exact.Selection, cancellationToken);
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
                        json_object('productionPackageId',$packageId,'machineAssignmentId',$assignmentId,'productionRunProgramId',$programId,'productionRunOutputId',$outputId));
                """, cancellationToken,
                ("$id", offsetLoader.ReleaseId), ("$runId", package.ProductionRunId!),
                ("$machineId", package.MachineId), ("$ncId", package.GCodeReleaseId!),
                ("$toolId", package.ToolTableReleaseId), ("$token", offsetLoader.ReleaseToken),
                ("$hash", offsetLoader.ArtifactHash), ("$at", Format(package.CreatedAt)),
                ("$by", package.CreatedBy), ("$packageId", package.ProductionPackageId),
                ("$assignmentId",exact.MachineAssignmentId),("$programId",exact.ProductionRunProgramId),("$outputId",exact.ProductionRunOutputId));
        }

        await ExecuteAsync(connection, transaction, """
            INSERT INTO production_packages (
                id,package_number,batch_operation_id,production_run_id,machine_assignment_id,machine_id,
                gcode_release_id,tool_table_release_id,offset_loader_release_id,execution_mode,
                verification_enabled,verification_configuration_version,verification_macro_version,tool_offset_mode,
                manifest_relative_path,manifest_hash,created_at,created_by,supersedes_package_id,tool_preparation_id)
            VALUES ($id,$number,$operationId,$runId,$assignmentId,$machineId,$gcodeId,$toolId,$loaderId,
                    $mode,$verification,$configVersion,$macroVersion,$offsetMode,$manifestPath,$manifestHash,
                    $at,$by,$supersedes,$preparationId);
            """, cancellationToken,
            ("$id", package.ProductionPackageId), ("$number", package.PackageNumber),
            ("$operationId", package.BatchOperationId),
            ("$runId", Db(package.ProductionRunId)), ("$assignmentId", package.MachineAssignmentId),
            ("$machineId", package.MachineId), ("$gcodeId", Db(package.GCodeReleaseId)),
            ("$toolId", package.ToolTableReleaseId), ("$loaderId", Db(package.OffsetLoaderReleaseId)),
            ("$mode", package.ExecutionMode), ("$verification", package.VerificationEnabled ? 1 : 0),
            ("$configVersion", Db(package.VerificationConfigurationVersion)),
            ("$macroVersion", Db(package.VerificationMacroVersion)),
            ("$offsetMode", package.ToolOffsetMode),
            ("$manifestPath", package.ManifestRelativePath), ("$manifestHash", package.ManifestHash),
            ("$at", Format(package.CreatedAt)), ("$by", package.CreatedBy),
            ("$supersedes", Db(package.SupersedesPackageId)), ("$preparationId", Db(package.ToolPreparationId)));

        await ExecuteAsync(connection, transaction, """
            INSERT INTO production_package_contexts(production_package_id,machine_assignment_id,
                production_run_program_id,production_run_output_id,context_json)
            VALUES($package,$assignment,$program,$output,$json);
            INSERT INTO production_package_context_current(machine_assignment_id,production_run_program_id,
                production_run_output_id,production_package_id)
            VALUES($assignment,$program,$output,$package)
            ON CONFLICT(machine_assignment_id,production_run_program_id,production_run_output_id)
            DO UPDATE SET production_package_id=excluded.production_package_id;
            """, cancellationToken, ("$package",package.ProductionPackageId),("$assignment",exact.MachineAssignmentId),
            ("$program",exact.ProductionRunProgramId),("$output",exact.ProductionRunOutputId),
            ("$json",JsonSerializer.Serialize(exact)));

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

        // Keep the old operation-only projection only when it cannot alias another live context.
        if ((await SqliteProductionPackageContext.ListAsync(connection,transaction,package.BatchOperationId,cancellationToken)).Count == 1
            && await IsLegacyAssignmentAsync(connection,transaction,exact,cancellationToken))
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
        // An operation back in setup for a newer G-code release now runs the package's release.
        if (package.GCodeReleaseId is not null)
            await SqliteSetupRestart.PinNewPackageAsync(
                connection, transaction, package.BatchOperationId, package.MachineAssignmentId,
                package.ProductionPackageId, package.GCodeReleaseId, package.CreatedAt, cancellationToken);
    }

    public Task<ProductionPackageRecord?> ReadCurrentAsync(string batchOperationId, CancellationToken cancellationToken)
        => ReadCurrentAsync(batchOperationId, null, cancellationToken);

    public async Task<ProductionPackageRecord?> ReadCurrentAsync(string batchOperationId,
        ProductionPackageSelection? selection, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var context = await SqliteProductionPackageContext.ResolveAsync(connection,transaction,batchOperationId,selection,cancellationToken);
        if (context is null) return null;
        return await ReadCurrentAsync(connection,transaction,context,cancellationToken);
    }

    internal static async Task<ProductionPackageRecord?> ReadCurrentAsync(SqliteConnection connection,
        SqliteTransaction transaction, ProductionPackageContext context, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT package.id,package.batch_operation_id,package.production_run_id,
                   package.machine_assignment_id,package.machine_id,package.gcode_release_id,
                   package.tool_table_release_id,package.offset_loader_release_id,
                   package.execution_mode,package.verification_enabled,
                   package.verification_configuration_version,package.verification_macro_version,
                   package.manifest_relative_path,package.manifest_hash,package.created_at,
                   package.created_by,package.supersedes_package_id,package.tool_offset_mode,
                   connection.enabled,connection.allow_write,connection.connection_status,
                   package.package_number,package.tool_preparation_id
            FROM production_package_context_current current
            JOIN production_packages package ON package.id=current.production_package_id
            LEFT JOIN machine_connections connection ON connection.machine_id=package.machine_id
            WHERE current.machine_assignment_id=$assignment AND current.production_run_program_id=$program
                AND current.production_run_output_id=$output;
            """;
        command.Parameters.AddWithValue("$assignment",context.MachineAssignmentId);
        command.Parameters.AddWithValue("$program",context.ProductionRunProgramId);
        command.Parameters.AddWithValue("$output",context.ProductionRunOutputId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var record = ReadPackage(reader, []);
        await reader.DisposeAsync();
        record = record with { Context = await ReadContextAsync(connection,transaction,record.ProductionPackageId,cancellationToken) };
        if (record.Context is null || !await ContextStillMatchesAsync(connection,transaction,record,cancellationToken)) return null;
        return record with { Artifacts = await ReadArtifactsAsync(connection,record.ProductionPackageId,cancellationToken,transaction) };
    }

    public async Task<ProductionPackageRecord?> ReadHistoricalAsync(string packageId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT package.id,package.batch_operation_id,package.production_run_id,
                   package.machine_assignment_id,package.machine_id,package.gcode_release_id,
                   package.tool_table_release_id,package.offset_loader_release_id,
                   package.execution_mode,package.verification_enabled,
                   package.verification_configuration_version,package.verification_macro_version,
                   package.manifest_relative_path,package.manifest_hash,package.created_at,
                   package.created_by,package.supersedes_package_id,package.tool_offset_mode,
                   connection.enabled,connection.allow_write,connection.connection_status,
                   package.package_number,package.tool_preparation_id
            FROM production_packages package
            LEFT JOIN machine_connections connection ON connection.machine_id=package.machine_id
            WHERE package.id=$id;
            """;
        command.Parameters.AddWithValue("$id",packageId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var record=ReadPackage(reader,[]);
        await reader.DisposeAsync();
        return record with { Context=await ReadContextAsync(connection,null,packageId,token),
            Artifacts=await ReadArtifactsAsync(connection,packageId,token) };
    }

    private static async Task<ProductionPackageContext?> ReadContextAsync(SqliteConnection connection,
        SqliteTransaction? transaction,string packageId,CancellationToken token)
    {
        await using var command=connection.CreateCommand(); command.Transaction=transaction;
        command.CommandText="SELECT context_json FROM production_package_contexts WHERE production_package_id=$id";
        command.Parameters.AddWithValue("$id",packageId);
        return await command.ExecuteScalarAsync(token) is string json ? JsonSerializer.Deserialize<ProductionPackageContext>(json) : null;
    }

    private static async Task<bool> IsLegacyAssignmentAsync(SqliteConnection connection,SqliteTransaction transaction,
        ProductionPackageContext context,CancellationToken token)
    {
        await using var command=connection.CreateCommand(); command.Transaction=transaction;
        command.CommandText="SELECT EXISTS(SELECT 1 FROM machine_assignments WHERE id=$id AND batch_operation_id=$operation)";
        command.Parameters.AddWithValue("$id",context.MachineAssignmentId);
        command.Parameters.AddWithValue("$operation",context.BatchOperationId);
        return (long)(await command.ExecuteScalarAsync(token))! == 1;
    }

    private static async Task<bool> ContextStillMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionPackageRecord package,
        CancellationToken token)
    {
        if (package.Context is not { } context) return false;
        var live = (await SqliteProductionPackageContext.ListAsync(connection, transaction, package.BatchOperationId, token))
            .SingleOrDefault(row => row.MachineAssignmentId == context.MachineAssignmentId
                && row.ProductionRunId == context.ProductionRunId && row.ProductionRunProgramId == context.ProductionRunProgramId
                && row.ProductionRunOutputId == context.ProductionRunOutputId && row.MachineId == context.MachineId);
        if (live is null || live.TargetQuantity != context.TargetQuantity
            || live.OutputAllocationStamp != context.OutputAllocationStamp) return false;
        var readiness=await SqliteProductionReadinessContextReader.ReadForPackageAsync(connection,transaction,context,token);
        var effective=package.ExecutionMode == "MANUAL" ? null : ProductionReadinessEvaluator.Evaluate(readiness).EffectiveGCodeReleaseId;
        if (readiness.ActiveProcessRevisionId != context.ProcessRevisionId || readiness.ActiveToolTableReleaseId != package.ToolTableReleaseId
            || effective != package.GCodeReleaseId) return false;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM batch_operations operation
                JOIN machine_assignments assignment
                  ON assignment.id=$assignmentId
                 AND assignment.machine_id=$machineId
                 AND assignment.released_at IS NULL
                JOIN machines machine
                  ON machine.id=assignment.machine_id
                 AND machine.execution_mode=$mode
                JOIN process_revisions process
                  ON process.id=$processId
                 AND process.is_active=1
                 AND process.tool_table_release_id=$toolId
                LEFT JOIN cnc_verification_settings settings ON settings.machine_id=machine.id
                LEFT JOIN machine_package_capabilities package_capability ON package_capability.machine_id=machine.id
                WHERE operation.id=$operationId
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
                  AND ($offsetMode<>'MEASURED'
                       OR $preparationId IS (
                           SELECT preparation.id FROM tool_preparations preparation
                           WHERE preparation.batch_operation_id=operation.id
                             AND preparation.machine_id=machine.id
                           ORDER BY preparation.version_number DESC LIMIT 1))
            );
            """;
        command.Parameters.AddWithValue("$processId", Db(context.ProcessRevisionId));
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
        command.Parameters.AddWithValue("$preparationId", Db(package.ToolPreparationId));
        return Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1;
    }

    private static ProductionPackageRecord ReadPackage(
        SqliteDataReader reader,
        IReadOnlyList<ProductionPackageArtifact> artifacts)
    {
        var configured = !reader.IsDBNull(18) && reader.GetBoolean(18)
            && !reader.IsDBNull(19) && reader.GetBoolean(19);
        return new(
            reader.GetString(0), reader.GetInt32(21), reader.GetString(1), Nullable(reader, 2), reader.GetString(3),
            reader.GetString(4), Nullable(reader, 5), reader.GetString(6), Nullable(reader, 7),
            reader.GetString(8), reader.GetString(17), reader.GetBoolean(9), NullableInt(reader, 10), NullableInt(reader, 11),
            reader.GetString(12), reader.GetString(13), Parse(reader.GetString(14)), reader.GetString(15),
            Nullable(reader, 16), configured,
            configured && !reader.IsDBNull(20) && reader.GetString(20) == "ONLINE", artifacts,
            Nullable(reader, 22));
    }

    private static async Task<IReadOnlyList<ProductionPackageArtifact>> ReadArtifactsAsync(
        SqliteConnection connection, string packageId, CancellationToken token, SqliteTransaction? transaction = null)
    {
        var values = new List<ProductionPackageArtifact>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
