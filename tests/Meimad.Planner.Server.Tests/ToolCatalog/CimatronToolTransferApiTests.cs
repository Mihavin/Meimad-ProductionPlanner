using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Application.LegacyImport;
using Meimad.Planner.Server.Application.ToolCatalog;
using Meimad.Planner.Server.Tests.ToolPreparations;
using Microsoft.AspNetCore.TestHost;

namespace Meimad.Planner.Server.Tests.ToolCatalog;

public sealed class CimatronToolTransferApiTests
{
    private const string Route = "/api/v1/tool-catalog";
    private const string ImportRoute = "/api/v1/tool-catalog/import/cimatron";
    private const string ExportRoute = "/api/v1/tool-catalog/export/cimatron";

    [Fact]
    public async Task A_real_Cimatron_cutter_workbook_is_previewed_then_imported_and_a_second_import_changes_nothing()
    {
        await using var server = await ToolPreparationApiTests.TestServer.StartAsync(verificationEnabled: false);
        var client = server.Client;
        var workbook = await File.ReadAllBytesAsync(SamplePath("FLAYCAT_80.xlsm"));

        // A preview reads the cutters and saves nothing; it needs no identity.
        using var anonymous = new HttpClient(server.Application.GetTestServer().CreateHandler()) { BaseAddress = client.BaseAddress };
        using var preview = await ImportAsync(anonymous, workbook, apply: false);
        Assert.False(preview.RootElement.GetProperty("applied").GetBoolean());
        Assert.Equal("mm", preview.RootElement.GetProperty("units").GetString());
        Assert.Equal(30, preview.RootElement.GetProperty("created").GetInt32());
        Assert.Equal(30, preview.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Empty(await ToolsAsync(client));

        // Applying needs a signed-in user.
        using var signedOut = anonymous.SignedOut();
        using var unidentified = await PostAsync(anonymous, workbook, apply: true);
        Assert.Equal(HttpStatusCode.Unauthorized, unidentified.StatusCode);

        using var applied = await ImportAsync(client, workbook, apply: true);
        Assert.True(applied.RootElement.GetProperty("applied").GetBoolean());
        Assert.Equal(30, applied.RootElement.GetProperty("created").GetInt32());
        var tools = await ToolsAsync(client);
        Assert.Equal(30, tools.Count);
        Assert.Equal("MT-00001", tools["FLAYCAT 80"].GetProperty("internalCode").GetString());

        var resek = tools["RESEK 10"];
        Assert.Equal("END_MILL", resek.GetProperty("toolType").GetString());
        Assert.Equal(10, Shape(resek, "cuttingDiameter"));
        Assert.Equal(15, Shape(resek, "fluteLength"));
        Assert.Equal(28, Shape(resek, "neckLength"));
        Assert.Equal(3, Shape(resek, "fluteCount"));
        Assert.Equal("BT40 ER16 X 100", resek.GetProperty("attributes").GetProperty("holderCode").GetString());
        Assert.Equal(("Cimatron", "RESEK 10"), ExternalId(resek));
        Assert.Equal(JsonValueKind.Null, resek.GetProperty("description").ValueKind);   // Cimatron's "No comment"

        var reduced = tools["FIN 2_L=22_SHIHRUR=12"];
        Assert.Equal(4, Shape(reduced, "shankDiameter"));
        Assert.Equal("EC-A2 020-30/12C4H50T", reduced.GetProperty("description").GetString());

        var chamfer = tools["MERKUZ 3 X 90* L=20"];
        Assert.Equal("CHAMFER_MILL", chamfer.GetProperty("toolType").GetString());
        Assert.Equal(3, Shape(chamfer, "cuttingDiameter"));
        Assert.Equal(0.1, Shape(chamfer, "tipDiameter"));
        Assert.Equal(45, Shape(chamfer, "taperAngle"));

        Assert.Equal("BALL_END_MILL", tools["BALL 1.5"].GetProperty("toolType").GetString());
        Assert.Equal("BULL_NOSE_END_MILL", tools["FIN 5_R1"].GetProperty("toolType").GetString());
        Assert.Equal(1, Shape(tools["FIN 5_R1"], "cornerRadius"));
        Assert.Equal("DRILL", tools["DRILL 1.45"].GetProperty("toolType").GetString());
        Assert.Equal(118, Shape(tools["DRILL 1.45"], "pointAngle"));
        Assert.Equal("REAMER", tools["REAMER 5 H7"].GetProperty("toolType").GetString());
        Assert.Equal("TAP", tools["MAVR_M6_SPIRALY"].GetProperty("toolType").GetString());
        Assert.Equal("M6", tools["MAVR_M6_SPIRALY"].GetProperty("attributes").GetProperty("threadProfile").GetString());

        using var again = await ImportAsync(client, workbook, apply: true);
        Assert.Equal(30, again.RootElement.GetProperty("unchanged").GetInt32());
        Assert.Equal(0, again.RootElement.GetProperty("created").GetInt32());
        Assert.Equal(30, (await ToolsAsync(client)).Count);
    }

    [Fact]
    public async Task A_reimport_updates_only_what_Cimatron_describes_and_keeps_the_catalogs_own_data()
    {
        await using var server = await ToolPreparationApiTests.TestServer.StartAsync(verificationEnabled: false);
        var client = server.Client;
        var workbook = await File.ReadAllBytesAsync(SamplePath("FLAYCAT_80.xlsm"));
        using (await ImportAsync(client, workbook, apply: true)) { }

        // The Tool Room renames a tool, adds its material and changes a Cimatron dimension.
        var resek = (await ToolsAsync(client))["RESEK 10"];
        var shape = resek.GetProperty("shape").EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.GetDouble());
        shape["cuttingDiameter"] = 9.98;
        shape["overallLength"] = 72;
        using var edited = await client.PutAsJsonAsync($"{Route}/{resek.GetProperty("catalogToolId").GetString()}", new
        {
            name = "Roughing end mill D10",
            toolType = "END_MILL",
            shape,
            attributes = new Dictionary<string, string> { ["holderCode"] = "BT40 ER16 X 100", ["material"] = "Carbide" },
            externalIds = new[] { new { system = "Cimatron", value = "RESEK 10" }, new { system = "ERP", value = "K-100" } },
            isActive = true,
            expectedVersion = 1
        });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        using var preview = await ImportAsync(client, workbook, apply: false);
        var row = preview.RootElement.GetProperty("rows").EnumerateArray().Single(entry => entry.GetProperty("cutterName").GetString() == "RESEK 10");
        Assert.Equal("UPDATE", row.GetProperty("action").GetString());
        Assert.Equal(29, preview.RootElement.GetProperty("unchanged").GetInt32());

        using (await ImportAsync(client, workbook, apply: true)) { }
        var updated = (await ToolsAsync(client))["Roughing end mill D10"];
        Assert.Equal(10, Shape(updated, "cuttingDiameter"));                  // Cimatron's value again
        Assert.Equal(72, Shape(updated, "overallLength"));                    // the catalog's own dimension stays
        Assert.Equal("Carbide", updated.GetProperty("attributes").GetProperty("material").GetString());
        Assert.Equal(2, updated.GetProperty("externalIds").GetArrayLength());
        Assert.Equal(3, updated.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task The_catalog_exports_as_a_Cimatron_workbook_that_imports_back_unchanged()
    {
        await using var server = await ToolPreparationApiTests.TestServer.StartAsync(verificationEnabled: false);
        var client = server.Client;
        var source = await File.ReadAllBytesAsync(SamplePath("FLAYCAT_80.xlsm"));
        using (await ImportAsync(client, source, apply: true)) { }
        using var turning = await client.PostAsJsonAsync(Route, new
        {
            name = "PCLNR 2525 M12", toolType = "TURNING_TOOL", hand = "RIGHT", shape = new Dictionary<string, double> { ["cornerRadius"] = 0.8 }
        });
        Assert.Equal(HttpStatusCode.Created, turning.StatusCode);
        using var drawn = await client.PostAsJsonAsync(Route, new
        {
            name = "Spot drill D6", toolType = "SPOT_DRILL",
            shape = new Dictionary<string, double> { ["cuttingDiameter"] = 6, ["pointAngle"] = 90, ["fluteLength"] = 12 }
        });
        Assert.Equal(HttpStatusCode.Created, drawn.StatusCode);

        using var export = await client.GetAsync(ExportRoute);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal(CimatronCutterWorkbookWriter.ContentType, export.Content.Headers.ContentType?.MediaType);
        Assert.Equal("31", export.Headers.GetValues("X-Meimad-Exported-Tools").Single());
        Assert.Equal("1", export.Headers.GetValues("X-Meimad-Skipped-Tools").Single());   // the turning tool
        var bytes = await export.Content.ReadAsByteArrayAsync();

        // The export is Cimatron's own workbook with the cutters in its Cutters sheet.
        await using var stream = new MemoryStream(bytes);
        var workbook = await new OpenXmlLegacyWorkbookReader().ReadAsync(stream, "export.xlsm", CancellationToken.None);
        Assert.Contains(workbook.Sheets, sheet => sheet.Name == ".Localize-Cutters");
        var cutters = CimatronCutterLibrary.Read(workbook).Cutters.ToDictionary(cutter => cutter.Name);
        Assert.Equal(31, cutters.Count);
        var original = CimatronCutterLibrary.Read(await ReadAsync(source)).Cutters.ToDictionary(cutter => cutter.Name);
        foreach (var (name, cutter) in original)
        {
            var exported = cutters[name];
            Assert.Equal(cutter.Technology, exported.Technology);
            Assert.Equal(cutter.Tip, exported.Tip);
            Assert.Equal(cutter.Diameter, exported.Diameter);
            Assert.Equal(cutter.CutLength, exported.CutLength);
            Assert.Equal(cutter.ClearLength, exported.ClearLength);
            Assert.Equal(cutter.Taper, exported.Taper);
            Assert.Equal(cutter.HolderName, exported.HolderName);
            Assert.Equal(cutter.Teeth, exported.Teeth);
            Assert.Equal(cutter.Comment, exported.Comment);
        }
        Assert.Equal("Center", cutters["Spot drill D6"].Tip);
        Assert.Equal(90, cutters["Spot drill D6"].TipAngle);
        Assert.Equal("M", cutters["MAVR_M6_SPIRALY"].CatalogName);
        Assert.Equal("M6", cutters["MAVR_M6_SPIRALY"].ThreadType);

        Assert.True(cutters["RESEK 3"].Shank);
        Assert.Equal(6, cutters["RESEK 3"].ShankTopDiameter);

        // Imported tools come back unchanged; the hand-made spot drill is matched by name and
        // gains its Cimatron id but stays a spot drill (Cimatron's center drill is the same kind).
        using var roundTrip = await ImportAsync(client, bytes, apply: false);
        Assert.Equal(30, roundTrip.RootElement.GetProperty("unchanged").GetInt32());
        Assert.Equal(1, roundTrip.RootElement.GetProperty("updated").GetInt32());
        Assert.Equal("SPOT_DRILL", roundTrip.RootElement.GetProperty("rows").EnumerateArray()
            .Single(row => row.GetProperty("cutterName").GetString() == "Spot drill D6").GetProperty("toolType").GetString());
    }

    [Fact]
    public async Task A_reimport_keeps_catalog_types_Cimatron_folds_into_one_cutter_kind()
    {
        await using var server = await ToolPreparationApiTests.TestServer.StartAsync(verificationEnabled: false);
        var client = server.Client;
        var source = await File.ReadAllBytesAsync(SamplePath("FLAYCAT_80.xlsm"));
        using (await ImportAsync(client, source, apply: true)) { }

        // Cimatron writes these as a flat, tapered flat, slot or center-drill cutter.
        var folded = new Dictionary<string, (string Type, Dictionary<string, double> Shape)>
        {
            ["Face mill D50"] = ("FACE_MILL", new() { ["cuttingDiameter"] = 50, ["fluteLength"] = 6 }),
            ["Counterbore D11"] = ("COUNTERBORE", new() { ["cuttingDiameter"] = 11, ["fluteLength"] = 8 }),
            ["Boring head 30-40"] = ("BORING_HEAD", new() { ["cuttingDiameter"] = 30 }),
            ["Engraver 0.2 x 30"] = ("ENGRAVER", new() { ["cuttingDiameter"] = 3, ["tipDiameter"] = 0.2, ["taperAngle"] = 30 }),
            ["T-slot D20"] = ("T_SLOT_MILL", new() { ["cuttingDiameter"] = 20, ["fluteLength"] = 4 }),
            ["Spot drill D6"] = ("SPOT_DRILL", new() { ["cuttingDiameter"] = 6, ["pointAngle"] = 90 })
        };
        foreach (var (name, (type, shape)) in folded)
        {
            using var created = await client.PostAsJsonAsync(Route, new { name, toolType = type, shape });
            Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        }

        using var export = await client.GetAsync(ExportRoute);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var bytes = await export.Content.ReadAsByteArrayAsync();
        using (var applied = await ImportAsync(client, bytes, apply: true))
        {
            Assert.Equal(folded.Count, applied.RootElement.GetProperty("updated").GetInt32());   // each gains its Cimatron id
        }
        var tools = await ToolsAsync(client);
        foreach (var (name, (type, _)) in folded)
        {
            Assert.Equal(type, tools[name].GetProperty("toolType").GetString());
            Assert.Equal(("Cimatron", name), ExternalId(tools[name]));
        }
        using (var again = await ImportAsync(client, bytes, apply: false))
        {
            Assert.Equal(30 + folded.Count, again.RootElement.GetProperty("unchanged").GetInt32());
        }

        // A cutter of another kind still sets Cimatron's type: the workbook's ball tip wins over an end mill.
        var ball = tools["BALL 1.5"];
        using var retyped = await client.PutAsJsonAsync($"{Route}/{ball.GetProperty("catalogToolId").GetString()}", new
        {
            name = "BALL 1.5",
            toolType = "END_MILL",
            shape = ball.GetProperty("shape").EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.GetDouble()),
            attributes = ball.GetProperty("attributes").EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.GetString()),
            externalIds = new[] { new { system = "Cimatron", value = "BALL 1.5" } },
            isActive = true,
            expectedVersion = ball.GetProperty("version").GetInt32()
        });
        Assert.Equal(HttpStatusCode.OK, retyped.StatusCode);
        using (await ImportAsync(client, source, apply: true)) { }
        Assert.Equal("BALL_END_MILL", (await ToolsAsync(client))["BALL 1.5"].GetProperty("toolType").GetString());
    }

