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

        // Every cycle names its own copy of the profile, with the variables of its pass.
        var cycles = lines.Where(line => line.StartsWith("G71 P", StringComparison.Ordinal) || line.StartsWith("G70 P", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, cycles.Length);
        Assert.Equal(4, cycles.Select(line => line.Split(' ')[1]).Distinct().Count());
        Assert.DoesNotContain(cycles, line => line.Contains("P10 ", StringComparison.Ordinal));
        Assert.Equal(2, lines.Count(line => line.StartsWith("N", StringComparison.Ordinal) && line.EndsWith("G0 X20.", StringComparison.Ordinal)));
        Assert.Equal(2, lines.Count(line => line.StartsWith("N", StringComparison.Ordinal) && line.EndsWith("G0 X15.", StringComparison.Ordinal)));
        Assert.Equal(lines.Where(line => line.StartsWith("N", StringComparison.Ordinal)).Select(line => line.Split(' ')[0]).Distinct().Count(),
            lines.Count(line => line.StartsWith("N", StringComparison.Ordinal)));
        Assert.Empty(analysis.Errors);
        Assert.True(analysis.FeedSeconds > 0);
    }

    [Fact]
    public void A_contour_cycle_finds_its_profile_behind_a_goto_and_the_profile_is_not_run_as_moves()
    {
        // The shop's FANUC style: GOTO jumps over the profile, and G70 reads it again later.
        var program = string.Join("\n",
            "G18 G21 G99", "T1111", "G50 S1800", "G96 S120 M3", "G0 X30 Z2",
            "G71 U1.5 R0.5",
            "G71 P100 Q200 U0.2 W0.05 F0.3",
            "GOTO300",
            "N100 G1 X-0.3 Z2",
            "G1 Z0",
            "G1 X12.6",
            "G1 Z-8.45",
            "N200 G1 X29",
            "N300 G0 X30 Z2",
            "G70 P100 Q200",
            "G0 X100 Z50", "M30");
        // The same cycle without the GOTO: the control resumes after the Q block.
        var plain = program.Replace("GOTO300\n", string.Empty, StringComparison.Ordinal);

        var lines = runtime.PrepareForTesting(new NcEnginePreviewRequest(program, Lathe)).GetProperty("lines").EnumerateArray().Select(line => line.GetString()!).ToArray();
        var analysis = runtime.Analyze(new NcEngineAnalysisRequest(program, "FANUC_MACRO_B", Lathe));
        var plainAnalysis = runtime.Analyze(new NcEngineAnalysisRequest(plain, "FANUC_MACRO_B", Lathe));

        Assert.Empty(analysis.Errors);
        Assert.Contains(analysis.Warnings, warning => warning.StartsWith("G71 type I", StringComparison.Ordinal));
        Assert.Contains(analysis.Warnings, warning => warning.StartsWith("G70 finish: expanded", StringComparison.Ordinal));
        // One copy of the profile per cycle, each behind a GOTO of the flat program.
        Assert.Equal(2, lines.Count(line => line.EndsWith("G1 X12.6", StringComparison.Ordinal)));
        Assert.Equal(2, lines.Count(line => line.StartsWith("GOTO ", StringComparison.Ordinal)));
        Assert.DoesNotContain("N100 G1 X-0.3 Z2", lines);
        Assert.True(analysis.SegmentCount > 20, $"segments: {analysis.SegmentCount}");
        // With or without the GOTO the tool path is the same: the profile is never drawn as plain moves.
        Assert.Equal(analysis.SegmentCount, plainAnalysis.SegmentCount);
        Assert.Equal(analysis.FeedSeconds, plainAnalysis.FeedSeconds, 6);
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

    [Fact]
    public void Every_called_program_at_any_depth_is_listed_from_the_folder_the_memory_and_the_file()
    {
        var memory = Path.Combine(folder, "memory");
        var program = Path.Combine(folder, "program");
        Directory.CreateDirectory(memory);
        Directory.CreateDirectory(program);
        runtime.SetReadableFolders([folder]);
        File.WriteAllText(Path.Combine(memory, "O9810.nc"), "O9810 (PROTECTED POSITIONING)\n#100=#24\nIF [#26 EQ #0] GOTO 10\nG65 P9724\nN10 G1 X#24 Y#25 F2000.\nM99\n");
        File.WriteAllText(Path.Combine(memory, "O9724.nc"), "O9724\nG65 P9723\nM99\n");
        File.WriteAllText(Path.Combine(memory, "O9723.nc"), "O9723\n#149=1\nM99\n");
        File.WriteAllText(Path.Combine(program, "O4999.nc"), "O4999 (ATTACHED)\nG0 Z20.\nM99\n");
        var text = string.Join("\n", "%", "O1000", "G21 G17 G90 G94", "T1 M6", "G54 G0 X0 Y0", "G43 Z50. H1 S3000 M3",
            "#1=5.", "G65 P9810 X10. Y5.", "M98 P4999", "#2=#1+1", "M98 P2000", "G65 P#1", "G0 X50. Y50.", "M30",
            "O2000", "G0 X20. Y20.", "M98 P4999", "M99", "%");

        var preview = runtime.ParsePreview(new NcEnginePreviewRequest(text, "fanuc-0i-mc-vmc-3axis", DocumentDirectory: program,
            ProgramMemory: new Dictionary<string, string> { ["fanuc-0i-mc-vmc-3axis"] = memory }));

        using var packed = JsonDocument.Parse(preview.Packed);
        var model = packed.RootElement.GetProperty("model");
        var tree = model.GetProperty("meimadCallTree").EnumerateArray()
            .Select(node => (node.GetProperty("label").GetString(), node.GetProperty("depth").GetInt32(), node.GetProperty("parent").GetString()))
            .ToArray();
        // O9724 and O9723 never run (the IF jumps over the call) but are listed under O9810.
        Assert.Equal(
        [
            ("O9810", 1, "main"), ("O9724", 2, "O9810.nc"), ("O9723", 3, "O9724.nc"),
            ("O4999", 1, "main"), ("O2000", 1, "main"), ("O4999", 2, "O2000"), ("G65 with a computed program number", 1, "main")
        ], tree);
        var units = model.GetProperty("meimadUnits");
        Assert.Contains("#149=1", units.GetProperty("O9723.nc").GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("folder", model.GetProperty("meimadCallTree")[3].GetProperty("location").GetString());

        // The trace: row executions, local variables per macro level and the used variables.
        var trace = model.GetProperty("meimadTrace");
        var writes = trace.GetProperty("writes").EnumerateArray().Select(write => (
            Id: write[1].GetInt32(), Level: write[3].GetInt32(), Key: write[4].GetString())).ToArray();
        Assert.Contains((1, 1, "main|7"), writes);
        Assert.Contains((24, 2, "main|8"), writes);               // the G65 argument opens level 2
        Assert.Contains((100, 0, "O9810.nc|2"), writes);
        Assert.Contains(26, trace.GetProperty("used").EnumerateArray().Select(value => value.GetInt32()));
        Assert.True(trace.GetProperty("rows").TryGetProperty("O9810.nc|5", out _));
        Assert.True(trace.GetProperty("rows").TryGetProperty("main|16", out _));  // O2000 rows are rows of this file
    }

    [Fact]
    public void Lathe_trace_orders_rows_and_writes_and_reads_system_variables()
    {
        var text = "G18 G21 G99\nT0101\nG0 X60. Z2.\n#1=1\nWHILE [#1 LE 2] DO1\nG1 X[60.-#1*5] F0.2\n#1=#1+1\nEND1\n#2=#5001\nM30\n";

        var preview = runtime.ParsePreview(new NcEnginePreviewRequest(text, Lathe));

        using var packed = JsonDocument.Parse(preview.Packed);
        var trace = packed.RootElement.GetProperty("model").GetProperty("meimadTrace");
        var loopRow = trace.GetProperty("rows").GetProperty("main|6").EnumerateArray().ToArray();
        Assert.Equal(2, loopRow.Length);                               // the loop row ran twice
        Assert.True(loopRow[1][0].GetInt32() > loopRow[0][0].GetInt32());
        var writes = trace.GetProperty("writes").EnumerateArray().Where(write => write[1].GetInt32() == 1).Select(write => write[2].GetDouble()).ToArray();
        Assert.Equal([1d, 2d, 3d], writes);
        var read = trace.GetProperty("systemReads").EnumerateArray().Single(entry => entry[1].GetInt32() == 5001);
        Assert.Equal(50d, read[2].GetDouble());                        // X after the second pass
    }

    public void Dispose()
    {
        runtime.Dispose();
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}
