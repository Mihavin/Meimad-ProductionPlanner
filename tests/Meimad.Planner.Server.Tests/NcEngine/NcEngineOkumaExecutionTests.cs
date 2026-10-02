using System.Text.Json;
using System.Text.RegularExpressions;
using Meimad.Planner.NcEngine;

namespace Meimad.Planner.Server.Tests.NcEngine;

/// <summary>
/// The Okuma OSP-P200L lathe executor (meimad-okuma.js): OSP programs are run with OSP rules and
/// expanded into plain moves, and they never share a path with FANUC lathe programs.
/// </summary>
public sealed partial class NcEngineOkumaExecutionTests : IDisposable
{
    private const string Okuma = "okuma-genos-l200e-m";
    private const string Fanuc = "chevalier-flc-200mc";
    private readonly NcEngineRuntime runtime = new();

    // The shape of the factory's OSP programs: the contour is defined before the cycle that calls
    // it by a numeric sequence name, coordinate words carry arithmetic, chamfers use the angle A.
    private static readonly string[] ShopProgram =
    [
        "G50   S2000",
        "NT11",
        "G0      T111111     (------WCNMG-431----)",
        "G96   S430  M3",
        "G0    X38      Z26.5",
        "N1   G81",
        "G0   X31.75-2",
        "G1             Z25  G42  F0.1",
        "G1    X31.75      A-45       F0.08",
        "G1                  Z6.6",
        "G1      X38       Z6.4   F0.1",
        "G40",
        "G80",
        "G85   N1      D2   U0.2  F0.24",
        "G87   N1",
        "G0  X500    Z500",
        "M1",
        "NT10",
        "G0   T101010",
        "G97   S700   M3",
        "G0    X34    Z27",
        "G71   X30.65     Z7.3   F0.9071  D0.08  M73   H1",
        "G0   X500        Z500",
        "NT5",
        "G0   T050505",
        "G97  S1000  M3",
        "G0  X0   Z31",
        "G74  X0      Z6.8  D3  F0.12",
        "G0   X500   Z500",
        "M2"
    ];

    [Fact]
    public void Lap_cycles_are_expanded_with_osp_rules_from_a_contour_defined_before_the_call()
    {
        var program = string.Join("\n", ShopProgram);
        var request = new NcEnginePreviewRequest(program, Okuma);

        var prepared = runtime.PrepareForTesting(request);
        var lines = Lines(prepared);
        var map = prepared.GetProperty("map").EnumerateArray().ToArray();
        var notes = prepared.GetProperty("notes").EnumerateArray().Select(note => note.GetString()!).ToArray();
        var analysis = runtime.Analyze(new NcEngineAnalysisRequest(program, "OKUMA_OSP", Okuma));

        Assert.Empty(notes);
        Assert.Empty(analysis.Errors);
        Assert.Equal("okuma-osp", analysis.Interpreter);
        // Nothing of an Okuma program reaches the interpreter as a FANUC cycle or macro.
        Assert.DoesNotContain(lines, line => CycleOrMacro().IsMatch(line));
        Assert.DoesNotContain(analysis.Warnings, warning => warning.Contains("FANUC", StringComparison.OrdinalIgnoreCase));

        // The contour definition is not run where it stands: the first cut after the positioning
        // block is the first G85 level, 2 mm (diameter) below the AP starting point X38.
        var rough = Row(ShopProgram, "G85   N1      D2   U0.2  F0.24");
        var roughMoves = lines.Where((line, index) => map[index].GetProperty("line").GetInt32() == rough && line != "G95").ToArray();
        Assert.Equal("G00 X36. Z26.5", roughMoves[0]);
        Assert.Equal("G01 X36. Z6.4704 F0.24", roughMoves[1]);             // up to the rough contour (U0.2)
        Assert.Equal("G01 X38. Z6.4064 F0.24", roughMoves[2]);             // and along it up to the previous level
        Assert.Equal("G00 X38.1 Z6.5064", roughMoves[3]);                  // 0.1 relief on both axes
        var passes = map.Where(entry => entry.GetProperty("line").GetInt32() == rough && Text(entry, "cyclePhase") == "rough")
            .Select(entry => entry.GetProperty("cyclePass").GetInt32()).Distinct().ToArray();
        Assert.Equal(new[] { 1, 2, 3, 4 }, passes);                        // X36, X34, X32, X30; the last pass follows the contour
        Assert.Contains("G01 X29.95 Z25. F0.24", lines);                   // rough contour: X29.75 + U0.2
        Assert.All(map.Where(entry => entry.GetProperty("line").GetInt32() == rough), entry => Assert.Equal("G85", Text(entry, "cycle")));

        // G87 runs the definition as programmed: arithmetic (31.75-2), the taper angle A-45 and G42.
        Assert.Contains("G00 X29.75 Z26.5", lines);
        Assert.Contains("G42 G01 X29.75 Z25. F0.1", lines);
        Assert.Contains("G01 X31.75 Z24. F0.08", lines);
        var finish = map.Where(entry => Text(entry, "cycle") == "G87").Select(entry => entry.GetProperty("line").GetInt32()).Distinct().ToArray();
        Assert.Equal(Enumerable.Range(Row(ShopProgram, "G0   X31.75-2"), 6), finish);

        // G71 is the Okuma compound thread cycle: pattern M73, 16 passes to the height H1.
        var thread = lines.Where(line => line.EndsWith("Z7.3 F0.9071", StringComparison.Ordinal)).ToArray();
        Assert.Equal(16, thread.Length);
        Assert.Equal("G01 X31.57 Z7.3 F0.9071", thread[0]);                // X30.65 + H1 - D0.08
        Assert.Equal("G01 X30.65 Z7.3 F0.9071", thread[^1]);
        // G74 drills in pecks of D3 from Z31 to Z6.8 and retracts 0.1 between them.
        Assert.Contains("G01 X0. Z28. F0.12", lines);
        Assert.Contains("G00 X0. Z28.1", lines);
        Assert.Contains("G01 X0. Z6.8 F0.12", lines);
        // T nnttoo: the tool is the middle pair, and the tool comment stays for the tool table.
        Assert.Contains("T1111 (WCNMG-431)", lines);
        Assert.True(analysis.FeedSeconds > 0);
        Assert.Equal(3, analysis.ToolChangeCount);
    }

