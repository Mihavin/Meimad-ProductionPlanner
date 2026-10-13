using System.IO;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;
using Meimad.Planner.Client.Windows.Presentation.CimatronImport;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>
/// Import from Cimatron (specification of 2026-10-13) without Cimatron: a fake exporter bridge and a fake Server. Covers the
/// allowed postprocessors, re-import preselection by Cimatron id, the destination in the Operation's G-code structure, the
/// Meimad-format conversion, registration with the Server, and that a failure removes only the import's own folder.
/// </summary>
public sealed class CimatronImportViewModelTests : IDisposable
{
    private const string DocumentPath = @"C:\CAM\30P450172200-002SOFI_NC1.elt";
    private readonly string root = Directory.CreateTempSubdirectory("meimad-cimatron-import-").FullName;

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

    private static readonly CaseOperation Operation = new("op-10", "case-1", 10, 0, "Pocket", "mill", null, null, "independent", null, null, Version: 4);

    private static CimatronListing Listing(bool? modified = false, string path = DocumentPath) => new(
        true, null, "0.3.0", "cimatron-com-6", "2026.0", "GetActiveApplication",
        new CimatronDocument("30P450172200-002SOFI_NC1", path, "PID-7", modified),
        [
            new CimatronSetup("NC_Setup", 3,
            [
                new CimatronToolpath(4, "TP_MODEL", [new CimatronProcedure(5, "PROFILE", "MODEL", true), new CimatronProcedure(6, "POCKET", "---90", true)]),
                new CimatronToolpath(105, "TP_MODEL_0", [new CimatronProcedure(117, "ROUGH", "MODEL", true), new CimatronProcedure(118, "FINISH", "MODEL", true)])
            ]),
            new CimatronSetup("SECOND", 207, [new CimatronToolpath(202, "NISAYON", [new CimatronProcedure(210, "SIDE", "MODEL", false)])])
        ],
        ["MODEL", "---90"]);

    [Fact]
    public async Task Load_offers_the_postprocessors_of_the_operations_machine_type_and_restores_the_last_choice_by_id()
    {
        var api = new FakeApi(root)
        {
            Source = Source(DocumentPath, [new CamOperationReference("toolpath", 105, "TP_MODEL_0", null), new CamOperationReference("procedure", 5, "PROFILE", "TP_MODEL"),
                new CamOperationReference("procedure", 999, "GONE", "TP_MODEL")])
        };
        var model = new CimatronImportViewModel(Context(api, out _), new FakeBridge(Listing()));

        await model.LoadAsync();

        Assert.Equal(["Mazak_5x"], model.Postprocessors.Select(item => item.Name)); // the lathe post is for another machine type
        Assert.Equal("Mazak_5x", model.SelectedPostprocessor?.Name);
        Assert.Equal([105], model.Selection.Toolpaths);
        Assert.Equal([5], model.Selection.Procedures);
        Assert.Equal(["NC_Setup"], model.Selection.Setups);
        Assert.Contains("1 of them no longer exist", model.StatusText);
        Assert.True(model.CanImport);

        // Another document: nothing is preselected, even where names match.
        var other = new CimatronImportViewModel(Context(api, out _), new FakeBridge(Listing(path: @"C:\CAM\OTHER.elt")));
        await other.LoadAsync();
        Assert.Empty(other.Selection.Toolpaths);
        Assert.Contains("another file", other.StatusText);
    }

    [Fact]
    public async Task A_pc_without_cimatron_is_told_so_and_nothing_can_be_imported()
    {
        var missing = new CimatronListing(false, new CimatronBridgeError("cimatron_not_installed", "no Program folder (use --cimatron-folder)"),
            "0.3.0", null, null, null, null, [], []);
        var model = new CimatronImportViewModel(Context(new FakeApi(root), out _), new FakeBridge(missing));
        await model.LoadAsync();
        Assert.Contains("not installed on this PC", model.StatusText);
        Assert.DoesNotContain("--cimatron-folder", model.StatusText);
        Assert.Empty(model.Setups);
        Assert.False(model.CanImport);
    }

