using System.Text.Json;
using Meimad.CimatronExporter;
using Meimad.Planner.SimulationPackage.Diagnostics;
using Meimad.Planner.SimulationPackage.Geometry;
using Meimad.Planner.SimulationPackage.Manifest;
using Meimad.Planner.SimulationPackage.Validation;

namespace Meimad.Planner.Server.Tests.SimulationPackages;

/// <summary>
/// Exporter workflow (§11) against a fake Cimatron API (§17 "API tests"): complete export, missing
/// UCS, post failure and fallback, stock failure, repeated export to the same folder, multiple
/// fixtures, mode B UCS, and the export log kept on a partial failure. The real COM adapter is
/// unverified until it runs on a PC with Cimatron.
/// </summary>
public sealed class CimatronExporterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MeimadPlanner.CimatronExporter.Tests", Guid.NewGuid().ToString("N"));

    public CimatronExporterTests() => Directory.CreateDirectory(root);

    [Fact]
    public void A_COM_class_is_created_from_its_dll_without_the_registry_and_answers_late_bound_calls()
    {
        // The registration-free path the exporter takes when CimAppAccess.AppAccess is not registered (Cimatron 2026.0
        // at Meimad): class id from the type library, class factory from the DLL. Checked with the Scripting runtime
        // every Windows has (its type library sits inside the DLL).
        if (!OperatingSystem.IsWindows()) return;
        var dll = Path.Combine(Environment.SystemDirectory, "scrrun.dll");
        if (!File.Exists(dll)) return;
        Assert.NotNull(CimatronTypeLibrary.ClassId(dll, "FileSystemObject"));
        var fileSystem = CimatronTypeLibrary.CreateFromDll(dll, dll, "FileSystemObject");
        var name = fileSystem.GetType().InvokeMember("GetTempName", System.Reflection.BindingFlags.InvokeMethod, null, fileSystem, []);
        Assert.EndsWith(".tmp", Assert.IsType<string>(name));
        Assert.Contains(CimatronTypeLibrary.Describe(dll), line => line.Contains("GetTempName", StringComparison.Ordinal));
        Assert.Null(CimatronTypeLibrary.FindProgramFolder(root)); // a folder without CimAppAccess.dll is not a Cimatron Program folder
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
        }
    }

    private string Output => Path.Combine(root, "Gcode", "GOLDEN-PART", "010");

    private ExportOptions Options(params string[] extra)
    {
        var args = new List<string> { "--active-cimatron", "--output", Output, "--post", "HAAS_NGC_VF3", "--ucs", "UCS_OP010", "--operation", "010",
            "--part-number", "GOLDEN-PART", "--machine-id", "VF3_01", "--machine-name", "Haas VF-3" };
        args.AddRange(extra);
        var (options, error) = ExportOptions.Parse(args);
        Assert.True(options is not null, error);
        return options!;
    }

    private static ExportResult Run(FakeCimatronApi api, ExportOptions options) => new PackageExporter(api, TimeProvider.System).Export(options);

    private async Task<PackageValidationReport> ValidateAsync(string package) =>
        await new SimulationPackageValidator(new PackageValidationOptions { ApprovedRoots = [root], TestedCimatronVersions = ["2026"] }).ValidateAsync(package);

    [Fact]
    public async Task Complete_export_publishes_a_valid_package_with_complete_written_last()
    {
        using var api = new FakeCimatronApi();
        var result = Run(api, Options("--mesh-tolerance", "0.005"));
        Assert.True(result.Complete, result.Log.ToReport());
        Assert.Equal(0, result.ExitCode);
        var package = Path.Combine(Output, "cimatron-export");
        Assert.Equal(package, result.PackagePath);
        Assert.True(File.Exists(Path.Combine(package, "COMPLETE")));
        Assert.False(Directory.Exists(Path.Combine(Output, ".staging")));
        Assert.True(File.Exists(Path.Combine(package, "metadata", "export-log.json")));
        Assert.True(File.Exists(Path.Combine(package, "package.json")));
        Assert.Contains("COMPLETE", File.ReadAllText(Path.Combine(Output, "last-export-report.txt")));

        // The posted G-code is published byte for byte (§2: the exporter never edits it).
        Assert.Equal(FakeCimatronApi.NcBytes, File.ReadAllBytes(Path.Combine(package, "nc", "O1001.nc")));

        var report = await ValidateAsync(package);
        Assert.False(report.HasBlockingErrors, string.Join("; ", report.Errors.Select(item => item.Code)));
        Assert.Empty(report.Warnings);
        var manifest = report.Manifest!;
        Assert.Equal("nc-procedure-ucs", manifest.CoordinateSystem!.Type);
        Assert.Equal("UCS_OP010", manifest.CoordinateSystem.Name);
        Assert.Equal("G54", manifest.CoordinateSystem.WorkOffset);
        Assert.Equal("2026.0", manifest.Exporter!.CimatronVersion);
        Assert.Equal("fake-1", manifest.Exporter.AdapterVersion);
        Assert.Equal(0.005, manifest.Exporter.MeshToleranceMm);
        Assert.Equal(17, manifest.Operation!.ProcedureId);
        Assert.Equal("VF3_01", manifest.Machine!.ExternalId);
        Assert.Equal("HAAS_NGC_VF3", manifest.Machine.Postprocessor);
        Assert.Equal(["Base plate", "Vise jaw"], manifest.Files!.Fixtures!.Select(item => item.Name));
        Assert.Equal("geometry/fixture-002.stl", manifest.Files.Fixtures![1].Path);
        Assert.Equal("initial", manifest.Files.Stock![0].Kind);
        Assert.NotNull(manifest.Files.Part);
        Assert.NotNull(manifest.Files.Cutters);
        Assert.NotNull(manifest.Files.JobsXml);
        Assert.Equal([1, 2], report.ToolTableNumbers);
        Assert.DoesNotContain(manifest.EnumerateFiles(), file => file.Path.Contains("measured", StringComparison.OrdinalIgnoreCase));
        using var log = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "metadata", "export-log.json")));
        Assert.Equal("complete", log.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("fake-1", log.RootElement.GetProperty("adapterVersion").GetString());
    }

    [Fact]
    public void Missing_ucs_blocks_before_anything_is_written()
    {
        using var api = new FakeCimatronApi { UcsNames = ["UCS_OP020"] };
        var result = Run(api, Options());
        Assert.False(result.Complete);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(result.Log.Entries, entry => entry.Code == "ucs_not_found");
        Assert.False(Directory.Exists(Path.Combine(Output, "cimatron-export")));
        Assert.Equal(0, api.PostCalls);
        using var log = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "last-export-log.json")));
        Assert.Equal("failed", log.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public void Missing_nc_document_is_reported_with_its_code()
    {
        using var api = new FakeCimatronApi { HasDocument = false };
        var result = Run(api, Options());
        Assert.Contains(result.Log.Entries, entry => entry.Code == "cimatron_no_nc_document");
        Assert.False(result.Complete);
    }

    [Fact]
    public void Post_failure_keeps_the_staging_folder_and_its_log_but_publishes_nothing()
    {
        using var api = new FakeCimatronApi { FailPost = true };
        var result = Run(api, Options());
        Assert.False(result.Complete);
        Assert.Contains(result.Log.Entries, entry => entry.Code == "post_failed");
        Assert.False(Directory.Exists(Path.Combine(Output, "cimatron-export")));
        var failed = Assert.Single(Directory.GetDirectories(Path.Combine(Output, ".failed")));
        Assert.False(File.Exists(Path.Combine(failed, "COMPLETE")));
        using var log = JsonDocument.Parse(File.ReadAllText(Path.Combine(failed, "metadata", "export-log.json")));
        Assert.Equal("failed", log.RootElement.GetProperty("outcome").GetString());
        Assert.Contains("post_failed", File.ReadAllText(Path.Combine(Output, "last-export-report.txt")));
    }

    [Fact]
    public async Task Post_fallback_still_records_the_procedure_ucs()
    {
        using var api = new FakeCimatronApi { SupportsPostByUcs = false };
        var result = Run(api, Options());
        Assert.True(result.Complete, result.Log.ToReport());
        Assert.Contains(result.Log.Entries, entry => entry.Code == "post_fallback_used" && entry.Status == "warning");
        var report = await ValidateAsync(result.PackagePath!);
        Assert.Equal("UCS_OP010", report.Manifest!.CoordinateSystem!.Name);
    }

    [Fact]
    public void Stock_failure_blocks_the_package()
    {
        using var api = new FakeCimatronApi { FailRemainingStock = true };
        var result = Run(api, Options("--stock-procedure", "9", "--source-operation", "005"));
        Assert.False(result.Complete);
        Assert.Contains(result.Log.Entries, entry => entry.Code == "stock_export_failed");
        Assert.False(Directory.Exists(Path.Combine(Output, "cimatron-export")));
    }

    [Fact]
    public async Task Remaining_stock_is_marked_with_its_source_operation()
    {
        using var api = new FakeCimatronApi();
        var result = Run(api, Options("--stock-procedure", "9", "--source-operation", "005"));
        Assert.True(result.Complete, result.Log.ToReport());
        var stock = (await ValidateAsync(result.PackagePath!)).Manifest!.Files!.Stock![0];
        Assert.Equal(("remaining", "005", "remaining-stock-api"), (stock.Kind, stock.SourceOperation, stock.FoundBy));
        Assert.Equal(9, api.RemainingStockProcedure);
    }

    [Fact]
    public async Task Repeated_export_to_the_same_folder_replaces_the_package_atomically()
    {
        using var api = new FakeCimatronApi();
        var first = Run(api, Options());
        var firstId = (await ValidateAsync(first.PackagePath!)).Manifest!.PackageId;
        var firstHash = PackageHashing.Sha256OfFile(Path.Combine(first.PackagePath!, "manifest.json"));
        api.FixtureCount = 1;
        var second = Run(api, Options());
        Assert.True(second.Complete, second.Log.ToReport());
        var report = await ValidateAsync(second.PackagePath!);
        Assert.False(report.HasBlockingErrors);
        Assert.NotEqual(firstId, report.Manifest!.PackageId);
        Assert.NotEqual(firstHash, report.ManifestSha256);
        Assert.Single(report.Manifest.Files!.Fixtures!);
        var superseded = Assert.Single(Directory.GetDirectories(Path.Combine(Output, ".superseded")));
        Assert.False(File.Exists(Path.Combine(superseded, "COMPLETE")), "the old package must not look importable any more");
        Assert.Equal(firstId, ManifestJson.Deserialize(File.ReadAllText(Path.Combine(superseded, "manifest.json"))).PackageId);
    }

    [Fact]
    public async Task Multiple_fixtures_stay_separate_and_two_targets_are_refused()
    {
        using var api = new FakeCimatronApi { FixtureCount = 3, OtherCount = 1 };
        var result = Run(api, Options());
        Assert.True(result.Complete, result.Log.ToReport());
        var fixtures = (await ValidateAsync(result.PackagePath!)).Manifest!.Files!.Fixtures!;
        Assert.Equal(["geometry/fixture-001.stl", "geometry/fixture-002.stl", "geometry/fixture-003.stl"], fixtures.Select(item => item.Path));
        Assert.All(fixtures, item => Assert.True(item.Collision));

        using var twoTargets = new FakeCimatronApi { TargetCount = 2 };
        Directory.Delete(Output, true);
        var refused = Run(twoTargets, Options());
        Assert.False(refused.Complete);
        Assert.Contains(refused.Log.Entries, entry => entry.Code == "part_ambiguous");
    }

    [Fact]
    public async Task Part_model_coordinates_carry_the_ucs_matrix_and_euler_angles()
    {
        using var api = new FakeCimatronApi { GeometryInUcs = false, UcsMatrix = [0, 0, 1, 80, 0, 1, 0, 0, -1, 0, 0, 40, 0, 0, 0, 1] };
        var result = Run(api, Options("--work-offset", "g55"));
        Assert.True(result.Complete, result.Log.ToReport());
        var report = await ValidateAsync(result.PackagePath!);
        var cs = report.Manifest!.CoordinateSystem!;
        Assert.Equal("part-model-coordinates", cs.Type);
        Assert.Equal("G55", cs.WorkOffset);
        Assert.Equal(new ManifestVector(80, 0, 40), cs.OriginMm);
        Assert.Equal(new ManifestVector(0, 90, 0), cs.RotationDeg);
        Assert.Equal("RzRyRx", cs.RotationOrder);
        Assert.DoesNotContain(report.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);

        using var unknown = new FakeCimatronApi { GeometryInUcs = false, UcsMatrix = null };
        Directory.Delete(Output, true);
        var refused = Run(unknown, Options());
        Assert.Contains(refused.Log.Entries, entry => entry.Code == "ucs_transform_unavailable");
        Assert.False(refused.Complete);
    }

    [Fact]
    public void Several_nc_files_need_an_explicit_choice()
    {
        using var api = new FakeCimatronApi { ExtraNcFile = true };
        var refused = Run(api, Options());
        Assert.Contains(refused.Log.Entries, entry => entry.Code == "nc_ambiguous");
        Directory.Delete(Output, true);
        var chosen = Run(api, Options("--nc-file", "O1001.nc"));
        Assert.True(chosen.Complete, chosen.Log.ToReport());
    }

    [Fact]
    public void An_nc_file_posted_into_a_subfolder_keeps_its_folder_in_the_package()
    {
        using var api = new FakeCimatronApi { NcSubfolder = "TP_MODEL", WritesPostReports = true };
        var result = Run(api, Options());
        Assert.True(result.Complete, result.Log.ToReport());
        var manifest = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(result.PackagePath!, "manifest.json")));
        Assert.Equal("nc/TP_MODEL/O1001.nc", manifest.GetProperty("files").GetProperty("nc").GetProperty("path").GetString());
        Assert.True(File.Exists(Path.Combine(result.PackagePath!, "nc", "TP_MODEL", "O1001.nc")));
    }

    [Fact]
    public void Post_reports_are_not_nc_and_the_tool_report_is_kept()
    {
        using var api = new FakeCimatronApi { WritesPostReports = true };
        var result = Run(api, Options());
        Assert.True(result.Complete, result.Log.ToReport());
        Assert.DoesNotContain(result.Log.Entries, entry => entry.Code == "nc_ambiguous");
        var manifest = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(result.PackagePath!, "manifest.json")));
        var files = manifest.GetProperty("files");
        Assert.Equal("nc/O1001.nc", files.GetProperty("nc").GetProperty("path").GetString());
        Assert.Equal("tools/PART_NC1.TP_MODEL_0.Mazak_5x.Tools.html", files.GetProperty("toolReport").GetProperty("path").GetString());
        Assert.True(File.Exists(Path.Combine(result.PackagePath!, "tools", "PART_NC1.TP_MODEL_0.Mazak_5x.Tools.html")));
        Assert.True(File.Exists(Path.Combine(result.PackagePath!, "metadata", "post-reports", "PART_NC1.TP_MODEL_0.Setup.pdf")));
        Assert.False(File.Exists(Path.Combine(result.PackagePath!, "nc", "PART_NC1.TP_MODEL_0.Mazak_5x.Tools.html")));
    }

    [Fact]
    public void Procedures_in_another_ucs_warn_when_posted_by_ucs_and_block_otherwise()
    {
        // 5-axis work: each procedure has its own orientation UCS; PostByUcs still writes the NC relative to --ucs.
        using var api = new FakeCimatronApi();
        var posted = Run(api, Options("--ucs", "UCS_OP020"));
        Assert.True(posted.Complete, posted.Log.ToReport());
        Assert.Contains(posted.Log.Entries, entry => entry.Code == "ucs_procedures_differ");

        Directory.Delete(Output, true);
        using var withoutPostByUcs = new FakeCimatronApi { SupportsPostByUcs = false };
        var blocked = Run(withoutPostByUcs, Options("--ucs", "UCS_OP020"));
        Assert.Equal(1, blocked.ExitCode);
        Assert.Contains(blocked.Log.Entries, entry => entry.Code == "ucs_mismatch");
    }

    [Fact]
    public void A_document_with_several_toolpaths_exports_the_chosen_one_with_its_setup_stock()
    {
        // The Meimad document of 2026-10-12: three setups, each with its own stock procedure and toolpath.
        CimatronToolpath[] toolpaths = [new(4, "TP_MODEL", "NC_Setup", 3), new(105, "TP_MODEL_0", "NC_Setup", 107), new(202, "NISAYON", null, null)];
        CimatronProcedure[] procedures =
        [
            new(17, "OP010_ROUGH", "UCS_OP010", true, "TP_MODEL"),
            new(117, "ROUGH_COPY", "UCS_OP010", true, "TP_MODEL_0"),
            new(118, "FINISH_COPY", "UCS_OP010", true, "TP_MODEL_0")
        ];
        using var first = new FakeCimatronApi { SupportsInitialStock = false, Toolpaths = toolpaths, Procedures = procedures, SelectedPostNeedsProcedures = true };
        var api = first;

        var unchosen = Run(api, Options());
        Assert.Contains(unchosen.Log.Entries, entry => entry.Code == "toolpath_required" && entry.Message.Contains("TP_MODEL_0"));
        Assert.Equal(0, api.PostCalls);

        Directory.Delete(Output, true);
        var unknown = Run(api, Options("--toolpath", "TP_OTHER"));
        Assert.Contains(unknown.Log.Entries, entry => entry.Code == "toolpath_not_found");

        // Geometry in the procedure UCS (mode A): the stock procedure's stock exists in part-model coordinates only.
        Directory.Delete(Output, true);
        var modeA = Run(api, Options("--toolpath", "TP_MODEL_0"));
        Assert.Contains(modeA.Log.Entries, entry => entry.Code == "stock_frame_unavailable");

        // Part-model coordinates (mode B).
        Directory.Delete(Output, true);
        using var modelApi = new FakeCimatronApi
        {
            SupportsInitialStock = false, GeometryInUcs = false, UcsMatrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
            Toolpaths = toolpaths, Procedures = procedures, SelectedPostNeedsProcedures = true
        };
        api = modelApi;
        var chosen = Run(api, Options("--toolpath", "tp_model_0"));
        Assert.True(chosen.Complete, chosen.Log.ToReport());
        // The toolpath node first; Cimatron wrote nothing for it, so its procedures.
        Assert.Equal(new[] { new[] { 105 }, new[] { 117, 118 } }, api.SelectedPosts.Select(ids => ids.ToArray()));
        Assert.Contains(chosen.Log.Entries, entry => entry.Code == "post_by_procedures");
        Assert.Equal(107, api.RemainingStockProcedure);
        Assert.Equal(CimatronUcsFrame.Model, api.RemainingStockFrame);
        var manifest = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(chosen.PackagePath!, "manifest.json")));
        Assert.Contains("stock-procedure", manifest.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Command_line_requires_output_post_and_ucs()
    {
        Assert.NotNull(ExportOptions.Parse(["--probe"]).Options);
        Assert.Contains("--post", ExportOptions.Parse(["--active-cimatron", "--output", root, "--ucs", "U"]).Error);
        Assert.Contains("--ucs", ExportOptions.Parse(["--active-cimatron", "--output", root, "--post", "P"]).Error);
        Assert.Contains("full folder path", ExportOptions.Parse(["--active-cimatron", "--output", "relative", "--post", "P", "--ucs", "U"]).Error);
        Assert.Contains("16 numbers", ExportOptions.Parse(["--active-cimatron", "--ucs-matrix", "1,2,3"]).Error);
        Assert.Contains("Unknown option", ExportOptions.Parse(["--frobnicate"]).Error);
        Assert.Equal(16, ExportOptions.Parse(["--active-cimatron", "--output", root, "--post", "P", "--ucs", "U",
            "--ucs-matrix", "1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1"]).Options!.UcsMatrix!.Count);
    }

    [Fact]
    public async Task Remaining_stock_is_exported_in_the_frame_of_the_geometry()
    {
        // Mode B (part-model coordinates) by default when the API exports nothing in the procedure UCS; mode A on request.
        using var modelFrame = new FakeCimatronApi { GeometryInUcs = false, UcsMatrix = [1, 0, 0, 10, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1] };
        var result = Run(modelFrame, Options("--stock-procedure", "9"));
        Assert.True(result.Complete, result.Log.ToReport());
        Assert.Equal(CimatronUcsFrame.Model, modelFrame.RemainingStockFrame);
        Assert.Equal("part-model-coordinates", (await ValidateAsync(result.PackagePath!)).Manifest!.CoordinateSystem!.Type);

        using var procedureFrame = new FakeCimatronApi { GeometryInUcs = false };
        Directory.Delete(Output, true);
        var modeA = Run(procedureFrame, Options("--stock-procedure", "9", "--geometry-in-ucs"));
        Assert.True(modeA.Complete, modeA.Log.ToReport());
        Assert.Equal(CimatronUcsFrame.Procedure, procedureFrame.RemainingStockFrame);
        Assert.Equal("nc-procedure-ucs", (await ValidateAsync(modeA.PackagePath!)).Manifest!.CoordinateSystem!.Type);
    }

    [Fact]
    public async Task A_single_stock_stl_beside_the_document_is_the_fallback_when_the_api_exports_no_stock()
    {
        // Cimatron 2026.0 has no initial-stock export; its own NC\STOCK_OP1.stl beside the document is taken, with a warning.
        var documentFolder = Path.Combine(root, "cimatron-doc");
        Directory.CreateDirectory(Path.Combine(documentFolder, "NC"));
        var stock = Path.Combine(documentFolder, "NC", "STOCK_OP1.stl");
        File.WriteAllBytes(stock, MeshFiles.WriteBinaryStl(MeshFiles.Box(new(0, 0, -30), new(100, 60, 0))));
        using var api = new FakeCimatronApi { SupportsInitialStock = false, DocumentPath = Path.Combine(documentFolder, "GOLDEN-PART.elt") };
        var result = Run(api, Options());
        Assert.True(result.Complete, result.Log.ToReport());
        Assert.Contains(result.Log.Entries, entry => entry.Code == "stock_fallback_rule" && entry.Message.Contains("STOCK_OP1.stl", StringComparison.Ordinal));
        var report = await ValidateAsync(result.PackagePath!);
        Assert.Equal(("initial", "fallback"), (report.Manifest!.Files!.Stock![0].Kind, report.Manifest.Files.Stock[0].FoundBy));

        // Two candidates are ambiguous; none blocks as before.
        File.Copy(stock, Path.Combine(documentFolder, "STOCK_OP2.stl"));
        Directory.Delete(Output, true);
        var ambiguous = Run(api, Options());
        Assert.False(ambiguous.Complete);
        Assert.Contains(ambiguous.Log.Entries, entry => entry.Code == "stock_ambiguous");
        using var none = new FakeCimatronApi { SupportsInitialStock = false, DocumentPath = Path.Combine(root, "nowhere", "X.elt") };
        var blocked = Run(none, Options());
        Assert.Contains(blocked.Log.Entries, entry => entry.Code == "stock_unavailable");
    }

    [Fact]
    public async Task Fallback_files_from_a_multi_stl_folder_and_a_stock_file_are_used()
    {
        var multi = Path.Combine(root, "multi-stl");
        Directory.CreateDirectory(multi);
        File.WriteAllBytes(Path.Combine(multi, "GOLDEN_TARGET.stl"), MeshFiles.WriteBinaryStl(MeshFiles.Box(new(5, 5, -25), new(95, 55, 0))));
        File.WriteAllBytes(Path.Combine(multi, "GOLDEN_FIXTURE_1.stl"), MeshFiles.WriteBinaryStl(MeshFiles.Box(new(-20, -20, -60), new(120, 80, -30))));
        var stock = Path.Combine(root, "STOCK_OP1.stl");
        File.WriteAllBytes(stock, MeshFiles.WriteBinaryStl(MeshFiles.Box(new(0, 0, -30), new(100, 60, 0))));
        using var api = new FakeCimatronApi { SupportsGeometry = false, SupportsInitialStock = false };
        var result = Run(api, Options("--geometry-folder", multi, "--stock-file", stock, "--geometry-in-ucs"));
        Assert.True(result.Complete, result.Log.ToReport());
        var report = await ValidateAsync(result.PackagePath!);
        Assert.Contains(report.Diagnostics, item => item.Code == PackageDiagnosticCodes.StockFallbackRule);
        Assert.Equal("GOLDEN_FIXTURE_1", report.Manifest!.Files!.Fixtures!.Single().Name);
        Assert.Equal("GOLDEN_TARGET", report.Manifest.Files.Part!.Name);
    }
}

