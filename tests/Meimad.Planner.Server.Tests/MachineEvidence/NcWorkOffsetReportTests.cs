using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Meimad.Planner.Server.Application.GCode;
using Meimad.Planner.Server.Application.MachineEvidence;

namespace Meimad.Planner.Server.Tests.MachineEvidence;

/// <summary>
/// The work offset report program per control (owner rule: implemented per control through the NC
/// dialect layer; Okuma OSP is not a FANUC dialect). Each rendering is checked line by line and then
/// "printed" with sample variable values, and the printed lines must parse into the expected message.
/// </summary>
public sealed class NcWorkOffsetReportTests
{
    private static readonly NcWorkOffsetReportRequest G54Request = new(
        "10", "20", "654321", "654321",
        [WorkOffsetNames.Parse("G54")!],
        NcWorkOffsetReports.ParseAxes("XYZ", haas: true),
        10504);

    [Fact]
    public void Haas_NGC_prints_with_DPRNT_between_G103_and_reads_G54_from_5221()
    {
        var report = NcDialects.Profile(NcDialects.HaasNgc).WorkOffsetReport(G54Request);
        Assert.Equal("work-offsets/O01992.nc", report.FileName);
        Assert.Equal(
        [
            "%",
            "O01992 (MEIMAD WORK OFFSET REPORT)",
            "(MEIMAD WORK OFFSET REPORT - NO MOTION)",
            "(MACHINE 10 - OPERATION 20 - NC RELEASE 654321)",
            "(OFFSETS G54 - RUN AFTER MEASURING)",
            "G103 P1",
            "#31=#3011",
            "#32=#3012",
            "DPRNT[MEIMAD/OFFSET-START/SESSION/WO-10-#31[80]-#32[60]-1/MACHINE/10/PROGRAM/654321/OPERATION/20/RELEASE/654321]",
            "DPRNT[MEIMAD/OFFSET/SESSION/WO-10-#31[80]-#32[60]-1/OFFSET/G54/UNITS/#4006[20]/AXES/XYZ]",
            "DPRNT[MEIMAD/OFFSET/SESSION/WO-10-#31[80]-#32[60]-1/X/#5221[44]/Y/#5222[44]/Z/#5223[44]]",
            "DPRNT[MEIMAD/OFFSET-END/SESSION/WO-10-#31[80]-#32[60]-1]",
            "G103 P0",
            "M30",
            "%",
            ""
        ], report.Lines);
        var message = PrintMacroB(report, new() { [5221] = 125.402, [5222] = -80.016, [5223] = 42.691 });
        Assert.Equal("G54", message.WorkOffset);
        Assert.Equal(125.402, message.Values["X"], 6);
        Assert.Equal("MM", message.Units);
    }

    [Fact]
    public void FANUC_opens_the_print_channel_and_reads_G54_1_P_offsets_from_7001_and_14001()
    {
        var request = G54Request with
        {
            MachineTag = "11",
            Offsets = [WorkOffsetNames.Parse("G54.1 P2")!, WorkOffsetNames.Parse("G54.1 P60")!],
            Axes = NcWorkOffsetReports.ParseAxes("X,Y,Z,B,C", haas: false)
        };
        var report = NcDialects.Profile(NcDialects.FanucMacroB).WorkOffsetReport(request);
        Assert.Equal("work-offsets/O1992.nc", report.FileName);
        Assert.DoesNotContain(report.Lines, line => line.StartsWith("G103", StringComparison.Ordinal));
        Assert.Equal("POPEN", report.Lines[report.Lines.ToList().IndexOf("#32=#3012") + 1]);
        Assert.Contains("PCLOS", report.Lines);
        Assert.Contains("DPRNT[MEIMAD/OFFSET/SESSION/WO-11-#31[80]-#32[60]-1/OFFSET/G541P2/UNITS/#4006[20]/AXES/XYZBC]", report.Lines);
        Assert.Contains("DPRNT[MEIMAD/OFFSET/SESSION/WO-11-#31[80]-#32[60]-1/X/#7021[44]/Y/#7022[44]/Z/#7023[44]]", report.Lines);
        Assert.Contains("DPRNT[MEIMAD/OFFSET/SESSION/WO-11-#31[80]-#32[60]-1/B/#7024[44]/C/#7025[44]]", report.Lines);
        Assert.Contains("DPRNT[MEIMAD/OFFSET/SESSION/WO-11-#31[80]-#32[60]-2/X/#15181[44]/Y/#15182[44]/Z/#15183[44]]", report.Lines);

        var printed = PrintAll(report, new() { [7021] = 10, [7022] = 20, [7023] = 30, [7024] = 0, [7025] = 90, [15181] = 1, [15182] = 2, [15183] = 3, [15184] = 0, [15185] = 0 });
        Assert.Equal(2, printed.Count);
        Assert.Equal("G54.1 P2", printed[0].WorkOffset);
        Assert.Equal(90, printed[0].Values["C"]);
        Assert.Equal("G54.1 P60", printed[1].WorkOffset);
        Assert.NotEqual(printed[0].Session, printed[1].Session);
    }

