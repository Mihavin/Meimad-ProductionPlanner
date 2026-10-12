using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;
using Meimad.Planner.Client.Windows.Views;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>
/// Cimatron simulation package import in the Windows client: the API calls (import, blocked 422 report,
/// stale 409, list), the Cimatron setup card's import (version and current package carried, links and
/// Work CS reloaded, release form fed), the report view model (choices, text and symbol severities) and
/// the finished-part thumbnail saved under the Working Folder's _MeimadPlanner folder.
/// </summary>
public sealed class SimulationPackageClientTests
{
    private const string ImportJson = """
        {"outcome":"imported","import":{"importId":"imp-1","caseId":"case-1","caseOperationId":"op-10","status":"imported",
         "packageId":"p","manifestSha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
         "packagePath":"\\\\srv\\data\\PN-1\\Gcode\\PN-1\\010\\cimatron-export","exporterVersion":"0.1.0","cimatronVersion":"2026.0",
         "ncFileName":"O1001.nc","operationNumber":"010","machineId":"machine-10","machineName":"Haas VF-3","machineMatchedBy":"postprocessor",
         "postprocessor":"HAAS_NGC_VF3","selectedStockPath":"geometry/stock.stl","ncSha256":null,"toolTableSha256":null,
         "matchingGcodeReleaseId":null,"toolNumbers":[1,2],"importedAt":"2026-10-12T06:00:00Z","importedBy":"cam-user",
         "files":[{"role":"nc","path":"nc/O1001.nc","sha256":"x","sizeBytes":10,"selected":false},
                  {"role":"toolTable","path":"tools/tool-table.json","sha256":"y","sizeBytes":10,"selected":false},
                  {"role":"part","path":"geometry/part.stl","sha256":"z","sizeBytes":10,"selected":false}]},
         "report":{"packagePath":"p","completeMarkerPresent":true,"hasBlockingErrors":false,
                   "diagnostics":[{"code":"fixture_missing","severity":"warning","message":"No fixture.","context":{}}]}}
        """;

    private const string BlockedJson = """
        {"error":{"code":"simulation_package_blocked","message":"The package was not imported.","details":[]},
         "report":{"packagePath":"p","completeMarkerPresent":true,"hasBlockingErrors":true,
                   "stockCandidates":["geometry/stock.stl","geometry/stock-b.stl"],
                   "machineCandidates":[{"machineId":"machine-14","number":"14","name":"Haas UMC-500","matchedBy":"postprocessor"},
                                        {"machineId":"machine-15","number":"15","name":"Haas UMC-500 SS","matchedBy":"postprocessor"}],
                   "files":[{"role":"initialStock","path":"geometry/stock.stl","sha256":"a","exists":true,"readable":true,"triangleCount":12},
                            {"role":"remainingStock","path":"geometry/stock-b.stl","sha256":"b","exists":true,"readable":true,"sourceOperation":"005"}],
                   "diagnostics":[{"code":"stock_selection_required","severity":"error","message":"Choose the stock.","context":{"candidates":"a,b"}},
                                  {"code":"machine_mapping_ambiguous","severity":"error","message":"Several Machines.","context":{}},
                                  {"code":"part_missing","severity":"warning","message":"No part.","context":{}}]}}
        """;

