using System.Globalization;
using System.Text.Json;
using Meimad.Planner.SimulationPackage.Coordinates;
using Meimad.Planner.SimulationPackage.Manifest;
using Meimad.Planner.SimulationPackage.Nc;
using Meimad.Planner.SimulationPackage.Tools;
using Meimad.Planner.SimulationPackage.Validation;

namespace Meimad.CimatronExporter;

internal sealed record ExportResult(bool Complete, string? PackagePath, ExportLog Log, int ExitCode);

/// <summary>
/// The export workflow of exporter definition §11: validate the Cimatron state, write everything to
/// <c>&lt;output&gt;\.staging\{guid}</c>, post the NC by UCS (never editing the posted G-code), export
/// the tool data, stock, fixtures and part, read the UCS, build the manifest with SHA-256 and versions,
/// validate the staging folder with the same validator the Server uses, move it atomically to
/// <c>&lt;output&gt;\&lt;package name&gt;</c> (an earlier package there is moved to <c>.superseded</c>
/// first) and write <c>COMPLETE</c> last. On any failure nothing is published: the staging folder is
/// kept under <c>.failed</c> with its export log, and <c>last-export-log.json</c> /
/// <c>last-export-report.txt</c> beside the package say what happened.
/// </summary>
internal sealed class PackageExporter(ICimatronApi api, TimeProvider clock)
{
    internal const string ExporterName = "Meimad.CimatronExporter";
    internal const double DefaultMeshToleranceMm = 0.01;

    public ExportResult Export(ExportOptions options)
    {
        var log = new ExportLog(clock)
        {
            CimatronVersion = api.CimatronVersion,
            AdapterVersion = api.AdapterVersion,
            Output = options.Output,
            Capabilities = api.Capabilities.All().ToDictionary(item => item.Name, item => item.Available)
        };
        var output = Path.GetFullPath(options.Output!);
        Directory.CreateDirectory(output);
        var staging = Path.Combine(output, ".staging", Guid.NewGuid().ToString("N"));
        string? published = null;
        try
        {
            var state = ValidateState(options, log);
            if (state is null) return Finish(log, output, staging, options, null);

            foreach (var folder in new[] { "nc", "tools", "geometry", "metadata" })
                Directory.CreateDirectory(Path.Combine(staging, folder));
            log.Ok("staging", $"Writing to {staging}.");

            var nc = ExportNc(options, state, staging, log, out var toolReport);
            if (nc is null) return Finish(log, output, staging, options, null);
            var ncFacts = NcProgramScanner.ScanFile(Path.Combine(staging, nc.Path.Replace('/', Path.DirectorySeparatorChar)));
            var tools = ExportTools(options, staging, ncFacts, log);
            var stock = ExportStock(options, state, staging, log);
            if (stock is null) return Finish(log, output, staging, options, null);
            var (fixtures, part) = ExportGeometry(options, state, staging, log);
            if (log.HasErrors) return Finish(log, output, staging, options, null);
            var coordinateSystem = ReadCoordinateSystem(options, state.GeometryInProcedureUcs,
                Path.Combine(staging, nc.Path.Replace('/', Path.DirectorySeparatorChar)), log);
            if (coordinateSystem is null) return Finish(log, output, staging, options, null);

            File.WriteAllText(Path.Combine(staging, "metadata", "coordinate-systems.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                coordinateSystems = new[] { coordinateSystem }
            }, ManifestJson.Options));

            var manifest = new SimulationPackageManifest
            {
                SchemaVersion = ManifestJson.CurrentSchemaVersion,
                PackageId = Guid.NewGuid().ToString("N"),
                Exporter = new ExporterInfo
                {
                    Name = ExporterName,
                    Version = log.ExporterVersion,
                    CimatronVersion = api.CimatronVersion,
                    AdapterVersion = api.AdapterVersion,
                    MeshToleranceMm = options.MeshToleranceGiven ? options.MeshToleranceMm : DefaultMeshToleranceMm,
                    UsedDefaultMeshTolerance = !options.MeshToleranceGiven
                },
                Source = new SourceInfo
                {
                    Application = "Cimatron",
                    Project = state.Document.Name,
                    NcFileName = Path.GetFileName(nc.Path),
                    ExportedAtUtc = clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    Host = Environment.MachineName,
                    User = Environment.UserName,
                    DocumentPath = state.Document.FilePath,
                    DocumentId = state.Document.DocumentId,
                    Setup = state.Selection?.SetupName ?? state.Toolpath?.SetupName,
                    Operations = state.Selection?.Operations
                        ?? (state.Toolpath is { } single ? [new CamOperationReference { Kind = "toolpath", Id = single.Id, Name = single.Name }] : null)
                },
                Meimad = options.ImportContext,
                Part = options.PartNumber is null && options.PartName is null && state.Document.PartNumber is null
                    ? null
                    : new PartInfo { Number = options.PartNumber ?? state.Document.PartNumber, Name = options.PartName },
                Operation = new OperationInfo
                {
                    Number = options.OperationNumber,
                    Name = options.OperationName ?? state.Procedures.FirstOrDefault()?.OperationName,
                    ProcedureId = state.Procedures.FirstOrDefault()?.Id,
                    ProcedureName = state.Procedures.FirstOrDefault()?.Name,
                    ProcedureIds = state.Procedures.Count > 1 ? state.Procedures.Select(item => item.Id).ToArray() : null
                },
                Machine = new MachineInfo
                {
                    ExternalId = options.MachineExternalId,
                    Name = options.MachineName,
                    Postprocessor = options.Post,
                    AxisConfiguration = options.AxisConfiguration
                },
                Units = PackageUnits.Millimetres,
                CoordinateSystem = coordinateSystem,
                Files = new PackageFiles
                {
                    Nc = nc,
                    ToolTable = tools.ToolTable,
                    Cutters = tools.Cutters,
                    Holders = tools.Holders,
                    Materials = tools.Materials,
                    JobsXml = tools.JobsXml,
                    ToolReport = toolReport,
                    CoordinateSystems = new PackageFileReference { Path = "metadata/coordinate-systems.json" },
                    Stock = [stock],
                    Fixtures = fixtures.Count == 0 ? null : fixtures,
                    Part = part
                }
            };
            manifest = PackageManifestBuilder.WithHashes(manifest, staging);
            File.WriteAllText(Path.Combine(staging, ManifestJson.ManifestFileName), ManifestJson.Serialize(manifest));
            File.WriteAllText(Path.Combine(staging, ManifestJson.PackageFileName), JsonSerializer.Serialize(new
            {
                format = "meimad-sim",
                schemaVersion = ManifestJson.CurrentSchemaVersion,
                packageId = manifest.PackageId,
                manifest = ManifestJson.ManifestFileName
            }, ManifestJson.Options));
            log.Ok("manifest", $"Manifest written with {manifest.EnumerateFiles().Count()} files, SHA-256 and versions.");

            var report = new SimulationPackageValidator(new PackageValidationOptions
            {
                ApprovedRoots = [output],
                RequireCompleteMarker = false
            }).ValidateAsync(staging).GetAwaiter().GetResult();
            log.AddDiagnostics(report.Diagnostics);
            if (report.HasBlockingErrors)
            {
                log.Error("validate", "package_invalid", "The package written to staging does not pass the Meimad package validation.");
                return Finish(log, output, staging, options, null);
            }

            log.Ok("validate", "Staging package passes the Meimad package validation.");
            published = Commit(options, output, staging, log);
            return Finish(log, output, staging, options, published);
        }
        catch (CimatronApiException exception)
        {
            log.Error("cimatron", exception.Code, exception.Message);
            return Finish(log, output, staging, options, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            log.Error("io", "export_io_failed", exception.Message);
            return Finish(log, output, staging, options, null);
        }
    }