    [Fact]
    public void Mazak_Matrix_uses_the_FANUC_macro_syntax_with_its_own_dialect_id()
    {
        var report = NcDialects.Profile(NcDialects.MazakMatrixEia).WorkOffsetReport(G54Request with
        {
            MachineTag = "12",
            Axes = NcWorkOffsetReports.ParseAxes("XYZ", haas: false)
        });
        Assert.Equal(NcDialects.MazakMatrixEia, report.Dialect);
        Assert.Contains("POPEN", report.Lines);
        var message = PrintMacroB(report, new() { [5221] = -400.5, [5222] = -200.25, [5223] = -300 });
        Assert.Equal("12", message.Machine);
        Assert.Equal(-400.5, message.Values["X"], 6);
    }

    [Fact]
    public void Haas_G154_offsets_read_14001_and_a_lathe_names_its_axes()
    {
        Assert.Equal(14001, NcWorkOffsetReports.MacroBVariable(WorkOffsetNames.Parse("G154 P1")!, 1, haas: true));
        Assert.Equal(15963, NcWorkOffsetReports.MacroBVariable(WorkOffsetNames.Parse("G154 P99")!, 3, haas: true));
        Assert.Equal(5321, NcWorkOffsetReports.MacroBVariable(WorkOffsetNames.Parse("G59")!, 1, haas: false));
        Assert.Equal(7941, NcWorkOffsetReports.MacroBVariable(WorkOffsetNames.Parse("G54.1 P48")!, 1, haas: false));
        Assert.Equal(14961, NcWorkOffsetReports.MacroBVariable(WorkOffsetNames.Parse("G54.1 P49")!, 1, haas: false));
        Assert.Equal(
            [new NcWorkOffsetAxis("X", 1), new NcWorkOffsetAxis("Z", 2)],
            NcWorkOffsetReports.ParseAxes("X1,Z2", haas: false));
        Assert.Equal(new NcWorkOffsetAxis("B", 5), NcWorkOffsetReports.ParseAxes("XYZBC", haas: true)[3]);
        Assert.Equal(new NcWorkOffsetAxis("B", 4), NcWorkOffsetReports.ParseAxes("XYZBC", haas: false)[3]);
        Assert.Throws<WorkOffsetValidationException>(() => NcWorkOffsetReports.ParseAxes("XX", haas: false));
        Assert.Throws<WorkOffsetValidationException>(() => NcWorkOffsetReports.ParseAxes("AB", haas: false));
        Assert.Throws<WorkOffsetValidationException>(() => NcWorkOffsetReports.ParseAxes("Q", haas: false));
    }

    [Fact]
    public void Okuma_OSP_prints_with_PUT_and_WRITE_C_and_reads_VZOF_system_variables()
    {
        var request = G54Request with
        {
            MachineTag = "13",
            Offsets = [WorkOffsetNames.Parse("G15 H1")!],
            Axes = NcWorkOffsetReports.ParseAxes("X1,Z2", haas: false),
            SequenceVariable = 5
        };
        var report = NcDialects.Profile(NcDialects.OkumaOsp).WorkOffsetReport(request);
        Assert.Equal("work-offsets/O1992.MIN", report.FileName);
        Assert.DoesNotContain(report.Lines, line => line.Contains("DPRNT", StringComparison.Ordinal) || line.Contains('#'));
        Assert.DoesNotContain(report.Lines, line => line.StartsWith('O') || line == "%");
        Assert.Equal("M02", report.Lines[^2]);
        Assert.Contains("VC191=VZOFX[1]", report.Lines);
        Assert.Contains("VC191=VZOFZ[1]", report.Lines);
        Assert.Contains("PUT '-1/OFFSET/G15H1/UNITS/MM/AXES/XZ'", report.Lines);
        Assert.Contains("NMWO2 VC5=VC5+1", report.Lines);

        var message = PrintOkuma(report, new() { ["VZOFX[1]"] = 0.0, ["VZOFZ[1]"] = 412.3456 }, sequence: 42);
        Assert.Equal("G15 H1", message.WorkOffset);
        Assert.Equal("WO-13-42-1", message.Session);
        Assert.Equal(412.3456, message.Values["Z"], 6);
        Assert.Equal("XZ", message.DeclaredAxes);
    }

