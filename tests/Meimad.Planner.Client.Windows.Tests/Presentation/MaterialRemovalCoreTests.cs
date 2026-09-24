using System.IO;
using System.Text.Json;
using Microsoft.ClearScript.V8;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>
/// The NC viewer's material removal core (NcViewer/meimad-material.js) run in V8, the same engine
/// the Server's NC analysis uses, so the page's stock cutting is checked without a browser.
/// </summary>
public sealed class MaterialRemovalCoreTests : IDisposable
{
    private readonly V8ScriptEngine engine = new();

    public MaterialRemovalCoreTests()
    {
        engine.Execute(File.ReadAllText(Path.Combine(RepositoryRoot(), "client-windows", "Meimad.Planner.Client.Windows", "NcViewer", "meimad-material.js")));
    }

    [Fact]
    public void Flat_end_mill_slot_cuts_exactly_the_swept_capsule()
    {
        var result = Run("""
            const s = M.createMillStock({ minX: 0, maxX: 20, minY: 0, maxY: 20, minZ: -10, maxZ: 0 }, 1);
            const changed = M.cutMillSegment(s, [{ x: 2, y: 10, z: -3 }, { x: 18, y: 10, z: -3 }], { radius: 2, type: "end-mill" });
            const at = (x, y) => s.top[y * s.nx + x];
            return { changed, nx: s.nx, slot: at(10, 10), edge: at(10, 12), outside: at(10, 7), corner: at(0, 0), endCap: at(0, 10), beyond: at(20, 10) };
            """);
        Assert.True(result.GetProperty("changed").GetBoolean());
        Assert.Equal(21, result.GetProperty("nx").GetInt32());
        Assert.Equal(-3, result.GetProperty("slot").GetDouble());
        Assert.Equal(-3, result.GetProperty("edge").GetDouble());   // the wall of the slot, 2 mm from the path
        Assert.Equal(0, result.GetProperty("outside").GetDouble());  // 3 mm away
        Assert.Equal(0, result.GetProperty("corner").GetDouble());
        Assert.Equal(-3, result.GetProperty("endCap").GetDouble()); // the tool's leading edge at the start
        Assert.Equal(-3, result.GetProperty("beyond").GetDouble());
    }

    [Fact]
    public void Ball_mills_and_ramps_follow_the_edge_geometry_and_a_tilted_end_mill_cuts_with_its_lead()
    {
        var result = Run("""
            const ball = M.createMillStock({ minX: 0, maxX: 20, minY: 0, maxY: 20, minZ: -10, maxZ: 0 }, 1);
            M.cutMillSegment(ball, [{ x: 10, y: 10, z: -3 }], { radius: 2, type: "ball-mill" });
            const ramp = M.createMillStock({ minX: 0, maxX: 20, minY: 0, maxY: 20, minZ: -10, maxZ: 0 }, 1);
            M.cutMillSegment(ramp, [{ x: 2, y: 10, z: 0 }, { x: 18, y: 10, z: -8 }], { radius: 1, type: "end-mill" });
            // A flat end mill tilted 30 degrees into its own motion: the trailing edge of the tip
            // sweeps the floor one radius times sin 30 deeper than the tip on the centre line.
            const tilted = M.createMillStock({ minX: 0, maxX: 20, minY: 0, maxY: 20, minZ: -10, maxZ: 0 }, 0.25);
            const tiltedChanged = M.cutMillSegment(tilted, [{ x: 2, y: 10, z: -3 }, { x: 18, y: 10, z: -3 }], { radius: 2, type: "end-mill", length: 30 }, { x: 0.5, y: 0, z: 0.866 });
            const col = (s, x, y) => s.top[Math.round((y - s.originY) / s.cell) * s.nx + Math.round((x - s.originX) / s.cell)];
            const floor = M.createMillStock({ minX: 0, maxX: 20, minY: 0, maxY: 20, minZ: -2, maxZ: 0 }, 1);
            M.cutMillSegment(floor, [{ x: 10, y: 10, z: -5 }], { radius: 3, type: "end-mill" });
            return {
              ballCenter: ball.top[10 * ball.nx + 10], ballNext: ball.top[10 * ball.nx + 11],
              rampMiddle: ramp.top[10 * ramp.nx + 10], rampEnd: ramp.top[10 * ramp.nx + 18],
              tiltedChanged, tiltedCentre: col(tilted, 10, 10), tiltedBeside: col(tilted, 10, 11), tiltedFar: col(tilted, 10, 13),
              floorCenter: floor.top[10 * floor.nx + 10], throughCut: floor.throughCut
            };
            """);
        Assert.Equal(-3, result.GetProperty("ballCenter").GetDouble(), 3);
        Assert.Equal(-3 + 2 - Math.Sqrt(3), result.GetProperty("ballNext").GetDouble(), 3);
        Assert.Equal(-4.5, result.GetProperty("rampMiddle").GetDouble(), 3);
        Assert.Equal(-8, result.GetProperty("rampEnd").GetDouble(), 3);
        Assert.True(result.GetProperty("tiltedChanged").GetBoolean());
        Assert.Equal(-4, result.GetProperty("tiltedCentre").GetDouble(), 0.12);
        Assert.Equal(-3 - Math.Sqrt(3) / 2, result.GetProperty("tiltedBeside").GetDouble(), 0.12);
        Assert.Equal(0, result.GetProperty("tiltedFar").GetDouble());
        // A cut below the stock bottom stops at the bottom and counts as a through cut.
        Assert.Equal(-2, result.GetProperty("floorCenter").GetDouble());
        Assert.True(result.GetProperty("throughCut").GetInt32() > 0);
    }

