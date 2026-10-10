using System.IO;
using System.Text.Json;
using Microsoft.ClearScript.V8;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>
/// The lathe Machine view's pure parts (NcViewer/meimad-lathe-axes-core.js) in V8 with the vendored
/// kinematics: C/Y milling axes read from program rows, the lathe chain with its turret on the tool
/// and the spindle face position.
/// </summary>
public sealed class NcViewerLatheAxesCoreTests : IDisposable
{
    private readonly V8ScriptEngine engine = new();

    public NcViewerLatheAxesCoreTests()
    {
        var root = RepositoryRoot();
        engine.Execute(File.ReadAllText(Path.Combine(root, "third_party", "chevalier-nc-viewer", "media", "kinematics.js")));
        engine.Execute(File.ReadAllText(Path.Combine(root, "client-windows", "Meimad.Planner.Client.Windows", "NcViewer", "meimad-lathe-axes-core.js")));
    }

    [Fact]
    public void Rows_give_c_h_y_v_and_ignore_chamfers_macros_and_comments()
    {
        var result = Run("""
            return [
              L.readRow("G01 X40. Z-10. C90. Y5. F0.1"),
              L.readRow("G01 X40. Z-12. ,C1.0 (C45 IN COMMENT)"),
              L.readRow("G00 H-30. V2.5"),
              L.readRow("V1=V1+1"),
              L.readRow("IF [V1 EQ 20] GOTO N200"),
              L.readRow("G65 P9010 H5. C3."),
              L.readRow("G112"),
              L.readRow("G113 C0")
            ];
            """);
        Assert.Equal(90, result[0].GetProperty("c").GetDouble());
        Assert.Equal(5, result[0].GetProperty("y").GetDouble());
        Assert.False(result[1].TryGetProperty("c", out _));
        Assert.Equal(-30, result[2].GetProperty("h").GetDouble());
        Assert.Equal(2.5, result[2].GetProperty("v").GetDouble());
        foreach (var index in new[] { 3, 4, 5 })
        {
            Assert.False(result[index].TryGetProperty("v", out _));
            Assert.False(result[index].TryGetProperty("h", out _));
            Assert.False(result[index].TryGetProperty("c", out _));
        }
        Assert.True(result[6].GetProperty("polarOn").GetBoolean());
        Assert.True(result[7].GetProperty("polarOff").GetBoolean());
    }

    [Fact]
    public void Segments_carry_c_and_y_including_indexing_rows_that_draw_nothing()
    {
        var result = Run("""
            const lines = ["O1000", "G00 X50. Z2. C0", "G00 C90.", "M05", "G01 X40. Z-5. Y4. F0.2", "G01 Z-10. H45.", "G112", "G01 X20. C10.", "G113", "G00 X100. Z50."];
            const segments = [
              { line: 2, raw: lines[1] }, { line: 5, raw: lines[4] }, { line: 6, raw: lines[5] }, { line: 8, raw: lines[7] }, { line: 10, raw: lines[9] }
            ];
            const a = L.annotate(segments, lines);
            return { a, mid: L.at(a, 2, 0.5), ranges: L.ranges(a, segments) };
            """);
        var a = result.GetProperty("a");
        Assert.True(a.GetProperty("usesC").GetBoolean());
        Assert.True(a.GetProperty("usesY").GetBoolean());
        Assert.True(a.GetProperty("polar").GetBoolean());
        var segments = a.GetProperty("segments");
        // The C90 indexing row between the first two moves turns the spindle before the cut.
        Assert.Equal((90.0, 90.0, 0.0, 4.0), Tuple(segments[1]));
        Assert.Equal((90.0, 135.0, 4.0, 4.0), Tuple(segments[2]));
        // Under polar interpolation C is not a spindle position.
        Assert.Equal((135.0, 135.0, 4.0, 4.0), Tuple(segments[3]));
        Assert.Equal(112.5, result.GetProperty("mid").GetProperty("C").GetDouble(), 6);
        var c = result.GetProperty("ranges").GetProperty("C");
        Assert.Equal((0.0, 135.0, 6), (c.GetProperty("min").GetDouble(), c.GetProperty("max").GetDouble(), c.GetProperty("maxLine").GetInt32()));
    }