    /// <summary>
    /// What step 1 settled: the document, its procedures, the stock STL to copy when Cimatron exports none (given or
    /// found beside the document) and the frame every geometry file is in (procedure UCS = mode A, else part-model
    /// coordinates = mode B). The stock export uses the same frame as the fixtures and the part.
    /// </summary>
    private sealed record ExportState(CimatronDocumentInfo Document, IReadOnlyList<CimatronProcedure> Procedures, string? StockFile,
        bool StockFileDiscovered, bool GeometryInProcedureUcs, CimatronToolpath? Toolpath = null, CamSelection? Selection = null)
    {
        public CimatronUcsFrame Frame => GeometryInProcedureUcs ? CimatronUcsFrame.Procedure : CimatronUcsFrame.Model;
    }

    // Mode A when the operator says so or the API exports entities in the procedure UCS; mode B otherwise.
    private bool GeometryInProcedureUcs(ExportOptions options) =>
        options.GeometryInUcs
        || (options.GeometryFolder is null && (api.Capabilities.EntityExport || api.Capabilities.MultiStl) && api.Capabilities.GeometryInUcs);

    /// <summary>Step 1: an NC document, procedures with toolpath, a post, a known UCS, stock and geometry sources.</summary>
    private ExportState? ValidateState(ExportOptions options, ExportLog log)
    {
        CimatronDocumentInfo document;
        try
        {
            document = api.GetActiveNcDocument();
        }
        catch (CimatronApiException exception)
        {
            log.Error("state", exception.Code, exception.Message);
            return null;
        }

        if (options.ExpectedDocumentPath is not null
            && !string.Equals(Path.GetFullPath(options.ExpectedDocumentPath), document.FilePath is null ? null : Path.GetFullPath(document.FilePath), StringComparison.OrdinalIgnoreCase))
        {
            log.Error("state", "document_changed",
                $"The active Cimatron document is now '{document.FilePath ?? document.Name}', not '{options.ExpectedDocumentPath}' the operations were chosen in; activate that document or choose again.");
            return null;
        }

        if (options.RequireSavedDocument && document.Modified == true)
        {
            log.Error("state", "document_unsaved", $"'{document.Name}' has unsaved changes in Cimatron: save it there, then import again.");
            return null;
        }

        var procedures = api.GetProcedures();

        // One package is one toolpath (one machine program): choose it when the document has several.
        var toolpaths = api.GetToolpaths();
        CimatronToolpath? toolpath = null;
        CamSelection? selection = null;
        if (options.HasIdSelection)
        {
            selection = CamSelection.From(options, toolpaths, procedures, log);
            if (selection is null) return null;
            procedures = selection.Procedures;
            toolpath = selection.StockToolpath;
            if (!api.Capabilities.PostSelected)
                log.Error("state", "post_selection_unavailable", "This Cimatron cannot post selected operations (no PostByUcsByIdVec).");
        }
        else if (options.Toolpath is not null)
        {
            toolpath = toolpaths.FirstOrDefault(item => string.Equals(item.Name, options.Toolpath, StringComparison.OrdinalIgnoreCase));
            if (toolpath is null)
            {
                log.Error("state", "toolpath_not_found", $"Toolpath '{options.Toolpath}' does not exist; the NC document has {DescribeToolpaths(toolpaths)}.");
                return null;
            }

            procedures = procedures.Where(item => string.Equals(item.OperationName, toolpath.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (!api.Capabilities.PostSelected)
                log.Error("state", "post_selection_unavailable", "This Cimatron cannot post one toolpath (no PostByUcsByIdVec).");
        }
        else if (toolpaths.Count == 1)
        {
            toolpath = toolpaths[0];
        }
        else if (toolpaths.Count > 1 && options.ProcedureId is null)
        {
            log.Error("state", "toolpath_required",
                $"The NC document has {toolpaths.Count} toolpaths ({DescribeToolpaths(toolpaths)}); a package holds one: choose it with --toolpath <name>.");
            return null;
        }

        if (options.ProcedureId is { } chosen)
        {
            procedures = procedures.Count == 0
                ? [new CimatronProcedure(chosen, $"Procedure {chosen}", options.Ucs, true)]
                : procedures.Where(item => item.Id == chosen).ToArray();
        }

        if (procedures.Count == 0)
        {
            log.Error("state", "cimatron_no_procedures", options.ProcedureId is null
                ? "The NC document has no procedure the exporter can read; give --procedure <id>."
                : $"Procedure {options.ProcedureId} does not exist in the NC document.");
            return null;
        }

        if (procedures.Any(item => !item.HasToolpath))
        {
            log.Error("state", "cimatron_procedure_without_toolpath",
                "Procedures without a calculated toolpath: " + string.Join(", ", procedures.Where(item => !item.HasToolpath).Select(item => item.Name)));
        }

        if (!api.Capabilities.PostByUcs && !api.Capabilities.PostToAll)
            log.Error("state", "post_unavailable", "This Cimatron exposes no post-processing method.");
        var ucsNames = api.GetUcsNames();
        if (ucsNames.Count > 0 && !ucsNames.Contains(options.Ucs!, StringComparer.OrdinalIgnoreCase))
            log.Error("state", "ucs_not_found", $"UCS '{options.Ucs}' does not exist in the NC document.");
        var procedureUcs = procedures.Select(item => item.UcsName).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (procedureUcs.Any(name => !string.Equals(name, options.Ucs, StringComparison.OrdinalIgnoreCase)))
        {
            // Proposed (implementation plan, 2026-10-12): with PostByUcsToAllNcProcedures the NC is written relative to
            // --ucs whatever orientation UCS each procedure has (5-axis and indexed work use many), so the package frame is
            // still one UCS. Without it the posted frame is unknown and the package is blocked.
            if (api.Capabilities.PostByUcs)
                log.Warning("state", "ucs_procedures_differ",
                    $"The procedures use UCS {string.Join(", ", procedureUcs)}; the NC is posted relative to '{options.Ucs}' and the geometry is placed in that UCS.");
            else
                log.Error("state", "ucs_mismatch",
                    $"The procedures use UCS {string.Join(", ", procedureUcs)}, not '{options.Ucs}'; export one UCS per package.");
        }

        var stockFile = options.StockFile;
        var stockDiscovered = false;
        var stockFromProcedure = api.Capabilities.RemainingStock && toolpath?.StockProcedureId is not null;
        if (stockFile is null && !api.Capabilities.InitialStock && !stockFromProcedure
            && !(api.Capabilities.RemainingStock && options.StockProcedureId is not null))
        {
            // Cimatron 2026.0 has no initial-stock export: a single STOCK*.stl Cimatron left beside the document is the fallback.
            var found = StockFileDiscovery.Find(document.FilePath);
            if (found.Count == 1)
            {
                stockFile = found[0];
                stockDiscovered = true;
            }
            else if (found.Count > 1)
            {
                log.Error("state", "stock_ambiguous", "Several stock STL files lie beside the Cimatron document; choose one with --stock-file: " + string.Join(", ", found));
            }
            else
            {
                log.Error("state", "stock_unavailable", toolpath is null && toolpaths.Count > 1
                    ? "No stock can be exported: choose the toolpath with --toolpath (its setup's stock procedure gives the stock), or give --stock-file."
                    : "No stock can be exported: give --stock-file, or --stock-procedure on Cimatron 2026+.");
            }
        }

        if (options.GeometryFolder is null && !api.Capabilities.EntityExport && !api.Capabilities.MultiStl)
            log.Warning("state", "geometry_unavailable", "No target/fixture export is available; the package will have no part or fixture (give --geometry-folder).");
        if (log.HasErrors) return null;
        var inUcs = GeometryInProcedureUcs(options);
        log.Ok("state", $"Active NC document '{document.Name}'{(toolpath is null ? "" : $", toolpath '{toolpath.Name}'")}, {procedures.Count} procedure(s), UCS '{options.Ucs}', geometry in {(inUcs ? "the procedure UCS (mode A)" : "part-model coordinates (mode B)")}.");
        return new ExportState(document, procedures, stockFile, stockDiscovered, inUcs, toolpath, selection);
    }

    private static string DescribeToolpaths(IReadOnlyList<CimatronToolpath> toolpaths) =>
        toolpaths.Count == 0
            ? "no readable toolpath"
            : string.Join(", ", toolpaths.Select(item => item.SetupName is null ? $"'{item.Name}'" : $"'{item.Name}' (setup '{item.SetupName}')"));

    // A file name for the posted NC from the toolpath name.
    private static string NcOutputName(string toolpath)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(toolpath.Select(character => invalid.Contains(character) || character == ' ' ? '_' : character).ToArray());
        return name.Length == 0 ? "toolpath" : name;
    }

    /// <summary>Step 3: post by UCS (fallback: post all, the UCS is still recorded). The NC text is never changed.</summary>
    private PackageFileReference? ExportNc(ExportOptions options, ExportState state, string staging, ExportLog log,
        out PackageFileReference? toolReport)
    {
        toolReport = null;
        var folder = Path.Combine(staging, "nc");
        IReadOnlyList<string> files;
        try
        {
            if (state.Selection is { } chosen)
            {
                // PostByUcsByIdVec with the chosen nodes; if Cimatron writes nothing for toolpath nodes, with their procedures.
                var name = NcOutputName(chosen.Name);
                files = api.PostSelected(options.Post!, folder, chosen.NodeIds, options.Ucs!, name);
                var procedureNodes = state.Procedures.Select(item => item.Id).ToArray();
                if (!files.Any(path => PackagePaths.IsUnder(path, folder) && File.Exists(path)) && !procedureNodes.SequenceEqual(chosen.NodeIds))
                {
                    log.Warning("nc", "post_by_procedures",
                        $"Posting nodes {string.Join(", ", chosen.NodeIds)} wrote nothing; posted their {procedureNodes.Length} procedure(s) instead.");
                    files = api.PostSelected(options.Post!, folder, procedureNodes, options.Ucs!, name);
                }
            }
            else if (options.Toolpath is not null && state.Toolpath is { } toolpath)
            {
                // PostByUcsByIdVec with the toolpath node; if Cimatron writes nothing for it, with its procedures.
                var name = NcOutputName(toolpath.Name);
                files = api.PostSelected(options.Post!, folder, [toolpath.Id], options.Ucs!, name);
                if (!files.Any(path => PackagePaths.IsUnder(path, folder) && File.Exists(path)))
                {
                    log.Warning("nc", "post_by_procedures",
                        $"Posting toolpath node {toolpath.Id} wrote nothing; posted its {state.Procedures.Count} procedure(s) instead.");
                    files = api.PostSelected(options.Post!, folder, state.Procedures.Select(item => item.Id).ToArray(), options.Ucs!, name);
                }
            }
            else if (api.Capabilities.PostByUcs)
            {
                files = api.PostByUcs(options.Post!, folder, options.StartHome, options.Ucs!);
            }
            else
            {
                files = api.PostToAll(options.Post!, folder, options.StartHome);
                log.Warning("nc", "post_fallback_used",
                    $"PostByUcsToAllNcProcedures is not available; posted with ExecutePostToAllNcProcedures and recorded the procedure UCS '{options.Ucs}'.");
            }
        }
        catch (CimatronApiException exception)
        {
            log.Error("nc", "post_failed", exception.Message);
            return null;
        }

        var written = files.Where(path => PackagePaths.IsUnder(path, folder) && File.Exists(path)).ToArray();
        // Cimatron posts also write reports beside the NC (2026-10-12: "<document>.<toolpath>.<post>.Tools.html"). They are
        // never NC candidates: the tool report goes to tools/ as the package's toolReport, any other report to
        // metadata/post-reports/ (not listed in the manifest).
        var inside = written.Where(path => !PostReport.IsReport(path)).ToArray();
        foreach (var report in written.Where(PostReport.IsReport))
        {
            var name = Path.GetFileName(report);
            if (toolReport is null && PostReport.IsToolReport(report))
            {
                File.Move(report, Path.Combine(staging, "tools", name));
                toolReport = new PackageFileReference { Path = "tools/" + name };
                log.Ok("nc", $"The post's tool report {name} is kept as the package tool report.");
            }
            else
            {
                var reports = Path.Combine(staging, "metadata", "post-reports");
                Directory.CreateDirectory(reports);
                File.Move(report, Path.Combine(reports, name));
                log.Ok("nc", $"The post's report {name} is kept under metadata/post-reports.");
            }
        }

        if (inside.Length == 0)
        {
            // Cimatron's GPP post explains a stop only in its log (2026-10-12: "Cannot move simultaneously in 3 axes during a
            // 2.5X procedure"): quote its error lines.
            var errors = PostReport.Errors(Path.Combine(staging, "metadata", "post-reports"));
            log.Error("nc", errors.Count > 0 ? "post_stopped" : "post_no_output", errors.Count > 0
                ? "The post-processor stopped without writing an NC file: " + string.Join(" ", errors)
                  + " Correct the procedure in Cimatron (or post it there to see the same message) and export again."
                : "The post-processor did not write an NC file into the package.");
            return null;
        }

        string selected;
        if (options.NcFile is not null)
        {
            // By file name, or by its path below nc/ when two folders hold the same name.
            var wanted = options.NcFile.Replace('\\', '/').TrimStart('/');
            selected = inside.FirstOrDefault(path => string.Equals(Path.GetFileName(path), wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetRelativePath(folder, path).Replace('\\', '/'), wanted, StringComparison.OrdinalIgnoreCase)) ?? "";
            if (selected.Length == 0)
            {
                log.Error("nc", "nc_file_not_found", $"The post did not write '{options.NcFile}'.");
                return null;
            }
        }
        else if (inside.Length > 1)
        {
            // A main program with its subprograms is one program; unrelated programs need the user's choice.
            var programs = inside.Select(NcProgramSet.Read).ToArray();
            if (NcProgramSet.MainProgram(programs) is not { } main)
            {
                log.Error("nc", "nc_ambiguous", "The post wrote several NC programs that do not call each other; choose one with --nc-file: "
                    + string.Join("; ", programs.Select(program => NcProgramSet.Describe(program, staging))));
                return null;
            }

            selected = main.Path;
            log.Warning("nc", "nc_subprograms_unlisted",
                $"{NcProgramSet.Describe(main, staging)} calls "
                + string.Join("; ", programs.Where(program => program != main).Select(program => NcProgramSet.Describe(program, staging)))
                + ". The package carries one NC program, so the subprograms stay beside it but are not hashed, checked or simulated "
                + "(open decision in the implementation plan).");
        }
        else
        {
            selected = inside[0];
        }

        // The package path keeps the folder Cimatron wrote into (2026-10-12: the NC was listed but not found at nc/<name>).
        var relative = Path.GetRelativePath(staging, selected).Replace('\\', '/');
        if (!File.Exists(selected))
        {
            log.Error("nc", "post_output_missing",
                $"Cimatron reported {relative}, but the file is gone. The post folder now holds: {DescribeFolder(staging, folder)}.");
            return null;
        }

        // Programs called but not posted live on the machine (or are missing there): the simulation cannot follow them.
        var posted = inside.Select(NcProgramSet.Read).ToArray();
        if (NcProgramSet.CallsOutside(posted) is { Count: > 0 } outside)
            log.Warning("nc", "nc_calls_unposted_programs",
                $"The NC calls program(s) {string.Join(", ", outside)}, which the post did not write; they must exist on the machine, "
                + "and the simulation does not follow them.");

        log.Ok("nc", $"Posted {relative} with '{options.Post}' by UCS '{options.Ucs}' ({state.Procedures.Count} procedure(s)); "
            + $"the post wrote {string.Join(", ", written.Select(path => Path.GetRelativePath(staging, path).Replace('\\', '/')))}.");
        return new PackageFileReference { Path = relative };
    }

    private static string DescribeFolder(string staging, string folder)
    {
        var files = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .Select(path => $"{Path.GetRelativePath(staging, path).Replace('\\', '/')} ({new FileInfo(path).Length} bytes)").ToArray()
            : [];
        return files.Length == 0 ? "nothing" : string.Join(", ", files);
    }

    private sealed record ToolFiles(PackageFileReference ToolTable, PackageFileReference? Cutters, PackageFileReference? Holders,
        PackageFileReference? Materials, PackageFileReference? JobsXml);

    /// <summary>Step 4: cutters/holders/materials CSV, Job Manager XML and tool-table.json keyed by T-number.</summary>
    private ToolFiles ExportTools(ExportOptions options, string staging, NcProgramFacts nc, ExportLog log)
    {
        PackageFileReference? cutters = null, holders = null, materials = null, jobs = null;
        if (api.Capabilities.CuttersAndHoldersCsv)
        {
            try
            {
                api.ExportCuttersAndHolders(Path.Combine(staging, "tools", "cutters.csv"), Path.Combine(staging, "tools", "holders.csv"),
                    Path.Combine(staging, "tools", "materials.csv"));
                cutters = Existing(staging, "tools/cutters.csv");
                holders = Existing(staging, "tools/holders.csv");
                materials = Existing(staging, "tools/materials.csv");
                log.Ok("tools", "Cutters, holders and materials exported to CSV.");
            }
            catch (CimatronApiException exception)
            {
                log.Warning("tools", "cutters_export_failed", exception.Message);
            }
        }
        else
        {
            log.Skipped("tools", "ExportCuttersAndHoldersToCSV is not available.");
        }

        if (api.Capabilities.JobsXml)
        {
            try
            {
                api.ExportJobsData(Path.Combine(staging, "metadata", "jobs.xml"));
                jobs = Existing(staging, "metadata/jobs.xml");
                if (jobs is not null)
                    log.Ok("jobs", "Job Manager XML exported.");
                else if (Directory.Exists(Path.Combine(staging, "metadata", "jobs")))
                    log.Warning("jobs", "jobs_export_many_files", "Cimatron wrote several Job Manager files; they are kept under metadata/jobs and not referenced by the manifest.");
                else
                    log.Warning("jobs", "jobs_export_failed", "Cimatron wrote no Job Manager XML.");
            }
            catch (CimatronApiException exception)
            {
                log.Warning("jobs", "jobs_export_failed", exception.Message);
            }
        }

        var fromApi = api.GetTools().GroupBy(tool => tool.Number).ToDictionary(group => group.Key, group => group.First());
        if (cutters is not null)
        {
            // Cimatron 2026.0 has no tool-list API; its cutters/holders CSV has the sizes, keyed by magazine (T) number.
            try
            {
                var fromCsv = CimatronToolCsv.Read(Path.Combine(staging, "tools", "cutters.csv"),
                    holders is null ? null : Path.Combine(staging, "tools", "holders.csv"));
                var shared = fromCsv.GroupBy(tool => tool.Number).Where(group => group.Count() > 1 && nc.ToolNumbers.Contains(group.Key)).ToArray();
                if (shared.Length > 0)
                    log.Warning("tools", "tool_number_shared",
                        "Several Cimatron cutters share a magazine number; the first one is used: "
                        + string.Join("; ", shared.Select(group => $"T{group.Key} = {string.Join(", ", group.Select(tool => tool.Name))}")) + ".");
                var added = new List<int>();
                foreach (var tool in fromCsv.Where(tool => nc.ToolNumbers.Contains(tool.Number)))
                    if (fromApi.TryAdd(tool.Number, tool)) added.Add(tool.Number);
                if (added.Count > 0) log.Ok("tools", $"Tool sizes and holders from cutters.csv for T{string.Join(", T", added.Order())}.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
            {
                log.Warning("tools", "cutters_csv_unreadable", exception.Message);
            }
        }

        var numbers = fromApi.Keys.Union(nc.ToolNumbers).Order().ToArray();
        var missing = nc.ToolNumbers.Where(number => !fromApi.ContainsKey(number)).ToArray();
        if (missing.Length > 0)
        {
            log.Warning("tools", "tool_data_from_nc",
                "Tool data was not available from Cimatron for T" + string.Join(", T", missing) + "; they are listed by number only.");
        }

        var table = new ToolTableDocument
        {
            Source = new ToolTableSource { Application = "Cimatron", ExportedAtUtc = clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) },
            Tools = numbers.Select(number => fromApi.TryGetValue(number, out var tool)
                ? new ToolTableEntry
                {
                    Number = number,
                    ToolIdentifier = $"T{number}",
                    Description = string.IsNullOrWhiteSpace(tool.Name) ? $"T{number}" : tool.Name,
                    Name = string.IsNullOrWhiteSpace(tool.Name) ? $"T{number}" : tool.Name,
                    Type = ToolType(tool.Type),
                    DiameterMm = tool.DiameterMm,
                    CornerRadiusMm = tool.CornerRadiusMm,
                    FluteLengthMm = tool.FluteLengthMm,
                    GaugeLengthMm = tool.GaugeLengthMm,
                    TaperAngleDeg = tool.TaperAngleDeg,
                    Holder = tool.HolderName is null && tool.HolderDiameterMm is null && tool.HolderLengthMm is null
                        ? null
                        : new ToolHolder { Name = tool.HolderName, DiameterMm = tool.HolderDiameterMm, LengthMm = tool.HolderLengthMm }
                }
                : new ToolTableEntry { Number = number, ToolIdentifier = $"T{number}", Description = $"T{number}", Name = $"T{number}" }).ToArray()
        };
        File.WriteAllText(Path.Combine(staging, "tools", "tool-table.json"), table.Serialize());
        log.Ok("tools", $"tool-table.json lists T{string.Join(", T", numbers)}.");
        return new ToolFiles(new PackageFileReference { Path = "tools/tool-table.json" }, cutters, holders, materials, jobs);
    }

    /// <summary>Step 5: remaining stock (2026+ API), initial stock, or an existing stock STL (fallback).</summary>
    private StockFileReference? ExportStock(ExportOptions options, ExportState state, string staging, ExportLog log)
    {
        var remaining = options.Stock == "remaining" || (options.Stock == "auto" && options.StockProcedureId is not null);
        try
        {
            if (state.StockFile is not null)
            {
                if (!File.Exists(state.StockFile))
                {
                    log.Error("stock", "stock_file_missing", $"The stock file '{state.StockFile}' does not exist.");
                    return null;
                }

                File.Copy(state.StockFile, Path.Combine(staging, "geometry", "stock.stl"));
                log.Warning("stock", "stock_fallback_rule", state.StockFileDiscovered
                    ? $"Stock taken from '{state.StockFile}', the only stock STL beside the Cimatron document (fallback rule; Cimatron 2026.0 exports no initial stock through its API). Check that it is in {(state.GeometryInProcedureUcs ? "the procedure UCS" : "part-model coordinates")}."
                    : $"Stock taken from '{state.StockFile}' (fallback rule).");
                return new StockFileReference
                {
                    Path = "geometry/stock.stl",
                    Kind = remaining ? StockKinds.Remaining : StockKinds.Initial,
                    SourceOperation = remaining ? options.SourceOperation : null,
                    FoundBy = "fallback"
                };
            }

            if (remaining)
            {
                if (!api.Capabilities.RemainingStock || options.StockProcedureId is null)
                {
                    log.Error("stock", "remaining_stock_unavailable", "Remaining stock needs Cimatron 2026+ (ExportProceduresRemainingStock) and --stock-procedure.");
                    return null;
                }

                var name = options.SourceOperation is null ? "stock-remaining.stl" : $"stock-from-op{options.SourceOperation}.stl";
                api.ExportRemainingStock(options.StockProcedureId.Value, state.Frame, Path.Combine(staging, "geometry", name));
                if (!File.Exists(Path.Combine(staging, "geometry", name)))
                {
                    log.Error("stock", "stock_export_failed", "Cimatron reported success but wrote no remaining-stock STL.");
                    return null;
                }

                log.Ok("stock", $"Remaining stock of procedure {options.StockProcedureId} exported in {(state.GeometryInProcedureUcs ? "the procedure UCS" : "part-model coordinates")}.");
                return new StockFileReference
                {
                    Path = "geometry/" + name,
                    Kind = StockKinds.Remaining,
                    SourceOperation = options.SourceOperation,
                    FoundBy = "remaining-stock-api"
                };
            }

            if (!api.Capabilities.InitialStock && api.Capabilities.RemainingStock && state.Toolpath?.StockProcedureId is { } stockProcedure)
            {
                // Cimatron 2026.0 exports no initial stock; the result of the setup's stock procedure ("Stock By
                // Contours") is that stock. It has no machining UCS, so it is exported in part-model coordinates only.
                if (state.GeometryInProcedureUcs)
                {
                    log.Error("stock", "stock_frame_unavailable",
                        $"The initial stock comes from Cimatron's stock procedure {stockProcedure}, in part-model coordinates only; export without --geometry-in-ucs or give --stock-file.");
                    return null;
                }

                var stockPath = Path.Combine(staging, "geometry", "stock.stl");
                api.ExportRemainingStock(stockProcedure, CimatronUcsFrame.Model, stockPath);
                if (!File.Exists(stockPath))
                {
                    log.Error("stock", "stock_export_failed", $"Cimatron wrote no STL for stock procedure {stockProcedure}.");
                    return null;
                }

                log.Ok("stock", $"Initial stock from Cimatron's stock procedure {stockProcedure}{(state.Toolpath.SetupName is null ? "" : $" of setup '{state.Toolpath.SetupName}'")}, in part-model coordinates.");
                return new StockFileReference { Path = "geometry/stock.stl", Kind = StockKinds.Initial, FoundBy = "stock-procedure" };
            }

            api.ExportInitialStock(state.Frame, Path.Combine(staging, "geometry", "stock.stl"));
            if (!File.Exists(Path.Combine(staging, "geometry", "stock.stl")))
            {
                log.Error("stock", "stock_export_failed", "Cimatron reported success but wrote no stock STL.");
                return null;
            }

            log.Ok("stock", "Initial stock exported.");
            return new StockFileReference { Path = "geometry/stock.stl", Kind = StockKinds.Initial, FoundBy = "stock-procedure" };
        }
        catch (CimatronApiException exception)
        {
            log.Error("stock", "stock_export_failed", exception.Message);
            return null;
        }
    }

    /// <summary>Step 6: separate fixture files and one target part, never merged.</summary>
    private (List<FixtureFileReference> Fixtures, PartFileReference? Part) ExportGeometry(ExportOptions options, ExportState state, string staging, ExportLog log)
    {
        var raw = Path.Combine(staging, "metadata", "geometry-export");
        Directory.CreateDirectory(raw);
        IReadOnlyList<CimatronGeometryFile> files;
        try
        {
            if (options.GeometryFolder is not null)
            {
                if (!Directory.Exists(options.GeometryFolder))
                {
                    log.Error("geometry", "geometry_folder_missing", $"The geometry folder '{options.GeometryFolder}' does not exist.");
                    return ([], null);
                }

                files = Directory.GetFiles(options.GeometryFolder)
                    .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".stl" or ".stp" or ".step")
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Select(path => new CimatronGeometryFile(path, GeometryClassifier.Classify(path), Path.GetFileNameWithoutExtension(path), "multi-stl-folder"))
                    .ToArray();
            }
            else if (api.Capabilities.EntityExport || api.Capabilities.MultiStl)
            {
                files = api.ExportPartGeometry(options.Ucs!, raw);
            }
            else
            {
                files = [];
            }
        }
        catch (CimatronApiException exception)
        {
            log.Error("geometry", "geometry_export_failed", exception.Message);
            return ([], null);
        }

        var targets = files.Where(file => file.Role == GeometryRoles.Target).ToArray();
        if (targets.Length > 1)
        {
            log.Error("geometry", "part_ambiguous", "Several target parts were exported: " + string.Join(", ", targets.Select(file => Path.GetFileName(file.Path))));
            return ([], null);
        }

        PartFileReference? part = null;
        if (targets.Length == 1)
        {
            var name = "part" + Path.GetExtension(targets[0].Path).ToLowerInvariant();
            File.Copy(targets[0].Path, Path.Combine(staging, "geometry", name));
            part = new PartFileReference { Path = "geometry/" + name, Role = "finished-part", Name = targets[0].Name };
        }
        else
        {
            log.Warning("geometry", "part_missing", "No target part was exported.");
        }

        var fixtures = new List<FixtureFileReference>();
        foreach (var fixture in files.Where(file => file.Role == GeometryRoles.Fixture))
        {
            var name = $"fixture-{fixtures.Count + 1:000}{Path.GetExtension(fixture.Path).ToLowerInvariant()}";
            File.Copy(fixture.Path, Path.Combine(staging, "geometry", name));
            fixtures.Add(new FixtureFileReference { Path = "geometry/" + name, Name = fixture.Name, Collision = true });
        }

        if (fixtures.Count == 0) log.Warning("geometry", "fixture_missing", "No fixture was exported.");
        var others = files.Count(file => file.Role == GeometryRoles.Other);
        if (others > 0) log.Skipped("geometry", $"{others} other part file(s) are not part of the package.");
        if (Directory.Exists(raw)) Directory.Delete(raw, true);
        log.Ok("geometry", $"{fixtures.Count} fixture(s), {(part is null ? "no" : "one")} finished part.");
        return (fixtures, part);
    }

    /// <summary>
    /// The work offset the Work CS is stored with: <c>--work-offset</c>, else the one the NC program selects (2026-10-12: the
    /// Mazak program cuts in G57 while the manifest said G54), else G54 with a warning.
    /// </summary>
    private static string WorkOffsetFor(ExportOptions options, string ncPath, ExportLog log)
    {
        var used = File.Exists(ncPath) ? NcProgramSet.WorkOffsets(ncPath) : [];
        if (!string.IsNullOrWhiteSpace(options.WorkOffset))
        {
            var given = options.WorkOffset.Trim().ToUpperInvariant();
            if (used.Count > 0 && !used.Contains(given, StringComparer.OrdinalIgnoreCase))
                log.Warning("ucs", "work_offset_not_in_nc", $"--work-offset {given} is not used by the NC program, which selects {string.Join(", ", used)}.");
            return given;
        }

        switch (used.Count)
        {
            case 0:
                log.Warning("ucs", "work_offset_default", "The NC program selects no work offset; the Work CS is stored as G54 (give --work-offset).");
                return "G54";
            case 1:
                log.Ok("ucs", $"Work offset {used[0]} taken from the NC program.");
                return used[0];
            default:
                log.Warning("ucs", "work_offset_several",
                    $"The NC program selects {string.Join(", ", used)}; the Work CS is stored with the first, {used[0]} (give --work-offset to choose).");
                return used[0];
        }
    }

    /// <summary>Step 7: mode A when the geometry is in the procedure UCS, else mode B with the UCS matrix. Never guessed.</summary>
    private CoordinateSystemDefinition? ReadCoordinateSystem(ExportOptions options, bool geometryInUcs, string ncPath, ExportLog log)
    {
        var workOffset = WorkOffsetFor(options, ncPath, log);
        IReadOnlyList<double>? matrix = options.UcsMatrix;
        if (matrix is null && !geometryInUcs)
        {
            try
            {
                matrix = api.Capabilities.UcsTransform ? api.GetUcs(options.Ucs!)?.Matrix : null;
            }
            catch (CimatronApiException exception)
            {
                log.Warning("ucs", "ucs_read_failed", exception.Message);
            }
        }

        if (geometryInUcs && options.UcsMatrix is null)
        {
            log.Ok("ucs", $"Geometry is in the procedure UCS '{options.Ucs}' (mode nc-procedure-ucs, identity).");
            return new CoordinateSystemDefinition
            {
                Type = CoordinateSystemTypes.NcProcedureUcs,
                Name = options.Ucs,
                WorkOffset = workOffset,
                FilesAreIn = CoordinateSystemTypes.FilesInNcWorkCoordinates,
                Transform = CoordinateSystemTypes.IdentityTransform
            };
        }

        if (matrix is null)
        {
            log.Error("ucs", "ucs_transform_unavailable",
                $"The transformation of UCS '{options.Ucs}' cannot be read and the geometry is not known to be in that UCS; give --ucs-matrix or --geometry-in-ucs.");
            return null;
        }

        var transform = Transform3D.FromMatrix4(matrix);
        if (!transform.IsProperRotation())
        {
            log.Error("ucs", "ucs_matrix_invalid", $"The matrix of UCS '{options.Ucs}' is not a rotation plus translation.");
            return null;
        }

        var euler = transform.ToEulerRzRyRx();
        log.Ok("ucs", $"UCS '{options.Ucs}' read: origin {transform.Translation}, rotation {euler} (RzRyRx), mode part-model-coordinates.");
        return new CoordinateSystemDefinition
        {
            Type = CoordinateSystemTypes.PartModelCoordinates,
            Name = options.Ucs,
            WorkOffset = workOffset,
            FilesAreIn = CoordinateSystemTypes.FilesInPartModelCoordinates,
            OriginMm = new ManifestVector(Round(transform.Translation.X), Round(transform.Translation.Y), Round(transform.Translation.Z)),
            RotationDeg = new ManifestVector(Round(euler.X), Round(euler.Y), Round(euler.Z)),
            RotationOrder = RotationOrders.RzRyRx,
            Matrix = matrix.ToArray()
        };
    }

    /// <summary>Step 10: move the staging folder in place atomically, then write COMPLETE last.</summary>
    private static string Commit(ExportOptions options, string output, string staging, ExportLog log)
    {
        var final = Path.Combine(output, options.PackageFolderName);
        if (Directory.Exists(final))
        {
            var marker = Path.Combine(final, ManifestJson.CompleteMarkerFileName);
            if (File.Exists(marker)) File.Delete(marker); // readers stop trusting the old package first
            var superseded = Path.Combine(output, ".superseded");
            Directory.CreateDirectory(superseded);
            var target = Path.Combine(superseded, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
            Directory.Move(final, target);
            log.Ok("commit", $"The previous package was moved to {target}.");
            foreach (var old in Directory.GetDirectories(superseded).Order(StringComparer.Ordinal).SkipLast(Math.Max(0, options.KeepSuperseded)))
                Directory.Delete(old, true);
        }

        Directory.Move(staging, final);
        log.PackagePath = final;
        log.Finish(complete: true);
        File.WriteAllText(Path.Combine(final, ManifestJson.ExportLogPath.Replace('/', Path.DirectorySeparatorChar)), log.ToJson());
        File.WriteAllText(Path.Combine(final, ManifestJson.CompleteMarkerFileName), "");
        log.Ok("commit", $"Package published to {final}; COMPLETE written last.");
        return final;
    }

    private static ExportResult Finish(ExportLog log, string output, string staging, ExportOptions options, string? published)
    {
        if (published is null)
        {
            log.Finish(complete: false);
            if (Directory.Exists(staging))
            {
                var failed = Path.Combine(output, ".failed", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Path.GetFileName(staging)}");
                Directory.CreateDirectory(Path.GetDirectoryName(failed)!);
                Directory.Move(staging, failed);
                Directory.CreateDirectory(Path.Combine(failed, "metadata"));
                File.WriteAllText(Path.Combine(failed, "metadata", "export-log.json"), log.ToJson());
            }
        }

        var stagingRoot = Path.Combine(output, ".staging");
        if (Directory.Exists(stagingRoot) && !Directory.EnumerateFileSystemEntries(stagingRoot).Any()) Directory.Delete(stagingRoot);
        File.WriteAllText(Path.Combine(output, "last-export-log.json"), log.ToJson());
        var report = log.ToReport();
        File.WriteAllText(Path.Combine(output, "last-export-report.txt"), report);
        if (options.ReportPath is not null) File.WriteAllText(options.ReportPath, report);
        return new ExportResult(published is not null, published, log, published is not null ? 0 : 1);
    }

    private static PackageFileReference? Existing(string staging, string relative) =>
        File.Exists(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar))) ? new PackageFileReference { Path = relative } : null;

