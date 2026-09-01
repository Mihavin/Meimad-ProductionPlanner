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
/// Validates the stable package-build placeholders in an immutable NC source template.
/// Executable verification content is generated only for a concrete Production Package.
/// </summary>
internal static partial class NcVerificationHookParser
{
    internal const int CurrentHookVersion = 1;

    internal static NcVerificationHook ParseRequired(IEnumerable<string> lines)
    {
        Line? firstExecutable = null;
        Line? placeholderLine = null;
        var placeholderCount = 0;
        var activeHookCount = 0;
        var cycleStartCount = 0;
        var cycleEndCount = 0;
        int? cycleStartLine = null;
        int? cycleEndLine = null;
        var lineNumber = 0;
        foreach (var text in lines)
        {
            lineNumber++;
            var line = new Line(lineNumber, text ?? string.Empty);
            var placeholder = PackageVerifyPlaceholder().Match(line.Text);
            if (placeholder.Success)
            {
                placeholderCount++;
                placeholderLine ??= line;
            }
            if (Marker().IsMatch(line.Text)) activeHookCount++;
            if (PackageCycleStartPlaceholder().IsMatch(line.Text))
            {
                cycleStartCount++;
                cycleStartLine ??= line.Number;
            }
            if (PackageCycleEndPlaceholder().IsMatch(line.Text))
            {
                cycleEndCount++;
                cycleEndLine ??= line.Number;
            }
            if (firstExecutable is null && !IsHeaderOrComment(line.Text))
                firstExecutable = line;
        }

        if (activeHookCount > 0)
            throw Invalid("verification_executable_not_allowed_in_template",
                "Released NC templates must contain package placeholders, not an always-active Meimad verification call.");
        if (placeholderCount == 0)
            throw Invalid("verification_placeholder_required",
                "Every released CNC template must contain `(MEIMAD PACKAGE VERIFY V1 NCID=xxxxxx)` before its first executable block.");
        if (placeholderCount != 1)
            throw Invalid("verification_placeholder_ambiguous",
                "The NC template must contain exactly one Meimad verification placeholder.");
        if (firstExecutable is not null && firstExecutable.Number < placeholderLine!.Number)
            throw Invalid("verification_placeholder_not_first",
                "The verification placeholder must precede the first executable NC block.");
        if (cycleStartCount != cycleEndCount || cycleStartCount > 1
            || (cycleStartLine is not null && cycleStartLine >= cycleEndLine))
            throw Invalid("verification_cycle_placeholders_invalid",
                "Cycle placeholders must be absent or contain exactly one START and one END marker.");

        var match = PackageVerifyPlaceholder().Match(placeholderLine!.Text);
        var token = int.Parse(match.Groups["token"].Value, CultureInfo.InvariantCulture);
        if (token < 100000)
            throw Invalid("verification_placeholder_invalid",
                "The NC identity token must be a six-digit integer from 100000 through 999999.");
        return new(CurrentHookVersion, NcVerificationHookInvocationKinds.G65, 9002,
            token, placeholderLine.Number);
    }

    internal static bool IsAcceptedHookBlock(string line) =>
        PackageVerifyPlaceholder().IsMatch(line);

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

    [GeneratedRegex(@"^\s*\(MEIMAD PACKAGE VERIFY V1 NCID=(?<token>\d{6})\)\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex PackageVerifyPlaceholder();

    [GeneratedRegex(@"^\s*\(MEIMAD PACKAGE CYCLE START V1\)\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex PackageCycleStartPlaceholder();

    [GeneratedRegex(@"^\s*\(MEIMAD PACKAGE CYCLE END V1\)\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex PackageCycleEndPlaceholder();

    [GeneratedRegex(@"^\s*(?:N\d+\s+)?G65\s+P(?<invocation>9\d{3})\s+A(?<token>\d{6})(?:\.0*)?\s*\(\s*MEIMAD\s+VERIFY\s+V1\s*\)\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex G65Hook();

    [GeneratedRegex(@"^\s*(?:N\d+\s+)?G(?<invocation>\d{1,3})\s+A(?<token>\d{6})(?:\.0*)?\s*\(\s*MEIMAD\s+VERIFY\s+V1\s*\)\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CustomHook();

    [GeneratedRegex(@"^\s*\([^)]*\)\s*;?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex FullLineComment();

    [GeneratedRegex(@"^\s*O\d{1,8}\b(?:\s*\([^)]*\))?\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProgramHeader();
}
