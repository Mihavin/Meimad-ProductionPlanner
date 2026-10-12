using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Application.EventLogging;
using Meimad.Planner.Server.Application.SimulationPackages;
using Meimad.Planner.SimulationPackage.Diagnostics;
using Meimad.Planner.SimulationPackage.Manifest;
using Meimad.Planner.SimulationPackage.Validation;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>A Case model-file link an import creates for a package geometry file.</summary>
internal sealed record SimulationPackageLink(string RelativePath, string Kind, string Format, string AbsolutePath, string Label);

/// <summary>Everything one import writes, applied in one immediate transaction.</summary>
internal sealed record SimulationPackageApplyCommand(
    string ImportId,
    string CaseId,
    string CaseOperationId,
    SimulationPackageManifest Manifest,
    string ManifestSha256,
    string PackagePath,
    string? SelectedStockPath,
    string? MachineId,
    string? MachineMatchedBy,
    string CoordinateSystemJson,
    string NcSha256,
    string? ToolTableSha256,
    IReadOnlyList<int> ToolNumbers,
    IReadOnlyList<SimulationPackageImportFile> Files,
    IReadOnlyList<SimulationPackageLink> Links,
    CaseOperationCamSetupValues WorkCs,
    bool OverwriteManualWorkCs,
    int? ExpectedCamSetupVersion,
    bool CheckActiveImport,
    string? ExpectedActiveImportId,
    (string Field, string Value)? RememberMapping,
    string ReportJson,
    string Actor,
    DateTimeOffset Now);

internal enum SimulationPackageApplyStatus
{
    Imported,
    AlreadyImported,
    Conflict,
    ManualWorkCs,
    OperationNotFound
}

internal sealed record SimulationPackageApplyResult(SimulationPackageApplyStatus Status, string? ImportId, SimulationPackageConflict? Conflict);

