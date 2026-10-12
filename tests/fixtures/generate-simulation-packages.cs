#:property PublishAot=false
#:project ../../shared/Meimad.Planner.SimulationPackage/Meimad.Planner.SimulationPackage.csproj
using System.Text.Json;
using System.Text.Json.Nodes;
using Meimad.Planner.SimulationPackage.Coordinates;
using Meimad.Planner.SimulationPackage.Geometry;
using Meimad.Planner.SimulationPackage.Manifest;
using Meimad.Planner.SimulationPackage.Tools;

// Regenerates the golden simulation packages (Stage 0 test data):
//   dotnet run tests/fixtures/generate-simulation-packages.cs -- tests/fixtures/simulation-packages
// expected-bounds.json and .gitattributes beside them are maintained by hand and kept.
var root = args.Length > 0 ? args[0] : Path.Combine("tests", "fixtures", "simulation-packages");
foreach (var generated in new[] { "3-axis", "3plus2", "5-axis", "broken" })
{
    var folder = Path.Combine(root, generated);
    if (Directory.Exists(folder)) Directory.Delete(folder, true);
}
Directory.CreateDirectory(root);

static void Stl(string folder, string relative, Vector3d min, Vector3d max, string header)
{
    var path = Path.Combine(folder, relative.Replace('/', '\\'));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, MeshFiles.WriteBinaryStl(MeshFiles.Box(min, max), header));
}

static void Text(string folder, string relative, string text)
{
    var path = Path.Combine(folder, relative.Replace('/', '\\'));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
}

static ToolTableDocument Tools(params (int number, string name, string type, double d, double r)[] tools) => new()
{
    Source = new ToolTableSource { Application = "Cimatron", ExportedAtUtc = "2026-10-12T06:00:00Z" },
    Tools = tools.Select(t => new ToolTableEntry
    {
        Number = t.number, Name = t.name, Type = t.type, DiameterMm = t.d, CornerRadiusMm = t.r,
        FluteLengthMm = 30, GaugeLengthMm = 90,
        Holder = new ToolHolder { Name = "BT40 ER32", DiameterMm = 50, LengthMm = 60 }
    }).ToArray()
};

static void Finish(string folder, SimulationPackageManifest manifest, bool complete = true)
{
    var hashed = PackageManifestBuilder.WithHashes(manifest, folder);
    File.WriteAllText(Path.Combine(folder, "manifest.json"), ManifestJson.Serialize(hashed) + "\r\n");
    File.WriteAllText(Path.Combine(folder, "package.json"),
        "{\r\n  \"format\": \"meimad-sim\",\r\n  \"schemaVersion\": 1,\r\n  \"packageId\": \"" + manifest.PackageId + "\",\r\n  \"manifest\": \"manifest.json\"\r\n}\r\n");
    if (complete) File.WriteAllText(Path.Combine(folder, "COMPLETE"), "");
}

static SimulationPackageManifest Base(string id, string nc, string op, string opName, int procedure, string post, string machine, string external, string axes, CoordinateSystemDefinition cs, PackageFiles files) => new()
{
    SchemaVersion = 1,
    PackageId = id,
    Exporter = new ExporterInfo { Name = "Meimad.CimatronExporter", Version = "0.1.0", CimatronVersion = "2026.0", AdapterVersion = "cimatron-com-1", MeshToleranceMm = 0.01, UsedDefaultMeshTolerance = false },
    Source = new SourceInfo { Application = "Cimatron", Project = "GOLDEN-PART", NcFileName = nc, ExportedAtUtc = "2026-10-12T06:00:00Z" },
    Part = new PartInfo { Number = "GOLDEN-PART", Name = "Golden test part" },
    Operation = new OperationInfo { Number = op, Name = opName, ProcedureId = procedure, ProcedureName = "OP" + op },
    Machine = new MachineInfo { ExternalId = external, Name = machine, Postprocessor = post, AxisConfiguration = axes },
    Units = "mm",
    CoordinateSystem = cs,
    Files = files
};

var expected = new JsonObject();