    [Fact]
    public async Task Api_imports_returns_blocked_reports_and_throws_stale_conflicts()
    {
        var handler = new Handler();
        using var client = new PlannerApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://planner-server:5080/") });

        handler.Next = (HttpStatusCode.Created, ImportJson);
        var imported = await client.ImportSimulationPackageAsync("case-1", "op-10",
            new SimulationPackageImportRequest(@"\\srv\pkg", 3, "imp-0", MachineId: "machine-10"), "windows-01", 7);
        Assert.Equal("imported", imported.Outcome);
        Assert.Equal("imp-1", imported.Import!.ImportId);
        Assert.Equal(@"\\srv\data\PN-1\Gcode\PN-1\010\cimatron-export\nc\O1001.nc", imported.Import.FilePath("nc"));
        Assert.Equal(@"\\srv\data\PN-1\Gcode\PN-1\010\cimatron-export\tools\tool-table.json", imported.Import.FilePath("toolReport", "toolTable"));
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/api/v1/cases/case-1/operations/op-10/simulation-packages", handler.Path);
        Assert.Equal("7", handler.Generation);
        using (var body = JsonDocument.Parse(handler.Body!))
        {
            Assert.Equal(3, body.RootElement.GetProperty("expectedCamSetupVersion").GetInt32());
            Assert.Equal("imp-0", body.RootElement.GetProperty("expectedCurrentImportId").GetString());
            Assert.Equal("machine-10", body.RootElement.GetProperty("machineId").GetString());
        }

        handler.Next = (HttpStatusCode.UnprocessableEntity, BlockedJson);
        var blocked = await client.ImportSimulationPackageAsync("case-1", "op-10", new SimulationPackageImportRequest(@"\\srv\pkg", 0, null), "windows-01", 7);
        Assert.True(blocked.IsBlocked);
        Assert.Null(blocked.Import);
        Assert.Equal(3, blocked.Report.Diagnostics!.Count);

        handler.Next = (HttpStatusCode.Conflict, """
            {"error":{"code":"cam_setup_changed","message":"The Operation Work CS changed since you opened it, so the package was not imported.",
             "details":[],"conflict":{"resource":"Operation CAM setup","changedBy":"other","changedAt":"2026-10-12T06:00:00Z","advice":"Refresh."}}}
            """);
        var stale = await Assert.ThrowsAsync<PlannerApiException>(() =>
            client.ImportSimulationPackageAsync("case-1", "op-10", new SimulationPackageImportRequest(@"\\srv\pkg", 0, null), "windows-01", 7));
        Assert.Equal("cam_setup_changed", stale.Code);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        handler.Next = (HttpStatusCode.UnprocessableEntity, """{"error":{"code":"validation_failed","message":"packagePath is required.","details":[]}}""");
        var invalid = await Assert.ThrowsAsync<PlannerApiException>(() =>
            client.ImportSimulationPackageAsync("case-1", "op-10", new SimulationPackageImportRequest("", 0, null), "windows-01", 7));
        Assert.Equal("validation_failed", invalid.Code);

        handler.Next = (HttpStatusCode.OK, "{\"items\":[" + System.Text.Json.Nodes.JsonNode.Parse(ImportJson)!["import"]!.ToJsonString() + "]}");
        var list = await client.ListSimulationPackageImportsAsync("case-1", "op-10");
        Assert.Equal("imp-1", Assert.Single(list).ImportId);
        Assert.Equal(HttpMethod.Get, handler.Method);
    }

