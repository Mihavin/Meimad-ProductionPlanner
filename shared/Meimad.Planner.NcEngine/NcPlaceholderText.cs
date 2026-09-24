using System.Text.RegularExpressions;

namespace Meimad.Planner.NcEngine;

/// <summary>
/// Prepares NC text for the engine without changing its line numbers, so viewer rows and engine
/// messages still point at the released file.
/// <list type="bullet">
/// <item><c>[[MEIMAD:KEY]]</c> tokens are Server-owned placeholders, not NC words: a standalone
/// placeholder line becomes a comment and an inline token in a comment becomes plain text.</item>
/// <item>Print-channel statements (<c>DPRNT</c>, <c>BPRNT</c>, and Okuma <c>PUT</c> / <c>WRITE</c>)
/// never move the machine. The mill interpreter already skips them; the lathe interpreter would
/// misread <c>DPRNT[...]</c> as a macro expression, so they become comments.</item>
/// </list>
/// </summary>
public static partial class NcPlaceholderText
{
    public static string ForEngine(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var hasToken = text.Contains("[[MEIMAD:", StringComparison.Ordinal);
        var hasPrint = PrintKeyword().IsMatch(text);
        if (!hasToken && !hasPrint) return text;
        var lines = NcTextFile.Normalize(text).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (PrintStatement().IsMatch(line))
            {
                lines[index] = $"({Comment(line)})";
                continue;
            }
            if (!line.Contains("[[MEIMAD:", StringComparison.Ordinal)) continue;
            var standalone = Standalone().Match(line);
            lines[index] = standalone.Success
                ? $"(MEIMAD {standalone.Groups["key"].Value})"
                : Token().Replace(line, match => $"MEIMAD-{match.Groups["key"].Value}");
        }
        return string.Join('\n', lines);
    }

    private static string Comment(string line) =>
        Token().Replace(line, match => $"MEIMAD-{match.Groups["key"].Value}")
            .Replace('(', '[')
            .Replace(')', ']')
            .Trim();

    [GeneratedRegex(@"^\s*\[\[MEIMAD:(?<key>[A-Z][A-Z0-9_]*)\]\]\s*;?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Standalone();

    [GeneratedRegex(@"\[\[MEIMAD:(?<key>[A-Z][A-Z0-9_]*)\]\]", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex(@"DPRNT|BPRNT|PUT|WRITE", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrintKeyword();

    [GeneratedRegex(@"^\s*(?:/\s*)?(?:N\d+\s*)?(?:DPRNT\s*\[|BPRNT\s*\[|PUT\s+['A-Z]|WRITE\s+[A-Z]\s*;?\s*$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrintStatement();
}
