using System.Text.Json;
using Meimad.CimatronExporter;
using Meimad.Planner.SimulationPackage.Manifest;

namespace Meimad.Planner.Server.Tests.SimulationPackages;

/// <summary>
/// The exporter as the Meimad client's helper process (Import from Cimatron, specification of 2026-10-13): the JSON listing
/// of the active document, the export of CAM operations chosen by Cimatron id, and the refusals that keep a wrong or
/// unsaved source out of Meimad.
/// </summary>
public sealed class CimatronBridgeTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("meimad-bridge-").FullName;

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

    private static readonly CimatronToolpath[] Toolpaths =
        [new(4, "TP_MODEL", "NC_Setup", 3), new(105, "TP_MODEL_0", "NC_Setup", 3), new(202, "NISAYON", "SECOND", 207)];

    private static readonly CimatronProcedure[] Procedures =
    [
        new(17, "OP010_ROUGH", "UCS_OP010", true, "TP_MODEL"),
        new(18, "OP010_FINISH", "UCS_OP010", true, "TP_MODEL"),
        new(117, "ROUGH_COPY", "UCS_OP010", true, "TP_MODEL_0"),
        new(118, "FINISH_COPY", "UCS_OP010", true, "TP_MODEL_0"),
        new(210, "SECOND_SIDE", "UCS_OP010", false, "NISAYON")
    ];

    private static FakeCimatronApi Api(bool? modified = false) => new()
    {
        SupportsInitialStock = false, GeometryInUcs = false, UcsMatrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
        Toolpaths = Toolpaths, Procedures = Procedures, Modified = modified
    };

    private BridgeExportRequest Request(IReadOnlyList<int>? toolpaths, IReadOnlyList<int>? procedures) => new()
    {
        Output = Path.Combine(root, "Gcode", "PN-1", "010", "A", "HAAS_NGC_VF3", "1"),
        Post = "HAAS_NGC_VF3",
        Ucs = "UCS_OP010",
        ToolpathIds = toolpaths,
        ProcedureIds = procedures,
        Operation = "010",
        PartNumber = "PN-1",
        ExpectedDocumentPath = @"C:\Cimatron\GOLDEN-PART.elt",
        Context = new MeimadImportContext { ImportId = "imp-1", CaseId = "case-1", CaseOperationId = "op-1", BasedOnVersion = "4" }
    };

    [Fact]
    public void The_listing_groups_toolpaths_and_procedures_by_setup_with_their_cimatron_ids()
    {
        var listing = ClientBridge.List(() => Api());

        Assert.True(listing.Ok);
        Assert.Equal(("GOLDEN-PART.elt", "PID-GOLDEN", false), (listing.Document!.Name, listing.Document.DocumentId, listing.Document.Modified));
        var setup = listing.Setups.Single(item => item.Name == "NC_Setup");
        Assert.Equal(3, setup.StockProcedureId);
        Assert.Equal([4, 105], setup.Toolpaths.Select(item => item.Id));
        Assert.Equal([17, 18], setup.Toolpaths[0].Procedures.Select(item => item.Id));
        Assert.False(listing.Setups.Single(item => item.Name == "SECOND").Toolpaths[0].Procedures[0].Calculated);

        var failed = ClientBridge.List(() => throw new CimatronApiException("cimatron_not_available", "Cimatron is not running."));
        Assert.False(failed.Ok);
        Assert.Equal("cimatron_not_available", failed.Error!.Code);
    }

    [Fact]
    public void Chosen_operations_post_as_their_nodes_and_the_manifest_records_the_source_and_the_meimad_import()
    {
        using var api = Api();
        // TP_MODEL_0 whole, and one procedure of TP_MODEL: both in setup NC_Setup.
        var result = ClientBridge.Export(Request([105], [17]), () => api, TimeProvider.System);

        Assert.True(result.Complete, result.Report);
        Assert.Equal(new[] { 105, 17 }, api.SelectedPosts[0].ToArray());
        var manifest = ManifestJson.Deserialize(File.ReadAllText(Path.Combine(result.PackagePath!, "manifest.json")));
        Assert.Equal((@"C:\Cimatron\GOLDEN-PART.elt", "PID-GOLDEN", "NC_Setup"), (manifest.Source!.DocumentPath, manifest.Source.DocumentId, manifest.Source.Setup));
        Assert.Equal(["toolpath:105", "procedure:17"], manifest.Source.Operations!.Select(item => $"{item.Kind}:{item.Id}"));
        Assert.Equal(("imp-1", "op-1", "4"), (manifest.Meimad!.ImportId, manifest.Meimad.CaseOperationId, manifest.Meimad.BasedOnVersion));
        Assert.Equal([17, 117, 118], manifest.Operation!.ProcedureIds!.Order());
        Assert.Equal(3, api.RemainingStockProcedure); // the setup's stock procedure
    }

    [Fact]
    public void A_wrong_or_unsaved_source_and_a_selection_across_setups_are_refused()
    {
        string Code(BridgeExportResult result) => Assert.Single(result.Entries, entry => entry.Status == "error").Code!;

        using (var api = Api())
            Assert.Equal("selection_spans_setups", Code(ClientBridge.Export(Request([105, 202], null), () => api, TimeProvider.System)));
        using (var api = Api())
            Assert.Equal("selection_not_found", Code(ClientBridge.Export(Request([999], null), () => api, TimeProvider.System)));
        using (var api = Api(modified: true))
            Assert.Equal("document_unsaved", Code(ClientBridge.Export(Request([105], null), () => api, TimeProvider.System)));
        using (var api = Api())
            Assert.Equal("document_changed",
                Code(ClientBridge.Export(Request([105], null) with { ExpectedDocumentPath = @"C:\Cimatron\OTHER.elt" }, () => api, TimeProvider.System)));

        var empty = ClientBridge.Export(Request(null, null), () => Api(), TimeProvider.System);
        Assert.Equal(("request_invalid", 2), (empty.Error!.Code, empty.ExitCode));
    }

    [Fact]
    public void The_helper_process_writes_its_json_answer_to_the_result_file()
    {
        var result = Path.Combine(root, "answer", "listing.json");
        Assert.Equal(0, ClientBridge.Run(["--list-json", "--result", result], () => Api()));
        using var json = JsonDocument.Parse(File.ReadAllText(result));
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("NC_Setup", json.RootElement.GetProperty("setups")[0].GetProperty("name").GetString());

        var requestFile = Path.Combine(root, "request.json");
        File.WriteAllText(requestFile, JsonSerializer.Serialize(Request([4], null), ClientBridge.Json));
        var exportAnswer = Path.Combine(root, "answer", "export.json");
        Assert.Equal(0, ClientBridge.Run(["--export-json", requestFile, "--result", exportAnswer], () => Api()));
        using var exported = JsonDocument.Parse(File.ReadAllText(exportAnswer));
        Assert.True(exported.RootElement.GetProperty("complete").GetBoolean());

        Assert.Equal(2, ClientBridge.Run(["--list-json"], () => Api()));
    }
}