    [Fact]
    public void A_horizontal_tool_slots_the_side_leaves_an_undercut_slab_and_an_upside_down_tool_cuts_from_below()
    {
        var result = Run("""
            const box = () => M.createMillStock({ minX: 0, maxX: 20, minY: 0, maxY: 20, minZ: -10, maxZ: 0 }, 0.25);
            const index = (s, x, y) => Math.round((y - s.originY) / s.cell) * s.nx + Math.round((x - s.originX) / s.cell);
            const side = box();
            // Axis +Y: the tip faces -Y, the body reaches from y = 15 to y = 45 through the block's side.
            M.cutMillSegment(side, [{ x: 2, y: 15, z: -5 }, { x: 18, y: 15, z: -5 }], { radius: 2, type: "end-mill", length: 30 }, { x: 0, y: 1, z: 0 });
            const inSlot = index(side, 10, 17);
            const before = index(side, 10, 14);
            // Copies: the plunge below edits the column's slab list in place.
            const slot = { top: side.top[inSlot], base: side.base[inSlot], extras: [...(side.extras.get(inSlot) || [])], undercut: side.undercut };
            const untouched = { top: side.top[before], base: side.base[before], extras: [...(side.extras.get(before) || [])] };
            const triangles = M.millTriangles(side);
            let finite = true;
            for (let i = 0; i < triangles.length; i += 1) if (!Number.isFinite(triangles[i])) { finite = false; break; }
            const roundTrip = M.readStl(M.writeStl(triangles, "undercut")).length;
            // A vertical plunge through the slab above the slot merges the column again.
            M.cutMillSegment(side, [{ x: 10, y: 17, z: -8 }], { radius: 1, type: "end-mill" });
            const merged = { top: side.top[inSlot], base: side.base[inSlot], extras: side.extras.get(inSlot) || [] };
            const below = box();
            M.cutMillSegment(below, [{ x: 2, y: 10, z: -7 }, { x: 18, y: 10, z: -7 }], { radius: 2, type: "end-mill", length: 30 }, { x: 0, y: 0, z: -1 });
            const fromBelow = { top: below.top[index(below, 10, 10)], base: below.base[index(below, 10, 10)], extras: below.extras.get(index(below, 10, 10)) || [] };
            return { slot, untouched, triangles: triangles.length / 9, finite, roundTrip: roundTrip / 9, merged, fromBelow };
            """);
        var slot = result.GetProperty("slot");
        Assert.Equal(0, slot.GetProperty("top").GetDouble());
        Assert.Equal(-3, slot.GetProperty("base").GetDouble(), 0.05);
        var extras = slot.GetProperty("extras").EnumerateArray().Select(value => value.GetDouble()).ToArray();
        Assert.Equal(2, extras.Length);
        Assert.Equal(-10, extras[0]);
        Assert.Equal(-7, extras[1], 0.05);
        Assert.True(slot.GetProperty("undercut").GetBoolean());
        var untouched = result.GetProperty("untouched");
        Assert.Equal(0, untouched.GetProperty("top").GetDouble());
        Assert.Equal(-10, untouched.GetProperty("base").GetDouble());
        Assert.Empty(untouched.GetProperty("extras").EnumerateArray());
        Assert.True(result.GetProperty("triangles").GetInt32() > 0);
        Assert.True(result.GetProperty("finite").GetBoolean());
        Assert.Equal(result.GetProperty("triangles").GetInt32(), result.GetProperty("roundTrip").GetInt32());
        var merged = result.GetProperty("merged");
        Assert.Equal(-8, merged.GetProperty("top").GetDouble(), 3);
        Assert.Equal(-10, merged.GetProperty("base").GetDouble());
        Assert.Empty(merged.GetProperty("extras").EnumerateArray());
        var fromBelow = result.GetProperty("fromBelow");
        Assert.Equal(0, fromBelow.GetProperty("top").GetDouble());
        Assert.Equal(-7, fromBelow.GetProperty("base").GetDouble(), 0.05);
        Assert.Empty(fromBelow.GetProperty("extras").EnumerateArray());
    }

