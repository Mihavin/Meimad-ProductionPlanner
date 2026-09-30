using System.IO;
using System.Text.Json;
using Microsoft.ClearScript.V8;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>
/// The NC viewer's playback rules (NcViewer/meimad-playback-core.js) run in V8: program stops
/// with the optional stop switch, breakpoints, row-by-row single step through called programs,
/// the called-program pane and the macro variables at a position.
/// </summary>
public sealed class PlaybackCoreTests : IDisposable
{
    // Rows 5 (two segments of one block), 6, 7 of the main program, then two rows of O9010 called
    // from row 8, then row 9. An M01 halts before segment 3, an M00 before segment 5.
    private const string Model = """
        const segments = [
          { line: 5, estimatedSeconds: 1 }, { line: 5, estimatedSeconds: 1 },
          { line: 6, estimatedSeconds: 2 },
          { line: 7, estimatedSeconds: 1 },
          { line: 8, sourceUnit: "O9010.nc", sourceLine: 2, callDepth: 1, estimatedSeconds: 1 },
          { line: 8, sourceUnit: "O9010.nc", sourceLine: 3, callDepth: 1, estimatedSeconds: 1 },
          { line: 9, estimatedSeconds: 0 }
        ];
        const stops = [{ executionIndex: 3, kind: "M01", line: 6, message: "CHECK" }, { executionIndex: 5, kind: "M00", line: 8, unit: "O9010.nc", unitLine: 3 }];
        const t = P.buildTimeline(segments);
        """;

    // A mill program: rows 3-4 move, row 5 sets #1, row 6 calls O9810 (X10. -> #24) whose row 2
    // sets #100, row 3 moves and row 4 returns; row 7 is M01, row 8 moves, row 9 is M30. The trace
    // is what the engine records: [position, level, step] per row execution.
    private const string Program = """
        const segments = [
          { line: 3, tool: 1, estimatedSeconds: 1, meimadLocals: 1 },
          { line: 4, tool: 1, estimatedSeconds: 2, meimadLocals: 1 },
          { line: 6, tool: 1, sourceUnit: "O9810.nc", sourceLine: 3, callDepth: 1, estimatedSeconds: 1, meimadLocals: 2 },
          { line: 8, tool: 2, estimatedSeconds: 2, meimadLocals: 1 }
        ];
        const units = { "O9810.nc": { text: "O9810", inFile: false } };
        const stops = [{ executionIndex: 3, step: 10, kind: "M01", line: 7, message: "CHECK" }];
        const trace = {
          rows: {
            "main|3": [[0, 1, 3]], "main|4": [[1, 1, 4]], "main|5": [[2, 1, 5]], "main|6": [[2, 1, 6]],
            "O9810.nc|2": [[2, 2, 7]], "O9810.nc|3": [[2, 2, 8]], "O9810.nc|4": [[3, 2, 9]],
            "main|7": [[3, 1, 10]], "main|8": [[3, 1, 11]], "main|9": [[4, 1, 12]]
          },
          writes: [[2, 1, 5, 1, "main|5", 5], [2, 24, 10, 2, "main|6", 6], [2, 100, 10, 0, "O9810.nc|2", 7]],
          systemReads: [[3, 5001, 10]],
          used: [1, 24, 100, 149, 5001]
        };
        const t = P.buildTimeline(segments);
        const list = P.executionList(trace);
        const isMainOwn = (entry) => entry.unit === "main";
        """;

    private readonly V8ScriptEngine engine = new();

    public PlaybackCoreTests()
    {
        engine.Execute(File.ReadAllText(Path.Combine(RepositoryRoot(), "client-windows", "Meimad.Planner.Client.Windows", "NcViewer", "meimad-playback-core.js")));
    }

    [Fact]
    public void Segments_of_one_row_form_one_block_on_the_upstream_timeline()
    {
        var result = Run(Model + """
            return { total: t.total, blocks: t.blocks.map((b) => [b.first, b.last, b.start, b.end]), mid: P.secondsAt(t, 2, 0.5) };
            """);

        Assert.Equal(7, result.GetProperty("total").GetDouble());
        Assert.Equal("[[0,1,0,2],[2,2,2,4],[3,3,4,5],[4,4,5,6],[5,5,6,7],[6,6,7,7]]", result.GetProperty("blocks").GetRawText());
        Assert.Equal(3, result.GetProperty("mid").GetDouble());
    }