/// <summary>A scripted Cimatron for exporter tests; it writes the golden 3-axis files.</summary>
internal sealed class FakeCimatronApi : ICimatronApi
{
    internal static readonly byte[] NcBytes = File.ReadAllBytes(Path.Combine(GoldenPackages.Source, "3-axis", "nc", "O1001.nc"));

    public bool HasDocument { get; init; } = true;
    public bool SupportsPostByUcs { get; init; } = true;
    public bool SupportsPostSelected { get; init; } = true;
    public bool SelectedPostNeedsProcedures { get; init; }
    public IReadOnlyList<CimatronToolpath> Toolpaths { get; init; } = [];
    public IReadOnlyList<CimatronProcedure> Procedures { get; init; } = [new(17, "OP010_ROUGH", "UCS_OP010", true, "Pocket")];
    public List<IReadOnlyList<int>> SelectedPosts { get; } = [];
    public bool SupportsGeometry { get; init; } = true;
    public bool SupportsInitialStock { get; init; } = true;
    public bool GeometryInUcs { get; init; } = true;
    public bool FailPost { get; init; }
    public bool FailRemainingStock { get; init; }
    public bool ExtraNcFile { get; init; }
    public bool WritesPostReports { get; init; }
    public string? NcSubfolder { get; init; }
    public IReadOnlyList<string> UcsNames { get; init; } = ["UCS_OP010", "UCS_OP020"];
    public IReadOnlyList<double>? UcsMatrix { get; init; }
    public int FixtureCount { get; set; } = 2;
    public int TargetCount { get; init; } = 1;
    public int OtherCount { get; init; }
    public int PostCalls { get; private set; }
    public int? RemainingStockProcedure { get; private set; }
    public CimatronUcsFrame? RemainingStockFrame { get; private set; }
    public string DocumentPath { get; init; } = @"C:\Cimatron\GOLDEN-PART.elt";

