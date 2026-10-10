using System.IO;
using System.Text.Json;
using Meimad.Planner.Client.Windows.Presentation.NcViewer;
using Microsoft.ClearScript.V8;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>
/// The NC viewer's axis travel calculation (NcViewer/meimad-axes-core.js) and setup-scene placement
/// (NcViewer/meimad-scene-core.js) run in V8 with the vendored kinematics module the page uses.
/// </summary>
public sealed class NcViewerAxesAndSceneCoreTests : IDisposable
{
    private readonly V8ScriptEngine engine = new();

    // A 3-axis mill: X -1016..0, Y -508..0, Z -635..0, B -35..120 (not on the chain), C continuous.
    private const string MillModel = """
        const mill = (segments, extra) => Object.assign({
          kind: "mill",
          frame: { x: -500, y: -250, z: -400, label: "G54" },
          view: { unitScale: 1 },
          meta: { defaultedWorkOffsets: [] },
          machineDefinition: {
            id: "test-mill",
            axes: {
              X: { type: "linear", min: -1016, max: 0 },
              Y: { type: "linear", min: -508, max: 0 },
              Z: { type: "linear", min: -635, max: 0 },
              B: { type: "rotary", min: -35, max: 120, wrap: false },
              C: { type: "rotary", min: null, max: null, wrap: true }
            },
            kinematics: { nodes: [
              { id: "base", label: "Base" },
              { id: "table", parent: "base", type: "fixed", workpieceMount: true }
            ] }
          },
          segments
        }, extra || {});
        const segments = [
          { line: 3, kind: "rapid", points: [{ x: 0, y: 0, z: 100 }, { x: 0, y: 0, z: 5 }], rotary: {}, toolLength: 100 },
          { line: 4, kind: "feed", points: [{ x: -10, y: 20, z: -5 }, { x: 600, y: 20, z: -5 }], rotary: { b0: 0, b1: 130, c0: 0, c1: 720 }, toolLength: 100 }
        ];
        """;

    public NcViewerAxesAndSceneCoreTests()
    {
        var root = RepositoryRoot();
        engine.Execute(File.ReadAllText(Path.Combine(root, "third_party", "chevalier-nc-viewer", "media", "kinematics.js")));
        foreach (var file in new[] { "meimad-axes-core.js", "meimad-scene-core.js" })
        {
            engine.Execute(File.ReadAllText(Path.Combine(root, "client-windows", "Meimad.Planner.Client.Windows", "NcViewer", file)));
        }
    }

    [Fact]
    public void Travel_ranges_report_the_reached_machine_positions_and_the_axis_outside_its_travel()
    {
        var result = Run(MillModel + """
            const r = A.travelRanges(mill(segments), K);
            const row = (axis) => r.rows.find((value) => value.axis === axis);
            return { kind: r.kind, outside: r.outside, unknown: r.positionsUnknown, order: r.rows.map((value) => value.axis), x: row("X"), y: row("Y"), z: row("Z"), b: row("B"), c: row("C") };
            """);
        Assert.Equal("mill", result.GetProperty("kind").GetString());
        Assert.Equal(["X", "Y", "Z", "B", "C"], result.GetProperty("order").EnumerateArray().Select(value => value.GetString()!));
        var x = result.GetProperty("x");
        Assert.Equal(-510, x.GetProperty("min").GetDouble(), 6);
        Assert.Equal(100, x.GetProperty("max").GetDouble(), 6);   // 600 + frame -500: past the X+ limit 0
        Assert.Equal("outside", x.GetProperty("status").GetString());
        Assert.Equal(100, x.GetProperty("overBy").GetDouble(), 6);
        Assert.Equal(4, x.GetProperty("overLine").GetInt32());
        var y = result.GetProperty("y");
        Assert.Equal(-250, y.GetProperty("min").GetDouble(), 6);
        Assert.Equal(-230, y.GetProperty("max").GetDouble(), 6);
        Assert.Equal("inside", y.GetProperty("status").GetString());
        var z = result.GetProperty("z");
        Assert.Equal(-305, z.GetProperty("min").GetDouble(), 6);  // tip -5 + frame -400 + tool length 100
        Assert.Equal(-200, z.GetProperty("max").GetDouble(), 6);  // the rapid at Z100
        Assert.Equal(3, z.GetProperty("maxLine").GetInt32());
        var b = result.GetProperty("b");
        Assert.Equal("outside", b.GetProperty("status").GetString());
        Assert.Equal(10, b.GetProperty("overBy").GetDouble(), 6);
        Assert.Equal("no-limit", result.GetProperty("c").GetProperty("status").GetString());
        Assert.Equal(2, result.GetProperty("outside").GetInt32());
        Assert.False(result.GetProperty("unknown").GetBoolean());
    }

