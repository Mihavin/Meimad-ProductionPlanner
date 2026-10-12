using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ClearScript.V8;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>
/// The NC viewer's Path selector (NcViewer/meimad-path-visibility-core.js) run in V8: the pick-id
/// layout it mirrors from the vendored viewer, the run-so-far / whole / not-yet-run / faint / hidden
/// rule, and the patch of the vendored line shader it depends on.
/// </summary>
public sealed class PathVisibilityCoreTests : IDisposable
{
    // Rapid 0, feeds 1-2 (tool 1), a tool change 3 (a return move: hidden unless G30 is shown),
    // feed 4 (tool 2); compensated twins of the feeds 1 and 4.
    private const string Model = """
        const model = {
          segments: [
            { kind: "rapid", tool: 1, executionIndex: 0 },
            { kind: "feed", tool: 1, executionIndex: 1 },
            { kind: "feed", tool: 1, executionIndex: 2 },
            { kind: "tool-change", tool: 2, executionIndex: 3 },
            { kind: "feed", tool: 2, executionIndex: 4 }
          ],
          compensatedSegments: [{ tool: 1, executionIndex: 1 }, { tool: 2, executionIndex: 4 }]
        };
        const filters = { tool: "all", showRapids: true, showReturns: false, showCompensated: true, showNonCutting: false };
        const layout = P.index(model, filters);
        // Pick ids: rapid 1, feed 2, feed 3, feed(4) 4; compensated 5 (execution 1), 6 (execution 4).
        const shown = (mode, playback) => {
          const values = P.uniforms(mode, layout, playback);
          return [1, 2, 3, 4, 5, 6].map((id) => { const d = P.drawn(id, values); return d.visible ? (d.alpha < 1 ? "f" : "y") : "-"; }).join("");
        };
        """;

    private readonly V8ScriptEngine engine = new();

    public PathVisibilityCoreTests()
    {
        engine.Execute(File.ReadAllText(Path.Combine(RepositoryRoot(), "client-windows", "Meimad.Planner.Client.Windows", "NcViewer", "meimad-path-visibility-core.js")));
    }

    [Fact]
    public void Pick_ids_follow_the_vendored_viewer_numbering_under_the_filters()
    {
        var result = Run("""
            const toolTwo = P.index(model, { ...filters, tool: "2", showReturns: true });
            return { base: layout.base.total, compensated: layout.compensated.total, toolTwoBase: toolTwo.base.total, toolTwoCompensated: toolTwo.compensated.total };
            """);
        Assert.Equal(4, result.GetProperty("base").GetInt32());
        Assert.Equal(2, result.GetProperty("compensated").GetInt32());
        Assert.Equal(2, result.GetProperty("toolTwoBase").GetInt32()); // the tool change and the feed of tool 2
        Assert.Equal(1, result.GetProperty("toolTwoCompensated").GetInt32());
    }

    [Fact]
    public void Each_mode_draws_its_part_of_the_toolpath_during_a_run()
    {
        // Executions 0-2 have run.
        var result = Run("""
            const run = { completedIndex: 2, complete: false };
            return { played: shown("played", run), all: shown("all", run), remaining: shown("remaining", run), dim: shown("dim", run), hidden: shown("hidden", run) };
            """);
        Assert.Equal("yyy-y-", result.GetProperty("played").GetString());
        Assert.Equal("yyyyyy", result.GetProperty("all").GetString());
        Assert.Equal("---y-y", result.GetProperty("remaining").GetString());
        Assert.Equal("yyyfyf", result.GetProperty("dim").GetString());
        Assert.Equal("------", result.GetProperty("hidden").GetString());
    }

    [Fact]
    public void Without_a_run_every_mode_but_hidden_draws_the_whole_toolpath()
    {
        var result = Run("""
            return { played: shown("played"), remaining: shown("remaining"), dim: shown("dim"), hidden: shown("hidden"),
              start: shown("remaining", { completedIndex: -1 }), unknown: P.normalizeMode("bogus") };
            """);
        Assert.Equal("yyyyyy", result.GetProperty("played").GetString());
        Assert.Equal("yyyyyy", result.GetProperty("remaining").GetString());
        Assert.Equal("yyyyyy", result.GetProperty("dim").GetString());
        Assert.Equal("------", result.GetProperty("hidden").GetString());
        Assert.Equal("yyyyyy", result.GetProperty("start").GetString()); // a run at its first move: nothing has run
        Assert.Equal("played", result.GetProperty("unknown").GetString());
    }

    [Fact]
    public void The_vendored_line_shader_takes_the_patch_exactly_once()
    {
        var viewer = File.ReadAllText(Path.Combine(RepositoryRoot(), "third_party", "chevalier-nc-viewer", "media", "viewer3d.js"));
        var vertex = Regex.Match(viewer, "const LINE_VERTEX = `(?<shader>[^`]*)`").Groups["shader"].Value;
        Assert.NotEmpty(vertex);
        engine.Script.vertexSource = vertex;
        var result = Run("""
            const patched = P.patchLineShader(vertexSource);
            return { patched, again: P.patchLineShader(patched), other: P.patchLineShader("void main() { gl_Position = vec4(0.0); }") };
            """);
        var patched = result.GetProperty("patched").GetString()!;
        Assert.Contains("uniform float meimadPathMode;", patched);
        Assert.Contains("if (instanceStyle.z < 0.5 || meimadHidden) {", patched);
        Assert.Contains("vColor.a *= meimadDim;", patched);
        Assert.True(patched.IndexOf("bool meimadHidden", StringComparison.Ordinal) < patched.IndexOf("|| meimadHidden", StringComparison.Ordinal));
        Assert.Equal(JsonValueKind.Null, result.GetProperty("other").ValueKind);
        // A patched shader is not patched again (a cloned material keeps its one set of uniforms).
        Assert.Equal(JsonValueKind.Null, result.GetProperty("again").ValueKind);
    }

    private JsonElement Run(string body)
    {
        var json = (string)engine.Evaluate("JSON.stringify((function () { const P = MeimadPathVisibility; " + Model + body + " })())");
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