    [Fact]
    public void Chamfer_ball_drill_and_turning_axes_follow_the_tilted_tool()
    {
        var result = Run("""
            const box = () => M.createMillStock({ minX: 0, maxX: 20, minY: 0, maxY: 20, minZ: -10, maxZ: 0 }, 0.25);
            const index = (s, x, y) => Math.round((y - s.originY) / s.cell) * s.nx + Math.round((x - s.originX) / s.cell);
            const col = (s, x, y) => s.top[index(s, x, y)];
            const u45 = { x: Math.SQRT1_2, y: 0, z: Math.SQRT1_2 };
            // A flat end mill at 45 degrees along the x = 0 edge: the column through the axis meets a chord of 2r/sin 45.
            const chamfer = box();
            M.cutMillSegment(chamfer, [{ x: 0, y: 2, z: -2 }, { x: 0, y: 18, z: -2 }], { radius: 2, type: "end-mill", length: 30 }, u45);
            // A ball mill plunging along its own 45-degree axis: the ball's lowest point lies r/sqrt2 beyond the tip.
            const ball = box();
            M.cutMillSegment(ball, [{ x: 10 + 6 * u45.x, y: 10, z: -3 + 6 * u45.z }, { x: 10, y: 10, z: -3 }], { radius: 2, type: "ball-mill", length: 30 }, u45);
            const tipColumn = index(ball, 10, 10);
            // A drill along a 30-degree axis: the point reaches the tip depth exactly on the tip column.
            const u30 = { x: 0.5, y: 0, z: 0.8660254 };
            const drill = box();
            M.cutMillSegment(drill, [{ x: 10 + 6 * u30.x, y: 10, z: -3 + 6 * u30.z }, { x: 10, y: 10, z: -3 }], { radius: 2, type: "drill", length: 30 }, u30);
            // The axis turning during one move: vertical at the start, 30 degrees at the end.
            const turning = box();
            const turned = M.cutMillSegment(turning, [{ x: 2, y: 10, z: -3 }, { x: 18, y: 10, z: -3 }], { radius: 2, type: "end-mill", length: 30 }, [{ x: 0, y: 0, z: 1 }, u30]);
            return {
              chamfer: [col(chamfer, 0, 10), col(chamfer, 2, 10), col(chamfer, 4, 10), col(chamfer, 6, 10)],
              ballLow: col(ball, 11.5, 10), ballTipTop: ball.top[tipColumn], ballTipBase: ball.base[tipColumn], ballTipExtras: ball.extras.get(tipColumn) || [],
              drillTip: col(drill, 10, 10), drillBeside: col(drill, 11, 10), drillClear: col(drill, 6, 10),
              turned, turningStart: col(turning, 3, 10), turningEnd: col(turning, 16, 10)
            };
            """);
        var chamfer = result.GetProperty("chamfer").EnumerateArray().Select(value => value.GetDouble()).ToArray();
        Assert.Equal(-2, chamfer[0], 0.05);
        Assert.Equal(-2 * Math.Sqrt(2), chamfer[1], 0.05);
        Assert.Equal(2 - 2 * Math.Sqrt(2), chamfer[2], 0.05);
        Assert.Equal(0, chamfer[3]);
        // Ball centre at tip + r * axis = (10 + sqrt2, 10, -3 + sqrt2); the column x = 11.5 meets its underside.
        Assert.Equal(-3 + Math.Sqrt(2) - Math.Sqrt(4 - Math.Pow(11.5 - 10 - Math.Sqrt(2), 2)), result.GetProperty("ballLow").GetDouble(), 0.05);
        // On the tip column the ball and the body only reach r / sin 45 above the tip: a skin stays above.
        Assert.Equal(0, result.GetProperty("ballTipTop").GetDouble());
        Assert.Equal(-3 + 2 * Math.Sqrt(2), result.GetProperty("ballTipBase").GetDouble(), 0.05);
        var ballExtras = result.GetProperty("ballTipExtras").EnumerateArray().Select(value => value.GetDouble()).ToArray();
        Assert.Equal(2, ballExtras.Length);
        Assert.Equal(-10, ballExtras[0]);
        Assert.Equal(-3, ballExtras[1], 0.05);
        Assert.Equal(-3, result.GetProperty("drillTip").GetDouble(), 0.05);
        Assert.InRange(result.GetProperty("drillBeside").GetDouble(), -3.05, -2.5);
        Assert.Equal(0, result.GetProperty("drillClear").GetDouble());
        Assert.True(result.GetProperty("turned").GetBoolean());
        Assert.Equal(-3, result.GetProperty("turningStart").GetDouble(), 0.1);
        Assert.True(result.GetProperty("turningEnd").GetDouble() < -3.6);
    }