    [Fact]
    public void Rapids_can_be_left_out_and_placeholder_work_offsets_are_flagged()
    {
        var result = Run(MillModel + """
            const withoutRapids = A.travelRanges(mill(segments), K, { includeRapids: false });
            const placeholder = A.travelRanges(mill(segments, { meta: { defaultedWorkOffsets: ["G54"] } }), K);
            return {
              zMax: withoutRapids.rows.find((value) => value.axis === "Z").max,
              unknown: placeholder.positionsUnknown,
              offsets: placeholder.defaultedWorkOffsets
            };
            """);
        Assert.Equal(-305, result.GetProperty("zMax").GetDouble(), 6);
        Assert.True(result.GetProperty("unknown").GetBoolean());
        Assert.Equal("G54", result.GetProperty("offsets")[0].GetString());
    }

    [Fact]
    public void Pose_joints_match_the_range_calculation_and_a_lathe_reports_program_diameter()
    {
        var result = Run(MillModel + """
            const model = mill(segments);
            const pose = A.poseJoints(model, K, { tip: { x: 10, y: 20, z: -5 }, rotary: { A: 0, B: 15, C: 0 }, length: 100 });
            const lathe = A.travelRanges({ kind: "lathe", segments: [{ line: 7, points: [{ x: 40, r: 20, z: -10 }, { x: 60, r: 30, z: 2 }] }] }, K);
            return { pose, lathe };
            """);
        var pose = result.GetProperty("pose");
        Assert.Equal(-490, pose.GetProperty("X").GetDouble(), 6);
        Assert.Equal(-230, pose.GetProperty("Y").GetDouble(), 6);
        Assert.Equal(-305, pose.GetProperty("Z").GetDouble(), 6);
        Assert.Equal(15, pose.GetProperty("B").GetDouble(), 6);
        var lathe = result.GetProperty("lathe");
        Assert.Equal("lathe", lathe.GetProperty("kind").GetString());
        var rows = lathe.GetProperty("rows");
        Assert.Equal("X", rows[0].GetProperty("axis").GetString());
        Assert.Equal(40, rows[0].GetProperty("min").GetDouble(), 6);
        Assert.Equal(60, rows[0].GetProperty("max").GetDouble(), 6);
        Assert.Equal("no-limit", rows[0].GetProperty("status").GetString());
        Assert.Equal("Z", rows[1].GetProperty("axis").GetString());
        Assert.Equal(0, lathe.GetProperty("outside").GetInt32());
    }

    [Fact]
    public void A_table_that_travels_in_x_and_y_carries_the_part_and_the_tool_stays_on_the_toolpath()
    {
        var result = Run("""
            // Physical C-frame VMC: Y saddle and X table carry the workpiece, Z head on the column.
            const model = {
              kind: "mill", frame: { x: -500, y: -250, z: -400 }, view: { unitScale: 1 },
              machineDefinition: { axes: { X: {}, Y: {}, Z: {} }, kinematics: { nodes: [
                { id: "base" },
                { id: "z-head", parent: "base", type: "linear", axis: "Z", direction: [0, 0, 1] },
                { id: "spindle", parent: "z-head", type: "spindle", toolMount: true },
                { id: "y-saddle", parent: "base", type: "linear", axis: "Y", direction: [0, -1, 0] },
                { id: "x-table", parent: "y-saddle", type: "linear", axis: "X", direction: [-1, 0, 0], workpieceMount: true }
              ] } }
            };
            const chain = A.chainFor(model, K);
            const pose = { tip: { x: 30, y: 40, z: -5 }, rotary: { A: 0, B: 0, C: 0 }, length: 100 };
            const joints = A.poseJoints(model, K, pose);
            const part = A.partMatrix(model, K, joints, chain);
            const tipWorld = K.transformPoint(part, pose.tip);
            const world = K.evaluateChain(chain, joints);
            const gauge = K.transformPoint(world.get("spindle"), { x: 0, y: 0, z: 0 });
            const table = K.transformPoint(world.get("x-table"), { x: 0, y: 0, z: 0 });
            const spindleOnly = A.chainFor(Object.assign({}, model, { machineDefinition: { axes: {}, kinematics: { nodes: [
              { id: "base" }, { id: "x", parent: "base", type: "linear", axis: "X", direction: [1, 0, 0] }, { id: "spindle", parent: "x", type: "spindle", toolMount: true },
              { id: "table", parent: "base", type: "fixed", workpieceMount: true } ] } } }), K);
            return { joints, tipWorld, gauge, table, moves: A.workpieceMovesLinearly(chain), spindleOnlyMoves: A.workpieceMovesLinearly(spindleOnly) };
            """);
        var joints = result.GetProperty("joints");
        Assert.Equal(-470, joints.GetProperty("X").GetDouble(), 6);   // machine coordinates as before
        Assert.Equal(-210, joints.GetProperty("Y").GetDouble(), 6);
        Assert.Equal(-305, joints.GetProperty("Z").GetDouble(), 6);
        // The table (and the part on it) moved -X/-Y; the spindle only moved in Z.
        AssertPoint(result.GetProperty("table"), 470, 210, 0);
        AssertPoint(result.GetProperty("gauge"), 0, 0, -305);
        // The tool tip sits one tool length below the gauge line, exactly on the moved toolpath point.
        AssertPoint(result.GetProperty("tipWorld"), 0, 0, -405);
        Assert.True(result.GetProperty("moves").GetBoolean());
        Assert.False(result.GetProperty("spindleOnlyMoves").GetBoolean());
    }