// ---------- 3-axis, mode A ----------
{
    var f = Path.Combine(root, "3-axis");
    Directory.CreateDirectory(f);
    Text(f, "nc/O1001.nc", """
        %
        O1001 (GOLDEN 3-AXIS POCKET)
        (CIMATRON GOLDEN-PART OP010 UCS_OP010)
        G21 G17 G40 G49 G80 G90
        T1 M06 (D10 FLAT)
        G00 G90 G54 X10. Y10. S8000 M03
        G43 H01 Z25.
        Z2.
        G01 Z-5. F300.
        X90. F1200.
        Y50.
        X10.
        Y10.
        G00 Z25.
        T2 M06 (D6.8 DRILL)
        G00 G90 G54 X50. Y30. S3000 M03
        G43 H02 Z25.
        G81 Z-20. R2. F150.
        G80
        G00 Z25.
        M05
        G28 G91 Z0.
        M30
        %
        """);
    Text(f, "tools/tool-table.json", Tools((1, "D10 FLAT", "flat", 10, 0), (2, "D6.8 DRILL", "drill", 6.8, 0)).Serialize() + "\r\n");
    Stl(f, "geometry/stock.stl", new(0, 0, -30), new(100, 60, 0), "GOLDEN 3-AXIS STOCK");
    Stl(f, "geometry/fixture-001.stl", new(-20, -20, -60), new(120, 80, -30), "GOLDEN BASE PLATE");
    Stl(f, "geometry/fixture-002.stl", new(-10, -15, -30), new(110, 0, -10), "GOLDEN VISE JAW");
    Stl(f, "geometry/part.stl", new(5, 5, -25), new(95, 55, 0), "GOLDEN PART");
    var cs = new CoordinateSystemDefinition { Type = "nc-procedure-ucs", Name = "UCS_OP010", WorkOffset = "G54", FilesAreIn = "nc-work-coordinates", Transform = "identity" };
    Finish(f, Base("0f1e2d3c4b5a69788796a5b4c3d2e1f0", "O1001.nc", "010", "Pocket", 17, "HAAS_NGC_VF3", "Haas VF-3", "VF3_01", "XYZ", cs, new PackageFiles
    {
        Nc = new() { Path = "nc/O1001.nc" },
        ToolTable = new() { Path = "tools/tool-table.json" },
        Stock = [new StockFileReference { Path = "geometry/stock.stl", Kind = "initial", Name = "Blank 100x60x30", FoundBy = "stock-procedure" }],
        Fixtures = [new FixtureFileReference { Path = "geometry/fixture-001.stl", Name = "Base plate", Collision = true },
                    new FixtureFileReference { Path = "geometry/fixture-002.stl", Name = "Vise jaw", Collision = true }],
        Part = new PartFileReference { Path = "geometry/part.stl", Role = "finished-part", Name = "Finished part" }
    }));
}

// ---------- 3+2, mode B (UCS rotated 90 deg about Y, origin on the +X face) ----------
{
    var f = Path.Combine(root, "3plus2");
    Directory.CreateDirectory(f);
    Text(f, "nc/O1002.nc", """
        %
        O1002 (GOLDEN 3+2 SIDE FACE)
        (CIMATRON GOLDEN-PART OP020 UCS_OP020)
        G21 G17 G40 G49 G80 G90
        T3 M06 (D12 BULL R1)
        G00 G90 G54 B90. C0.
        G68.2 X0. Y0. Z0. I0. J90. K0.
        G53.1
        G00 X5. Y5. S6000 M03
        G43 H03 Z25.
        Z2.
        G01 Z-3. F250.
        X35. F1000.
        Y45.
        X5.
        Y5.
        G00 Z25.
        G69
        M05
        G28 G91 Z0.
        M30
        %
        """);
    Text(f, "tools/tool-table.json", Tools((3, "D12 R1 BULL", "bull-nose", 12, 1)).Serialize() + "\r\n");
    Stl(f, "geometry/stock-op020-from-op010.stl", new(0, 0, 0), new(80, 50, 40), "GOLDEN 3+2 REMAINING STOCK");
    Stl(f, "geometry/fixture-001.stl", new(-30, -10, -20), new(20, 60, 0), "GOLDEN TOMBSTONE");
    Stl(f, "geometry/part.stl", new(0, 0, 0), new(77, 50, 40), "GOLDEN PART");
    var cs = new CoordinateSystemDefinition
    {
        Type = "part-model-coordinates", Name = "UCS_OP020", WorkOffset = "G55", FilesAreIn = "part-model-coordinates",
        OriginMm = new(80, 0, 40), RotationDeg = new(0, 90, 0), RotationOrder = "RzRyRx",
        Matrix = [0, 0, 1, 80, 0, 1, 0, 0, -1, 0, 0, 40, 0, 0, 0, 1]
    };
    Finish(f, Base("1f1e2d3c4b5a69788796a5b4c3d2e1f0", "O1002.nc", "020", "Side face", 23, "HAAS_NGC_UMC500", "Haas UMC-500", "UMC500_01", "B_C", cs, new PackageFiles
    {
        Nc = new() { Path = "nc/O1002.nc" },
        ToolTable = new() { Path = "tools/tool-table.json" },
        Stock = [new StockFileReference { Path = "geometry/stock-op020-from-op010.stl", Kind = "remaining", SourceOperation = "010", FoundBy = "remaining-stock-api" }],
        Fixtures = [new FixtureFileReference { Path = "geometry/fixture-001.stl", Name = "Tombstone", Collision = true }],
        Part = new PartFileReference { Path = "geometry/part.stl", Role = "finished-part" }
    }));
}

