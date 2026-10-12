using System.IO;
using System.Text.Json;
using Meimad.Planner.Client.Windows.Presentation.ToolPreparation;
using Microsoft.ClearScript.V8;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>
/// The NC viewer's 3D stock simulation (NcViewer/meimad-voxel-core.js, meimad-voxel-mesh.js,
/// meimad-voxel-sim.js) run in V8: material removal by every parametric tool shape, motions that must
/// not tunnel or skip material, tilted and simultaneously turning tool axes, collisions with their NC
/// rows, the finished-part comparison, checkpoints, a closed STL and the adapter from the Tool Room's
/// ToolShapeGeometry to the cutting and collision profiles.
/// </summary>
public sealed class VoxelSimulationTests : IDisposable
{
    private const string Helpers = """
        const box = (x0, x1, y0, y1, z0, z1) => {
          const p = [[x0,y0,z0],[x1,y0,z0],[x1,y1,z0],[x0,y1,z0],[x0,y0,z1],[x1,y0,z1],[x1,y1,z1],[x0,y1,z1]];
          const f = [[0,2,1],[0,3,2],[4,5,6],[4,6,7],[0,1,5],[0,5,4],[1,2,6],[1,6,5],[2,3,7],[2,7,6],[3,0,4],[3,4,7]];
          const out = []; for (const t of f) for (const i of t) out.push(...p[i]); return new Float32Array(out);
        };
        const at = (g, x, y, z) => V.voxel(g, Math.floor((x - g.ox) / g.cell), Math.floor((y - g.oy) / g.cell), Math.floor((z - g.oz) / g.cell));
        const cut = (g, tool, points, axis) => { let n = 0; for (const s of V.motionSweeps(points, axis, tool.reach, g.cell / 2)) n += V.removeSweep(g, V.sweep(s.a, s.b, s.u), tool.cutting); return n; };
        const block = () => V.createStock({ minX: 0, maxX: 40, minY: 0, maxY: 40, minZ: -20, maxZ: 0 }, 0.5);
        """;

    private readonly V8ScriptEngine engine = new();

    public VoxelSimulationTests()
    {
        foreach (var file in new[] { "meimad-voxel-core.js", "meimad-voxel-mesh.js", "meimad-voxel-sim.js" })
        {
            engine.Execute(File.ReadAllText(Path.Combine(RepositoryRoot(), "client-windows", "Meimad.Planner.Client.Windows", "NcViewer", file)));
        }
    }

    [Fact]
    public void Flat_end_mill_slot_removes_exactly_its_swept_volume()
    {
        var result = Run("""
            const g = block();
            const tool = V.profileFromDefinition({ radius: 4, type: "end-mill", length: 40 });
            const removed = cut(g, tool, [{ x: -10, y: 20, z: -5 }, { x: 50, y: 20, z: -5 }]);
            return { volume: removed * g.cell ** 3, inSlot: at(g, 20, 20, -2), below: at(g, 20, 20, -5.5), wall: at(g, 20, 24.5, -2), initial: g.initialMaterial * g.cell ** 3 };
            """);
        Assert.Equal(40 * 40 * 20, result.GetProperty("initial").GetDouble(), 0);
        // 8 wide, 5 deep, 40 long (to the voxel grid).
        Assert.InRange(result.GetProperty("volume").GetDouble(), 8 * 5 * 40 * 0.97, 8 * 5 * 40 * 1.03);
        Assert.Equal(2, result.GetProperty("inSlot").GetInt32());
        Assert.Equal(1, result.GetProperty("below").GetInt32());
        Assert.Equal(1, result.GetProperty("wall").GetInt32());
    }