    public string? CimatronVersion => "2026.0";
    public string AdapterVersion => "fake-1";

    public CimatronCapabilities Capabilities => new(
        PostByUcs: SupportsPostByUcs, PostToAll: true, CuttersAndHoldersCsv: true, JobsXml: true, RemainingStock: true,
        InitialStock: SupportsInitialStock, EntityExport: SupportsGeometry, MultiStl: false, UcsTransform: UcsMatrix is not null,
        GeometryInUcs: GeometryInUcs, ToolList: true, PostSelected: SupportsPostSelected);

    public CimatronDocumentInfo GetActiveNcDocument() =>
        HasDocument ? new("GOLDEN-PART.elt", DocumentPath, null) : throw new CimatronApiException("cimatron_no_nc_document", "No document is open.");

    public IReadOnlyList<CimatronProcedure> GetProcedures() => Procedures;

    public IReadOnlyList<CimatronToolpath> GetToolpaths() => Toolpaths;

    public IReadOnlyList<string> PostSelected(string postName, string outputFolder, IReadOnlyList<int> nodeIds, string ucsName, string outputName)
    {
        SelectedPosts.Add(nodeIds);
        return SelectedPostNeedsProcedures && nodeIds.Any(id => Toolpaths.Any(toolpath => toolpath.Id == id)) ? [] : Post(outputFolder);
    }

