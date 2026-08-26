using System.Globalization;
using System.Text.RegularExpressions;

namespace Meimad.Planner.Server.Application.GCode;

internal static class NcVerificationHookInvocationKinds
{
    internal const string G65 = "G65";
    internal const string CustomGCode = "CUSTOM_GCODE";
}

internal sealed record NcVerificationHook(
    int HookVersion,
    string InvocationKind,
    int InvocationNumber,
    int NcIdentityToken,
    int LineNumber);

/// <summary>
/// Validates the accepted fallback hook as the first executable NC block.
/// The Server never inserts or rewrites this hook in an uploaded program.
/// </summary>
internal static partial class NcVerificationHookParser
{
    internal const int CurrentHookVersion = 1;

    internal static NcVerificationHook ParseRequired(IEnumerable<string> lines)
    {
        Line? firstExecutable = null;
        Line? markerLine = null;
        var markerCount = 0;
        var lineNumber = 0;
        foreach (var text in lines)
        {
            lineNumber++;
            var line = new Line(lineNumber, text ?? string.Empty);
            if (Marker().IsMatch(line.Text))
            {
                markerCount++;
                markerLine ??= line;
            }
            if (firstExecutable is null && !IsHeaderOrComment(line.Text))
                firstExecutable = line;
        }

        if (markerCount == 0)
            throw Invalid("verification_hook_required",
                "Every approved NC program must contain a Meimad verification hook as its first executable block.");
        if (markerCount != 1)
            throw Invalid("verification_hook_ambiguous",
                "The NC program must contain exactly one Meimad verification hook.");

        if (firstExecutable is null || firstExecutable.Number != markerLine!.Number)
            throw Invalid("verification_hook_not_first",
                "The Meimad verification hook must be the first executable NC block, before motion, spindle, or tool commands.");

        var match = G65Hook().Match(firstExecutable.Text);
        if (match.Success)
            return Hook(match, NcVerificationHookInvocationKinds.G65,
                int.Parse(match.Groups["invocation"].Value, CultureInfo.InvariantCulture),
                firstExecutable.Number);

        match = CustomHook().Match(firstExecutable.Text);
        if (match.Success)
        {
            var alias = int.Parse(match.Groups["invocation"].Value, CultureInfo.InvariantCulture);
            if (alias is 0 or 65)
                throw Invalid("verification_hook_invalid",
                    "A custom hook must use G1 through G999, excluding G65; G65 requires a P9xxx address.");
            return Hook(match, NcVerificationHookInvocationKinds.CustomGCode, alias,
                firstExecutable.Number);
        }

        throw Invalid("verification_hook_invalid",
            "Use `G65 P9xxx Axxxxxx. (MEIMAD VERIFY V1)` or `Gxxx Axxxxxx. (MEIMAD VERIFY V1)` as the first executable block.");
    }

    internal static bool IsAcceptedHookBlock(string line) =>
        G65Hook().IsMatch(line) || CustomHook().IsMatch(line);

    private static NcVerificationHook Hook(Match match, string kind, int invocation, int lineNumber)
    {
        var token = int.Parse(match.Groups["token"].Value, CultureInfo.InvariantCulture);
        if (token < 100000)
            throw Invalid("verification_hook_invalid",
                "The NC identity token must be a six-digit integer from 100000 through 999999.");
        return new(CurrentHookVersion, kind, invocation, token, lineNumber);
    }

    private static bool IsHeaderOrComment(string line)
    {
        var value = line.Trim();
        if (value.Length == 0 || value == "%") return true;
        if (FullLineComment().IsMatch(value)) return true;
        return ProgramHeader().IsMatch(value);
    }

    private static GCodeValidationException Invalid(string code, string message) =>
        new("gCodeFile", code, message);

    private sealed record Line(int Number, string Text);

    [GeneratedRegex(@"\(\s*MEIMAD\s+VERIFY\s+V1\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Marker();

    [GeneratedRegex(@"^\s*(?:N\d+\s+)?G65\s+P(?<invocation>9\d{3})\s+A(?<token>\d{6})(?:\.0*)?\s*\(\s*MEIMAD\s+VERIFY\s+V1\s*\)\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex G65Hook();

    [GeneratedRegex(@"^\s*(?:N\d+\s+)?G(?<invocation>\d{1,3})\s+A(?<token>\d{6})(?:\.0*)?\s*\(\s*MEIMAD\s+VERIFY\s+V1\s*\)\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CustomHook();

    [GeneratedRegex(@"^\s*\([^)]*\)\s*;?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex FullLineComment();

    [GeneratedRegex(@"^\s*O\d{1,8}\b(?:\s*\([^)]*\))?\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProgramHeader();
}