    [Fact]
    public void Ball_bull_nose_taper_and_drill_leave_their_own_shapes()
    {
        var result = Run("""
            const plunge = (tool) => { const g = block(); cut(g, tool, [{ x: 20, y: 20, z: 5 }, { x: 20, y: 20, z: -10 }]); return g; };
            const ball = plunge(V.profileFromDefinition({ radius: 5, type: "ball-mill", length: 40 }));
            const bull = plunge(V.profileFromDefinition({ radius: 5, type: "bull-nose-mill", cornerRadius: 2, length: 40 }));
            const drill = plunge(V.profileFromDefinition({ radius: 5, type: "drill", pointAngle: 90, length: 40 }));
            const taper = plunge(V.profileFromDefinition({ radius: 5, type: "chamfer-mill", taperAngle: 45, length: 40 }));
            return {
              // ball: centre at -5 from the tip at -10; 4 mm off-axis the bottom rises by 5 - 3 = 2.
              ballCentre: at(ball, 20, 20, -9.8), ballOff: at(ball, 24, 20, -9.2), ballOffAbove: at(ball, 24, 20, -6.5),
              // bull nose 2 mm corner: 1 mm in from the edge (r 4) the bottom rises by 2 - sqrt(4 - 1) ≈ 0.27.
              bullFlat: at(bull, 22.9, 20, -9.8), bullCorner: at(bull, 24.7, 20, -9.4), bullCornerAbove: at(bull, 24.7, 20, -8.3),
              // 90° drill point: the cone reaches radius r at 5 mm above the tip.
              drillTip: at(drill, 20, 20, -9.4), drillSide: at(drill, 23, 20, -8), drillSideAbove: at(drill, 23, 20, -6.5),
              taperSide: at(taper, 23, 20, -8), taperSideAbove: at(taper, 23, 20, -6.5)
            };
            """);
        Assert.Equal(2, result.GetProperty("ballCentre").GetInt32());
        Assert.Equal(1, result.GetProperty("ballOff").GetInt32());
        Assert.Equal(2, result.GetProperty("ballOffAbove").GetInt32());
        Assert.Equal(2, result.GetProperty("bullFlat").GetInt32());
        Assert.Equal(1, result.GetProperty("bullCorner").GetInt32());
        Assert.Equal(2, result.GetProperty("bullCornerAbove").GetInt32());
        Assert.Equal(2, result.GetProperty("drillTip").GetInt32());
        Assert.Equal(1, result.GetProperty("drillSide").GetInt32());
        Assert.Equal(2, result.GetProperty("drillSideAbove").GetInt32());
        Assert.Equal(1, result.GetProperty("taperSide").GetInt32());
        Assert.Equal(2, result.GetProperty("taperSideAbove").GetInt32());
    }

    [Fact]
    public void Long_moves_do_not_tunnel_and_equal_the_same_path_in_short_moves()
    {
        var result = Run("""
            const tool = V.profileFromDefinition({ radius: 2, type: "end-mill", length: 40 });
            const one = block();
            cut(one, tool, [{ x: -5, y: 5, z: -3 }, { x: 45, y: 35, z: -8 }]);
            const many = block();
            const pts = []; for (let n = 0; n <= 50; n++) pts.push({ x: -5 + n, y: 5 + n * 0.6, z: -3 - n * 0.1 });
            cut(many, tool, pts);
            return { one: one.removed, many: many.removed, middle: at(one, 20, 20, -5) };
            """);
        Assert.Equal(result.GetProperty("one").GetInt32(), result.GetProperty("many").GetInt32());
        Assert.Equal(2, result.GetProperty("middle").GetInt32());
    }

    [Fact]
    public void Tilted_and_turning_tools_cut_along_their_axis_and_from_below()
    {
        var result = Run("""
            const tool = V.profileFromDefinition({ radius: 3, type: "end-mill", length: 60 });
            const s = Math.SQRT1_2;
            const tilted = block();
            cut(tilted, tool, [{ x: 10, y: -5, z: -12 }, { x: 10, y: 45, z: -12 }], { x: s, y: 0, z: s });
            // From below: the tool axis points down (-Z), tip at -15 under the stock... reaching up.
            const below = V.createStock({ minX: 0, maxX: 40, minY: 0, maxY: 40, minZ: -20, maxZ: 0 }, 0.5);
            cut(below, tool, [{ x: 20, y: -5, z: -16 }, { x: 20, y: 45, z: -16 }], { x: 0, y: 0, z: -1 });
            // A tool turning from vertical to horizontal along one move.
            const turning = block();
            cut(turning, tool, [{ x: 5, y: 20, z: -6 }, { x: 35, y: 20, z: -6 }], [{ x: 0, y: 0, z: 1 }, { x: 1, y: 0, z: 0 }]);
            return {
              alongAxis: at(tilted, 18, 20, -4), behind: at(tilted, 4, 20, -12), underTip: at(tilted, 10, 20, -15),
              belowCut: at(below, 20, 20, -18), belowAbove: at(below, 20, 20, -14),
              turnStart: at(turning, 5, 20, -2), turnEnd: at(turning, 38, 20, -6), turnEndBelow: at(turning, 38, 20, -10.5)
            };
            """);
        Assert.Equal(2, result.GetProperty("alongAxis").GetInt32());
        Assert.Equal(1, result.GetProperty("behind").GetInt32());
        Assert.Equal(1, result.GetProperty("underTip").GetInt32());
        Assert.Equal(2, result.GetProperty("belowCut").GetInt32());
        Assert.Equal(1, result.GetProperty("belowAbove").GetInt32());
        Assert.Equal(2, result.GetProperty("turnStart").GetInt32());
        Assert.Equal(2, result.GetProperty("turnEnd").GetInt32());
        Assert.Equal(1, result.GetProperty("turnEndBelow").GetInt32());
    }