    [Fact]
    public void M01_stops_only_with_optional_stop_on_and_m00_always_stops()
    {
        var result = Run(Model + """
            const kind = (stop) => stop ? stop.kind : null;
            return {
              optionalOff: kind(P.stopCrossed(stops, 1, 4, false, segments.length)),
              optionalOn: kind(P.stopCrossed(stops, 1, 4, true, segments.length)),
              resumedAtStop: kind(P.stopCrossed(stops, 3, 4, true, segments.length)),
              m00: kind(P.stopCrossed(stops, 4, 6, false, segments.length)),
              text: P.stopText(stops[0]),
              calledText: P.stopText(stops[1])
            };
            """);

        Assert.Equal(JsonValueKind.Null, result.GetProperty("optionalOff").ValueKind);
        Assert.Equal("M01", result.GetProperty("optionalOn").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("resumedAtStop").ValueKind);   // Run after the stop continues
        Assert.Equal("M00", result.GetProperty("m00").GetString());
        Assert.Equal("M01 optional stop at row 6 (CHECK). Press Run to continue.", result.GetProperty("text").GetString());
        Assert.Equal("M00 program stop at O9010.nc row 3. Press Run to continue.", result.GetProperty("calledText").GetString());
    }

    [Fact]
    public void Called_program_rows_come_from_the_segment()
    {
        var result = Run(Model + """
            return { main: P.callOf(segments[0]) === undefined, call: P.callOf(segments[5]) };
            """);

        Assert.True(result.GetProperty("main").GetBoolean());
        Assert.Equal("{\"unit\":\"O9010.nc\",\"line\":3,\"callerLine\":8,\"depth\":1}", result.GetProperty("call").GetRawText());
    }

    [Fact]
    public void Executed_rows_are_ordered_and_the_current_one_follows_the_playback_position()
    {
        var result = Run(Program + """
            return {
              order: list.map((entry) => entry.key),
              drawingRow4: P.executionAt(list, 1, false),
              beforeRow4Moves: P.executionAt(list, 1, true),
              drawingMacroMove: P.executionAt(list, 2, false),
              beforeStart: P.executionAt(list, 0, true)
            };
            """);

        Assert.Equal("[\"main|3\",\"main|4\",\"main|5\",\"main|6\",\"O9810.nc|2\",\"O9810.nc|3\",\"O9810.nc|4\",\"main|7\",\"main|8\",\"main|9\"]",
            result.GetProperty("order").GetRawText());
        Assert.Equal(1, result.GetProperty("drawingRow4").GetInt32());
        Assert.Equal(0, result.GetProperty("beforeRow4Moves").GetInt32());     // row 4 has not run yet
        Assert.Equal(5, result.GetProperty("drawingMacroMove").GetInt32());     // O9810.nc row 3 emits segment 2
        Assert.Equal(-1, result.GetProperty("beforeStart").GetInt32());
    }

    [Fact]
    public void A_step_lands_after_a_rows_move_or_where_a_macro_statement_runs()
    {
        var result = Run(Program + """
            const target = (index) => { const s = P.stepTarget(t, segments, list, index); return [s.seconds, s.moves, s.tool ?? null]; };
            return { row3: target(0), row5: target(2), macroRow3: target(5), m30: target(9),
                     caller: P.callerRow(list, 5, isMainOwn), next: P.nextCall(list, -1, isMainOwn), afterMacro: P.nextCall(list, 6, isMainOwn) === undefined };
            """);

        Assert.Equal("[1,true,1]", result.GetProperty("row3").GetRawText());        // end of its move
        Assert.Equal("[3,false,1]", result.GetProperty("row5").GetRawText());       // #1=5.: where the tool is (before segment 2)
        Assert.Equal("[4,true,1]", result.GetProperty("macroRow3").GetRawText());   // the macro's move
        Assert.Equal("[6,false,2]", result.GetProperty("m30").GetRawText());        // the end
        Assert.Equal(6, result.GetProperty("caller").GetInt32());                    // O9810 is called from row 6
        var next = result.GetProperty("next");
        Assert.Equal(4, next.GetProperty("index").GetInt32());                       // the first called row, O9810.nc row 2
        Assert.Equal(6, next.GetProperty("callerRow").GetInt32());
        Assert.True(result.GetProperty("afterMacro").GetBoolean());
    }