    [Fact]
    public async Task A_selection_across_setups_an_uncalculated_procedure_or_an_unsaved_document_cannot_be_imported()
    {
        var model = new CimatronImportViewModel(Context(new FakeApi(root), out _), new FakeBridge(Listing()));
        await model.LoadAsync();
        model.Setups[0].Toolpaths[0].IsChecked = true;
        Assert.True(model.CanImport);
        model.Setups[1].Toolpaths[0].Procedures[0].IsChecked = true;
        Assert.False(model.CanImport);
        Assert.Contains("one setup", model.SelectionText);

        var unsaved = new CimatronImportViewModel(Context(new FakeApi(root), out _), new FakeBridge(Listing(modified: true)));
        await unsaved.LoadAsync();
        unsaved.Setups[0].Toolpaths[0].IsChecked = true;
        Assert.False(unsaved.CanImport);
        Assert.Contains("unsaved changes", unsaved.StatusText);
    }

    [Fact]
    public async Task Import_writes_the_data_set_into_the_operations_gcode_folder_converts_the_nc_and_registers_it()
    {
        var api = new FakeApi(root);
        var bridge = new FakeBridge(Listing());
        var model = new CimatronImportViewModel(Context(api, out var calls), bridge, () => new DateTimeOffset(2026, 10, 13, 9, 30, 0, TimeSpan.Zero));
        await model.LoadAsync();
        model.Setups[0].Toolpaths[1].IsChecked = true;                 // TP_MODEL_0 whole
        model.Setups[0].Toolpaths[0].Procedures[1].IsChecked = true;   // one procedure of TP_MODEL

        await model.ImportAsync();

        var request = Assert.Single(bridge.Requests);
        Assert.Equal([105], request.ToolpathIds);
        Assert.Equal([6], request.ProcedureIds);
        Assert.Equal(("Mazak_5x", "10", "case-1", "op-10", "4", DocumentPath, true),
            (request.Post, request.Operation, request.Context.CaseId, request.Context.CaseOperationId, request.Context.BasedOnVersion,
                request.ExpectedDocumentPath, request.RequireSavedDocument));
        var dataset = request.Output;
        Assert.StartsWith(Path.Combine(root, "Gcode", "Golden part", "10"), dataset);
        Assert.Contains("Mazak_5x", dataset);
        Assert.EndsWith("cimatron-20261013-093000", dataset);

        // The main NC in the Meimad format (Mazak dialect, from the Machines of the post), the subprogram unchanged.
        Assert.Equal("MAZAK_MATRIX_EIA", api.FormattedDialect);
        var program = Path.Combine(dataset, "6000.EIA");
        Assert.Contains("[[MEIMAD:CYCLE_START]]", File.ReadAllText(program));
        Assert.Equal("O5999\r\nM99\r\n", File.ReadAllText(Path.Combine(dataset, "5999.EIA")));
        Assert.DoesNotContain("[[MEIMAD", File.ReadAllText(Path.Combine(dataset, "cimatron-export", "nc", "6000.EIA")));

        Assert.Equal(Path.Combine(dataset, "cimatron-export"), Assert.Single(calls.Registered).Folder);
        Assert.Equal(program, calls.Program);
        Assert.Equal(program, model.ImportedProgram);
        Assert.True(model.CanOpenSimulation);
        Assert.Contains("Incomplete for simulation: no finished part, no fixture", model.StatusText);
    }

    [Fact]
    public async Task A_failed_export_or_conversion_removes_only_the_imports_folder_and_registers_nothing()
    {
        var api = new FakeApi(root);
        var failing = new FakeBridge(Listing()) { FailWith = "post_stopped" };
        var model = new CimatronImportViewModel(Context(api, out var calls), failing);
        await model.LoadAsync();
        model.Setups[0].Toolpaths[0].IsChecked = true;
        await model.ImportAsync();
        Assert.Empty(calls.Registered);
        Assert.Contains("Not imported", model.StatusText);
        Assert.False(Directory.Exists(failing.Requests.Single().Output));

        api.FormatValid = false;
        var bridge = new FakeBridge(Listing());
        var unconvertible = new CimatronImportViewModel(Context(api, out var calls2), bridge);
        await unconvertible.LoadAsync();
        unconvertible.Setups[0].Toolpaths[0].IsChecked = true;
        await unconvertible.ImportAsync();
        Assert.Empty(calls2.Registered);
        Assert.Contains("cannot be converted to the Meimad format", unconvertible.StatusText);
        Assert.False(Directory.Exists(bridge.Requests.Single().Output));
    }