    [Fact]
    public void An_undercut_stays_under_the_material_above_it()
    {
        var result = Run("""
            const g = block();
            const tool = V.profileFromDefinition({ radius: 2, type: "end-mill", length: 50 });
            // A horizontal tool from the side cuts a slot at z = -10 under the untouched top.
            cut(g, tool, [{ x: -60, y: 20, z: -10 }, { x: 25, y: 20, z: -10 }], { x: -1, y: 0, z: 0 });
            const surface = M.triangles(g);
            return { slot: at(g, 10, 20, -10), top: at(g, 10, 20, -2), floor: at(g, 10, 20, -15), triangles: surface.length / 9 };
            """);
        Assert.Equal(2, result.GetProperty("slot").GetInt32());
        Assert.Equal(1, result.GetProperty("top").GetInt32());
        Assert.Equal(1, result.GetProperty("floor").GetInt32());
        Assert.True(result.GetProperty("triangles").GetInt32() > 1000);
    }

    [Fact]
    public void Surface_is_closed_and_its_volume_matches_the_material()
    {
        var result = Run("""
            const g = block();
            cut(g, V.profileFromDefinition({ radius: 3, type: "ball-mill", length: 40 }), [{ x: 5, y: 5, z: -4 }, { x: 35, y: 30, z: -9 }]);
            const t = M.triangles(g);
            const key = (i) => `${t[i].toFixed(4)},${t[i + 1].toFixed(4)},${t[i + 2].toFixed(4)}`;
            const edges = new Map();
            let volume = 0;
            for (let i = 0; i < t.length; i += 9) {
              const a = key(i), b = key(i + 3), c = key(i + 6);
              for (const [p, q] of [[a, b], [b, c], [c, a]]) edges.set(`${p}|${q}`, (edges.get(`${p}|${q}`) || 0) + 1);
              volume += (t[i] * (t[i + 4] * t[i + 8] - t[i + 5] * t[i + 7]) - t[i + 1] * (t[i + 3] * t[i + 8] - t[i + 5] * t[i + 6]) + t[i + 2] * (t[i + 3] * t[i + 7] - t[i + 4] * t[i + 6])) / 6;
            }
            let open = 0;
            for (const [edge, count] of edges) { const [p, q] = edge.split("|"); if ((edges.get(`${q}|${p}`) || 0) !== count) open++; }
            return { open, volume, material: (g.initialMaterial - g.removed) * g.cell ** 3 };
            """);
        Assert.Equal(0, result.GetProperty("open").GetInt32());
        var material = result.GetProperty("material").GetDouble();
        Assert.InRange(result.GetProperty("volume").GetDouble(), material * 0.99, material * 1.01);
    }