    [Fact]
    public void Work_cs_maps_cimatron_model_coordinates_into_program_coordinates()
    {
        var result = Run("""
            const workCs = { workOffset: "G54", originMm: { x: 10, y: 20, z: 30 }, rotationDeg: { x: 0, y: 0, z: 90 } };
            const onAxis = S.modelPointToWork({ x: 10, y: 21, z: 30 }, workCs);
            const tilted = S.modelPointToWork({ x: 0, y: 0, z: 1 }, { originMm: { x: 0, y: 0, z: 0 }, rotationDeg: { x: 90, y: 0, z: 0 } });
            const drawn = S.modelToDrawing([10, 21, 30], workCs, { scale: 0.5, shift: { x: 100, y: 0, z: 0 } });
            const none = S.modelToDrawing([1, 2, 3], null);
            const node = S.placeOnNode([1, 0, 0], { offsetMm: { x: 5, y: 0, z: 0 }, rotationDeg: { x: 0, y: 0, z: 90 } }, 1);
            return { onAxis, tilted, drawn: Array.from(drawn), none: Array.from(none), node: Array.from(node) };
            """);
        // The Work CS X axis points along model Y, so one millimetre along model Y is Work CS X+1.
        AssertPoint(result.GetProperty("onAxis"), 1, 0, 0);
        // Rotated 90 degrees about X: the Work CS Y axis is model Z.
        AssertPoint(result.GetProperty("tilted"), 0, 1, 0);
        AssertArray(result.GetProperty("drawn"), 100.5, 0, 0);
        AssertArray(result.GetProperty("none"), 1, 2, 3);
        AssertArray(result.GetProperty("node"), 5, 1, 0);
    }

    [Fact]
    public void Work_offset_shift_and_file_kinds_follow_the_stock_rules()
    {
        var result = Run("""
            const model = { kind: "mill", frame: { label: "G54" }, workOffsetTable: { rows: [
              { label: "G54", values: { X: { saved: -500 }, Y: { initial: -250 }, Z: { saved: -400 } } },
              { label: "G55", values: { X: { saved: -400 }, Y: { initial: -250 }, Z: { saved: -410 } } }
            ] } };
            return {
              g55: S.offsetShift(model, "g55"),
              same: S.offsetShift(model, "G54"),
              unknown: S.offsetShift(model, "G59"),
              kinds: ["J:\\DPD\\30P\\NC\\STOCK_OP1.stl", "J:\\DPD\\1199\\NC\\STOCK\\FIN_GAS_1.stl", "J:\\DPD\\vise.stp", "C:\\x\\rest_op2.STL"].map(S.guessKind)
            };
            """);
        AssertPoint(result.GetProperty("g55"), 100, 0, -10);
        AssertPoint(result.GetProperty("same"), 0, 0, 0);
        AssertPoint(result.GetProperty("unknown"), 0, 0, 0);
        Assert.Equal(["stock", "stock", "fixture", "stock"], result.GetProperty("kinds").EnumerateArray().Select(value => value.GetString()!));
    }

    [Theory]
    [InlineData(@"J:\DPD\30P\NC\STOCK_OP1.stl", "stock")]
    [InlineData(@"J:\DPD\1199\NC\STOCK\FIN_GAS_1.stl", "stock")]
    [InlineData(@"J:\DPD\vise.stp", "fixture")]
    [InlineData(@"C:\x\rest_op2.STL", "stock")]
    public void Host_guesses_the_same_kind_as_the_page(string path, string kind) =>
        Assert.Equal(kind, NcViewerScenes.GuessKind(path));

    private static void AssertPoint(JsonElement point, double x, double y, double z)
    {
        Assert.Equal(x, point.GetProperty("x").GetDouble(), 5);
        Assert.Equal(y, point.GetProperty("y").GetDouble(), 5);
        Assert.Equal(z, point.GetProperty("z").GetDouble(), 5);
    }

    private static void AssertArray(JsonElement values, double x, double y, double z)
    {
        Assert.Equal(x, values[0].GetDouble(), 4);
        Assert.Equal(y, values[1].GetDouble(), 4);
        Assert.Equal(z, values[2].GetDouble(), 4);
    }

    private JsonElement Run(string body)
    {
        var json = (string)engine.Evaluate(
            "JSON.stringify((function () { const A = MeimadAxes; const S = MeimadScene; const K = CncKinematics; " + body + " })())");
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