/// <summary>Persists Cimatron simulation package imports and the Machine mapping table (schema v117).</summary>
internal sealed class SqliteSimulationPackageRepository(SqliteDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string ImportColumns = """
        SELECT id, case_id, case_operation_id, status, package_id, manifest_sha256, schema_version, package_path,
               exporter_name, exporter_version, cimatron_version, source_application, source_project, nc_file_name,
               exported_at, operation_number, operation_name, procedure_id, procedure_name, machine_external_id,
               machine_name, postprocessor, axis_configuration, machine_id, machine_matched_by, units,
               coordinate_system_json, selected_stock_path, nc_sha256, tool_table_sha256, tool_numbers_json,
               report_json, imported_at, imported_by, superseded_at, superseded_by_import_id
        FROM simulation_package_imports
        """;

    internal async Task<NetworkFolderSettings> ReadNetworkFolderAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await SqliteNetworkFolderSettings.ReadAsync(connection, null, cancellationToken);
    }

    internal async Task<SimulationPackageCase?> GetCaseAsync(string caseId, CancellationToken cancellationToken)
    {
        var cases = await ReadCasesAsync("WHERE id = $id", ("$id", caseId), cancellationToken);
        return cases.Count == 1 ? cases[0] : null;
    }

    /// <summary>Cases whose working folder contains the package, or whose part number equals the manifest's.</summary>
    internal async Task<IReadOnlyList<SimulationPackageCase>> FindCasesAsync(string packagePath, string? partNumber, CancellationToken cancellationToken)
    {
        var all = await ReadCasesAsync("", null, cancellationToken);
        var byFolder = all.Where(item => !string.IsNullOrWhiteSpace(item.WorkingFolder)
                && PackagePaths.IsUnder(packagePath, item.WorkingFolder!)).ToArray();
        if (byFolder.Length > 0) return byFolder;
        if (string.IsNullOrWhiteSpace(partNumber)) return [];
        return all.Where(item => string.Equals(item.PartNumber.Trim(), partNumber.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private async Task<List<SimulationPackageCase>> ReadCasesAsync(string where, (string Name, string Value)? parameter, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var paths = await SqliteNetworkFolderSettings.ReadAsync(connection, null, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id, part_number, name, working_folder_path FROM cases {where};";
        if (parameter is { } p) command.Parameters.AddWithValue(p.Name, p.Value);
        var result = new List<SimulationPackageCase>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var folder = reader.IsDBNull(3) ? null : reader.GetString(3);
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                string.IsNullOrWhiteSpace(folder) ? null : paths.ToAbsolute(folder)));
        }

        return result;
    }

    internal async Task<IReadOnlyList<SimulationPackageOperation>> ListOperationsAsync(string caseId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, operation_number, name FROM case_operations WHERE case_id = $caseId ORDER BY route_position, operation_number;";
        command.Parameters.AddWithValue("$caseId", caseId);
        var result = new List<SimulationPackageOperation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2)));
        return result;
    }

    /// <summary>The current CAM setup version, its source and who saved it (null when none).</summary>
    internal async Task<(int Version, string Source, string? UpdatedBy, DateTimeOffset UpdatedAt)?> ReadCamSetupAsync(
        string caseOperationId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ReadCamSetupAsync(connection, null, caseOperationId, cancellationToken);
    }

    /// <summary>
    /// The Machine mapping of exporter definition §10, in order: (1) the configured external machine id,
    /// (2) the exact postprocessor name (Machines that support it), (3) the explicit mapping table
    /// (postprocessor or machine name rows), (4) a compatible pair (the machine name equals a Machine's
    /// number or name, ignoring case, spaces, dashes and underscores). The first step that finds anything
    /// decides: one Machine resolves, several are listed as candidates and never picked.
    /// </summary>
    internal async Task<MachineMappingResolution> ResolveMachineAsync(MachineInfo? machine, CancellationToken cancellationToken)
    {
        if (machine is null) return new(null, null, [], false);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var steps = new List<(string Kind, string Sql, string? Value)>
        {
            (MachineMatchKinds.ExternalId, """
                SELECT m.id, m.number, m.name FROM simulation_package_machine_mappings map
                JOIN machines m ON m.id = map.machine_id
                WHERE map.match_field = 'external_id' AND map.match_value = $value
                """, machine.ExternalId),
            (MachineMatchKinds.Postprocessor, """
                SELECT DISTINCT m.id, m.number, m.name FROM postprocessors p
                JOIN machine_supported_postprocessors s ON s.postprocessor_id = p.id
                JOIN machines m ON m.id = s.machine_id
                WHERE p.name = $value COLLATE NOCASE AND p.is_active = 1
                """, machine.Postprocessor),
            (MachineMatchKinds.MappingTable, """
                SELECT DISTINCT m.id, m.number, m.name FROM simulation_package_machine_mappings map
                JOIN machines m ON m.id = map.machine_id
                WHERE (map.match_field = 'postprocessor' AND map.match_value = $value)
                   OR (map.match_field = 'machine_name' AND map.match_value = $name)
                """, machine.Postprocessor ?? "")
        };
        foreach (var (kind, sql, value) in steps)
        {
            if (kind != MachineMatchKinds.MappingTable && string.IsNullOrWhiteSpace(value)) continue;
            if (kind == MachineMatchKinds.MappingTable && string.IsNullOrWhiteSpace(machine.Postprocessor) && string.IsNullOrWhiteSpace(machine.Name)) continue;
            await using var command = connection.CreateCommand();
            command.CommandText = sql + " ORDER BY m.number;";
            command.Parameters.AddWithValue("$value", value?.Trim() ?? "");
            command.Parameters.AddWithValue("$name", machine.Name?.Trim() ?? "");
            var found = await ReadCandidatesAsync(command, kind, cancellationToken);
            if (found.Count > 0) return Decide(found, kind);
        }

        if (!string.IsNullOrWhiteSpace(machine.Name))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, number, name FROM machines ORDER BY number;";
            var all = await ReadCandidatesAsync(command, MachineMatchKinds.CompatiblePair, cancellationToken);
            var key = Compact(machine.Name);
            var found = all.Where(item => Compact(item.Number) == key || Compact(item.Name) == key).ToList();
            if (found.Count > 0) return Decide(found, MachineMatchKinds.CompatiblePair);
        }

        return new(null, null, [], false);

        static MachineMappingResolution Decide(List<MachineMappingCandidate> found, string kind) =>
            found.Count == 1 ? new(found[0].MachineId, kind, found, false) : new(null, kind, found, true);
    }

    internal async Task<IReadOnlyList<MachineMappingCandidate>> ListMachinesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, number, name FROM machines ORDER BY number;";
        return await ReadCandidatesAsync(command, MachineMatchKinds.PlannerSelected, cancellationToken);
    }

    private static async Task<List<MachineMappingCandidate>> ReadCandidatesAsync(SqliteCommand command, string kind, CancellationToken cancellationToken)
    {
        var result = new List<MachineMappingCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), kind));
        return result;
    }

    private static string Compact(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    internal async Task<SimulationPackageImport?> FindActiveAsync(string caseOperationId, string manifestSha256, CancellationToken cancellationToken)
    {
        var items = await ListCoreAsync("WHERE case_operation_id = $op AND manifest_sha256 = $sha AND status = 'imported'",
            [("$op", caseOperationId), ("$sha", manifestSha256)], 1, cancellationToken);
        return items.FirstOrDefault();
    }

    internal async Task<SimulationPackageImport?> FindCurrentForOperationAsync(string caseOperationId, CancellationToken cancellationToken)
    {
        var items = await ListCoreAsync("WHERE case_operation_id = $op AND status = 'imported'", [("$op", caseOperationId)], 1, cancellationToken);
        return items.FirstOrDefault();
    }

    internal async Task<bool> HasBlockedAttemptAsync(string manifestSha256, string packagePath, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM simulation_package_imports
                          WHERE manifest_sha256 = $sha AND package_path = $path AND status IN ('blocked', 'imported', 'superseded'));
            """;
        command.Parameters.AddWithValue("$sha", manifestSha256);
        command.Parameters.AddWithValue("$path", packagePath);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    internal Task<IReadOnlyList<SimulationPackageImport>> ListAsync(
        string? caseId, string? caseOperationId, string? status, int limit, CancellationToken cancellationToken)
    {
        var clauses = new List<string>();
        var parameters = new List<(string, string)>();
        if (caseId is not null) { clauses.Add("case_id = $case"); parameters.Add(("$case", caseId)); }
        if (caseOperationId is not null) { clauses.Add("case_operation_id = $op"); parameters.Add(("$op", caseOperationId)); }
        if (status is not null) { clauses.Add("status = $status"); parameters.Add(("$status", status)); }
        var where = clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);
        return ListCoreAsync(where, parameters, limit, cancellationToken);
    }

    internal async Task<SimulationPackageImport?> GetAsync(string importId, CancellationToken cancellationToken) =>
        (await ListCoreAsync("WHERE id = $id", [("$id", importId)], 1, cancellationToken)).FirstOrDefault();

    private async Task<IReadOnlyList<SimulationPackageImport>> ListCoreAsync(
        string where, IReadOnlyList<(string Name, string Value)> parameters, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var paths = await SqliteNetworkFolderSettings.ReadAsync(connection, null, cancellationToken);
        var rows = new List<SimulationPackageImport>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"{ImportColumns} {where} ORDER BY imported_at DESC, id LIMIT $limit;";
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadImport(reader, paths));
        }

        var result = new List<SimulationPackageImport>(rows.Count);
        foreach (var row in rows)
        {
            var files = new List<SimulationPackageImportFile>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT role, relative_path, sha256, size_bytes, stock_kind, source_operation, name, collision, selected, case_model_file_id
                    FROM simulation_package_import_files WHERE import_id = $id ORDER BY role, relative_path;
                    """;
                command.Parameters.AddWithValue("$id", row.ImportId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    files.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                        NullableString(reader, 4), NullableString(reader, 5), NullableString(reader, 6),
                        reader.IsDBNull(7) ? null : reader.GetInt32(7) == 1, reader.GetInt32(8) == 1, NullableString(reader, 9)));
                }
            }

            string? releaseId = null;
            if (row.CaseOperationId is not null && row.NcSha256 is not null)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id FROM gcode_releases WHERE case_operation_id = $op AND file_hash = $hash
                    ORDER BY released_at DESC, id LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$op", row.CaseOperationId);
                command.Parameters.AddWithValue("$hash", row.NcSha256);
                releaseId = await command.ExecuteScalarAsync(cancellationToken) as string;
            }

            result.Add(row with { Files = files, MatchingGcodeReleaseId = releaseId });
        }

        return result;
    }

    private static SimulationPackageImport ReadImport(SqliteDataReader reader, NetworkFolderSettings paths) => new(
        reader.GetString(0),
        NullableString(reader, 1),
        NullableString(reader, 2),
        reader.GetString(3),
        NullableString(reader, 4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetInt32(6),
        paths.ToAbsolute(reader.GetString(7)) ?? reader.GetString(7),
        NullableString(reader, 8),
        NullableString(reader, 9),
        NullableString(reader, 10),
        NullableString(reader, 11),
        NullableString(reader, 12),
        NullableString(reader, 13),
        NullableString(reader, 14),
        NullableString(reader, 15),
        NullableString(reader, 16),
        reader.IsDBNull(17) ? null : reader.GetInt32(17),
        NullableString(reader, 18),
        NullableString(reader, 19),
        NullableString(reader, 20),
        NullableString(reader, 21),
        NullableString(reader, 22),
        NullableString(reader, 23),
        NullableString(reader, 24),
        NullableString(reader, 25),
        NullableString(reader, 26),
        NullableString(reader, 27),
        NullableString(reader, 28),
        NullableString(reader, 29),
        JsonSerializer.Deserialize<int[]>(reader.GetString(30)) ?? [],
        reader.GetString(31),
        Parse(reader.GetString(32)),
        reader.GetString(33),
        reader.IsDBNull(34) ? null : Parse(reader.GetString(34)),
        NullableString(reader, 35),
        [],
        null);

    /// <summary>
    /// Applies an import atomically: re-checks the CAM setup version and the Operation's current package
    /// the request was based on, supersedes the previous package (removing only the model-file links it
    /// created), links the selected stock, every fixture and the finished part to the Operation, stores
    /// the import with its files and report, saves the Work CS and appends the audit events.
    /// </summary>
    internal async Task<SimulationPackageApplyResult> ApplyAsync(SimulationPackageApplyCommand command, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT EXISTS(SELECT 1 FROM case_operations WHERE id = $op AND case_id = $case);";
            exists.Parameters.AddWithValue("$op", command.CaseOperationId);
            exists.Parameters.AddWithValue("$case", command.CaseId);
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
                return new(SimulationPackageApplyStatus.OperationNotFound, null, null);
        }

        var setup = await ReadCamSetupAsync(connection, transaction, command.CaseOperationId, cancellationToken);
        if (command.ExpectedCamSetupVersion is { } expected && (setup?.Version ?? 0) != expected)
        {
            return new(SimulationPackageApplyStatus.Conflict, null, new(
                "cam_setup_changed",
                "The Operation Work CS changed since you opened it, so the package was not imported.",
                setup?.UpdatedBy, setup?.UpdatedAt, setup?.Version ?? 0, null));
        }

        if (setup is { Source: CaseOperationCamSetupSources.Manual } && !command.OverwriteManualWorkCs)
            return new(SimulationPackageApplyStatus.ManualWorkCs, null, null);

        var previous = new List<(string Id, DateTimeOffset At, string By)>();
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT id, imported_at, imported_by, manifest_sha256 FROM simulation_package_imports
                WHERE case_operation_id = $op AND status = 'imported' ORDER BY imported_at DESC;
                """;
            current.Parameters.AddWithValue("$op", command.CaseOperationId);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(3) == command.ManifestSha256)
                    return new(SimulationPackageApplyStatus.AlreadyImported, reader.GetString(0), null);
                previous.Add((reader.GetString(0), Parse(reader.GetString(1)), reader.GetString(2)));
            }
        }

        var currentImportId = previous.Count == 0 ? null : previous[0].Id;
        if (command.CheckActiveImport && !string.Equals(currentImportId, command.ExpectedActiveImportId, StringComparison.Ordinal))
        {
            return new(SimulationPackageApplyStatus.Conflict, null, new(
                "simulation_package_import_changed",
                "Another Cimatron package was imported for this Operation since you opened it, so your import was not applied.",
                previous.Count == 0 ? null : previous[0].By, previous.Count == 0 ? null : previous[0].At, setup?.Version ?? 0, currentImportId));
        }

        var paths = await SqliteNetworkFolderSettings.ReadAsync(connection, transaction, cancellationToken);
        var now = Format(command.Now);
        foreach (var (previousId, _, _) in previous)
        {
            await ExecuteAsync(connection, transaction, """
                DELETE FROM case_model_files WHERE id IN (
                    SELECT case_model_file_id FROM simulation_package_import_files
                    WHERE import_id = $id AND case_model_file_id IS NOT NULL);
                UPDATE simulation_package_imports
                SET status = 'superseded', superseded_at = $now, superseded_by_import_id = $new
                WHERE id = $id;
                """, cancellationToken, ("$id", previousId), ("$now", now), ("$new", command.ImportId));
        }

        var linkIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sortOrder = Convert.ToInt32(await ScalarAsync(connection, transaction,
            "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM case_model_files WHERE case_id = $case;",
            cancellationToken, ("$case", command.CaseId)), CultureInfo.InvariantCulture);
        foreach (var link in command.Links)
        {
            var id = Guid.NewGuid().ToString("N");
            linkIds[link.RelativePath] = id;
            await ExecuteAsync(connection, transaction, """
                INSERT INTO case_model_files (id, case_id, case_operation_id, kind, format, file_path, label,
                    is_primary, sort_order, version, created_at, updated_at)
                VALUES ($id, $case, $op, $kind, $format, $path, $label, 0, $sort, 1, $now, $now);
                """, cancellationToken,
                ("$id", id), ("$case", command.CaseId), ("$op", command.CaseOperationId), ("$kind", link.Kind),
                ("$format", link.Format), ("$path", paths.ToStored(link.AbsolutePath) ?? link.AbsolutePath),
                ("$label", link.Label), ("$sort", sortOrder++), ("$now", now));
        }

        var manifest = command.Manifest;
        await ExecuteAsync(connection, transaction, """
            INSERT INTO simulation_package_imports (
                id, case_id, case_operation_id, status, package_id, manifest_sha256, schema_version, package_path,
                exporter_name, exporter_version, cimatron_version, source_application, source_project, nc_file_name,
                exported_at, operation_number, operation_name, procedure_id, procedure_name, machine_external_id,
                machine_name, postprocessor, axis_configuration, machine_id, machine_matched_by, units,
                coordinate_system_json, selected_stock_path, nc_sha256, tool_table_sha256, tool_numbers_json,
                report_json, imported_at, imported_by)
            VALUES ($id, $case, $op, 'imported', $packageId, $sha, $schema, $path,
                $exporterName, $exporterVersion, $cimatron, $application, $project, $ncFile,
                $exportedAt, $opNumber, $opName, $procedureId, $procedureName, $externalId,
                $machineName, $post, $axes, $machineId, $matchedBy, $units,
                $cs, $stock, $nc, $toolTable, $tools, $report, $now, $actor);
            """, cancellationToken,
            ("$id", command.ImportId), ("$case", command.CaseId), ("$op", command.CaseOperationId),
            ("$packageId", manifest.PackageId), ("$sha", command.ManifestSha256), ("$schema", manifest.SchemaVersion),
            ("$path", paths.ToStored(command.PackagePath) ?? command.PackagePath),
            ("$exporterName", manifest.Exporter?.Name), ("$exporterVersion", manifest.Exporter?.Version),
            ("$cimatron", manifest.Exporter?.CimatronVersion), ("$application", manifest.Source?.Application),
            ("$project", manifest.Source?.Project), ("$ncFile", manifest.Source?.NcFileName),
            ("$exportedAt", manifest.Source?.ExportedAtUtc), ("$opNumber", manifest.Operation?.Number),
            ("$opName", manifest.Operation?.Name), ("$procedureId", manifest.Operation?.ProcedureId),
            ("$procedureName", manifest.Operation?.ProcedureName), ("$externalId", manifest.Machine?.ExternalId),
            ("$machineName", manifest.Machine?.Name), ("$post", manifest.Machine?.Postprocessor),
            ("$axes", manifest.Machine?.AxisConfiguration), ("$machineId", command.MachineId),
            ("$matchedBy", command.MachineMatchedBy), ("$units", manifest.Units), ("$cs", command.CoordinateSystemJson),
            ("$stock", command.SelectedStockPath), ("$nc", command.NcSha256), ("$toolTable", command.ToolTableSha256),
            ("$tools", JsonSerializer.Serialize(command.ToolNumbers)), ("$report", command.ReportJson),
            ("$now", now), ("$actor", command.Actor));

        foreach (var file in command.Files)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO simulation_package_import_files (import_id, role, relative_path, sha256, size_bytes,
                    stock_kind, source_operation, name, collision, selected, case_model_file_id)
                VALUES ($id, $role, $path, $sha, $size, $kind, $source, $name, $collision, $selected, $link);
                """, cancellationToken,
                ("$id", command.ImportId), ("$role", file.Role), ("$path", file.RelativePath), ("$sha", file.Sha256),
                ("$size", file.SizeBytes), ("$kind", file.StockKind), ("$source", file.SourceOperation), ("$name", file.Name),
                ("$collision", file.Collision is null ? null : file.Collision.Value ? 1 : 0), ("$selected", file.Selected ? 1 : 0),
                ("$link", linkIds.GetValueOrDefault(file.RelativePath)));
        }

        var values = command.WorkCs;
        var version = (setup?.Version ?? 0) + 1;
        await ExecuteAsync(connection, transaction, setup is null
            ? """
              INSERT INTO case_operation_cam_setups (case_operation_id, source, work_offset, origin_x_mm, origin_y_mm, origin_z_mm,
                  rotation_x_deg, rotation_y_deg, rotation_z_deg, notes, version, created_at, updated_at, updated_by)
              VALUES ($op, $source, $offset, $ox, $oy, $oz, $rx, $ry, $rz, $notes, $version, $now, $now, $actor);
              """
            : """
              UPDATE case_operation_cam_setups
              SET source = $source, work_offset = $offset, origin_x_mm = $ox, origin_y_mm = $oy, origin_z_mm = $oz,
                  rotation_x_deg = $rx, rotation_y_deg = $ry, rotation_z_deg = $rz, notes = $notes,
                  version = $version, updated_at = $now, updated_by = $actor
              WHERE case_operation_id = $op;
              """, cancellationToken,
            ("$op", command.CaseOperationId), ("$source", values.Source), ("$offset", values.WorkOffset),
            ("$ox", values.OriginMm.X), ("$oy", values.OriginMm.Y), ("$oz", values.OriginMm.Z),
            ("$rx", values.RotationDeg.X), ("$ry", values.RotationDeg.Y), ("$rz", values.RotationDeg.Z),
            ("$notes", values.Notes), ("$version", version), ("$now", now), ("$actor", command.Actor));

        if (command.RememberMapping is { } mapping && command.MachineId is not null)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT OR IGNORE INTO simulation_package_machine_mappings (id, match_field, match_value, machine_id, created_at, created_by)
                VALUES ($id, $field, $value, $machine, $now, $actor);
                """, cancellationToken,
                ("$id", Guid.NewGuid().ToString("N")), ("$field", mapping.Field), ("$value", mapping.Value.Trim()),
                ("$machine", command.MachineId), ("$now", now), ("$actor", command.Actor));
        }

        var related = new Dictionary<string, string>
        {
            ["caseId"] = command.CaseId,
            ["caseOperationId"] = command.CaseOperationId,
            ["simulationPackageImportId"] = command.ImportId
        };
        if (command.MachineId is not null) related["machineId"] = command.MachineId;
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction, new StructuredEventWrite(
            "simulation_package_imported", command.Now, command.Actor, related, "cimatron_package", null,
            previous.Count == 0 ? null : new { supersededImportIds = previous.Select(item => item.Id).ToArray() },
            new
            {
                manifestSha256 = command.ManifestSha256,
                packagePath = command.PackagePath,
                exporter = manifest.Exporter?.Version,
                cimatronVersion = manifest.Exporter?.CimatronVersion,
                operation = manifest.Operation?.Number,
                ucs = manifest.CoordinateSystem?.Name,
                machineId = command.MachineId,
                machineMatchedBy = command.MachineMatchedBy,
                selectedStock = command.SelectedStockPath,
                links = command.Links.Select(link => new { link.Kind, link.RelativePath }).ToArray()
            }), cancellationToken);
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction, new StructuredEventWrite(
            "case_operation_cam_setup_saved", command.Now, command.Actor,
            new Dictionary<string, string> { ["caseId"] = command.CaseId, ["caseOperationId"] = command.CaseOperationId },
            "cimatron_package", null,
            setup is null ? null : new { setup.Value.Version, setup.Value.Source },
            new { values.Source, values.WorkOffset, values.OriginMm, values.RotationDeg, values.Notes, Version = version }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(SimulationPackageApplyStatus.Imported, command.ImportId, null);
    }

    /// <summary>Stores a watcher attempt that a blocking diagnostic stopped, so the report stays visible.</summary>
    internal async Task<string> RecordBlockedAsync(
        string? caseId, string? caseOperationId, SimulationPackageManifest? manifest, string manifestSha256, string packagePath,
        string reportJson, IReadOnlyList<PackageDiagnostic> blocking, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var paths = await SqliteNetworkFolderSettings.ReadAsync(connection, transaction, cancellationToken);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO simulation_package_imports (id, case_id, case_operation_id, status, package_id, manifest_sha256,
                schema_version, package_path, exporter_version, cimatron_version, operation_number, machine_name, postprocessor,
                report_json, imported_at, imported_by)
            VALUES ($id, $case, $op, 'blocked', $packageId, $sha, $schema, $path, $exporterVersion, $cimatron, $opNumber,
                $machineName, $post, $report, $now, $actor);
            """, cancellationToken,
            ("$id", id), ("$case", caseId), ("$op", caseOperationId), ("$packageId", manifest?.PackageId), ("$sha", manifestSha256),
            ("$schema", manifest?.SchemaVersion), ("$path", paths.ToStored(packagePath) ?? packagePath),
            ("$exporterVersion", manifest?.Exporter?.Version), ("$cimatron", manifest?.Exporter?.CimatronVersion),
            ("$opNumber", manifest?.Operation?.Number), ("$machineName", manifest?.Machine?.Name),
            ("$post", manifest?.Machine?.Postprocessor), ("$report", reportJson), ("$now", Format(now)), ("$actor", actor));
        var related = new Dictionary<string, string> { ["simulationPackageImportId"] = id };
        if (caseId is not null) related["caseId"] = caseId;
        if (caseOperationId is not null) related["caseOperationId"] = caseOperationId;
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction, new StructuredEventWrite(
            "simulation_package_import_blocked", now, actor, related, "cimatron_package", null, null,
            new { manifestSha256, packagePath, codes = blocking.Select(item => item.Code).Distinct().ToArray() }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    /// <summary>Appends a blocked interactive attempt to the audit log only (the requester sees the report).</summary>
    internal async Task LogBlockedAsync(string? caseId, string? caseOperationId, string packagePath, string? manifestSha256,
        IReadOnlyList<PackageDiagnostic> blocking, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var related = new Dictionary<string, string>();
        if (caseId is not null) related["caseId"] = caseId;
        if (caseOperationId is not null) related["caseOperationId"] = caseOperationId;
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction, new StructuredEventWrite(
            "simulation_package_import_blocked", now, actor, related, "cimatron_package", null, null,
            new { manifestSha256, packagePath, codes = blocking.Select(item => item.Code).Distinct().ToArray() }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal async Task<IReadOnlyList<SimulationPackageMachineMapping>> ListMappingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT map.id, map.match_field, map.match_value, map.machine_id, m.number, m.name, map.created_at, map.created_by
            FROM simulation_package_machine_mappings map LEFT JOIN machines m ON m.id = map.machine_id
            ORDER BY map.match_field, map.match_value, m.number;
            """;
        var result = new List<SimulationPackageMachineMapping>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                NullableString(reader, 4), NullableString(reader, 5), Parse(reader.GetString(6)), reader.GetString(7)));
        }

        return result;
    }

    /// <summary>Adds a mapping row; returns null when the Machine does not exist, throws on a duplicate.</summary>
    internal async Task<SimulationPackageMachineMapping?> AddMappingAsync(
        string field, string value, string machineId, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var machine = await ScalarAsync(connection, transaction, "SELECT number FROM machines WHERE id = $id;", cancellationToken, ("$id", machineId));
        if (machine is null) return null;
        var id = Guid.NewGuid().ToString("N");
        await ExecuteAsync(connection, transaction, """
            INSERT INTO simulation_package_machine_mappings (id, match_field, match_value, machine_id, created_at, created_by)
            VALUES ($id, $field, $value, $machine, $now, $actor);
            """, cancellationToken,
            ("$id", id), ("$field", field), ("$value", value.Trim()), ("$machine", machineId), ("$now", Format(now)), ("$actor", actor));
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction, new StructuredEventWrite(
            "simulation_package_machine_mapping_added", now, actor,
            new Dictionary<string, string> { ["machineId"] = machineId, ["mappingId"] = id }, "planner_entered", null, null,
            new { field, value = value.Trim() }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ListMappingsAsync(cancellationToken)).First(item => item.MappingId == id);
    }

    internal async Task<bool> DeleteMappingAsync(string mappingId, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT match_field, match_value, machine_id FROM simulation_package_machine_mappings WHERE id = $id;";
        read.Parameters.AddWithValue("$id", mappingId);
        string field, value, machineId;
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return false;
            (field, value, machineId) = (reader.GetString(0), reader.GetString(1), reader.GetString(2));
        }

        await ExecuteAsync(connection, transaction, "DELETE FROM simulation_package_machine_mappings WHERE id = $id;", cancellationToken, ("$id", mappingId));
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction, new StructuredEventWrite(
            "simulation_package_machine_mapping_removed", now, actor,
            new Dictionary<string, string> { ["machineId"] = machineId, ["mappingId"] = mappingId }, "planner_entered", null,
            new { field, value }, null), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<(int Version, string Source, string? UpdatedBy, DateTimeOffset UpdatedAt)?> ReadCamSetupAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string caseOperationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version, source, updated_by, updated_at FROM case_operation_cam_setups WHERE case_operation_id = $op;";
        command.Parameters.AddWithValue("$op", caseOperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.GetInt32(0), reader.GetString(1), NullableString(reader, 2), Parse(reader.GetString(3)));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is DBNull ? null : result;
    }

    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    internal static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
}