    [Fact]
    public async Task A_blocked_registration_asks_for_the_machine_in_the_window_and_retries_with_the_answer()
    {
        var api = new FakeApi(root);
        var model = new CimatronImportViewModel(Context(api, out var calls, blockFirst: true), new FakeBridge(Listing()));
        await model.LoadAsync();
        model.Setups[0].Toolpaths[0].IsChecked = true;
        await model.ImportAsync();

        Assert.True(model.NeedsMachineChoice);
        Assert.Contains("Retry registration", model.StatusText);
        model.SelectedMachine = model.MachineCandidates.Single(item => item.MachineId == "machine-14");
        await model.RetryRegistrationAsync();

        Assert.Equal(2, calls.Registered.Count);
        Assert.Equal("machine-14", calls.Registered[1].Choices?.MachineId);
        Assert.Contains("Imported", model.StatusText);
    }

    private static OperationCamSourceResource Source(string path, IReadOnlyList<CamOperationReference> operations) => new(
        "op-10", "cimatron", path, false, "30P450172200-002SOFI_NC1", "PID-7", "NC_Setup", operations, "Mazak_5x", "client-1", "import-1",
        "CAM-PC", DateTimeOffset.UtcNow, "oleg", 1, null, "6000.EIA", "imported");

    private sealed class Calls
    {
        public List<(string Folder, SimulationPackageChoices? Choices)> Registered { get; } = [];
        public string? Program { get; set; }
    }

    private static CimatronImportContext Context(FakeApi api, out Calls calls, bool blockFirst = false)
    {
        var recorded = new Calls();
        calls = recorded;
        return new CimatronImportContext(api, "case-1", "Golden part", "GOLDEN", Operation,
            (folder, choices) =>
            {
                recorded.Registered.Add((folder, choices));
                if (blockFirst && recorded.Registered.Count == 1)
                {
                    return Task.FromResult<SimulationPackageImportResult?>(new(SimulationPackageImportResult.Blocked, null, Report(folder,
                        [new("machine_mapping_ambiguous", "error", "Several Machines match the package machine; choose one.", null)],
                        [new("machine-14", "14", "Mazak 14", "postprocessor"), new("machine-15", "15", "Mazak 15", "postprocessor")])));
                }

                var import = new SimulationPackageImport("import-1", "case-1", "op-10", "imported", null, "sha", folder, "0.3.0", "2026.0", "6000.EIA", "10",
                    "machine-14", "Mazak 14", "postprocessor", "Mazak_5x", "geometry/stock.stl", null, null, null, [1, 2], DateTimeOffset.UtcNow, "oleg",
                    [new("nc", "nc/6000.EIA", "a", 1, null, null, null, null, false, null), new("initialStock", "geometry/stock.stl", "b", 1, "initial", null, null, null, true, null)],
                    null);
                return Task.FromResult<SimulationPackageImportResult?>(new(SimulationPackageImportResult.Imported, import, Report(folder, [], [])));
            },
            path => recorded.Program = path);
    }

    private static SimulationPackageReport Report(string folder, IReadOnlyList<SimulationPackageDiagnostic> diagnostics,
        IReadOnlyList<SimulationPackageMachineCandidate> candidates) => new(
        folder, true, "sha", 1, null, "0.3.0", "2026.0", "10", "TP_MODEL", null, "mm",
        new SimulationPackageCoordinateSystem("part-model-coordinates", "MODEL", "G57", "part-model-coordinates", null, null, "RzRyRx"),
        null, [], null, null, null, candidates, null, null, diagnostics.Any(item => item.Severity == "error"), diagnostics);

    private sealed class FakeBridge(CimatronListing listing) : ICimatronBridge
    {
        public string? FailWith { get; init; }
        public List<CimatronExportRequest> Requests { get; } = [];

        public Task<CimatronListing> ListAsync(CancellationToken cancellationToken) => Task.FromResult(listing);

