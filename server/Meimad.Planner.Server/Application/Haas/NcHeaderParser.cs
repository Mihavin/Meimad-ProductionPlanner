using System.Text.RegularExpressions;
using Meimad.Planner.Server.Domain.Haas;

namespace Meimad.Planner.Server.Application.Haas;

internal sealed class NcHeaderParser : INcHeaderParser
{
    internal const string CurrentVersion = "haas-header-v1";
    internal static readonly IReadOnlyList<string> DefaultPartPatterns =
        [
            @"\bPART(?:\s+NAME)?\s*[:=]\s*([^()\r\n]+)",
            // Meimad CAM output: O1000 (16E2509-7PSOFI-1_NC1)
            @"(?im)^\s*O\d{1,8}\s*\(\s*([A-Za-z0-9][A-Za-z0-9.-]*)(?:_NC\d+)?\s*\)"
        ];

    public NcHeaderMetadata Parse(
        IEnumerable<string> lines,
        IReadOnlyList<string>? partPatterns = null)
    {
        var headerLines = lines.Take(200).Select(value => value ?? string.Empty).ToArray();
        var rawHeader = string.Join("\n", headerLines);
        var patterns = partPatterns is { Count: > 0 } ? partPatterns : DefaultPartPatterns;
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in patterns)
        {
            Regex regex;
            try
            {
                regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException exception)
            {
                throw new HaasValidationException("headerPartPatterns", $"Invalid NC header pattern: {exception.Message}");
            }

            foreach (Match match in regex.Matches(rawHeader))
            {
                var value = Clean(match.Groups.Count > 1 ? match.Groups[1].Value : match.Value);
                if (!string.IsNullOrEmpty(value))
                {
                    parts.Add(value);
                }
            }
        }

        var partName = parts.Count == 1 ? parts.Single() : null;
        return new NcHeaderMetadata(
            partName is null ? "HEADER_INVALID" : "VALID",
            partName,
            FindValue(rawHeader, @"\bCASE(?:\s+NUMBER)?\s*[:=]\s*([^()\r\n]+)"),
            FindValue(rawHeader, @"\bOP(?:ERATION)?\s*[:=]\s*([^()\r\n]+)"),
            FindValue(rawHeader, @"\bREV(?:ISION)?\s*[:=]\s*([^()\r\n]+)"),
            Regex.Match(rawHeader, @"(?im)^\s*O(?<number>\d{1,8})\b") is { Success: true } program
                ? $"O{program.Groups["number"].Value}"
                : null,
            rawHeader,
            CurrentVersion);
    }

    private static string? FindValue(string text, string pattern)
    {
        var match = Regex.Match(text, pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        return match.Success ? Clean(match.Groups[1].Value) : null;
    }

    private static string? Clean(string value)
    {
        var normalized = value.Trim().Trim('(', ')', '[', ']', ';').Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
