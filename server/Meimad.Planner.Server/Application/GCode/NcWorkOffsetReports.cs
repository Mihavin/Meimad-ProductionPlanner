using System.Globalization;
using Meimad.Planner.Server.Application.MachineEvidence;

namespace Meimad.Planner.Server.Application.GCode;

/// <summary>One axis the report prints: its letter and the control's axis number (1 = first controlled axis).</summary>
internal sealed record NcWorkOffsetAxis(string Letter, int Number);

/// <summary>
/// What the work offset report program of one Machine, Case Operation and NC release prints.
/// <paramref name="MachineTag"/>, <paramref name="Operation"/>, <paramref name="Release"/> and
/// <paramref name="Program"/> are CNC-safe text (letters, digits, hyphens).
/// </summary>
internal sealed record NcWorkOffsetReportRequest(
    string MachineTag,
    string Operation,
    string Release,
    string? Program,
    IReadOnlyList<WorkOffsetName> Offsets,
    IReadOnlyList<NcWorkOffsetAxis> Axes,
    int SequenceVariable);

/// <summary>The rendered report program and where it belongs in a package or on the control.</summary>
internal sealed record NcWorkOffsetReport(string Dialect, string FileName, IReadOnlyList<string> Lines)
{
    internal string Text => string.Join("\r\n", Lines);
}

/// <summary>
/// Renders the measured work offset report (exporter definition §20.5-§20.7) in each control's own
/// syntax. After the setupist measures the part zero the program prints, per offset, the CNC-safe
/// framed message
/// <code>
/// MEIMAD/OFFSET-START/SESSION/WO-10-20261012-63000-1/MACHINE/10/PROGRAM/654321/OPERATION/20/RELEASE/654321
/// MEIMAD/OFFSET/SESSION/WO-10-20261012-63000-1/OFFSET/G54/UNITS/21/AXES/XYZ
/// MEIMAD/OFFSET/SESSION/WO-10-20261012-63000-1/X/125.4020/Y/-80.0160/Z/42.6910
/// MEIMAD/OFFSET-END/SESSION/WO-10-20261012-63000-1
/// </code>
/// on the Machine's DPRNT channel, which the Server parses with <see cref="OffsetEvidenceParser"/>.
/// No motion and no write: the program only reads the control's work offset variables.
/// <list type="bullet">
/// <item>Haas NGC: <c>DPRNT</c> between <c>G103 P1</c>/<c>G103 P0</c>; G54-G59 at #5221 + 20 per offset,
/// G154 P1-P99 at #14001 + 20 per offset; axis n at +n-1 (X Y Z A B C); units from #4006.</item>
/// <item>FANUC custom macro B: <c>POPEN</c>/<c>DPRNT</c>/<c>PCLOS</c>; G54-G59 at #5221 + 20 per offset,
/// G54.1 P1-P48 at #7001 + 20 per offset, P49-P300 at #14001 + 20 per offset; axis n at +n-1.</item>
/// <item>Mazak Matrix EIA: the FANUC form (same variables, POPEN/PCLOS tolerated by the DPR14 file device).</item>
/// <item>Okuma OSP: <c>PUT</c>/<c>WRITE C</c>; offset Hn read from the system variables <c>VZOFX[n]</c>,
/// <c>VZOFY[n]</c>, <c>VZOFZ[n]</c>, ... through scratch <c>VC191</c>; no date/time variable, so the
/// session number is the Machine's event-sequence common variable. Okuma OSP is not a FANUC dialect.</item>
/// </list>
/// The session joins the date (#3011) and time (#3012), captured once into locals #31/#32 so every line
/// of one report carries the same session, and the offset's position in the report.
/// </summary>
internal static class NcWorkOffsetReports
{
    internal const string ProgramComment = "(MEIMAD WORK OFFSET REPORT - NO MOTION)";