    [Fact]
    public void Breakpoints_and_stops_halt_in_execution_order_and_are_not_repeated_after_a_step()
    {
        var result = Run(Program + """
            const all = P.halts(stops, ["O9810.nc|3", "main|8"], trace);
            const onM01 = P.haltsUpTo(list, 7, all, units, false);
            const onM01Optional = P.haltsUpTo(list, 7, all, units, true);
            const onRow8 = P.haltsUpTo(list, 8, all, units, false);
            return {
              order: all.map((h) => [h.executionIndex, h.kind, P.haltKey(h, units)]),
              m01Here: onM01.here.length, m01Passed: [...onM01.passed].map((h) => h.kind),
              m01OptionalHere: onM01Optional.here.map((h) => h.kind),
              row8Here: onRow8.here.map((h) => h.kind), row8Passed: [...onRow8.passed].map((h) => h.kind),
              runFromRow4: P.stopCrossed(all, 1, 3, false, segments.length).key,
              runAfterM01: P.stopCrossed(all, 2, 3, false, segments.length, onM01.passed).key,
              m01Row: P.haltExecution(list, stops[0], units),
              text: P.stopText(all[0])
            };
            """);

        Assert.Equal("[[2,\"BREAK\",\"O9810.nc|3\"],[3,\"M01\",\"main|7\"],[3,\"BREAK\",\"main|8\"]]", result.GetProperty("order").GetRawText());
        Assert.Equal(0, result.GetProperty("m01Here").GetInt32());                      // optional stop off: no halt on row 7
        Assert.Equal("[\"M01\"]", result.GetProperty("m01Passed").GetRawText());         // but Run must not stop on it later
        Assert.Equal("[\"M01\"]", result.GetProperty("m01OptionalHere").GetRawText());
        Assert.Equal("[\"BREAK\"]", result.GetProperty("row8Here").GetRawText());
        Assert.Equal("[\"M01\",\"BREAK\"]", result.GetProperty("row8Passed").GetRawText());
        Assert.Equal("O9810.nc|3", result.GetProperty("runFromRow4").GetString());        // the macro's breakpoint comes first
        Assert.Equal("main|8", result.GetProperty("runAfterM01").GetString());            // the M01 is skipped, row 8's breakpoint halts
        Assert.Equal(7, result.GetProperty("m01Row").GetInt32());
        Assert.Equal("Breakpoint at O9810.nc row 3. Press Run to continue.", result.GetProperty("text").GetString());
    }

    [Fact]
    public void Variable_values_follow_the_step_and_the_macro_level()
    {
        var result = Run(Program + """
            const at = (context) => Object.fromEntries(P.variablesAt(trace, context).map((row) => [row.variable, [row.value, row.scope, row.setAt]]));
            const before = (index) => ({ position: list[index].position, level: list[index].level, step: list[index].step - 1 });
            const after = (index) => ({ position: list[index].position, level: list[index].level, step: list[index].step });
            return { beforeMacroRow2: at(before(4)), afterMacroRow2: at(after(4)), end: at(after(9)), playback: at({ position: 3, level: 1 }) };
            """);

        var beforeMacro = result.GetProperty("beforeMacroRow2");
        Assert.Equal("[\"vacant\",\"Local\",\"\"]", beforeMacro.GetProperty("#1").GetRawText());       // the main program's #1 is another level
        Assert.Equal("[\"10\",\"Local\",\"row 6\"]", beforeMacro.GetProperty("#24").GetRawText());     // the G65 argument
        Assert.Equal("[\"vacant\",\"Common\",\"\"]", beforeMacro.GetProperty("#100").GetRawText());
        Assert.Equal("[\"10\",\"Common\",\"O9810.nc row 2\"]", result.GetProperty("afterMacroRow2").GetProperty("#100").GetRawText());
        var end = result.GetProperty("end");
        Assert.Equal("[\"5\",\"Local\",\"row 5\"]", end.GetProperty("#1").GetRawText());
        Assert.Equal("[\"vacant\",\"Local\",\"\"]", end.GetProperty("#24").GetRawText());
        Assert.Equal("[\"vacant\",\"Common\",\"\"]", end.GetProperty("#149").GetRawText());             // named by a macro that never ran
        Assert.Equal("[\"10\",\"System\",\"read\"]", result.GetProperty("playback").GetProperty("#5001").GetRawText());
    }

    private JsonElement Run(string body)
    {
        var json = (string)engine.Evaluate("JSON.stringify((function () { const P = MeimadPlaybackCore; " + body + " })())");
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