    [Fact]
    public void Collisions_report_their_rows_once_per_contact_and_rapids_do_not_cut()
    {
        var result = Run("""
            const assembly = { totalLength: 90, segments: [
              { kind: "HOLDER", top: 0, height: 40, diameter: 40 },
              { kind: "CYLINDER", top: 40, height: 30, diameter: 10, label: "Shank" },
              { kind: "CUTTER", top: 70, height: 20, diameter: 10 } ] };
            const motions = [
              { tool: 1, cutting: true, points: [{ x: -10, y: 20, z: -5 }, { x: 60, y: 20, z: -5 }], line: 10 },
              { tool: 1, cutting: true, points: [{ x: -10, y: 40, z: -25 }, { x: 25, y: 40, z: -25 }], line: 11 },
              { tool: 1, cutting: true, points: [{ x: 25, y: 40, z: -25 }, { x: 60, y: 40, z: -25 }], line: 12 },
              { tool: 1, rapid: true, points: [{ x: 25, y: 5, z: -3 }, { x: 25, y: 45, z: -3 }], line: 13 },
              { tool: 1, cutting: true, points: [{ x: 20, y: -20, z: -12 }, { x: 20, y: -5, z: -12 }], line: 14 },
              { tool: 1, cutting: true, points: [{ x: 5, y: 10, z: -2 }, { x: 5, y: 30, z: -2 }], line: 15 }
            ];
            const sim = S.create({
              stock: { minX: 0, maxX: 50, minY: 0, maxY: 50, minZ: -30, maxZ: 0 }, resolution: 0.5,
              assemblies: { 1: assembly }, tools: {}, motions,
              fixtures: [{ name: "Vise jaw", positions: box(0, 50, -15, -2, -40, -8) }],
              target: box(0, 50, 0, 50, -30, -10),
              extent: { minX: -20, maxX: 70, minY: -25, maxY: 60, minZ: -45, maxZ: 10 }
            });
            const removedBeforeRapid = (() => { sim.cutTo(2, 1); return sim.grid.removed; })();
            sim.cutTo(3, 1);
            const afterRapid = sim.grid.removed;
            sim.cutTo(5, 1);
            const events = sim.events.map((e) => ({ check: e.check, line: e.line, first: e.executionIndex, last: e.lastIndex, severity: e.severity }));
            const removedAll = sim.grid.removed;
            sim.cutTo(1, 1);
            sim.cutTo(5, 1);
            return { events, removedBeforeRapid, afterRapid, again: sim.grid.removed === removedAll, eventsAgain: sim.events.length };
            """);
        var events = result.GetProperty("events").EnumerateArray().Select(e => (Check: e.GetProperty("check").GetString(), Line: e.GetProperty("line").GetInt32(),
            First: e.GetProperty("first").GetInt32(), Last: e.GetProperty("last").GetInt32(), Severity: e.GetProperty("severity").GetString())).ToArray();
        // The shank goes 5 mm into the stock over two consecutive moves: one event from row 11 to 12.
        Assert.Contains(events, e => e is { Check: "holderStock", Line: 11, First: 1, Last: 2, Severity: "error" });
        Assert.Single(events, e => e.Check == "holderStock");
        Assert.Contains(events, e => e is { Check: "rapidStock", Line: 13 });
        Assert.Contains(events, e => e is { Check: "toolFixture", Line: 14, Severity: "stop" });
        // Row 11 goes down to -25 inside the finished part (z < -10): a gouge; row 10 at -5 does not.
        Assert.Contains(events, e => e is { Check: "gouge", Line: 11 });
        Assert.DoesNotContain(events, e => e.Check == "gouge" && e.Line == 10);
        // Cutting the stock with the cutter is not an event, and the rapid removes nothing.
        Assert.DoesNotContain(events, e => e.Check == "holderStock" && e.Line == 10);
        Assert.Equal(result.GetProperty("removedBeforeRapid").GetInt32(), result.GetProperty("afterRapid").GetInt32());
        Assert.True(result.GetProperty("again").GetBoolean());
        Assert.Equal(events.Length, result.GetProperty("eventsAgain").GetInt32());
    }

    [Fact]
    public void Finished_part_comparison_reports_gouges_and_material_left()
    {
        var result = Run("""
            const sim = S.create({
              stock: { minX: 0, maxX: 40, minY: 0, maxY: 40, minZ: -20, maxZ: 0 }, resolution: 0.5,
              tools: { 1: { diameter: 6, type: "end-mill", length: 40 } },
              motions: [{ tool: 1, cutting: true, points: [{ x: -5, y: 20, z: -12 }, { x: 45, y: 20, z: -12 }], line: 20 }],
              target: box(0, 40, 0, 40, -20, -10)
            });
            sim.cutTo(0, 1);
            return sim.deviation(10);
            """);
        Assert.True(result.GetProperty("gougeVoxelCount").GetInt32() > 0);
        Assert.Equal(20, result.GetProperty("firstGougeRow").GetInt32());
        Assert.InRange(result.GetProperty("maxGouge").GetDouble(), 1.0, 3.5); // 2 mm into the part
        Assert.True(result.GetProperty("remainingVoxelCount").GetInt32() > 0);
        Assert.InRange(result.GetProperty("maxRemainingStock").GetDouble(), 8.0, 11.0); // 10 mm left above the part
    }