    [Fact]
    public void Preview_lists_okuma_cycles_and_no_fanuc_cycle_rows()
    {
        var preview = runtime.ParsePreview(new NcEnginePreviewRequest(string.Join("\n", ShopProgram), Okuma));

        Assert.Equal(Okuma, preview.Summary.Machine?.Id);
        Assert.Equal(0, preview.Summary.ErrorCount);
        foreach (var expected in new[] { "lap-rough", "lap-contour", "lap-finish", "osp-thread", "osp-groove", "G85 LAP rough passes", "Okuma OSP executor (Meimad)" })
        {
            Assert.Contains(expected, preview.Packed, StringComparison.Ordinal);
        }
        foreach (var fanuc in new[] { "G71 rough passes", "5104#2 FCK", "G76 rough/finish", "g71-finish", "CNC-PARA" })
        {
            Assert.DoesNotContain(fanuc, preview.Packed, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Part_counter_loops_zero_shift_arcs_and_chamfers_follow_the_osp_manual()
    {
        var program = string.Join("\n",
            "V1=0",
            "N100",
            "G90",
            "G0 X20 Z5",
            "G1 Z0 F0.1",
            "V1=V1+1",
            "IF [V1  EQ   3 ]  GOTO   N200",
            "G50 G91 Z10",
            "GOTO   N100",
            "N200",
            "G90",
            "G0 X11 Z23",
            "G2 X14 Z21.5 L1.5 F0.08",           // arc by radius L
            "G0 X50 Z120",
            "G75 G1 X120 L-5 F0.2",              // C-chamfer: to X110, then to X120 Z115
            "G1 Z50",
            "G4 F2",
            "M2");
        var request = new NcEnginePreviewRequest(program, Okuma);

        var prepared = runtime.PrepareForTesting(request);
        var lines = Lines(prepared);
        var notes = prepared.GetProperty("notes").EnumerateArray().Select(note => note.GetString()!).ToArray();
        var analysis = runtime.Analyze(new NcEngineAnalysisRequest(program, "OKUMA_OSP", Okuma));

        // Three parts; G50 G91 Z10 shifts the zero, so every part is cut 10 mm further along the bar.
        Assert.Contains("G01 X20. Z0. F0.1", lines);
        Assert.Contains("G01 X20. Z-10. F0.1", lines);
        Assert.Contains("G01 X20. Z-20. F0.1", lines);
        Assert.Contains(notes, note => note.Contains("ran 3 times", StringComparison.Ordinal));
        // After two shifts of 10 mm, program Z23 is Z3 of the first part's coordinates.
        Assert.Contains("G00 X11. Z3.", lines);
        Assert.Contains("G02 X14. Z1.5 I1.5 K0. F0.08", lines);
        Assert.Contains("G01 X110. Z100. F0.2", lines);
        Assert.Contains("G01 X120. Z95. F0.2", lines);
        Assert.Contains("G01 X120. Z30. F0.2", lines);
        Assert.Equal(2, analysis.DwellSeconds, 6);
        Assert.Empty(analysis.Errors);
    }

    [Fact]
    public void An_unconditional_jump_back_is_shown_once_and_a_missing_contour_is_reported()
    {
        var program = string.Join("\n", "N9999", "G0 X30 Z2", "G1 Z-5 F0.1", "G85 N7 D1 F0.2", "GOTO N9999", "M2");

        var prepared = runtime.PrepareForTesting(new NcEnginePreviewRequest(program, Okuma));
        var lines = Lines(prepared);
        var notes = prepared.GetProperty("notes").EnumerateArray().Select(note => note.GetString()!).ToArray();

        Assert.Single(lines, line => line.StartsWith("G01 X30. Z-5.", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.StartsWith("Row 5: the jump to N9999 repeats the program without a condition", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.StartsWith("Row 4: G85 names contour N7", StringComparison.Ordinal));
    }

    [Fact]
    public void Okuma_and_fanuc_lathe_programs_are_told_apart_by_syntax_and_never_share_an_interpreter()
    {
        // FANUC: G87 is a side drilling cycle and G95 a feed mode here; neither makes it an OSP program.
        const string fanuc = "%\nO1234\nG21 G40 G99\nT0101\nG50 S3000\nG96 S200 M03\nG00 X62. Z2.\nG71 U2. R0.5\nG71 P100 Q200 U0.4 W0.1 F0.3\n"
            + "N100 G00 X20.\nG01 Z0 F0.15\nN200 X62.\nG95 G97 S500 M3\nG87 X-4 R6 Q0050 F50\nG28 U0 W0\nM30\n%\n";
        var okuma = string.Join("\n", ShopProgram);

        var fanucAuto = runtime.Analyze(new NcEngineAnalysisRequest(fanuc));
        var okumaAuto = runtime.Analyze(new NcEngineAnalysisRequest(okuma));
        var fanucPrepared = Lines(runtime.PrepareForTesting(new NcEnginePreviewRequest(fanuc)));

        Assert.Equal(Fanuc, fanucAuto.MachineId);
        Assert.Null(fanucAuto.Translation);
        Assert.Equal("fanuc-lathe", fanucAuto.Interpreter);
        Assert.Contains(fanucAuto.Warnings, warning => warning.StartsWith("G71 type", StringComparison.Ordinal));
        Assert.Contains(fanucPrepared, line => line.StartsWith("G71 P", StringComparison.Ordinal));   // FANUC keeps its own cycles

        Assert.Equal(Okuma, okumaAuto.MachineId);
        Assert.Equal("okuma-osp-lathe", okumaAuto.Translation);
        Assert.Equal("okuma-osp", okumaAuto.Interpreter);
        Assert.StartsWith("Detected", okumaAuto.SelectionReason, StringComparison.Ordinal);
        // The Meimad Machine's dialect still decides when it is known.
        Assert.Equal(Fanuc, runtime.Analyze(new NcEngineAnalysisRequest(okuma, "FANUC_MACRO_B")).MachineId);
        Assert.Equal(Okuma, runtime.Analyze(new NcEngineAnalysisRequest(fanuc, "OKUMA_OSP")).MachineId);
    }

    public void Dispose() => runtime.Dispose();

    private static string[] Lines(JsonElement prepared) =>
        prepared.GetProperty("lines").EnumerateArray().Select(line => line.GetString()!).ToArray();

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Row(string[] program, string line) => Array.IndexOf(program, line) + 1;

    [GeneratedRegex(@"(?<![A-Z])G7[0-6](?!\d)|M9[789](?!\d)|#\d|\bGOTO\b")]
    private static partial Regex CycleOrMacro();
}
