using System.Text.Json;
using Meimad.Planner.NcEngine;

namespace Meimad.Planner.Server.Tests.NcEngine;

/// <summary>
/// The Meimad custom macro executor for lathe programs (meimad-macro.js) and the mill hooks that
/// report program stops and called programs to the NC viewer.
/// </summary>
public sealed class NcEngineMacroExecutionTests : IDisposable
{
    private const string Lathe = "chevalier-flc-200mc";
    private readonly string folder = Path.Combine(Path.GetTempPath(), "MeimadPlanner.NcMacro.Tests", Guid.NewGuid().ToString("N"));
    private readonly NcEngineRuntime runtime = new();

    public NcEngineMacroExecutionTests()
    {
        Directory.CreateDirectory(folder);
        runtime.SetReadableFolders([folder]);
    }

    [Fact]
    public void Lathe_macro_program_runs_loops_calls_and_system_variables_like_the_control()
    {
        File.WriteAllText(Path.Combine(folder, "O9010.nc"), string.Join("\n",
            "O9010 (GROOVE)", "#1=99 (A local of this macro level only)", "G0 X[#24+2.] Z#26", "G1 X#24 F#9", "G0 X[#24+2.]", "#100=#100+1", "M99"));
        var program = string.Join("\n",
            "%", "O2000", "G18 G21 G99", "T0101", "G96 S150 M03", "#100=0", "#1=5", "G0 X60. Z2.",
            "#20=0",
            "WHILE [#20 LT 3] DO1",
            "G65 P9010 X[40.-#20*2] Z[-10.-#20*5] F0.1",
            "#20=#20+1",
            "END1",
            "IF [#1 EQ 5] THEN #30=1",
            "IF [#30 NE 1] GOTO 100",
            "G0 X70. Z5.",
            "N100 G0 Z10.",
            "#31=#5001",
            "DPRNT[COUNT*#100[20]*X*#31[33]]",
            "M01 (CHECK PART)",
            "M98 P2100 L2",
            "M97 P300",
            "#3006=1 (TURN PART)",
            "G0 X100. Z50.",
            "M30",
            "O2100", "G1 U-1. F0.2", "G0 U1.", "M99",
            "N300 G0 X90.", "M99", "%");
        var request = new NcEnginePreviewRequest(program, Lathe, DocumentDirectory: folder);

        var prepared = runtime.PrepareForTesting(request);
        var preview = runtime.ParsePreview(request);

        var lines = prepared.GetProperty("lines").EnumerateArray().Select(line => line.GetString()!).ToArray();
        var map = prepared.GetProperty("map").EnumerateArray().ToArray();
        // Three G65 calls with the loop counter as arguments; the macro's #1 does not reach the main program.
        Assert.Equal(["G1 X40. F0.1", "G1 X38. F0.1", "G1 X36. F0.1"], lines.Where(line => line.StartsWith("G1 X", StringComparison.Ordinal)));
        Assert.Equal(3, lines.Count(line => line.StartsWith("(G65 P9010 -> O9010.nc", StringComparison.Ordinal)));
        Assert.Contains("G0 X70. Z5.", lines);                        // IF [#1 EQ 5] THEN ran in the main level
        Assert.Equal(4, lines.Count(line => line.StartsWith("G1 U-1.", StringComparison.Ordinal) || line.StartsWith("G0 U1.", StringComparison.Ordinal)));
        Assert.Contains("N300 G0 X90.", lines);
        Assert.DoesNotContain(lines, line => line.Contains('#') || line.Contains("WHILE", StringComparison.Ordinal) || line.Contains("GOTO", StringComparison.Ordinal));
        var macroRow = map.First(entry => entry.TryGetProperty("unit", out var unit) && unit.GetString() == "O9010.nc");
        Assert.Equal(11, macroRow.GetProperty("line").GetInt32());   // the calling row of the main program
        Assert.Equal(1, macroRow.GetProperty("depth").GetInt32());
        Assert.Contains(map, entry => entry.TryGetProperty("unit", out var unit) && unit.GetString() == "O2100"
            && entry.GetProperty("unitLine").GetInt32() == 27);         // in-file program: its own row of this file

        var prints = prepared.GetProperty("prints").EnumerateArray().Select(print => print.GetProperty("text").GetString()!).ToArray();
        Assert.Equal(["COUNT 3 X 70.000"], prints);                  // #100 counted three macro calls, #5001 = X70
        var stops = prepared.GetProperty("stops").EnumerateArray().Select(stop => (stop.GetProperty("kind").GetString(), stop.GetProperty("message").GetString())).ToArray();
        Assert.Equal([("M01", "CHECK PART"), ("#3006", "TURN PART")], stops);

        using var packed = JsonDocument.Parse(preview.Packed);
        var model = packed.RootElement.GetProperty("model");
        var viewerStops = model.GetProperty("meimadStops").EnumerateArray().ToArray();
        Assert.Equal(2, viewerStops.Length);
        Assert.True(viewerStops[0].GetProperty("executionIndex").GetInt32() > 0);
        Assert.True(viewerStops[1].GetProperty("executionIndex").GetInt32() > viewerStops[0].GetProperty("executionIndex").GetInt32());
        var units = model.GetProperty("meimadUnits");
        Assert.Contains("GROOVE", units.GetProperty("O9010.nc").GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.True(units.GetProperty("O2100").GetProperty("inFile").GetBoolean());
        Assert.Equal(0, preview.Summary.ErrorCount);
    }

    [Fact]
    public void A_roughing_cycle_in_a_loop_keeps_its_own_contour_every_pass()
    {
        var program = string.Join("\n",
            "G18 G21 G99", "T0101", "G96 S150 M03", "#1=0",
            "WHILE [#1 LT 2] DO1",
            "G0 X[62.-#1*10] Z2.",
            "G71 U2. R0.5",
            "G71 P10 Q20 U0.4 W0.1 F0.3",
            "N10 G0 X[20.-#1*5]",
            "G1 Z-20. F0.15",
            "N20 X[62.-#1*10]",
            "G70 P10 Q20",
            "#1=#1+1",
            "END1",
            "G0 X100. Z50.", "M30");
        var request = new NcEnginePreviewRequest(program, Lathe);

        var lines = runtime.PrepareForTesting(request).GetProperty("lines").EnumerateArray().Select(line => line.GetString()!).ToArray();
        var analysis = runtime.Analyze(new NcEngineAnalysisRequest(program, "FANUC_MACRO_B", Lathe));

        var cycles = lines.Where(line => line.StartsWith("G71 P", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, cycles.Length);
        Assert.StartsWith("G71 P10 Q20", cycles[0], StringComparison.Ordinal);
        Assert.DoesNotContain("P10 ", cycles[1], StringComparison.Ordinal); // the second pass names its own copies
        Assert.Contains(lines, line => line.EndsWith("G0 X15.", StringComparison.Ordinal) && !line.StartsWith("N10 ", StringComparison.Ordinal));
        Assert.Equal(lines.Where(line => line.StartsWith("N", StringComparison.Ordinal)).Select(line => line.Split(' ')[0]).Distinct().Count(),
            lines.Count(line => line.StartsWith("N", StringComparison.Ordinal)));
        Assert.Empty(analysis.Errors);
        Assert.True(analysis.FeedSeconds > 0);
    }

    [Fact]
    public void Custom_macro_calls_by_g_code_follow_the_cnc_parameters()
    {
        File.WriteAllText(Path.Combine(folder, "O9010.nc"), "O9010\nG1 X#24 Z#26 F0.2\nG100 X1. (inside the called macro G100 is an ordinary code)\nM99\n");
        var program = "G18 G21 G99\nT0101\nG0 X50. Z2.\nG100 X30. Z-5.\nM30\n";
        var request = new NcEnginePreviewRequest(program, Lathe, DocumentDirectory: folder,
            MachineParametersText: "N06050Q1L1P100\n");

        var lines = runtime.PrepareForTesting(request).GetProperty("lines").EnumerateArray().Select(line => line.GetString()!).ToArray();

        Assert.Contains(lines, line => line.StartsWith("(G65 P9010 -> O9010.nc", StringComparison.Ordinal));
        Assert.Contains("G1 X30. Z-5. F0.2", lines);
        Assert.Contains("G100 X1.", lines);
    }

    [Theory]
    [InlineData("haas-vf-3ss")]
    [InlineData("fanuc-0i-mc-vmc-3axis")]
    public void Mill_preview_reports_stops_and_the_called_program_of_every_segment(string machine)
    {
        File.WriteAllText(Path.Combine(folder, "O9100.nc"), "O9100 (HOLE)\nG0 X#24 Y#25\nG1 Z-5. F200.\nG0 Z5.\nM99\n");
        var program = string.Join("\n",
            "%", "O1000", "G21 G17 G90 G94", "T1 M6", "G54 G0 X0 Y0", "G43 Z50. H1 S3000 M3",
            "G65 P9100 X10. Y5.",
            "M01 (CHECK)",
            "M98 P2000",
            "M00",
            "G0 X50. Y50.", "M30",
            "O2000", "G0 X20. Y20.", "G1 Z-2. F100.", "G0 Z50.", "M99", "%");

        var preview = runtime.ParsePreview(new NcEnginePreviewRequest(program, machine, DocumentDirectory: folder));

        using var packed = JsonDocument.Parse(preview.Packed);
        var model = packed.RootElement.GetProperty("model");
        var stops = model.GetProperty("meimadStops").EnumerateArray()
            .Select(stop => (stop.GetProperty("kind").GetString(), stop.GetProperty("line").GetInt32())).ToArray();
        Assert.Equal([("M01", 8), ("M00", 10)], stops);
        var columns = packed.RootElement.GetProperty("lists").GetProperty("segments").GetProperty("columns");
        var depths = columns.GetProperty("callDepth").EnumerateArray().Select(value => value.ValueKind == JsonValueKind.Null ? 0 : value.GetInt32()).ToArray();
        var rows = columns.GetProperty("line").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        // The in-file O2000 segments stay on the main program's calling row (9); the call pane shows O2000.
        Assert.Contains(Enumerable.Range(0, depths.Length), index => depths[index] == 1 && rows[index] == 9);
        Assert.Contains(Enumerable.Range(0, depths.Length), index => depths[index] == 1 && rows[index] == 7);
        var units = model.GetProperty("meimadUnits");
        Assert.True(units.GetProperty("O2000").GetProperty("inFile").GetBoolean());
        Assert.Contains("HOLE", units.GetProperty("O9100.nc").GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, preview.Summary.ErrorCount);
    }

    public void Dispose()
    {
        runtime.Dispose();
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}