    public IReadOnlyList<string> GetUcsNames() => UcsNames;

    public CimatronUcs? GetUcs(string name) => UcsMatrix is null ? null : new(name, UcsMatrix);

    public IReadOnlyList<CimatronTool> GetTools() =>
    [
        new(1, "D10 FLAT", "flat", 10, 0, 30, 90, "BT40 ER32", 50, 60),
        new(2, "D6.8 DRILL", "drill", 6.8, 0, 40, 100, "BT40 ER20", 40, 55)
    ];

    public IReadOnlyList<string> PostByUcs(string postName, string outputFolder, bool startHome, string ucsName) => Post(outputFolder);

    public IReadOnlyList<string> PostToAll(string postName, string outputFolder, bool startHome) => Post(outputFolder);

    private IReadOnlyList<string> Post(string folder)
    {
        PostCalls++;
        if (FailPost) throw new CimatronApiException("cimatron_call_failed", "The post-processor stopped with an error.");
        var ncFolder = NcSubfolder is null ? folder : Directory.CreateDirectory(Path.Combine(folder, NcSubfolder)).FullName;
        var path = Path.Combine(ncFolder, "O1001.nc");
        File.WriteAllBytes(path, NcBytes);
        if (WritesPostReports)
        {
            // What Cimatron 2026.0's Mazak post wrote at Meimad on 2026-10-12, beside 6000.EIA.
            var tools = Path.Combine(folder, "PART_NC1.TP_MODEL_0.Mazak_5x.Tools.html");
            File.WriteAllText(tools, "<html><body><table><tr><td>T1</td><td>D10 FLAT</td></tr></table></body></html>");
            var setup = Path.Combine(folder, "PART_NC1.TP_MODEL_0.Setup.pdf");
            File.WriteAllText(setup, "%PDF-1.4");
            return [path, tools, setup];
        }
        if (!ExtraNcFile) return [path];
        var extra = Path.Combine(folder, "O1001_SUB.nc");
        File.WriteAllText(extra, "%\r\nO9001\r\nM99\r\n%\r\n");
        return [path, extra];
    }