// ---------- simultaneous 5-axis, mode A ----------
{
    var f = Path.Combine(root, "5-axis");
    Directory.CreateDirectory(f);
    Text(f, "nc/O1003.nc", """
        %
        O1003 (GOLDEN SIMULTANEOUS 5-AXIS SWARF)
        (CIMATRON GOLDEN-PART OP030 UCS_OP030)
        G21 G17 G40 G49 G80 G90
        T4 M06 (D8 BALL)
        G00 G90 G54 A0. C0.
        G43.4 H04
        G00 X-10. Y0. Z30. S10000 M03
        G01 Z5. F500.
        X0. Y0. Z0. A-10. C0. F800.
        X10. Y5. Z-1. A-20. C15.
        X20. Y10. Z-2. A-30. C30.
        X30. Y15. Z-2. A-30. C45.
        G00 Z30. A0. C0.
        G49
        M05
        G28 G91 Z0.
        M30
        %
        """);
    Text(f, "tools/tool-table.json", Tools((4, "D8 BALL", "ball", 8, 4)).Serialize() + "\r\n");
    Stl(f, "geometry/stock.stl", new(-20, -20, -20), new(40, 30, 0), "GOLDEN 5-AXIS STOCK");
    Stl(f, "geometry/fixture-001.stl", new(-25, -25, -45), new(45, 35, -20), "GOLDEN TRUNNION PLATE");
    Stl(f, "geometry/part.stl", new(-15, -15, -20), new(35, 25, -2), "GOLDEN PART");
    var cs = new CoordinateSystemDefinition { Type = "nc-procedure-ucs", Name = "UCS_OP030", WorkOffset = "G54", FilesAreIn = "nc-work-coordinates", Transform = "identity" };
    Finish(f, Base("2f1e2d3c4b5a69788796a5b4c3d2e1f0", "O1003.nc", "030", "Swarf", 31, "HAAS_NGC_UMC500", "Haas UMC-500", "UMC500_01", "A_C", cs, new PackageFiles
    {
        Nc = new() { Path = "nc/O1003.nc" },
        ToolTable = new() { Path = "tools/tool-table.json" },
        Stock = [new StockFileReference { Path = "geometry/stock.stl", Kind = "initial" }],
        Fixtures = [new FixtureFileReference { Path = "geometry/fixture-001.stl", Name = "Trunnion plate" }],
        Part = new PartFileReference { Path = "geometry/part.stl", Role = "finished-part" }
    }));
}

// ---------- broken variants (copies of 3-axis) ----------
void Copy(string from, string to)
{
    foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        Directory.CreateDirectory(dir.Replace(from, to));
    Directory.CreateDirectory(to);
    foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        File.Copy(file, file.Replace(from, to));
}

var golden3 = Path.Combine(root, "3-axis");
var broken = Path.Combine(root, "broken");
{
    var f = Path.Combine(broken, "missing-complete"); Copy(golden3, f); File.Delete(Path.Combine(f, "COMPLETE"));
}
{
    var f = Path.Combine(broken, "hash-mismatch"); Copy(golden3, f);
    File.AppendAllText(Path.Combine(f, "nc", "O1001.nc"), "(EDITED AFTER EXPORT)\r\n");
}
{
    var f = Path.Combine(broken, "missing-file"); Copy(golden3, f); File.Delete(Path.Combine(f, "geometry", "fixture-002.stl"));
}
{
    var f = Path.Combine(broken, "old-schema"); Copy(golden3, f);
    var text = File.ReadAllText(Path.Combine(f, "manifest.json")).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 0");
    File.WriteAllText(Path.Combine(f, "manifest.json"), text);
}
{
    var f = Path.Combine(broken, "path-traversal"); Copy(golden3, f);
    var text = File.ReadAllText(Path.Combine(f, "manifest.json")).Replace("\"geometry/part.stl\"", "\"../part.stl\"");
    File.WriteAllText(Path.Combine(f, "manifest.json"), text);
}
{
    var f = Path.Combine(broken, "tool-mismatch"); Copy(golden3, f);
    var doc = ToolTableDocument.Parse(File.ReadAllText(Path.Combine(f, "tools", "tool-table.json")));
    File.WriteAllText(Path.Combine(f, "tools", "tool-table.json"), (doc with { Tools = doc.Tools.Where(t => t.Number == 1).ToArray() }).Serialize() + "\r\n");
    var manifest = ManifestJson.Deserialize(File.ReadAllText(Path.Combine(f, "manifest.json")));
    File.WriteAllText(Path.Combine(f, "manifest.json"), ManifestJson.Serialize(PackageManifestBuilder.WithHashes(manifest, f)) + "\r\n");
}
{
    var f = Path.Combine(broken, "missing-ucs"); Copy(golden3, f);
    var node = JsonNode.Parse(File.ReadAllText(Path.Combine(f, "manifest.json")))!.AsObject();
    node.Remove("coordinateSystem");
    File.WriteAllText(Path.Combine(f, "manifest.json"), node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\r\n");
}
Console.WriteLine("ok");
