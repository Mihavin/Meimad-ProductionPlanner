using Meimad.Planner.Server.Domain.Readiness;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal static partial class SqliteProductionReadinessContextReader
{
    internal static async Task<ProductionReadinessContext> ReadForPackageAsync(
        SqliteConnection connection, SqliteTransaction? transaction,
        Application.ProductionPackages.ProductionPackageContext context, CancellationToken token, bool useProgramRevision = false,
        ProductionReadinessContext? preloadedBasis = null,
        IReadOnlySet<(string BatchOperationId, string MachineId)>? verifiedPackages = null,
        ReferenceFacts? references = null)
    {
        references?.ValidateScope(connection, transaction);
        var basis = preloadedBasis ?? (await ReadManyAsync(connection, transaction, [context.BatchOperationId], token, context.MachineAssignmentId, verifiedPackages)).GetValueOrDefault(context.BatchOperationId)
            ?? throw new Application.ProductionPackages.ProductionPackageBuildException("production_package_context_changed", "The Operation no longer exists.");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT " + PackageRecipeColumns + "\n" + PackageRecipeRowsSql(batch: false);
        command.Parameters.AddWithValue("$machine", context.MachineId);
        command.Parameters.AddWithValue("$output", context.ProductionRunOutputId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new Application.ProductionPackages.ProductionPackageBuildException("production_package_context_changed", "The selected output no longer exists.");
        var recipe = ReadPackageRecipe(reader, 0);
        if (!useProgramRevision && recipe.SingleLegacyOutput && basis.MachineAssignmentId == context.MachineAssignmentId)
            return basis;
        await reader.DisposeAsync();
        var result = await ApplyPackageRecipeAsync(connection, transaction, context, basis, recipe, token, references);
        return await WithOffsetModeAsync(connection, transaction, result, token, context.ProductionRunOutputId, verifiedPackages);
    }

    private static async Task<ProductionReadinessContext> ApplyPackageRecipeAsync(SqliteConnection connection,
        SqliteTransaction? transaction, Application.ProductionPackages.ProductionPackageContext context,
        ProductionReadinessContext basis, PackageRecipe recipe, CancellationToken token, ReferenceFacts? references)
    {
        var recipeOperation = recipe.CaseOperationId;
        var supported = references is not null && references.Machines.Contains(context.MachineId)
            ? references.Supported : await ReadSupportedPostprocessorsAsync(connection,transaction,[context.MachineId],token);
        var releases = recipeOperation is null ? null
            : references is not null && references.CaseOperations.Contains(recipeOperation)
                ? references.Releases : await ReadReleasesAsync(connection,transaction,[recipeOperation],token);
        var preparations = references is not null && references.Operations.Contains(context.BatchOperationId)
            ? references.Preparations : await ReadToolPreparationFactsAsync(connection,transaction,[context.BatchOperationId],token);
        return basis with { MachineAssignmentId=context.MachineAssignmentId, MachineId=context.MachineId,
            ExecutionMode=recipe.Mode, UsableToolPositions=recipe.Capacity, ActiveProcessRevisionId=recipe.ProcessId,
            ActiveToolTableReleaseId=recipe.ToolTableId, RequiredToolCount=recipe.ToolCount,
            SupportedPostprocessorIds=supported.GetValueOrDefault(context.MachineId) ?? [],
            Releases=recipeOperation is null ? [] : releases!.GetValueOrDefault(recipeOperation) ?? [],
            SelectedGCodeReleaseId=recipe.ReleaseId, ProductionPinned=recipe.Pinned, ReplacedGCodeReleaseId=null,
            ToolOffsetMode="MEASURED", ToolPreparation=preparations.GetValueOrDefault((context.BatchOperationId,context.MachineId)) };
    }

    private const int ChunkSize = 500;

    internal static async Task<ProductionReadinessContext?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string batchOperationId,
        CancellationToken token,
        IReadOnlySet<(string BatchOperationId, string MachineId)>? verifiedPackages = null)
    {
        var contexts = await ReadManyAsync(connection, transaction, [batchOperationId], token, verifiedPackages: verifiedPackages);
        return contexts.GetValueOrDefault(batchOperationId);
    }

    internal static async Task<IReadOnlyDictionary<string, ProductionReadinessContext>> ReadManyAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyCollection<string> batchOperationIds,
        CancellationToken token, string? assignmentId = null,
        IReadOnlySet<(string BatchOperationId, string MachineId)>? verifiedPackages = null)
    {
        var ids = batchOperationIds.Distinct(StringComparer.Ordinal).ToArray();
        var result = new Dictionary<string, ProductionReadinessContext>(StringComparer.Ordinal);
        if (ids.Length == 0) return result;

        var rows = await ReadOperationRowsAsync(connection, transaction, ids, token, assignmentId);
        if (rows.Count == 0) return result;

        var materials = await ReadMaterialsAsync(
            connection, transaction,
            rows.Select(row => row.BatchId).Distinct(StringComparer.Ordinal).ToArray(), token);
        var supported = await ReadSupportedPostprocessorsAsync(
            connection, transaction,
            rows.Where(row => row.MachineId is not null).Select(row => row.MachineId!)
                .Distinct(StringComparer.Ordinal).ToArray(), token);
        var releases = await ReadReleasesAsync(
            connection, transaction,
            rows.Select(row => row.SourceOperationId).Distinct(StringComparer.Ordinal).ToArray(), token);
        var offsetFacts = await ReadOffsetFactsAsync(
            connection, transaction, rows.Select(row => row.BatchOperationId).ToArray(), token);
        var preparations = await ReadToolPreparationFactsAsync(
            connection, transaction, rows.Select(row => row.BatchOperationId).ToArray(), token);

        foreach (var row in rows)
        {
            var (materialStatus, materialComment) = materials[row.BatchId];
            result[row.BatchOperationId] = new ProductionReadinessContext(
                row.BatchOperationId,
                row.AssignmentId,
                row.MachineId,
                row.ExecutionMode,
                row.MachineId is null ? new HashSet<string>(StringComparer.Ordinal)
                    : supported.GetValueOrDefault(row.MachineId) ?? new HashSet<string>(StringComparer.Ordinal),
                row.UsablePositions,
                row.ProcessId,
                row.ToolTableId,
                row.RequiredToolCount,
                releases.GetValueOrDefault(row.SourceOperationId) ?? [],
                row.SelectedReleaseId,
                offsetFacts.GetValueOrDefault(row.BatchOperationId) ?? [],
                materialStatus,
                materialComment,
                row.MachineId is null ? null : preparations.GetValueOrDefault((row.BatchOperationId, row.MachineId)),
                row.ReplacedReleaseId,
                row.ProductionPinned, ExecutionContextVersion: row.ExecutionContextVersion);
        }

        // Reuse only facts observed in this caller-owned snapshot. Untransactional reads
        // retain their existing per-context lookup; nothing survives the transaction.
        if (transaction is not null && rows.Any(row => row.AssignmentId is not null))
            verifiedPackages ??= await SqliteMachineWorkflowReporting.ReadVerifiedPackagesAsync(connection, transaction, token);
        var ambiguous = rows.GroupBy(row => row.BatchOperationId, StringComparer.Ordinal)
            .Where(group => group.Skip(1).Any()).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var id in result.Keys.ToArray())
            result[id] = result[id] with { AmbiguousExecutionContext = ambiguous.Contains(id) };
        if (transaction is not null && result.Count > 1)
        {
            foreach (var enriched in await WithOffsetModesAsync(connection, transaction, result.Values.ToArray(), token, verifiedPackages))
                result[enriched.BatchOperationId] = enriched;
            return result;
        }
        foreach (var id in result.Keys.ToArray())
            result[id] = await WithOffsetModeAsync(connection, transaction,
                result[id], token, verifiedPackages: verifiedPackages);
        return result;
    }

    private static async Task<ProductionReadinessContext> WithOffsetModeAsync(SqliteConnection connection,
        SqliteTransaction? transaction, ProductionReadinessContext context, CancellationToken token, string? outputId = null,
        IReadOnlySet<(string BatchOperationId, string MachineId)>? verifiedPackages = null)
    {
        if (context.MachineAssignmentId is null) return context;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var release = ProductionReadinessEvaluator.Evaluate(context).EffectiveGCodeReleaseId;
        context = await WithExecutionEvidenceAsync(connection, transaction, context, release, token, verifiedPackages);
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT COUNT(*), COALESCE(MIN(package.tool_offset_mode='MANUAL_DUMMY'),0),
                (SELECT COUNT(*) FROM tool_table_release_tools WHERE tool_table_release_id=$tools AND is_active=1)
            """ + "\n" + OffsetModeMatchingRowsSql(batch: false);
        query.Parameters.AddWithValue("$assignment", context.MachineAssignmentId);
        query.Parameters.AddWithValue("$operation", context.BatchOperationId);
        query.Parameters.AddWithValue("$output", (object?)outputId ?? DBNull.Value);
        query.Parameters.AddWithValue("$machine", (object?)context.MachineId ?? DBNull.Value);
        query.Parameters.AddWithValue("$process", (object?)context.ActiveProcessRevisionId ?? DBNull.Value);
        query.Parameters.AddWithValue("$release", (object?)release ?? DBNull.Value);
        query.Parameters.AddWithValue("$tools", (object?)context.ActiveToolTableReleaseId ?? DBNull.Value);
        await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return context;
        Application.Projections.PlannerPerformanceMetrics.ReadinessEnrichment.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return context with { ReleasedToolCount = reader.GetInt32(2),
            ToolOffsetMode = reader.GetInt32(0) == 1 && reader.GetBoolean(1) ? "MANUAL_DUMMY" : "MEASURED" };
    }

    // Both scalar and batch readers use these exact package/offset matching predicates.
    private static string OffsetModeMatchingRowsSql(bool batch)
    {
        var source = batch ? "requested request CROSS JOIN " : "";
        var value = batch ? "request." : "$";
        return $"""
            FROM {source}production_package_context_current current
            JOIN production_packages package ON package.id=current.production_package_id
            JOIN production_package_contexts binding ON binding.production_package_id=package.id
            JOIN production_run_outputs output ON output.id=current.production_run_output_id
            JOIN production_run_programs program ON program.id=output.production_run_program_id
            JOIN machine_assignments assignment ON assignment.id=current.machine_assignment_id
                AND assignment.production_run_id=program.production_run_id AND assignment.released_at IS NULL
            JOIN machine_package_capabilities capability ON capability.machine_id=assignment.machine_id
            LEFT JOIN cnc_verification_settings settings ON settings.machine_id=assignment.machine_id
            WHERE current.machine_assignment_id={value}assignment AND output.batch_operation_id={value}operation
                AND ({value}output IS NULL OR output.id={value}output)
                AND package.production_run_id=program.production_run_id
                AND package.machine_assignment_id=assignment.id
                AND json_extract(binding.context_json,'$.TargetQuantity')=output.target_quantity
                AND json_extract(binding.context_json,'$.OutputAllocationStamp')=(
                    SELECT group_concat(evidence, '|') FROM
                        (SELECT sibling.id || ':' || sibling.target_quantity || ':' || sibling.quantity_per_cycle
                            || ':' || COALESCE(sibling.revision_output_id,'') AS evidence
                         FROM production_run_outputs sibling WHERE sibling.production_run_program_id=program.id ORDER BY sibling.id))
                AND package.machine_id={value}machine AND json_extract(binding.context_json,'$.ProcessRevisionId') IS {value}process
                AND package.gcode_release_id IS {value}release AND package.tool_table_release_id IS {value}tools
                AND capability.allow_manual_dummy_tool_offsets=1
                AND (package.execution_mode='MANUAL' OR (package.verification_enabled=1 AND settings.enabled=1
                    AND package.verification_configuration_version=settings.version))
            """;
    }

    private static async Task<ProductionReadinessContext> WithExecutionEvidenceAsync(SqliteConnection connection,
        SqliteTransaction? transaction, ProductionReadinessContext context, string? release, CancellationToken token,
        IReadOnlySet<(string BatchOperationId, string MachineId)>? verifiedPackages)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT " + ExecutionEvidenceColumns + "\n" + ExecutionEvidenceRowsSql(batch: false);
        query.Parameters.AddWithValue("$assignment", context.MachineAssignmentId!);
        query.Parameters.AddWithValue("$release", (object?)release ?? DBNull.Value);
        await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return context;
        var values = new object[reader.FieldCount]; reader.GetValues(values);
        await reader.DisposeAsync();
        return ApplyExecutionEvidence(context, values,
            verifiedPackages ?? await SqliteMachineWorkflowReporting.ReadVerifiedPackagesAsync(connection, transaction, token));
    }

    private const string ExecutionEvidenceColumns = """
        COALESCE(settings.enabled,0), COALESCE(settings.version,0),
                current.offset_loader_release_id, current.version, session.id, session.state,
                COALESCE(connection.enabled,0), connection.configuration_json
        """;

    private static string ExecutionEvidenceRowsSql(bool batch)
    {
        var source = batch ? "requested request CROSS JOIN " : "";
        var value = batch ? "request." : "$";
        return $"""
            FROM {source}machine_assignments assignment
            LEFT JOIN cnc_verification_settings settings ON settings.machine_id=assignment.machine_id
            LEFT JOIN machine_connections connection ON connection.machine_id=assignment.machine_id
            LEFT JOIN production_run_current_offset_loaders current ON current.production_run_id=assignment.production_run_id
                AND current.machine_id=assignment.machine_id
            LEFT JOIN cnc_setup_verification_sessions session ON session.production_run_id=assignment.production_run_id
                AND session.machine_id=assignment.machine_id AND session.nc_release_id={value}release
                AND session.offset_loader_release_id=current.offset_loader_release_id AND session.state IN ('ARMED','PENDING','SUCCEEDED')
            WHERE assignment.id={value}assignment AND assignment.released_at IS NULL
            """;
    }

    private static ProductionReadinessContext ApplyExecutionEvidence(ProductionReadinessContext context,
        object[] values, IReadOnlySet<(string BatchOperationId, string MachineId)> verifiedPackages)
    {
        var source = SqliteProductionPackageRepository.DprntSource(values[7] is DBNull ? null : (string)values[7]);
        var dprnt = context.ExecutionMode == "CNC_GCODE" && Convert.ToBoolean(values[6])
            && source is not null && source != Domain.Cnc.CncDprntSources.None;
        var required = Convert.ToBoolean(values[0]) && dprnt;
        var reporting = SqliteMachineWorkflowReporting.Mode(context.MachineId!, context.BatchOperationId,
            dprnt ? new HashSet<string>([context.MachineId!]) : new HashSet<string>(), verifiedPackages);
        return context with { VerificationRequired = required, VerificationSucceeded = values[5] is string state && state == "SUCCEEDED",
            ManualSetupReportingSupported = reporting != WorkflowReportingMode.Machine, LoaderExecutionObserved = values[4] is not DBNull,
            ExecutionEvidenceStamp = ProductionActionPolicy.Stamp(new { required, values = values.Select(x => x is DBNull ? null : x).ToArray() }) };
    }

    private sealed record OperationRow(
        string BatchOperationId,
        string SourceOperationId,
        string? AssignmentId,
        string? MachineId,
        string? ExecutionMode,
        int? UsablePositions,
        string? ProcessId,
        string? ToolTableId,
        int? RequiredToolCount,
        string? SelectedReleaseId,
        string BatchId,
        string? ReplacedReleaseId,
        bool ProductionPinned,
        string ExecutionContextVersion);

    private static async Task<List<OperationRow>> ReadOperationRowsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<string> ids,
        CancellationToken token, string? assignmentId)
    {
        var rows = new List<OperationRow>();
        foreach (var chunk in ids.Chunk(ChunkSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT operation.id,
                       operation.source_case_operation_id,
                       assignment.id,
                       assignment.machine_id,
                       machine.execution_mode,
                       machine.usable_tool_positions,
                       CASE WHEN operation.status = 'not_started'
                            THEN active_process.id
                            ELSE operation.production_process_revision_id END,
                       CASE WHEN operation.status = 'not_started'
                            THEN active_process.tool_table_release_id
                            ELSE operation.production_tool_table_release_id END,
                       CASE WHEN operation.status = 'not_started'
                            THEN active_tools.required_tool_count
                            ELSE pinned_tools.required_tool_count END,
                       CASE WHEN operation.status = 'not_started' OR restart.id IS NOT NULL
                            THEN assignment.selected_gcode_release_id
                            ELSE operation.production_gcode_release_id END,
                       operation.production_batch_id,
                       restart.replaced_gcode_release_id,
                       CASE WHEN operation.status <> 'not_started' AND restart.id IS NULL
                                 AND operation.production_gcode_release_id IS NOT NULL
                            THEN 1 ELSE 0 END,
                       operation.version || ':' || operation.status || ':' || COALESCE(assignment.version,'none')
                           || ':' || COALESCE(assignment.production_run_id,'none') || ':' ||
                           COALESCE((SELECT version FROM production_runs WHERE id=assignment.production_run_id),'none')
                FROM batch_operations operation
                -- An operation back in setup for a newer local G-code version (schema v90) keeps
                -- its process revision and tool table, and chooses its release again like one that
                -- has not started, until a new Production Package pins the new release.
                LEFT JOIN batch_operation_setup_restarts restart
                  ON restart.batch_operation_id = operation.id
                 AND restart.resolved_at IS NULL
                LEFT JOIN machine_assignments assignment
                  ON assignment.batch_operation_id = operation.id
                 AND assignment.released_at IS NULL
                 AND ($assignmentId IS NULL OR assignment.id=$assignmentId)
                LEFT JOIN machines machine ON machine.id = assignment.machine_id
                LEFT JOIN process_revisions active_process
                  ON active_process.case_operation_id = operation.source_case_operation_id
                 AND active_process.is_active = 1
                LEFT JOIN tool_table_releases active_tools
                  ON active_tools.id = active_process.tool_table_release_id
                LEFT JOIN tool_table_releases pinned_tools
                  ON pinned_tools.id = operation.production_tool_table_release_id
                WHERE operation.id IN ({InList(command, chunk)});
                """;
            command.Parameters.AddWithValue("$assignmentId", (object?)assignmentId ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                rows.Add(new OperationRow(
                    reader.GetString(0), reader.GetString(1), String(reader, 2), String(reader, 3),
                    String(reader, 4), Int(reader, 5), String(reader, 6), String(reader, 7),
                    Int(reader, 8), String(reader, 9), reader.GetString(10),
                    String(reader, 11), reader.GetInt64(12) == 1, reader.GetString(13)));
            }
        }

        return rows;
    }

    private static async Task<Dictionary<string, (string Status, string Message)>> ReadMaterialsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<string> batchIds,
        CancellationToken token)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var chunk in batchIds.Chunk(ChunkSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT batch.id,
                       batch.planned_quantity,
                       COALESCE((SELECT SUM(quantity) FROM batch_material_reservations
                                 WHERE production_batch_id = batch.id), 0),
                       COALESCE((SELECT SUM(receipt.quantity)
                                 FROM verified_material_receipts receipt
                                 WHERE receipt.case_id = batch.case_id), 0)
                       - COALESCE((SELECT SUM(reservation.quantity)
                                   FROM batch_material_reservations reservation
                                   WHERE reservation.production_batch_id <> batch.id
                                     AND reservation.receipt_id IN (
                                         SELECT receipt.id
                                         FROM verified_material_receipts receipt
                                         WHERE receipt.case_id = batch.case_id)), 0)
                FROM production_batches batch
                WHERE batch.id IN ({InList(command, chunk)});
                """;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                result[reader.GetString(0)] = MaterialState(
                    reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
            }
        }

        return result;
    }

    private static (string Status, string Message) MaterialState(
        int plannedQuantity, int reserved, int availableToBatch)
    {
        if (reserved >= plannedQuantity)
            return ("READY",
                $"{reserved} of {plannedQuantity} verified material piece(s) are reserved for this Production Batch.");
        if (availableToBatch < plannedQuantity)
            return ("MISSING",
                $"Production Batch requires {plannedQuantity} material piece(s); {availableToBatch} verified piece(s) are available to it. Shortage: {plannedQuantity - availableToBatch}.");
        return ("UNVERIFIED",
            $"Production Batch requires {plannedQuantity} material piece(s); {availableToBatch} verified piece(s) are available, but only {reserved} are explicitly reserved.");
    }

    private static async Task<Dictionary<string, HashSet<string>>> ReadSupportedPostprocessorsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<string> machineIds,
        CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var chunk in machineIds.Chunk(ChunkSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT machine_id, postprocessor_id
                FROM machine_supported_postprocessors
                WHERE machine_id IN ({InList(command, chunk)});
                """;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (!result.TryGetValue(reader.GetString(0), out var set))
                    result[reader.GetString(0)] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(reader.GetString(1));
            }
        }

        Application.Projections.PlannerPerformanceMetrics.ReadinessReferences.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }

    private static async Task<Dictionary<string, List<ReadinessRelease>>> ReadReleasesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<string> caseOperationIds,
        CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = new Dictionary<string, List<ReadinessRelease>>(StringComparer.Ordinal);
        foreach (var chunk in caseOperationIds.Chunk(ChunkSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT release.case_operation_id, release.id, release.process_revision_id,
                       release.postprocessor_id, postprocessor.name,
                       release.original_file_name, release.post_specific_revision
                FROM gcode_releases release
                JOIN postprocessors postprocessor ON postprocessor.id = release.postprocessor_id
                WHERE release.case_operation_id IN ({InList(command, chunk)})
                  AND NOT EXISTS (
                      SELECT 1 FROM gcode_releases newer
                      WHERE newer.process_revision_id = release.process_revision_id
                        AND newer.postprocessor_id = release.postprocessor_id
                        AND newer.post_specific_revision > release.post_specific_revision)
                ORDER BY release.case_operation_id, release.released_at, release.id;
                """;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (!result.TryGetValue(reader.GetString(0), out var list))
                    result[reader.GetString(0)] = list = [];
                list.Add(new ReadinessRelease(
                    reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetInt32(6)));
            }
        }

        Application.Projections.PlannerPerformanceMetrics.ReadinessReferences.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }

    private static async Task<Dictionary<string, List<ToolOffsetReadinessFact>>> ReadOffsetFactsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<string> batchOperationIds,
        CancellationToken token)
    {
        var result = new Dictionary<string, List<ToolOffsetReadinessFact>>(StringComparer.Ordinal);
        foreach (var chunk in batchOperationIds.Chunk(ChunkSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT batch_operation_id, machine_id, process_revision_id, gcode_release_id,
                       status, comment, recorded_at
                FROM tool_offset_readiness_records
                WHERE batch_operation_id IN ({InList(command, chunk)})
                ORDER BY batch_operation_id, recorded_at DESC, id DESC;
                """;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (!result.TryGetValue(reader.GetString(0), out var list))
                    result[reader.GetString(0)] = list = [];
                list.Add(new ToolOffsetReadinessFact(
                    reader.GetString(1), reader.GetString(2), String(reader, 3),
                    reader.GetString(4), String(reader, 5),
                    DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        return result;
    }

    /// <summary>
    /// The latest Tool Room tool preparation per Operation and Machine (schema v79) with the number
    /// of required released tools that still lack a measured length, diameter or offset number
    /// (an offset number is implied by a numbered tool identifier such as T7).
    /// </summary>
    private static async Task<Dictionary<(string BatchOperationId, string MachineId), ToolPreparationReadinessFact>> ReadToolPreparationFactsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<string> batchOperationIds,
        CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = new Dictionary<(string, string), ToolPreparationReadinessFact>();
        foreach (var chunk in batchOperationIds.Chunk(ChunkSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT preparation.batch_operation_id, preparation.machine_id, preparation.tool_table_release_id,
                       preparation.version_number, preparation.saved_at,
                       (SELECT COUNT(*) FROM tool_table_release_tools released
                         WHERE released.tool_table_release_id = preparation.tool_table_release_id
                           AND released.is_active = 1 AND released.is_required = 1),
                       (SELECT COUNT(*) FROM tool_table_release_tools released
                         WHERE released.tool_table_release_id = preparation.tool_table_release_id
                           AND released.is_active = 1 AND released.is_required = 1
                           AND NOT EXISTS (
                               SELECT 1 FROM tool_preparation_tools measured
                               WHERE measured.tool_preparation_id = preparation.id
                                 AND lower(trim(measured.tool_identifier)) = lower(trim(released.tool_identifier))
                                 AND measured.measured_length IS NOT NULL
                                 AND measured.measured_diameter IS NOT NULL
                                 AND (measured.offset_number IS NOT NULL OR measured.tool_identifier GLOB '*[0-9]*')))
                FROM tool_preparations preparation
                WHERE preparation.batch_operation_id IN ({InList(command, chunk)})
                  AND preparation.version_number = (
                      SELECT MAX(newer.version_number) FROM tool_preparations newer
                      WHERE newer.batch_operation_id = preparation.batch_operation_id
                        AND newer.machine_id = preparation.machine_id);
                """;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                result[(reader.GetString(0), reader.GetString(1))] = new ToolPreparationReadinessFact(
                    reader.GetString(2), reader.GetInt32(3), reader.GetInt32(5), reader.GetInt32(6),
                    DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        Application.Projections.PlannerPerformanceMetrics.ReadinessReferences.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }

    private static string InList(SqliteCommand command, IReadOnlyList<string> values)
    {
        var names = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            names[index] = $"$in{index}";
            command.Parameters.AddWithValue(names[index], values[index]);
        }
        return string.Join(',', names);
    }

    private static string? String(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? Int(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
