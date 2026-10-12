using System.Globalization;
using System.Text.RegularExpressions;
using Meimad.Planner.SimulationPackage.Coordinates;

namespace Meimad.Planner.SimulationPackage.Nc;

/// <summary>What the package validator needs to know about the posted NC program.</summary>
public sealed record NcProgramFacts(
    bool HasHeader,
    string? ProgramNumber,
    IReadOnlyList<int> ToolNumbers,
    string? Units,
    bool UsesRotaryAxes,
    bool UsesTiltedPlane,
    bool UsesIncrementalMoves,
    BoundingBox? CuttingBounds,
    BoundingBox? AllMoveBounds,
    int LineCount);

/// <summary>
/// A light, dialect-tolerant scan of a posted NC program (FANUC/Haas/Mazak EIA style word addresses):
/// the header (<c>%</c>, <c>O</c>/<c>:</c> program number or a leading comment), the T-numbers called,
/// the G20/G21 units, rotary-axis and tilted-plane use, and the linear-axis bounds of absolute moves.
/// It is not a simulator: arcs contribute their end points only. The NC text is never modified.
/// </summary>
public static partial class NcProgramScanner
{
    private const int HeaderWindow = 20;

    public static NcProgramFacts Scan(IEnumerable<string> lines)
    {
        var tools = new SortedSet<int>();
        string? program = null;
        var headerSeen = false;
        var codeSeen = false;
        string? units = null;
        var rotary = false;
        var tilted = false;
        var incremental = false;
        var absolute = true;
        var motion = 0; // 0 rapid, 1 feed
        double? x = null, y = null, z = null;
        var cutting = new List<Vector3d>();
        var all = new List<Vector3d>();
        var index = 0;
        var nonBlank = 0;
        foreach (var raw in lines)
        {
            index++;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            nonBlank++;
            var code = Comment().Replace(line, " ");
            var semicolon = code.IndexOf(';');
            if (semicolon >= 0) code = code[..semicolon];
            code = code.Trim().ToUpperInvariant();
            if (nonBlank <= HeaderWindow)
            {
                if (line == "%" || line.StartsWith('(')) headerSeen = true;
                var number = ProgramNumber().Match(code);
                if (number.Success)
                {
                    headerSeen = true;
                    program ??= number.Value;
                }
            }

            if (code.Length == 0 || code == "%") continue;
            if (Word().IsMatch(code)) codeSeen = true;
            if (code.Contains("G65", StringComparison.Ordinal) || code.Contains('#')) continue; // macro arguments are not tool calls or coordinates

            var notToolpath = false;
            foreach (Match g in GWord().Matches(code))
            {
                switch (g.Groups["value"].Value)
                {
                    case "0": case "00": case "80": motion = 0; break;
                    case "1": case "01": case "2": case "02": case "3": case "03": motion = 1; break;
                    case "73": case "76": case "81": case "82": case "83": case "84": case "85": case "86": case "87": case "88": case "89": motion = 1; break;
                    // Reference returns, machine coordinates, offset settings and frame definitions carry
                    // X/Y/Z words that are not toolpath.
                    case "10": case "28": case "30": case "52": case "53": case "92": notToolpath = true; break;
                    case "20": units ??= "inch"; if (units != "inch") units = "mixed"; break;
                    case "21": units ??= "mm"; if (units != "mm") units = "mixed"; break;
                    case "90": absolute = true; break;
                    case "91": absolute = false; break;
                    case "68": case "68.2": case "68.3": case "68.4": tilted = true; notToolpath = true; break;
                    case "43.4": case "43.5": case "53.1": tilted = true; break;
                }
            }

            foreach (Match t in TWord().Matches(code))
            {
                if (int.TryParse(t.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var tool) && tool > 0)
                    tools.Add(tool);
            }

            if (RotaryWord().IsMatch(code)) rotary = true;
            if (notToolpath) continue;

            var moved = false;
            foreach (Match axis in LinearWord().Matches(code))
            {
                if (!double.TryParse(axis.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
                if (!absolute)
                {
                    incremental = true; // an incremental toolpath move: bounds are not tracked
                    continue;
                }

                moved = true;
                switch (axis.Groups["axis"].Value)
                {
                    case "X": x = value; break;
                    case "Y": y = value; break;
                    default: z = value; break;
                }
            }

            if (moved && x is not null && y is not null && z is not null)
            {
                var point = new Vector3d(x.Value, y.Value, z.Value);
                all.Add(point);
                if (motion == 1) cutting.Add(point);
            }
        }

        return new NcProgramFacts(
            headerSeen && codeSeen,
            program,
            tools.ToArray(),
            units,
            rotary,
            tilted,
            incremental,
            BoundingBox.Of(cutting),
            BoundingBox.Of(all),
            index);
    }

    public static NcProgramFacts ScanFile(string path) => Scan(File.ReadLines(path));

    [GeneratedRegex(@"\([^)]*\)?", RegexOptions.CultureInvariant)]
    private static partial Regex Comment();

    [GeneratedRegex(@"^(O\d{1,8}|:\d{1,8}|O<[^>]+>)", RegexOptions.CultureInvariant)]
    private static partial Regex ProgramNumber();

    [GeneratedRegex(@"[GMXYZ]-?\d", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    [GeneratedRegex(@"(?<![A-Z])G(?<value>\d{1,2}(\.\d)?)(?![\d])", RegexOptions.CultureInvariant)]
    private static partial Regex GWord();

    [GeneratedRegex(@"(?<![A-Z])T(?<value>\d{1,5})(?![\d.])", RegexOptions.CultureInvariant)]
    private static partial Regex TWord();

    [GeneratedRegex(@"(?<![A-Z])[ABC]\s*[-+]?\d", RegexOptions.CultureInvariant)]
    private static partial Regex RotaryWord();

    [GeneratedRegex(@"(?<![A-Z])(?<axis>[XYZ])\s*(?<value>[-+]?(\d+\.?\d*|\.\d+))", RegexOptions.CultureInvariant)]
    private static partial Regex LinearWord();
}