    [Fact]
    public void Lathe_chain_gets_a_y_slide_and_placeholders_and_the_turret_follows_the_tool()
    {
        var result = Run("""
            const definition = {
              axes: { X: { min: -426, max: 0, diameter: true }, Y: { min: -50.8, max: 50.8 }, Z: { min: -572, max: 0 }, C: { wrap: true } },
              kinematics: { nodes: [
                { id: "bed" },
                { id: "z-carriage", parent: "bed", type: "linear", axis: "Z", direction: [0, 0, 1] },
                { id: "x-slide", parent: "z-carriage", type: "linear", axis: "X", direction: [1, 0, 0], scale: 0.5 },
                { id: "turret", parent: "x-slide", type: "spindle", toolMount: true },
                { id: "c-spindle", parent: "bed", type: "rotary", axis: "C", direction: [0, 0, 1], workpieceMount: true,
                  geometry: { shape: "cylinder", radius: 127, height: 80, center: [0, 0, -80], axis: "z" } }
              ] }
            };
            const nodes = L.prepareNodes(definition, 1);
            const chain = K.buildChain({ nodes });
            const world = K.evaluateChain(chain, { X: 60, Y: 5, Z: -30, C: 90 });
            const turret = K.transformPoint(world.get("turret"), { x: 0, y: 0, z: 0 });
            const spindleX = K.transformPoint(world.get("c-spindle"), { x: 1, y: 0, z: 0 });
            return {
              ids: nodes.map((node) => node.id),
              turretParent: nodes.find((node) => node.id === "turret").parent,
              placeholders: nodes.filter((node) => node.geometry && node.geometry.placeholder).map((node) => node.id),
              chuck: nodes.find((node) => node.id === "c-spindle").geometry.placeholder === true,
              turret, spindleX,
              faceFromStock: L.spindleFaceZ({ segments: [] }, { frontZ: 2, length: 60 }),
              faceFromPath: L.spindleFaceZ({ segments: [{ points: [{ x: 40, z: 1 }, { x: 40, z: -42 }] }] }, null),
              noYSlide: L.prepareNodes({ axes: { X: {}, Z: {} }, kinematics: definition.kinematics }, 1).some((node) => node.id === "meimad-y-slide")
            };
            """);
        Assert.Contains("meimad-y-slide", result.GetProperty("ids").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("meimad-y-slide", result.GetProperty("turretParent").GetString());
        Assert.Equal(["bed", "z-carriage", "x-slide", "turret", "meimad-y-slide"],
            result.GetProperty("placeholders").EnumerateArray().Select(value => value.GetString()!));
        Assert.False(result.GetProperty("chuck").GetBoolean());
        // X is a diameter: the turret tool point is at radius 30, Y 5, 30 mm in front of the spindle face.
        var turret = result.GetProperty("turret");
        Assert.Equal(30, turret.GetProperty("x").GetDouble(), 6);
        Assert.Equal(5, turret.GetProperty("y").GetDouble(), 6);
        Assert.Equal(-30, turret.GetProperty("z").GetDouble(), 6);
        // C90 turns the spindle a quarter turn about Z.
        var spindleX = result.GetProperty("spindleX");
        Assert.Equal(0, spindleX.GetProperty("x").GetDouble(), 6);
        Assert.Equal(1, Math.Abs(spindleX.GetProperty("y").GetDouble()), 6);
        Assert.Equal(-58, result.GetProperty("faceFromStock").GetDouble(), 6);
        Assert.Equal(-47, result.GetProperty("faceFromPath").GetDouble(), 6);
        Assert.False(result.GetProperty("noYSlide").GetBoolean());
    }

    private static (double, double, double, double) Tuple(JsonElement entry) =>
        (entry.GetProperty("c0").GetDouble(), entry.GetProperty("c1").GetDouble(), entry.GetProperty("y0").GetDouble(), entry.GetProperty("y1").GetDouble());

    private JsonElement Run(string body)
    {
        var json = (string)engine.Evaluate("JSON.stringify((function () { const L = MeimadLatheAxes; const K = CncKinematics; " + body + " })())");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public void Dispose() => engine.Dispose();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