    [Theory]
    [InlineData(NcDialects.HaasNgc, "G54.1 P1")]
    [InlineData(NcDialects.FanucMacroB, "G154 P1")]
    [InlineData(NcDialects.OkumaOsp, "G54")]
    [InlineData(NcDialects.MazakMatrixEia, "G15 H1")]
    public void Each_control_names_only_its_own_work_offsets(string dialect, string offset) =>
        Assert.Null(NcDialects.Profile(dialect).NormalizeWorkOffset(WorkOffsetNames.Parse(offset)!));

    /// <summary>What a Haas/FANUC/Mazak control prints for the DPRNT lines, with blank padding like FANUC 6001#1.</summary>
    private static WorkOffsetMessage PrintMacroB(NcWorkOffsetReport report, Dictionary<int, double> variables) =>
        Assert.Single(PrintAll(report, variables));

    private static List<WorkOffsetMessage> PrintAll(NcWorkOffsetReport report, Dictionary<int, double> variables)
    {
        variables[31] = 20261012;
        variables[32] = 63000;
        variables[4006] = 21;
        var parser = new OffsetEvidenceParser();
        var messages = new List<WorkOffsetMessage>();
        foreach (var line in report.Lines.Where(line => line.StartsWith("DPRNT[", StringComparison.Ordinal)))
        {
            var text = line["DPRNT[".Length..^1];
            var printed = Regex.Replace(text, @"#(\d+)\[(\d)(\d)\]", match =>
            {
                var value = variables[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)];
                var decimals = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                return "  " + value.ToString(decimals == 0 ? "0" : "0." + new string('0', decimals), CultureInfo.InvariantCulture);
            });
            var result = parser.Accept(printed);
            if (result.Outcome == OffsetEvidenceParseOutcome.Completed) messages.Add(result.Message!);
            else Assert.Equal(OffsetEvidenceParseOutcome.Pending, result.Outcome);
        }
        return messages;
    }

    /// <summary>What an Okuma OSP control prints: PUT pieces joined until WRITE C, numbers right-aligned.</summary>
    private static WorkOffsetMessage PrintOkuma(NcWorkOffsetReport report, Dictionary<string, double> system, int sequence)
    {
        var parser = new OffsetEvidenceParser();
        var line = new StringBuilder();
        var common = new Dictionary<string, double> { ["VC5"] = sequence - 1 };
        WorkOffsetMessage? message = null;
        string? skipTo = null;
        foreach (var statement in report.Lines)
        {
            // The sample counter is in range, so the IF branches are not taken and GOTO jumps over the reset.
            if (skipTo is not null && !statement.StartsWith(skipTo + " ", StringComparison.Ordinal)) continue;
            skipTo = null;
            if (statement.StartsWith("IF [", StringComparison.Ordinal)) continue;
            if (statement.StartsWith("GOTO ", StringComparison.Ordinal)) { skipTo = statement[5..]; continue; }
            var assignment = Regex.Match(statement, @"^(?:NMWO\d )?(VC\d+)=(.+)$");
            if (assignment.Success)
            {
                var expression = assignment.Groups[2].Value;
                common[assignment.Groups[1].Value] = expression switch
                {
                    _ when system.TryGetValue(expression, out var value) => value,
                    _ when expression.StartsWith("ROUND[", StringComparison.Ordinal) => Math.Round(common[expression[6..^1]]),
                    _ when expression.EndsWith("+1", StringComparison.Ordinal) => common[expression[..^2]] + 1,
                    _ => double.Parse(expression, CultureInfo.InvariantCulture)
                };
                continue;
            }
            if (statement.StartsWith("PUT '", StringComparison.Ordinal)) line.Append(statement[5..^1]);
            else if (statement.StartsWith("PUT ", StringComparison.Ordinal))
            {
                var parts = statement[4..].Split(',');
                var decimals = int.Parse(parts[2], CultureInfo.InvariantCulture);
                var width = int.Parse(parts[1], CultureInfo.InvariantCulture);
                line.Append(common[parts[0]].ToString(decimals == 0 ? "0" : "0." + new string('0', decimals), CultureInfo.InvariantCulture).PadLeft(width));
            }
            else if (statement == "WRITE C")
            {
                var result = parser.Accept(line.ToString());
                line.Clear();
                if (result.Outcome == OffsetEvidenceParseOutcome.Completed) message = result.Message;
                else Assert.Equal(OffsetEvidenceParseOutcome.Pending, result.Outcome);
            }
        }
        return message!;
    }
}