    internal static NcWorkOffsetReport MacroB(NcWorkOffsetReportRequest request, string dialect, bool haas)
    {
        var lines = new List<string> { "%", haas ? "O01992 (MEIMAD WORK OFFSET REPORT)" : "O1992 (MEIMAD WORK OFFSET REPORT)" };
        lines.AddRange(Comments(request));
        if (haas) lines.Add("G103 P1");
        lines.Add("#31=#3011");
        lines.Add("#32=#3012");
        if (!haas) lines.Add("POPEN");
        for (var index = 0; index < request.Offsets.Count; index++)
        {
            var offset = request.Offsets[index];
            var session = $"WO-{request.MachineTag}-#31[80]-#32[60]-{Invariant(index + 1)}";
            lines.Add($"DPRNT[{StartText(request, session)}]");
            lines.Add($"DPRNT[MEIMAD/OFFSET/SESSION/{session}/OFFSET/{offset.CncSafe}/UNITS/#4006[20]/AXES/{AxisLetters(request)}]");
            foreach (var group in Groups(request.Axes))
            {
                var values = string.Concat(group.Select(axis =>
                    $"/{axis.Letter}/#{Invariant(MacroBVariable(offset, axis.Number, haas))}[44]"));
                lines.Add($"DPRNT[MEIMAD/OFFSET/SESSION/{session}{values}]");
            }
            lines.Add($"DPRNT[MEIMAD/OFFSET-END/SESSION/{session}]");
        }
        if (!haas) lines.Add("PCLOS");
        if (haas) lines.Add("G103 P0");
        lines.Add("M30");
        lines.Add("%");
        lines.Add(string.Empty);
        return new NcWorkOffsetReport(dialect, haas ? "work-offsets/O01992.nc" : "work-offsets/O1992.nc", lines);
    }

    internal static NcWorkOffsetReport Okuma(NcWorkOffsetReportRequest request, string dialect)
    {
        // A .MIN main program: no O line (it would turn the file into a subprogram), M02 at the end.
        var lines = new List<string> { "(MEIMAD WORK OFFSET REPORT)" };
        lines.AddRange(Comments(request));
        var sequence = "VC" + Invariant(request.SequenceVariable);
        lines.Add($"{sequence}=ROUND[{sequence}]");
        lines.Add($"IF [{sequence} LT 0] NMWO1");
        lines.Add($"IF [{sequence} GE 899999] NMWO1");
        lines.Add("GOTO NMWO2");
        lines.Add($"NMWO1 {sequence}=0");
        lines.Add($"NMWO2 {sequence}={sequence}+1");
        for (var index = 0; index < request.Offsets.Count; index++)
        {
            var offset = request.Offsets[index];
            var suffix = "-" + Invariant(index + 1);
            IEnumerable<string> Session(string prefix)
            {
                yield return $"PUT '{prefix}SESSION/WO-{request.MachineTag}-'";
                yield return $"PUT {sequence},6,0";
            }
            lines.AddRange(Session("MEIMAD/OFFSET-START/"));
            lines.Add($"PUT '{suffix}{StartText(request, null)}'");
            lines.Add("WRITE C");
            lines.AddRange(Session("MEIMAD/OFFSET/"));
            lines.Add($"PUT '{suffix}/OFFSET/{offset.CncSafe}/UNITS/MM/AXES/{AxisLetters(request)}'");
            lines.Add("WRITE C");
            foreach (var group in Groups(request.Axes))
            {
                lines.AddRange(Session("MEIMAD/OFFSET/"));
                lines.Add($"PUT '{suffix}'");
                foreach (var axis in group)
                {
                    lines.Add($"VC191=VZOF{axis.Letter}[{Invariant(offset.Number)}]");
                    lines.Add($"PUT '/{axis.Letter}/'");
                    lines.Add("PUT VC191,9,4");
                }
                lines.Add("WRITE C");
            }
            lines.AddRange(Session("MEIMAD/OFFSET-END/"));
            lines.Add($"PUT '{suffix}'");
            lines.Add("WRITE C");
        }
        lines.Add("M02");
        lines.Add(string.Empty);
        return new NcWorkOffsetReport(dialect, "work-offsets/O1992.MIN", lines);
    }

