using Meimad.Planner.NcEngine;
using Meimad.Planner.Server.Application.GCode;
using Meimad.Planner.Server.Domain.GCode;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.GCode;

public sealed class NcEngineProgramAnalyzerTests : IDisposable
{
    private const string MillProgram = """
        (machine-independent released program)
        N10 G21 G90 G17 G54
        N20 T1 M6
        N30 G0 X0 Y0 Z100.
        N40 G0 X300.
        N50 G1 X360. F60.
        N60 G4 P2.
        N70 M30

        """;

    private readonly string folder = Path.Combine(Path.GetTempPath(), "MeimadPlanner.NcEngine.Tests", Guid.NewGuid().ToString("N"));
    private readonly NcEngineProgramAnalyzer analyzer = new(NullLogger<NcEngineProgramAnalyzer>.Instance);

    public NcEngineProgramAnalyzerTests() => Directory.CreateDirectory(folder);

    [Fact]
    public async Task Mill_program_is_timed_by_the_engine_as_feed_rapid_distance_tool_changes_and_dwell()
    {
        var analysis = await analyzer.AnalyzeAsync(Write("mill.nc", MillProgram), [Machine("HAAS_NGC")], Now, CancellationToken.None);

        Assert.Equal(NcEngineInfo.AnalysisVersion, analysis.ParserVersion);
        Assert.Equal(60d, analysis.FeedMotionSeconds, 6);
        Assert.Equal(300d, analysis.RapidDistanceMillimeters, 6);
        Assert.Equal(1, analysis.ToolChangeCount);
        Assert.Equal(2d, analysis.DwellSeconds, 6);
        Assert.Equal("MILLIMETER", analysis.DetectedUnits);
        Assert.Equal(NcEstimateConfidence.High, analysis.Confidence);
        Assert.Equal(NcAnalysisStatus.Complete, analysis.Status);
        Assert.StartsWith("Interpreted as Haas UMC-500", analysis.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_configured_nc_viewer_machine_interprets_the_release()
    {
        var analysis = await analyzer.AnalyzeAsync(
            Write("mill-vf3.nc", MillProgram), [Machine("HAAS_NGC", "haas-vf-3ss"), Machine("HAAS_NGC", "haas-vf-3ss")], Now, CancellationToken.None);

        Assert.StartsWith("Interpreted as Haas VF-3SS (haas-mill): Selected machine.", analysis.Warnings[0], StringComparison.Ordinal);
        Assert.Equal(60d, analysis.FeedMotionSeconds, 6);
        Assert.Equal(300d, analysis.RapidDistanceMillimeters, 6);

        // Machines that disagree on the viewer machine leave the choice to the engine.
        var mixed = await analyzer.AnalyzeAsync(
            Write("mill-mixed.nc", MillProgram), [Machine("HAAS_NGC", "haas-vf-3ss"), Machine("HAAS_NGC", "haas-umc-500")], Now, CancellationToken.None);
        Assert.StartsWith("Interpreted as Haas UMC-500", mixed.Warnings[0], StringComparison.Ordinal);
        Assert.Contains(mixed.Warnings, warning => warning.Contains("different NC viewer machines", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Canned_cycles_and_macro_loops_are_simulated_instead_of_excluded()
    {
        // The former parser excluded G81 and any line with a macro variable.
        var program = """
            O1000
            G21 G90 G17 G54
            T1 M6
            G0 X0 Y0 Z50.
            #1=0
            WHILE [#1 LT 3] DO1
            G81 X[#1*20] Y0 Z-10. R2. F100.
            #1=#1+1
            END1
            G80
            G0 Z50.
            M30

            """;

        var analysis = await analyzer.AnalyzeAsync(Write("drill.nc", program), [Machine("FANUC_MACRO_B")], Now, CancellationToken.None);

        Assert.Equal(NcEngineInfo.AnalysisVersion, analysis.ParserVersion);
        // Three holes, 12 mm feed each (R2 to Z-10) at 100 mm/min.
        Assert.Equal(3 * 12d / 100d * 60d, analysis.FeedMotionSeconds, 3);
        Assert.Empty(analysis.UnsupportedConstructs);
        Assert.Contains("Doosan DVF 5000", analysis.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lathe_template_with_meimad_placeholders_uses_css_feed_and_counts_programmed_dwell()
    {
        var program = """
            %
            O1500
            (PART: [[MEIMAD:PART_NAME]])
            [[MEIMAD:VERIFICATION_HOOK]]
            POPEN
            [[MEIMAD:EVENT_CONTEXT]]
            DPRNT[[[MEIMAD:PART_NAME]]]
            [[MEIMAD:CYCLE_START]]
            G18 G21 G99
            T0101
            G50 S2000
            G96 S150 M03
            G0 X50. Z2.
            G1 Z-20. F0.2
            G4 X1.5
            G0 X100. Z100.
            [[MEIMAD:CYCLE_END]]
            PCLOS
            M30
            %

            """;

        var analysis = await analyzer.AnalyzeAsync(Write("lathe.nc", program), [Machine("FANUC_MACRO_B")], Now, CancellationToken.None);

        Assert.Equal(NcEngineInfo.AnalysisVersion, analysis.ParserVersion);
        Assert.Contains("Chevalier FLC-200MC", analysis.Warnings[0], StringComparison.Ordinal);
        // 22 mm at 0.2 mm/rev and 150 m/min on a 50 mm diameter (955 rpm) is about 6.9 s.
        Assert.InRange(analysis.FeedMotionSeconds, 6.5, 7.5);
        Assert.Equal(1.5, analysis.DwellSeconds, 6);
        Assert.Equal(1, analysis.ToolChangeCount);
        Assert.DoesNotContain(analysis.Warnings, warning => warning.Contains("DPRNT", StringComparison.Ordinal)
            || warning.Contains("MEIMAD", StringComparison.Ordinal) && warning.Contains("macro", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Okuma_lathe_programs_are_translated_on_the_okuma_lathe_and_estimated_with_confidence()
    {
        // OSP syntax: six-digit T word, G95 feed per revolution, LAP cycle with a named contour,
        // G04 F dwell, IF ... Nlabel branching.
        var program = """
            G50 S2000
            G96 S150 M03
            T010101
            G95
            G00 X62 Z2
            G85 NLAP1 D2 U0.4 W0.1 F0.3
            NLAP1 G81
            G00 X20
            G01 Z0 F0.15
            X50 Z-30
            X62
            G80
            G87 NLAP1
            G04 F1.5
            VC1=1
            IF [VC1 EQ 1] NEND
            G00 X200
            NEND G00 X200 Z100
            M02

            """;

        var analysis = await analyzer.AnalyzeAsync(Write("okuma.min", program), [Machine("OKUMA_OSP")], Now, CancellationToken.None);

        Assert.StartsWith("Interpreted as Okuma GENOS L200E-M", analysis.Warnings[0], StringComparison.Ordinal);
        Assert.DoesNotContain("OKUMA_OSP_SYNTAX", analysis.UnsupportedConstructs);
        Assert.NotEqual(NcEstimateConfidence.Low, analysis.Confidence);
        Assert.Equal(1.5, analysis.DwellSeconds, 6);
        Assert.InRange(analysis.FeedMotionSeconds, 20, 600);
        Assert.Contains(analysis.Warnings, warning => warning.Contains("LAP cycles are converted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lathe_subprograms_next_to_the_release_are_inlined()
    {
        File.WriteAllText(Path.Combine(folder, "O9100.nc"), "%\nO9100 (FACE)\nG0 X60. Z1.\nG1 X-1. F0.15\nG0 Z2.\nM99\n%\n");
        var program = "%\nO1000\nG21 G99\nT0101\nG97 S1000 M03\nG0 X70. Z5.\nM98 P9100\nM98 P9100\nG0 X100. Z100.\nM30\n%\n";

        var analysis = await analyzer.AnalyzeAsync(Write("main.nc", program), [Machine("FANUC_MACRO_B")], Now, CancellationToken.None);

        Assert.Contains("Subprograms inlined from the release folder: O9100.nc.", analysis.Warnings);
        // Two face passes from X60 to X-1 (diameter words: 30.5 mm of travel each) at
        // 0.15 mm/rev and 1000 rpm are 2 x 12.2 s.
        Assert.Equal(24.4, analysis.FeedMotionSeconds, 3);
        Assert.Equal(NcEstimateConfidence.High, analysis.Confidence);
    }

    [Fact]
    public async Task Okuma_mill_programs_without_a_translation_keep_low_confidence()
    {
        var program = "G90 G17 G21\nT1 M6\nG0 X0 Y0 Z50.\nG1 Z-5. F100.\nG1 X20. F200.\nM02\n";

        var analysis = await analyzer.AnalyzeAsync(Write("okuma-mill.min", program), [Machine("OKUMA_OSP")], Now, CancellationToken.None);

        Assert.Equal(NcEstimateConfidence.Low, analysis.Confidence);
        Assert.Contains("OKUMA_OSP_SYNTAX", analysis.UnsupportedConstructs);
    }

    [Fact]
    public async Task Engine_failure_falls_back_to_the_basic_parser_with_a_warning()
    {
        using var failing = new NcEngineProgramAnalyzer(
            NullLogger<NcEngineProgramAnalyzer>.Instance,
            () => throw new NcEngineException("engine files missing"));

        var analysis = await failing.AnalyzeAsync(Write("fallback.nc", "G21 G90\nG1 X60 F60\nM30\n"), [], Now, CancellationToken.None);

        Assert.Equal(NcProgramParser.CurrentVersion, analysis.ParserVersion);
        Assert.StartsWith("NC engine unavailable (engine files missing)", analysis.Warnings[0], StringComparison.Ordinal);
        Assert.Equal(60d, analysis.FeedMotionSeconds, 6);
    }

    [Fact]
    public void A_dialect_is_passed_only_when_all_supporting_machines_agree()
    {
        Assert.Equal("FANUC_MACRO_B", NcEngineProgramAnalyzer.SingleDialect([Machine("fanuc_macro_b"), Machine("FANUC_MACRO_B")]));
        Assert.Null(NcEngineProgramAnalyzer.SingleDialect([Machine("HAAS_NGC"), Machine("FANUC_MACRO_B")]));
        Assert.Null(NcEngineProgramAnalyzer.SingleDialect([]));
    }

    [Fact]
    public void A_viewer_machine_is_passed_when_every_configured_machine_agrees_and_auto_machines_do_not_count()
    {
        Assert.Equal("haas-vf-3ss", NcEngineProgramAnalyzer.SingleViewerMachine([Machine("HAAS_NGC", "haas-vf-3ss"), Machine("HAAS_NGC", null)]));
        Assert.Equal("haas-vf-3ss", NcEngineProgramAnalyzer.SingleViewerMachine([Machine("HAAS_NGC", "haas-vf-3ss"), Machine("HAAS_NGC", "haas-vf-3ss")]));
        Assert.Null(NcEngineProgramAnalyzer.SingleViewerMachine([Machine("HAAS_NGC", "haas-vf-3ss"), Machine("HAAS_NGC", "haas-umc-500")]));
        Assert.Null(NcEngineProgramAnalyzer.SingleViewerMachine([Machine("HAAS_NGC"), Machine("FANUC_MACRO_B")]));
        Assert.Null(NcEngineProgramAnalyzer.SingleViewerMachine([]));
    }

    [Fact]
    public void Untimed_motion_lowers_confidence_and_program_issues_lower_it_to_medium()
    {
        var clean = Result() with { Errors = [] };
        var untimed = Result() with { UnestimatedSegmentCount = 2 };
        var issues = Result() with { Errors = ["X is outside the X travel"] };

        Assert.Equal(NcEstimateConfidence.High, NcEngineProgramAnalyzer.Map(clean, "HAAS_NGC", [Machine("HAAS_NGC")], Now).Confidence);
        var lowered = NcEngineProgramAnalyzer.Map(untimed, "HAAS_NGC", [Machine("HAAS_NGC")], Now);
        Assert.Equal(NcEstimateConfidence.Low, lowered.Confidence);
        Assert.Contains("UNTIMED_MOTION", lowered.UnsupportedConstructs);
        var medium = NcEngineProgramAnalyzer.Map(issues, "HAAS_NGC", [Machine("HAAS_NGC")], Now);
        Assert.Equal(NcEstimateConfidence.Medium, medium.Confidence);
        Assert.Contains("Program issue: X is outside the X travel", medium.Warnings);
    }

    [Fact]
    public void Legacy_verification_blocks_are_skipped_without_changing_line_numbers()
    {
        var text = "O1\n(MEIMAD PACKAGE VERIFY V1 NCID=123456)\nG0 X0\nM30";

        var prepared = NcEngineProgramAnalyzer.WithoutLegacyHookBlocks(text);

        Assert.Equal(4, prepared.Split('\n').Length);
        Assert.Equal("(MEIMAD VERIFICATION HOOK)", prepared.Split('\n')[1]);
    }

    public void Dispose()
    {
        analyzer.Dispose();
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private static DateTimeOffset Now => DateTimeOffset.Parse("2026-09-23T10:00:00Z");

    private static MachineNcInterpretation Machine(string dialect, string? viewerMachine = null) => new(dialect, viewerMachine);

    private string Write(string name, string text)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text.ReplaceLineEndings("\r\n"));
        return path;
    }

    private static NcEngineAnalysis Result() => new(
        "haas-umc-500", "Haas UMC-500", "mill", "haas-mill", "Selected machine", "mill", "mm",
        10, 10, 5, 60, 300, 3, 1, 2.8, 2, 67.8, 0, false, [], []);
}
