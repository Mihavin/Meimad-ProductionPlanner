using System.IO;
using System.Text.Json;
using Microsoft.ClearScript.V8;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>
/// The NC viewer's playback rules (NcViewer/meimad-playback-core.js) run in V8: program stops
/// with the optional stop switch, single block stepping and the called-program pane.
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
    public void Single_step_runs_one_block_and_halts_on_a_stop_block_first()
    {
        var result = Run(Model + """
            const first = P.nextStep(t, stops, 0, true);
            const mid = P.nextStep(t, stops, 1, true);            // inside the first block: to its end
            const beforeStop = P.nextStep(t, stops, 4, true);     // the M01 block is a step of its own
            const afterStop = P.nextStep(t, stops, 4, true, 3);   // acknowledged: the next block runs
            const skipped = P.nextStep(t, stops, 4, false);       // optional stop off: no halt
            const end = P.nextStep(t, stops, 7, true);
            return { first: first.seconds, mid: mid.seconds, beforeStop: [beforeStop.seconds, beforeStop.stop.kind],
                     afterStop: afterStop.seconds, skipped: skipped.seconds, end: end === undefined };
            """);

        Assert.Equal(2, result.GetProperty("first").GetDouble());
        Assert.Equal(2, result.GetProperty("mid").GetDouble());
        Assert.Equal("[4,\"M01\"]", result.GetProperty("beforeStop").GetRawText());
        Assert.Equal(5, result.GetProperty("afterStop").GetDouble());
        Assert.Equal(5, result.GetProperty("skipped").GetDouble());
        Assert.True(result.GetProperty("end").GetBoolean());
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

    private const string Trace = """
        const units = { "O9010.nc": { text: "O9010" } };
        const trace = {
          rows: { "main|5": [[0, 1, 3]], "main|20": [[3, 1, 6], [5, 1, 9]], "O9010.nc|3": [[5, 2, 8]], "main|8": [[4, 2, 7]] },
          writes: [[-1, 500, 1, 0, "initial", 0], [3, 1, 5, 1, "main|20", 6], [4, 1, 9, 2, "main|8", 7], [5, 1, 6, 1, "main|20", 9], [5, 100, 2, 0, "O9010.nc|3", 8]],
          systemReads: [[3, 5001, 10], [5, 5001, 20]],
          used: [1, 100, 500, 5001]
        };
        """;

    [Fact]
    public void Breakpoints_halt_before_every_execution_of_their_row()
    {
        var result = Run(Model + Trace + """
            const list = P.halts(stops, ["main|20", "O9010.nc|3"], trace);
            const crossed = P.stopCrossed(list, 0, 3, false, segments.length);
            return { list: list.map((h) => [h.executionIndex, h.kind]), crossed: [crossed.executionIndex, crossed.kind], text: P.stopText(crossed),
                     called: P.stopText(list.find((h) => h.kind === "BREAK" && h.unit === "O9010.nc")) };
            """);

        Assert.Equal("[[3,\"M01\"],[3,\"BREAK\"],[5,\"M00\"],[5,\"BREAK\"],[5,\"BREAK\"]]", result.GetProperty("list").GetRawText());
        Assert.Equal("[3,\"BREAK\"]", result.GetProperty("crossed").GetRawText());   // the M01 is skipped with optional stop off
        Assert.Equal("Breakpoint at row 20. Press Run to continue.", result.GetProperty("text").GetString());
        Assert.Equal("Breakpoint at O9010.nc row 3. Press Run to continue.", result.GetProperty("called").GetString());
    }

    [Fact]
    public void The_tool_goes_to_the_end_of_a_rows_move_or_where_a_macro_row_runs_and_cycles_through_executions()
    {
        var result = Run(Model + Trace + """
            const move = P.rowTarget(t, segments, trace, units, "main|5", undefined);
            const macro = P.rowTarget(t, segments, trace, units, "main|20", undefined);
            const again = P.rowTarget(t, segments, trace, units, "main|20", macro);
            const called = P.rowTarget(t, segments, trace, units, "O9010.nc|3", undefined);
            return { move: [move.seconds, move.moves, move.tool === undefined], macro: [macro.seconds, macro.position, macro.count],
                     again: [again.seconds, again.choice], called: [called.seconds, called.moves], none: P.rowTarget(t, segments, trace, units, "main|99") === undefined };
            """);

        Assert.Equal("[2,true,true]", result.GetProperty("move").GetRawText());       // row 5: end of its two-segment block
        Assert.Equal("[4,3,2]", result.GetProperty("macro").GetRawText());            // row 20 (no motion) runs before segment 3
        Assert.Equal("[6,1]", result.GetProperty("again").GetRawText());              // its second execution
        Assert.Equal("[7,true]", result.GetProperty("called").GetRawText());          // O9010.nc row 3 draws segment 5
        Assert.True(result.GetProperty("none").GetBoolean());
    }

    [Fact]
    public void Variable_values_follow_the_position_the_macro_level_and_the_row_execution()
    {
        var result = Run(Trace + """
            const at = (context) => Object.fromEntries(P.variablesAt(trace, context).map((row) => [row.variable, [row.value, row.scope, row.setAt]]));
            return {
              start: at({ position: 0, level: 1 }),
              inMacro: at({ position: 4, level: 2 }),
              back: at({ position: 5, level: 1 }),
              afterFirstRun: at({ position: 3, level: 1, step: 6 })
            };
            """);

        var start = result.GetProperty("start");
        Assert.Equal("[\"vacant\",\"Local\",\"\"]", start.GetProperty("#1").GetRawText());
        Assert.Equal("[\"1\",\"Common\",\"Initial # vars\"]", start.GetProperty("#500").GetRawText());
        Assert.Equal("[\"9\",\"Local\",\"row 8\"]", result.GetProperty("inMacro").GetProperty("#1").GetRawText());   // the macro level's own #1
        Assert.Equal("[\"10\",\"System\",\"read\"]", result.GetProperty("inMacro").GetProperty("#5001").GetRawText());
        Assert.Equal("[\"6\",\"Local\",\"row 20\"]", result.GetProperty("back").GetProperty("#1").GetRawText());
        Assert.Equal("[\"2\",\"Common\",\"O9010.nc row 3\"]", result.GetProperty("back").GetProperty("#100").GetRawText());
        Assert.Equal("[\"20\",\"System\",\"read\"]", result.GetProperty("back").GetProperty("#5001").GetRawText());
        Assert.Equal("[\"5\",\"Local\",\"row 20\"]", result.GetProperty("afterFirstRun").GetProperty("#1").GetRawText());
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
