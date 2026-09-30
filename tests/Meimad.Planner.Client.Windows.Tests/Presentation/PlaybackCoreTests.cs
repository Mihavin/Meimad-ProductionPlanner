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