    [Fact]
    public async Task Cimatron_card_import_carries_what_it_saw_and_feeds_the_release_form()
    {
        var api = new FakeApi();
        var viewModel = new OperationCamSetupViewModel();
        SimulationPackageImport? handedOver = null;
        viewModel.PackageImported = import =>
        {
            handedOver = import;
            return Task.CompletedTask;
        };
        viewModel.AttachSession(api, "windows-01", 4, editor: true);
        await viewModel.LoadAsync("case-1", "op-10");
        Assert.Contains("O0999.nc", viewModel.PackageSummary, StringComparison.Ordinal);

        var result = await viewModel.ImportPackageAsync(@"\\srv\pkg", new SimulationPackageChoices("geometry/stock.stl", "machine-15", true, false));

        Assert.Equal("imported", result!.Outcome);
        var request = Assert.Single(api.Requests);
        Assert.Equal((2, "imp-0", "geometry/stock.stl", "machine-15", true), (request.ExpectedCamSetupVersion, request.ExpectedCurrentImportId,
            request.SelectedStockPath, request.MachineId, request.RememberMachineMapping));
        Assert.Equal(4, api.Generation);
        Assert.Equal("imp-1", handedOver!.ImportId);
        Assert.Equal(2, api.CamSetupReads);
        Assert.StartsWith("Cimatron package imported with 1 warning(s)", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains(viewModel.Files, file => file.Kind == "part" && file.KindLabel == "Finished part");

        api.NextOutcome = "blocked";
        var blocked = await viewModel.ImportPackageAsync(@"\\srv\pkg");
        Assert.True(blocked!.IsBlocked);
        Assert.Equal("The Cimatron package was not imported: 2 blocking problem(s). Nothing was changed.", viewModel.StatusMessage);
        Assert.Same(blocked, viewModel.LastPackageResult);

        var readOnly = new OperationCamSetupViewModel();
        readOnly.AttachSession(api, "windows-01", 4, editor: false);
        await readOnly.LoadAsync("case-1", "op-10");
        Assert.Null(await readOnly.ImportPackageAsync(@"\\srv\pkg"));
    }

    [Fact]
    public void Report_offers_the_choices_the_server_asked_for_and_never_signals_with_color_alone()
    {
        var report = JsonSerializer.Deserialize<SimulationPackageReport>(
            JsonDocument.Parse(BlockedJson).RootElement.GetProperty("report").GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var model = new SimulationPackageReportViewModel(new SimulationPackageImportResult("blocked", null, report), canManageSetup: true);

        Assert.True(model.CanImportWithChoices);
        Assert.Equal(["geometry/stock.stl", "geometry/stock-b.stl"], model.StockOptions.Select(option => option.Path));
        Assert.Equal("Remaining stock from OP005: geometry/stock-b.stl", model.StockOptions[1].Label);
        Assert.Equal(["machine-14", "machine-15"], model.MachineOptions.Select(option => option.MachineId));
        Assert.Equal(["⛔ Error", "⛔ Error", "⚠ Warning"], model.Diagnostics.Select(row => row.SeverityText));
        Assert.Equal("candidates: a,b", model.Diagnostics[0].Details);
        Assert.Equal("⛔ Not imported", model.OutcomeText);
        Assert.Null(model.Choices(out var missing));
        Assert.Equal("Choose the stock to start from.", missing);
        model.SelectedStock = model.StockOptions[1];
        Assert.Null(model.Choices(out missing));
        Assert.Equal("Choose the Machine for this package.", missing);
        model.SelectedMachine = model.MachineOptions[0];
        model.RememberMachineMapping = true;
        Assert.Equal(new SimulationPackageChoices("geometry/stock-b.stl", "machine-14", true, false), model.Choices(out _));

        var withoutSetup = new SimulationPackageReportViewModel(new SimulationPackageImportResult("blocked", null, report), canManageSetup: false);
        withoutSetup.SelectedStock = withoutSetup.StockOptions[0];
        withoutSetup.SelectedMachine = withoutSetup.MachineOptions[0];
        withoutSetup.RememberMachineMapping = true;
        Assert.False(withoutSetup.Choices(out _)!.RememberMachineMapping);

        var hashMismatch = report with
        {
            Diagnostics = [new SimulationPackageDiagnostic("file_hash_mismatch", "error", "Changed after export.", null), .. report.Diagnostics!]
        };
        Assert.False(new SimulationPackageReportViewModel(new SimulationPackageImportResult("blocked", null, hashMismatch), true).CanImportWithChoices);
    }

    [Fact]
    public async Task Finished_part_thumbnail_goes_below_the_working_folders_generated_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "MeimadPlanner.Thumbnail.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var part = Path.Combine(root, "part.stl");
            await File.WriteAllBytesAsync(part, BoxStl());
            var sink = new PendingOperationThumbnailSink();
            byte[] png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
            var thumbnail = await OperationThumbnailStore.SaveAsync(root, "PN/1", 10, "case-1", "op-10", part, png, sink);
            Assert.Equal(Path.Combine(root, "_MeimadPlanner", "thumbnails", "PN_1-op010-finished-part.png"), thumbnail.SavedPath);
            Assert.Equal(png, await File.ReadAllBytesAsync(thumbnail.SavedPath));
            Assert.Same(thumbnail, sink.Latest("op-10"));
            Assert.Equal(64, thumbnail.Sha256.Length);
            Assert.Equal(64, thumbnail.SourceModelSha256.Length);
            Assert.Equal(["_MeimadPlanner", "part.stl"], Directory.GetFileSystemEntries(root).Select(entry => Path.GetFileName(entry)!).Order(StringComparer.Ordinal));
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
                OperationThumbnailStore.SaveAsync(Path.Combine(root, "missing"), "PN", 10, "case-1", "op-10", part, png, sink));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Part_thumbnail_renders_an_stl_offscreen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meimad-thumbnail-{Guid.NewGuid():N}.stl");
        File.WriteAllBytes(path, BoxStl());
        byte[]? png = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                png = PartThumbnailRenderer.Render(StlMeshLoader.Load(path), "part.stl");
            }
            catch (Exception exception)
            {
                error = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        File.Delete(path);
        Assert.Null(error);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png![..4]);
    }

    private static byte[] BoxStl()
    {
        // Twelve triangles of a 10 x 20 x 5 box, binary STL.
        (float X, float Y, float Z) P(int i) => ((i & 1) == 0 ? 0 : 10, (i & 2) == 0 ? 0 : 20, (i & 4) == 0 ? 0 : 5);
        int[][] faces = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[80]);
        writer.Write((uint)12);
        foreach (var f in faces)
        {
            foreach (var triangle in new[] { new[] { f[0], f[1], f[2] }, new[] { f[0], f[2], f[3] } })
            {
                writer.Write(0f); writer.Write(0f); writer.Write(0f);
                foreach (var index in triangle)
                {
                    var p = P(index);
                    writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z);
                }

                writer.Write((ushort)0);
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal (HttpStatusCode Status, string Body) Next { get; set; }
        internal HttpMethod? Method { get; private set; }
        internal string? Path { get; private set; }
        internal string? Generation { get; private set; }
        internal string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            Generation = request.Headers.TryGetValues("X-Meimad-Edit-Generation", out var values) ? values.Single() : null;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Next.Status) { Content = new StringContent(Next.Body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeApi : StubPlannerApiClient, IPlannerApiClient
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-12T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        private bool imported;

        internal List<SimulationPackageImportRequest> Requests { get; } = [];
        internal long Generation { get; private set; }
        internal int CamSetupReads { get; private set; }
        internal string NextOutcome { get; set; } = "imported";

        public Task<CaseOperationCamSetupResource?> GetCaseOperationCamSetupAsync(string caseId, string caseOperationId, CancellationToken cancellationToken = default)
        {
            CamSetupReads++;
            var version = imported ? 3 : 2;
            return Task.FromResult<CaseOperationCamSetupResource?>(new(
                new CaseOperationCamSetup(caseOperationId, "cimatron", "G54", new PlannerXyz(0, 0, 0), new PlannerXyz(0, 0, 0), null, version, Now, "cam-user"),
                $"\"case-operation-cam-setup:{caseOperationId}:v{version}\""));
        }

        public Task<IReadOnlyList<CaseModelFile>> ListCaseModelFilesAsync(string caseId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CaseModelFile>>(imported
                ? [new("f-1", caseId, "op-10", "part", "stl", @"\\srv\pkg\geometry\part.stl", "Finished part: part.stl", false, 0, 1, Now, Now)]
                : []);

        public Task<IReadOnlyList<SimulationPackageImport>> ListSimulationPackageImportsAsync(string caseId, string caseOperationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SimulationPackageImport>>([Import(imported ? "imp-1" : "imp-0", imported ? "O1001.nc" : "O0999.nc")]);

        public Task<SimulationPackageImportResult> ImportSimulationPackageAsync(string caseId, string caseOperationId, SimulationPackageImportRequest request,
            string clientId, long editGeneration, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Generation = editGeneration;
            if (NextOutcome == "blocked")
            {
                return Task.FromResult(new SimulationPackageImportResult("blocked", null, Report(
                    new SimulationPackageDiagnostic("stock_selection_required", "error", "Choose.", null),
                    new SimulationPackageDiagnostic("machine_mapping_ambiguous", "error", "Choose.", null))));
            }

            imported = true;
            return Task.FromResult(new SimulationPackageImportResult("imported", Import("imp-1", "O1001.nc"),
                Report(new SimulationPackageDiagnostic("fixture_missing", "warning", "No fixture.", null))));
        }

        private static SimulationPackageImport Import(string id, string nc) => new(
            id, "case-1", "op-10", "imported", null, new string('a', 64), @"\\srv\pkg", "0.1.0", "2026.0", nc, "010", "machine-15",
            "Haas UMC-500 SS", "planner_selected", "HAAS_NGC_UMC500", "geometry/stock.stl", null, null, null, [1], Now, "cam-user",
            [new SimulationPackageImportFile("nc", "nc/" + nc, "x", 1, null, null, null, null, false, null)], null);

        private static SimulationPackageReport Report(params SimulationPackageDiagnostic[] diagnostics) => new(
            @"\\srv\pkg", true, null, 1, null, null, null, null, null, null, "mm", null, null, [], null, [], [], [], null, null,
            diagnostics.Any(item => item.Severity == "error"), diagnostics);
    }
}
