using System.IO;
using System.Text.Json;
using Microsoft.ClearScript.V8;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>
/// Exporter definition §20.15 acceptance test 9: the NC viewer's measured work offset card
/// (NcViewer/meimad-work-offsets.js) shows the Cimatron / NC Work CS and the measured G54 separately,
/// warns when the measurement is missing or belongs to another NC release, and converts the measured
/// values for the Home offsets table. The pure part runs in V8.
/// </summary>
public sealed class NcViewerWorkOffsetsCoreTests : IDisposable
{
    private readonly V8ScriptEngine engine = new();

    private const string Data = """
        const record = (id, offset, status, release, x, measuredAt, extra) => Object.assign({
          id, machineId: "machine-10", machineLabel: "10 - Haas VF-3", workOffset: offset, x, y: -80.016, z: 42.691,
          a: null, b: 0, c: 90, units: "MM", source: "DPRNT_TCP", measuredAt, status, ncReleaseId: release,
          ncReleaseLabel: release === "release-2" ? "process r1 · Haas r2" : "process r1 · Haas r1", ncReleaseIsCurrent: release === "release-2"
        }, extra || {});
        const workCs = { source: "CIMATRON", workOffset: "G54", originMm: { x: 10, y: 20, z: 30 }, rotationDeg: { x: 0, y: 0, z: 90 } };
        const data = (records) => ({ available: true, machineId: "machine-10", machineLabel: "10 - Haas VF-3", releaseId: "release-2", records });
        """;

    public NcViewerWorkOffsetsCoreTests()
    {
        engine.Execute(File.ReadAllText(Path.Combine(RepositoryRoot(),
            "client-windows", "Meimad.Planner.Client.Windows", "NcViewer", "meimad-work-offsets.js")));
    }

    [Fact]
    public void Acceptance_9_the_design_Work_CS_and_the_measured_G54_are_shown_separately()
    {
        var result = Run(Data + """
            const s = W.summarize(data([
              record("new", "G54", "VALID", "release-2", 125.402, "2026-10-12T07:00:00Z"),
              record("old", "G54", "SUPERSEDED", "release-2", 125.3, "2026-10-12T06:00:00Z")
            ]), workCs, null);
            return { offset: s.offset, design: s.design, measured: s.measured.values, id: s.measured.record.id, source: s.measured.source,
                     status: s.measured.status, other: s.measured.otherRelease, warnings: s.warnings, history: s.history.map((r) => r.id) };
            """);
        Assert.Equal("G54", result.GetProperty("offset").GetString());
        // Design: the Cimatron Work CS as saved for the Operation, untouched by the measurement.
        Assert.Equal(10, result.GetProperty("design").GetProperty("origin").GetProperty("x").GetDouble());
        Assert.Equal(90, result.GetProperty("design").GetProperty("rotation").GetProperty("z").GetDouble());
        // Measured: the newest VALID record for this release.
        Assert.Equal("new", result.GetProperty("id").GetString());
        Assert.Equal(125.402, result.GetProperty("measured").GetProperty("X").GetDouble());
        Assert.Equal(90, result.GetProperty("measured").GetProperty("C").GetDouble());
        Assert.False(result.GetProperty("measured").TryGetProperty("A", out _));
        Assert.Equal("DPRNT TCP", result.GetProperty("source").GetString());
        Assert.Equal("✔ Valid", result.GetProperty("status").GetString());
        Assert.False(result.GetProperty("other").GetBoolean());
        Assert.Equal(0, result.GetProperty("warnings").GetArrayLength());
        Assert.Equal(["new", "old"], result.GetProperty("history").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public void Missing_measurements_measurements_of_another_release_and_waiting_candidates_are_warned_about()
    {
        var missing = Run(Data + "return W.summarize(data([]), null, \"G54\");");
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("design").ValueKind);
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("measured").ValueKind);
        Assert.Equal("missing", missing.GetProperty("warnings")[0].GetProperty("code").GetString());
        Assert.Contains("No measured G54 for this Operation on 10 - Haas VF-3 yet.",
            missing.GetProperty("warnings")[0].GetProperty("text").GetString(), StringComparison.Ordinal);

        var other = Run(Data + """
            return W.summarize(data([
              record("old-release", "G54", "VALID", "release-1", 125.1, "2026-10-12T06:00:00Z"),
              record("waiting", "G54", "REVIEW_REQUIRED", "release-2", 140, "2026-10-12T08:00:00Z")
            ]), workCs, "G54");
            """);
        Assert.True(other.GetProperty("measured").GetProperty("otherRelease").GetBoolean());
        Assert.Equal(["other_release", "review"],
            other.GetProperty("warnings").EnumerateArray().Select(value => value.GetProperty("code").GetString()));
        Assert.Contains("another NC release (process r1 · Haas r1)",
            other.GetProperty("warnings")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Offsets_are_matched_without_blanks_and_the_measurement_is_converted_for_the_Home_offsets_table()
    {
        var result = Run(Data + """
            const s = W.summarize(data([record("p1", "G54.1 P1", "VALID", "release-2", 25.4, "2026-10-12T07:00:00Z")]), null, "G54.1P1");
            return {
              found: s.measured && s.measured.record.id,
              offsets: s.offsets,
              same: W.sameOffset("G54.1 P1", "g54.1p1"),
              inch: W.homeValues(s.measured.record, "inch", ["X", "Y", "Z"]),
              mm: W.homeValues(s.measured.record, "mm", ["X", "Y", "Z", "C"])
            };
            """);
        Assert.Equal("p1", result.GetProperty("found").GetString());
        Assert.True(result.GetProperty("same").GetBoolean());
        Assert.Equal(1, result.GetProperty("inch").GetProperty("X").GetDouble(), 6);
        Assert.False(result.GetProperty("inch").TryGetProperty("C", out _));
        Assert.Equal(25.4, result.GetProperty("mm").GetProperty("X").GetDouble(), 6);
        Assert.Equal(90, result.GetProperty("mm").GetProperty("C").GetDouble(), 6);
    }

    private JsonElement Run(string body)
    {
        var json = (string)engine.Evaluate("JSON.stringify((function () { const W = MeimadWorkOffsets; " + body + " })())");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public void Dispose() => engine.Dispose();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