    public void ExportCuttersAndHolders(string cuttersCsv, string holdersCsv, string materialsCsv)
    {
        File.WriteAllText(cuttersCsv, "T,Name,Diameter\r\n1,D10 FLAT,10\r\n2,D6.8 DRILL,6.8\r\n");
        File.WriteAllText(holdersCsv, "Name,Diameter,Length\r\nBT40 ER32,50,60\r\nBT40 ER20,40,55\r\n");
        File.WriteAllText(materialsCsv, "Name\r\nAL6061\r\n");
    }

    public void ExportJobsData(string xmlPath) => File.WriteAllText(xmlPath, "<jobs><job procedure=\"17\" name=\"OP010_ROUGH\"/></jobs>");

    public void ExportRemainingStock(int procedureId, CimatronUcsFrame frame, string stlPath)
    {
        if (FailRemainingStock) throw new CimatronApiException("cimatron_call_failed", "Remaining stock calculation failed.");
        RemainingStockProcedure = procedureId;
        RemainingStockFrame = frame;
        File.WriteAllBytes(stlPath, MeshFiles.WriteBinaryStl(MeshFiles.Box(new(0, 0, -30), new(100, 60, 0))));
    }

    public void ExportInitialStock(CimatronUcsFrame frame, string stlPath) =>
        File.WriteAllBytes(stlPath, MeshFiles.WriteBinaryStl(MeshFiles.Box(new(0, 0, -30), new(100, 60, 0))));