    private static string? ToolType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "flat" or "end mill" or "endmill" => "flat",
        "ball" or "ball mill" => "ball",
        "bull" or "bull-nose" or "bullnose" or "radius" => "bull-nose",
        "drill" or "center" => "drill",
        "tap" => "tap",
        "full radius" => "ball",
        "corner radius" => "bull-nose",
        "taper" => "taper",
        "chamfer" => "chamfer",
        null or "" => null,
        _ => "other"
    };

    private static double Round(double value)
    {
        var rounded = Math.Round(value, 6);
        return rounded == 0 ? 0 : rounded;
    }
}

/// <summary>Files a Cimatron post writes beside the NC program that are reports, not NC.</summary>
internal static class PostReport
{
    private static readonly HashSet<string> ReportExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".mht", ".mhtml", ".pdf", ".xml", ".csv", ".log", ".doc", ".docx", ".xls", ".xlsx", ".png", ".jpg", ".bmp"
    };

    public static bool IsReport(string path) => ReportExtensions.Contains(Path.GetExtension(path));

    /// <summary>The error lines of the post logs in a folder ("*** GPP Runtime Error …", "Execution Stopped!"), at most ten.</summary>
    public static IReadOnlyList<string> Errors(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.log")
            .Order(StringComparer.OrdinalIgnoreCase)
            .SelectMany(path => File.ReadLines(path))
            .Select(line => line.Trim())
            .Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("stopped", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToArray();
    }

    /// <summary>Cimatron's tool report: "*.Tools.html" from the post, "*.TOOLS.mht" from the report templates.</summary>
    public static bool IsToolReport(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm" or ".mht" or ".mhtml"
        && Path.GetFileNameWithoutExtension(path).EndsWith(".tools", StringComparison.OrdinalIgnoreCase);
}