        public Task<CimatronExportResult> ExportAsync(CimatronExportRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (FailWith is not null)
            {
                // The exporter leaves its failed staging folder inside the output folder.
                Directory.CreateDirectory(Path.Combine(request.Output, ".failed", "x"));
                return Task.FromResult(new CimatronExportResult(true, null, false, null, 1, "FAILED",
                    [new("error", "nc", FailWith, "The post-processor stopped without writing an NC file.")]));
            }

            var package = Path.Combine(request.Output, request.PackageFolderName);
            Directory.CreateDirectory(Path.Combine(package, "nc"));
            File.WriteAllText(Path.Combine(package, "manifest.json"), """{"files":{"nc":{"path":"nc/6000.EIA"}}}""");
            File.WriteAllText(Path.Combine(package, "nc", "6000.EIA"), "%\r\nO6000\r\nT1 M6 T28\r\nG1 X10.\r\nM30\r\n%\r\n");
            File.WriteAllText(Path.Combine(package, "nc", "5999.EIA"), "O5999\r\nM99\r\n");
            File.WriteAllText(Path.Combine(package, "COMPLETE"), "");
            return Task.FromResult(new CimatronExportResult(true, null, true, package, 0, "COMPLETE", []));
        }
    }

    private sealed class FakeApi(string workingFolder) : StubPlannerApiClient, IPlannerApiClient
    {
        public OperationCamSourceResource? Source { get; init; }
        public string? FormattedDialect { get; private set; }
        public bool FormatValid { get; set; } = true;

        public override Task<CaseResource> GetCaseAsync(string caseId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CaseResource(new PlannerCase("case-1", "GOLDEN", "Golden part", null, null, null, null, workingFolder, null, null, null, null,
                null, null, null, true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), "\"case:case-1:v1\""));

        public override Task<IReadOnlyList<CaseOperation>> ListCaseOperationsAsync(string caseId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CaseOperation>>([Operation]);

        Task<PlannerGCodeCatalog> IPlannerApiClient.GetOperationGCodeAsync(string caseId, string caseOperationId, CancellationToken cancellationToken) =>
            Task.FromResult(new PlannerGCodeCatalog("op-10", null, [], [new PlannerPostprocessorReleaseStatus("post-mazak", "Mazak_5x", true, "missing", null, null)], []));

        Task<IReadOnlyList<PlannerPostprocessor>> IPlannerApiClient.ListPostprocessorsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PlannerPostprocessor>>(
            [
                new("post-mazak", "Mazak_5x", null, true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new("post-okuma", "Okuma_LB", null, true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new("post-old", "Retired", null, false, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            ]);

        Task<IReadOnlyList<PlannerMachine>> IPlannerApiClient.ListMachinesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PlannerMachine>>(
            [
                Machine("machine-14", "mill", "post-mazak", "MAZAK_MATRIX_EIA"),
                Machine("machine-20", "lathe", "post-okuma", "OKUMA_OSP")
            ]);

        Task<NcTemplateFormatResult> IPlannerApiClient.FormatNcTemplateAsync(string text, string ncDialect, CancellationToken cancellationToken)
        {
            FormattedDialect = ncDialect;
            return Task.FromResult(FormatValid
                ? new NcTemplateFormatResult(text.Replace("G1 X10.", "[[MEIMAD:CYCLE_START]]\nG1 X10.", StringComparison.Ordinal), ncDialect, true,
                    ["Added CYCLE_START"], [], new NcTemplateValidation(true, null, null))
                : new NcTemplateFormatResult(text, ncDialect, false, [], [], new NcTemplateValidation(false, "production_package_cycle_marker_required", "No cycle could be found.")));
        }

        Task<OperationCamSourceResource?> IPlannerApiClient.GetOperationCamSourceAsync(string caseId, string caseOperationId, CancellationToken cancellationToken) =>
            Task.FromResult(Source);

        private static PlannerMachine Machine(string id, string type, string post, string dialect) => new(
            id, id[^2..], id, type, null, [], "calendar", true, false, null, null, 0, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            SupportedPostprocessorIds: [post], NcDialect: dialect);
    }
}