    public IReadOnlyList<CimatronGeometryFile> ExportPartGeometry(string ucsName, string folder)
    {
        var files = new List<CimatronGeometryFile>();
        for (var index = 0; index < TargetCount; index++)
        {
            var target = Path.Combine(folder, $"TARGET_{index + 1}.stl");
            File.WriteAllBytes(target, MeshFiles.WriteBinaryStl(MeshFiles.Box(new(5, 5, -25), new(95, 55, 0))));
            files.Add(new(target, GeometryRoles.Target, "Finished part", "entity"));
        }

        string[] names = ["Base plate", "Vise jaw", "Clamp 3", "Clamp 4"];
        for (var index = 0; index < FixtureCount; index++)
        {
            var fixture = Path.Combine(folder, $"FIXTURE_{index + 1}.stl");
            File.WriteAllBytes(fixture, MeshFiles.WriteBinaryStl(MeshFiles.Box(new(-20 - index, -20, -60), new(120, 80 + index, -30))));
            files.Add(new(fixture, GeometryRoles.Fixture, names[index], "entity"));
        }

        for (var index = 0; index < OtherCount; index++)
        {
            var other = Path.Combine(folder, $"OTHER_{index + 1}.stl");
            File.WriteAllBytes(other, MeshFiles.WriteBinaryStl(MeshFiles.Box(new(0, 0, 0), new(1, 1, 1))));
            files.Add(new(other, GeometryRoles.Other, "Other", "entity"));
        }

        return files;
    }

    public void Dispose()
    {
    }
}