    [Fact]
    public void Tool_room_drawing_becomes_cutting_and_collision_profiles()
    {
        var geometry = ToolShapeBuilder.Build(
            "BALL_END_MILL",
            new Dictionary<string, double> { ["cuttingDiameter"] = 10, ["fluteLength"] = 25, ["overallLength"] = 75 },
            [new ToolShapeComponent("HOLDER", "ER32", 60, 40)],
            measuredLength: null,
            measuredDiameter: null);
        var json = JsonSerializer.Serialize(new
        {
            totalLength = geometry.TotalLength,
            shapeType = "BALL_END_MILL",
            segments = geometry.Segments.Select(s => new { kind = s.Kind, top = s.Top, height = s.Height, diameter = s.Diameter, label = s.Label, tipAngle = s.TipAngle, cornerRadius = s.CornerRadius, isDefault = s.IsDefault })
        });
        engine.Script.assemblyJson = json;
        var result = Run("""
            const p = V.profileFromAssembly(JSON.parse(assemblyJson));
            const cutting = V.extentOf(p.cutting), collision = V.extentOf(p.collision);
            const fromTable = V.profileFromDefinition({ radius: 4, type: "end-mill" });
            return { cutLo: cutting.sLo, cutHi: cutting.sHi, cutR: cutting.r, colLo: collision.sLo, colHi: collision.sHi, colR: collision.r,
              reach: p.reach, ball: p.cutting.some((piece) => piece.kind === "sphere"), notes: p.notes, tableNotes: fromTable.notes, tableCollision: fromTable.collision.length };
            """);
        Assert.Equal(0, result.GetProperty("cutLo").GetDouble(), 6);
        Assert.Equal(25, result.GetProperty("cutHi").GetDouble(), 6);  // the flutes cut
        Assert.Equal(5, result.GetProperty("cutR").GetDouble(), 6);
        Assert.True(result.GetProperty("ball").GetBoolean());
        Assert.Equal(25, result.GetProperty("colLo").GetDouble(), 6); // the shank and holder above them do not
        Assert.Equal(geometry.TotalLength, result.GetProperty("colHi").GetDouble(), 6);
        Assert.Equal(20, result.GetProperty("colR").GetDouble(), 6);  // the holder
        Assert.Equal(geometry.TotalLength, result.GetProperty("reach").GetDouble(), 6);
        // A tool known only from the tool table says what is estimated and checks no holder.
        Assert.Contains(result.GetProperty("tableNotes").EnumerateArray(), note => note.GetString()!.Contains("holder unknown"));
        Assert.Equal(0, result.GetProperty("tableCollision").GetInt32());
    }

    [Fact]
    public void Same_program_gives_the_same_stock_every_time()
    {
        var result = Run("""
            const once = () => {
              const g = block();
              const tool = V.profileFromDefinition({ radius: 3, type: "bull-nose-mill", cornerRadius: 1, length: 40 });
              for (let n = 0; n < 12; n++) cut(g, tool, [{ x: -5, y: 3 + n * 3, z: -2 - n * 0.5 }, { x: 45, y: 3 + n * 3, z: -2 - n * 0.5 }]);
              let hash = 0;
              for (let k = 0; k < g.nz; k += 3) for (let j = 0; j < g.ny; j += 3) for (let i = 0; i < g.nx; i += 3) hash = (hash * 31 + V.voxel(g, i, j, k)) >>> 0;
              return [g.removed, hash];
            };
            const a = once(), b = once();
            return { same: a[0] === b[0] && a[1] === b[1] };
            """);
        Assert.True(result.GetProperty("same").GetBoolean());
    }

    private JsonElement Run(string body)
    {
        var json = (string)engine.Evaluate("JSON.stringify((function () { const V = MeimadVoxel; const M = MeimadVoxelMesh; const S = MeimadVoxelSim; " + Helpers + body + " })())");
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
