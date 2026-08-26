using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Meimad.Planner.Server.Domain.GCode;

namespace Meimad.Planner.Server.Application.GCode;

internal static partial class NcProgramParser
{
    internal const string CurrentVersion = "1.0.0";

    internal static async Task<NcProgramAnalysis> ParseAsync(
        string path,
        DateTimeOffset analyzedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            var lines = await File.ReadAllLinesAsync(path, cancellationToken);
            return Parse(lines, analyzedAt);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or DecoderFallbackException)
        {
            return Unavailable(analyzedAt, $"Estimate unavailable: NC file could not be parsed ({exception.GetType().Name}).");
        }
    }

    internal static NcProgramAnalysis Parse(
        IEnumerable<string> lines,
        DateTimeOffset analyzedAt)
    {
        var state = new ParserState();
        var warnings = new List<string>();
        var unsupported = new HashSet<string>(StringComparer.Ordinal);
        var lineNumber = 0;

        foreach (var original in lines)
        {
            lineNumber++;
            // The required verification invocation is control flow, not machining motion.
            // It has already been strictly validated before release publication.
            if (NcVerificationHookParser.IsAcceptedHookBlock(original)) continue;
            var block = StripComments(original).Trim().ToUpperInvariant();
            if (block.Length == 0 || block == "%") continue;

            if (block.Contains('#'))
            {
                AddUnsupported(unsupported, warnings, "MACRO_VARIABLE",
                    $"Line {lineNumber}: estimate contains an unresolved macro variable.");
            }

            var words = WordRegex().Matches(block)
                .Select(match => new Word(match.Groups[1].Value[0],
                    double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)))
                .ToArray();
            if (words.Length == 0)
            {
                warnings.Add($"Line {lineNumber}: malformed or unsupported block was ignored.");
                continue;
            }

            var gCodes = words.Where(value => value.Letter == 'G').Select(value => value.Value).ToArray();
            var mCodes = words.Where(value => value.Letter == 'M').Select(value => value.Value).ToArray();

            foreach (var code in gCodes)
            {
                var integral = (int)Math.Truncate(code);
                if (integral is >= 81 and <= 89)
                {
                    AddUnsupported(unsupported, warnings, $"G{integral}",
                        $"Line {lineNumber}: estimate excludes unsupported canned cycle G{integral}.");
                }
                else if (code is 93 or 95)
                {
                    state.FeedModeSupported = false;
                    AddUnsupported(unsupported, warnings, $"G{FormatCode(code)}",
                        $"Line {lineNumber}: {(code == 93 ? "inverse-time feed G93" : "feed-per-revolution G95")} is not supported.");
                }
                else if (code == 94)
                {
                    state.FeedModeSupported = true;
                }
                else if (integral is 68 or 69 || code is 43.4 or 43.5 or 234)
                {
                    AddUnsupported(unsupported, warnings, $"G{FormatCode(code)}",
                        $"Line {lineNumber}: coordinate transformation/TCP command G{FormatCode(code)} is not supported.");
                }
                else if (code is 90.1 or 91.1)
                {
                    AddUnsupported(unsupported, warnings, $"G{FormatCode(code)}",
                        $"Line {lineNumber}: arc-center mode G{FormatCode(code)} is not supported; I/J/K are treated as offsets.");
                }
            }

            if (mCodes.Any(value => (int)value == 98))
            {
                AddUnsupported(unsupported, warnings, "M98",
                    $"Line {lineNumber}: estimate excludes unsupported subprogram call M98.");
            }

            if (words.Any(value => value.Letter is 'A' or 'B' or 'C'))
            {
                AddUnsupported(unsupported, warnings, "ROTARY_AXIS",
                    $"Line {lineNumber}: rotary/5-axis motion is not included in the estimate.");
            }

            ApplyModes(state, gCodes);
            var feed = Last(words, 'F');
            if (feed.HasValue)
            {
                state.FeedMillimetersPerMinute = feed.Value * state.UnitScale;
            }

            if (mCodes.Any(value => (int)value == 6)) state.ToolChangeCount++;

            if (gCodes.Any(value => (int)value == 4))
            {
                var seconds = Last(words, 'X');
                var milliseconds = Last(words, 'P');
                if (seconds is >= 0)
                {
                    state.DwellSeconds += seconds.Value;
                }
                else if (milliseconds is >= 0)
                {
                    state.DwellSeconds += milliseconds.Value / 1000d;
                }
                else
                {
                    warnings.Add($"Line {lineNumber}: G4 dwell format was not recognized.");
                }
                continue;
            }

            if (gCodes.Any(value => (int)value is >= 81 and <= 89)
                || block.Contains('#'))
            {
                continue;
            }

            var explicitMotion = gCodes.LastOrDefault(value => (int)value is >= 0 and <= 3, double.NaN);
            if (!double.IsNaN(explicitMotion)) state.MotionMode = (int)explicitMotion;
            if (state.MotionMode is null || !HasCoordinate(words)) continue;

            var end = Endpoint(state, words);
            var motionDistance = state.MotionMode is 2 or 3
                ? ArcDistance(state, end, words, state.MotionMode == 2, lineNumber, warnings)
                : Distance(state.X, state.Y, state.Z, end.X, end.Y, end.Z);

            if (motionDistance.HasValue)
            {
                if (state.MotionMode == 0)
                {
                    state.RapidDistanceMillimeters += motionDistance.Value;
                }
                else if (!state.FeedModeSupported)
                {
                    warnings.Add($"Line {lineNumber}: feed motion was excluded while an unsupported feed mode was active.");
                }
                else if (state.FeedMillimetersPerMinute is null or <= 0)
                {
                    warnings.Add($"Line {lineNumber}: feed motion has no positive programmed F value and was excluded.");
                }
                else
                {
                    state.FeedMotionSeconds += motionDistance.Value
                        / state.FeedMillimetersPerMinute.Value * 60d;
                }
            }

            state.X = end.X;
            state.Y = end.Y;
            state.Z = end.Z;
        }

        var distinctWarnings = warnings.Distinct(StringComparer.Ordinal).ToArray();
        var confidence = unsupported.Count > 0
            ? NcEstimateConfidence.Low
            : distinctWarnings.Length > 0 ? NcEstimateConfidence.Medium : NcEstimateConfidence.High;
        return new NcProgramAnalysis(
            CurrentVersion,
            unsupported.Count > 0 || distinctWarnings.Length > 0
                ? NcAnalysisStatus.Partial : NcAnalysisStatus.Complete,
            state.FeedMotionSeconds,
            state.RapidDistanceMillimeters,
            state.ToolChangeCount,
            state.DwellSeconds,
            state.DetectedUnits,
            distinctWarnings,
            unsupported.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            confidence,
            analyzedAt);
    }

    internal static NcProgramAnalysis Unavailable(DateTimeOffset at, string warning) => new(
        CurrentVersion, NcAnalysisStatus.Unavailable, 0, 0, 0, 0, null,
        [warning], [], NcEstimateConfidence.Unavailable, at);

    private static void ApplyModes(ParserState state, IEnumerable<double> codes)
    {
        foreach (var code in codes)
        {
            switch (code)
            {
                case 17: state.Plane = 17; break;
                case 18: state.Plane = 18; break;
                case 19: state.Plane = 19; break;
                case 20: state.UnitScale = 25.4; state.DetectedUnits = "INCH"; break;
                case 21: state.UnitScale = 1; state.DetectedUnits = "MILLIMETER"; break;
                case 90: state.Absolute = true; break;
                case 91: state.Absolute = false; break;
            }
        }
    }

    private static Point Endpoint(ParserState state, IReadOnlyList<Word> words)
    {
        var x = Last(words, 'X');
        var y = Last(words, 'Y');
        var z = Last(words, 'Z');
        return state.Absolute
            ? new Point(x.HasValue ? x.Value * state.UnitScale : state.X,
                y.HasValue ? y.Value * state.UnitScale : state.Y,
                z.HasValue ? z.Value * state.UnitScale : state.Z)
            : new Point(state.X + (x ?? 0) * state.UnitScale,
                state.Y + (y ?? 0) * state.UnitScale,
                state.Z + (z ?? 0) * state.UnitScale);
    }

    private static double? ArcDistance(
        ParserState state,
        Point end,
        IReadOnlyList<Word> words,
        bool clockwise,
        int lineNumber,
        ICollection<string> warnings)
    {
        var (startA, startB, endA, endB, orthogonalDelta, firstOffset, secondOffset) = state.Plane switch
        {
            18 => (state.X, state.Z, end.X, end.Z, end.Y - state.Y, 'I', 'K'),
            19 => (state.Y, state.Z, end.Y, end.Z, end.X - state.X, 'J', 'K'),
            _ => (state.X, state.Y, end.X, end.Y, end.Z - state.Z, 'I', 'J')
        };
        var offsetA = Last(words, firstOffset);
        var offsetB = Last(words, secondOffset);
        double planarLength;
        if (offsetA.HasValue || offsetB.HasValue)
        {
            var centerA = startA + (offsetA ?? 0) * state.UnitScale;
            var centerB = startB + (offsetB ?? 0) * state.UnitScale;
            var startRadius = Distance(startA, startB, 0, centerA, centerB, 0);
            var endRadius = Distance(endA, endB, 0, centerA, centerB, 0);
            if (startRadius <= 0 || Math.Abs(startRadius - endRadius) > Math.Max(0.01, startRadius * 0.01))
            {
                warnings.Add($"Line {lineNumber}: arc center/radius is inconsistent and was excluded.");
                return null;
            }
            var startAngle = Math.Atan2(startB - centerB, startA - centerA);
            var endAngle = Math.Atan2(endB - centerB, endA - centerA);
            var sweep = Sweep(startAngle, endAngle, clockwise,
                Math.Abs(endA - startA) < 1e-9 && Math.Abs(endB - startB) < 1e-9);
            planarLength = startRadius * sweep;
        }
        else if (Last(words, 'R') is { } radiusWord)
        {
            var radius = Math.Abs(radiusWord * state.UnitScale);
            var chord = Math.Sqrt(Math.Pow(endA - startA, 2) + Math.Pow(endB - startB, 2));
            if (radius <= 0 || chord > 2 * radius + 1e-9 || chord <= 0)
            {
                warnings.Add($"Line {lineNumber}: R-format arc geometry is invalid and was excluded.");
                return null;
            }
            var sweep = 2 * Math.Asin(Math.Min(1, chord / (2 * radius)));
            if (radiusWord < 0) sweep = 2 * Math.PI - sweep;
            planarLength = radius * sweep;
        }
        else
        {
            warnings.Add($"Line {lineNumber}: arc lacks a supported I/J/K or R definition and was excluded.");
            return null;
        }

        return Math.Sqrt(planarLength * planarLength + orthogonalDelta * orthogonalDelta);
    }

    private static double Sweep(double start, double end, bool clockwise, bool fullCircle)
    {
        if (fullCircle) return 2 * Math.PI;
        var delta = clockwise ? start - end : end - start;
        while (delta < 0) delta += 2 * Math.PI;
        while (delta >= 2 * Math.PI) delta -= 2 * Math.PI;
        return delta;
    }

    private static bool HasCoordinate(IEnumerable<Word> words) =>
        words.Any(value => value.Letter is 'X' or 'Y' or 'Z');

    private static double? Last(IEnumerable<Word> words, char letter) =>
        words.Where(value => value.Letter == letter).Select(value => (double?)value.Value).LastOrDefault();

    private static double Distance(double x1, double y1, double z1, double x2, double y2, double z2) =>
        Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2) + Math.Pow(z2 - z1, 2));

    private static string StripComments(string value) =>
        SemicolonCommentRegex().Replace(ParenthesisCommentRegex().Replace(value, " "), string.Empty);

    private static void AddUnsupported(
        ISet<string> unsupported,
        ICollection<string> warnings,
        string construct,
        string warning)
    {
        unsupported.Add(construct);
        warnings.Add(warning);
    }

    private static string FormatCode(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"([A-Z])\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex ParenthesisCommentRegex();

    [GeneratedRegex(@";.*$")]
    private static partial Regex SemicolonCommentRegex();

    private readonly record struct Word(char Letter, double Value);
    private readonly record struct Point(double X, double Y, double Z);

    private sealed class ParserState
    {
        internal double X { get; set; }
        internal double Y { get; set; }
        internal double Z { get; set; }
        internal bool Absolute { get; set; } = true;
        internal double UnitScale { get; set; } = 1;
        internal string? DetectedUnits { get; set; }
        internal int Plane { get; set; } = 17;
        internal int? MotionMode { get; set; }
        internal bool FeedModeSupported { get; set; } = true;
        internal double? FeedMillimetersPerMinute { get; set; }
        internal double FeedMotionSeconds { get; set; }
        internal double RapidDistanceMillimeters { get; set; }
        internal int ToolChangeCount { get; set; }
        internal double DwellSeconds { get; set; }
    }
}