    [Fact]
    public async Task Workbooks_without_cutters_are_rejected()
    {
        await using var server = await ToolPreparationApiTests.TestServer.StartAsync(verificationEnabled: false);
        await using var template = typeof(CimatronCutterWorkbookWriter).Assembly.GetManifestResourceStream(CimatronCutterWorkbookWriter.TemplateResource)!;
        using var copy = new MemoryStream();
        await template.CopyToAsync(copy);
        using var empty = await PostAsync(server.Client, copy.ToArray(), apply: false);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Contains("cimatron_no_cutters", await empty.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var notExcel = await PostAsync(server.Client, "not a workbook"u8.ToArray(), apply: false);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notExcel.StatusCode);
    }

    private static async Task<JsonDocument> ImportAsync(HttpClient client, byte[] workbook, bool apply)
    {
        using var response = await PostAsync(client, workbook, apply);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, byte[] workbook, bool apply)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(workbook);
        file.Headers.ContentType = new MediaTypeHeaderValue(CimatronCutterWorkbookWriter.ContentType);
        content.Add(file, "workbook", "FLAYCAT_80.xlsm");
        content.Add(new StringContent(apply ? "true" : "false"), "apply");
        return await client.PostAsync(ImportRoute, content);
    }

    private static async Task<Dictionary<string, JsonElement>> ToolsAsync(HttpClient client)
    {
        using var response = await client.GetAsync($"{Route}?includeInactive=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("name").GetString()!, item => item.Clone());
    }

    private static async Task<Meimad.Planner.Server.Application.LegacyImport.LegacyWorkbookData> ReadAsync(byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes);
        return await new OpenXmlLegacyWorkbookReader().ReadAsync(stream, "source.xlsm", CancellationToken.None);
    }

    private static double Shape(JsonElement tool, string key) => tool.GetProperty("shape").GetProperty(key).GetDouble();

    private static (string, string) ExternalId(JsonElement tool)
    {
        var entry = tool.GetProperty("externalIds").EnumerateArray().Single();
        return (entry.GetProperty("system").GetString()!, entry.GetProperty("value").GetString()!);
    }

    private static string SamplePath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "data", "sample data for testing", "cimatron", name);
            if (File.Exists(path)) return path;
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"Could not locate the Cimatron sample '{name}'.");
    }
}