    /// <summary>
    /// The custom-macro-B variable of one axis of one work offset. Haas NGC: G54-G59 #5221-#5326,
    /// G154 P1-P99 #14001-#15986. FANUC/Mazak: G54-G59 #5221-#5340, G54.1 P1-P48 #7001-#7960,
    /// G54.1 P49-P300 #14961-#19980.
    /// </summary>
    internal static int MacroBVariable(WorkOffsetName offset, int axisNumber, bool haas)
    {
        var axis = axisNumber - 1;
        return offset.Family switch
        {
            WorkOffsetFamily.Standard => 5221 + 20 * (offset.Number - 54) + axis,
            WorkOffsetFamily.G154 when haas => 14001 + 20 * (offset.Number - 1) + axis,
            WorkOffsetFamily.G54Point1 when !haas && offset.Number <= 48 => 7001 + 20 * (offset.Number - 1) + axis,
            WorkOffsetFamily.G54Point1 when !haas => 14001 + 20 * (offset.Number - 1) + axis,
            _ => throw new ArgumentOutOfRangeException(nameof(offset), $"{offset.Canonical} has no macro variable on this control.")
        };
    }

    /// <summary>Axis letters and numbers from text such as <c>XYZ</c>, <c>X,Y,Z,B,C</c> or <c>X1,Z2</c>; Haas numbers X Y Z A B C as 1-6.</summary>
    internal static IReadOnlyList<NcWorkOffsetAxis> ParseAxes(string? text, bool haas)
    {
        var value = string.IsNullOrWhiteSpace(text) ? "XYZ" : text.Trim().ToUpperInvariant();
        var tokens = value.Contains(',')
            ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : value.Select(character => character.ToString()).ToArray();
        var result = new List<NcWorkOffsetAxis>();
        var nextRotary = 4;
        foreach (var token in tokens)
        {
            var letter = token[..1];
            if (!WorkOffsetAxes.All.Contains(letter))
                throw new WorkOffsetValidationException("invalid_axes", $"'{token}' is not an axis (X, Y, Z, A, B or C).");
            int number;
            if (token.Length > 1)
            {
                if (!int.TryParse(token[1..], NumberStyles.None, CultureInfo.InvariantCulture, out number) || number is < 1 or > 20)
                    throw new WorkOffsetValidationException("invalid_axes", $"'{token}' needs an axis number from 1 to 20.");
            }
            else if (haas)
            {
                number = WorkOffsetAxes.All.ToList().IndexOf(letter) + 1;
            }
            else
            {
                number = letter switch { "X" => 1, "Y" => 2, "Z" => 3, _ => nextRotary++ };
            }
            if (result.Any(axis => axis.Letter == letter || axis.Number == number))
                throw new WorkOffsetValidationException("invalid_axes", $"Axis {letter} or number {number} is listed twice.");
            result.Add(new NcWorkOffsetAxis(letter, number));
        }
        if (!result.Any(axis => WorkOffsetAxes.Linear.Contains(axis.Letter)))
            throw new WorkOffsetValidationException("invalid_axes", "The report needs at least one of X, Y and Z.");
        return result;
    }

    private static IEnumerable<string> Comments(NcWorkOffsetReportRequest request)
    {
        yield return ProgramComment;
        yield return $"(MACHINE {request.MachineTag} - OPERATION {request.Operation} - NC RELEASE {request.Release})";
        yield return $"(OFFSETS {string.Join(" ", request.Offsets.Select(offset => offset.CncSafe))} - RUN AFTER MEASURING)";
    }

    private static string StartText(NcWorkOffsetReportRequest request, string? session) =>
        (session is null ? string.Empty : $"MEIMAD/OFFSET-START/SESSION/{session}")
        + $"/MACHINE/{request.MachineTag}"
        + (request.Program is null ? string.Empty : $"/PROGRAM/{request.Program}")
        + $"/OPERATION/{request.Operation}/RELEASE/{request.Release}";

    private static string AxisLetters(NcWorkOffsetReportRequest request) =>
        string.Concat(request.Axes.Select(axis => axis.Letter));

    /// <summary>At most three axes per printed line keeps every line short enough for any control.</summary>
    private static IEnumerable<IReadOnlyList<NcWorkOffsetAxis>> Groups(IReadOnlyList<NcWorkOffsetAxis> axes)
    {
        for (var index = 0; index < axes.Count; index += 3)
            yield return axes.Skip(index).Take(3).ToArray();
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