    [Fact]
    public void Turning_removes_the_material_beside_the_insert_and_drilling_bores_the_axis()
    {
        var result = Run("""
            const l = M.createLatheStock({ od: 40, id: 0, length: 50, frontZ: 0 }, 1);
            const col = (z) => Math.round((z - l.originZ) / l.cell);
            M.cutLatheSegment(l, [{ x: 30, z: 0 }, { x: 30, z: -30 }], { noseRadius: 0.8, tip: 3, type: "external-cutter" });
            const turned = M.latheProfiles(l);
            const afterTurn = { middle: turned.outer[col(-15)], behind: turned.outer[col(-45)], front: turned.outer[col(0)] };
            M.cutLatheSegment(l, [{ x: 40, z: -30 }, { x: 0, z: -30 }], { noseRadius: 0.8, tip: 3, type: "external-cutter" });
            const faced = M.latheProfiles(l);
            const bore = M.createLatheStock({ od: 40, id: 0, length: 50, frontZ: 0 }, 1);
            M.cutLatheSegment(bore, [{ x: 0, z: 0 }, { x: 0, z: -20 }], { type: "drill", radius: 5 });
            const drilled = M.latheProfiles(bore);
            return {
              afterTurn, facedFront: faced.outer[col(-29)], facedBehind: faced.outer[col(-31)],
              boreInside: drilled.inner[col(-10)], boreBehind: drilled.inner[col(-30)], triangles: M.triangles(bore, 24).length / 9
            };
            """);
        var afterTurn = result.GetProperty("afterTurn");
        Assert.Equal(15, afterTurn.GetProperty("middle").GetDouble(), 1);
        Assert.Equal(20, afterTurn.GetProperty("behind").GetDouble(), 3);
        Assert.Equal(15, afterTurn.GetProperty("front").GetDouble(), 1);
        Assert.True(result.GetProperty("facedFront").GetDouble() < 0.2);   // everything in front of the face is gone
        Assert.Equal(20, result.GetProperty("facedBehind").GetDouble(), 3);
        Assert.Equal(5, result.GetProperty("boreInside").GetDouble(), 3);
        Assert.Equal(0, result.GetProperty("boreBehind").GetDouble(), 3);
        Assert.True(result.GetProperty("triangles").GetInt32() > 0);
    }

    [Fact]
    public void Stl_round_trips_and_a_machined_stock_can_be_reloaded_as_the_next_stock()
    {
        var result = Run("""
            const s = M.createMillStock({ minX: 0, maxX: 20, minY: 0, maxY: 20, minZ: -10, maxZ: 0 }, 1);
            M.cutMillSegment(s, [{ x: 2, y: 10, z: -3 }, { x: 18, y: 10, z: -3 }], { radius: 2, type: "end-mill" });
            const triangles = M.millTriangles(s);
            const stl = M.writeStl(triangles, "test");
            const back = M.readStl(stl);
            const next = M.millStockFromStl(back, 1);
            const asciiText = "solid a\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 0\nendloop\nendfacet\nendsolid a\n";
            const ascii = Uint8Array.from(asciiText, (character) => character.charCodeAt(0));
            return {
              triangles: triangles.length / 9, bytes: stl.byteLength, back: back.length / 9,
              bounds: M.bounds(back), slot: next.top[10 * next.nx + 10], corner: next.top[0], fromStl: next.fromStl,
              ascii: M.readStl(ascii.buffer).length / 9
            };
            """);
        var count = result.GetProperty("triangles").GetInt32();
        Assert.True(count > 0);
        Assert.Equal(84 + count * 50, result.GetProperty("bytes").GetInt32());
        Assert.Equal(count, result.GetProperty("back").GetInt32());
        Assert.Equal(-10, result.GetProperty("bounds").GetProperty("minZ").GetDouble());
        Assert.Equal(20, result.GetProperty("bounds").GetProperty("maxX").GetDouble());
        Assert.Equal(-3, result.GetProperty("slot").GetDouble());
        Assert.Equal(0, result.GetProperty("corner").GetDouble());
        Assert.True(result.GetProperty("fromStl").GetBoolean());
        Assert.Equal(1, result.GetProperty("ascii").GetInt32());
    }

    [Fact]
    public void Round_blanks_have_no_material_outside_the_cylinder()
    {
        var result = Run("""
            const r = M.createMillStock({ minX: 0, maxX: 20, minY: 0, maxY: 20, minZ: -10, maxZ: 0, cylinder: { cx: 10, cy: 10, radius: 8 } }, 1);
            const before = M.millTriangles(r).length / 9;
            const changed = M.cutMillSegment(r, [{ x: 0, y: 0, z: -3 }], { radius: 1, type: "end-mill" });
            return { cornerEmpty: M.isEmpty(r.top[0]), center: r.top[10 * r.nx + 10], masked: r.masked, before, changedOutside: changed };
            """);
        Assert.True(result.GetProperty("cornerEmpty").GetBoolean());
        Assert.Equal(0, result.GetProperty("center").GetDouble());
        Assert.True(result.GetProperty("masked").GetBoolean());
        Assert.True(result.GetProperty("before").GetInt32() > 0);
        Assert.False(result.GetProperty("changedOutside").GetBoolean());
    }

    private JsonElement Run(string body)
    {
        var json = (string)engine.Evaluate("(function () { const M = MeimadMaterial; " + body + " })().valueOf() === undefined ? null : JSON.stringify((function () { const M = MeimadMaterial; " + body + " })())");
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
