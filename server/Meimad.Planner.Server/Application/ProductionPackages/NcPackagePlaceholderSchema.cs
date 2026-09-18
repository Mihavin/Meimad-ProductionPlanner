using System.Text.RegularExpressions;

namespace Meimad.Planner.Server.Application.ProductionPackages;

internal static class NcPackagePlaceholderKeys
{
    internal const string PartName = "PART_NAME";
    internal const string OperationName = "OPERATION_NAME";
    internal const string ProductionRunId = "PRODUCTION_RUN_ID";
    internal const string ProductionPackageId = "PRODUCTION_PACKAGE_ID";
    internal const string MachineId = "MACHINE_ID";
    internal const string NcReleaseId = "NC_RELEASE_ID";
    internal const string OffsetLoaderReleaseId = "OFFSET_LOADER_RELEASE_ID";
    internal const string EventContext = "EVENT_CONTEXT";
    internal const string VerificationHook = "VERIFICATION_HOOK";
    internal const string CycleStart = "CYCLE_START";
    internal const string CycleEnd = "CYCLE_END";
}

internal sealed record NcPackageTemplateValidation(
    int ProtocolVersion,
    int VerificationHookLineNumber,
    IReadOnlyDictionary<string, int> Counts);

/// <summary>
/// Structural parser for canonical server-owned NC package placeholders. Legacy V1
/// markers are handled separately and only as an explicit compatibility protocol.
/// </summary>
internal static partial class NcPackagePlaceholderSchema
{
    internal const int CurrentProtocolVersion = 2;

    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        NcPackagePlaceholderKeys.PartName,
        NcPackagePlaceholderKeys.OperationName,
        NcPackagePlaceholderKeys.ProductionRunId,
        NcPackagePlaceholderKeys.ProductionPackageId,
        NcPackagePlaceholderKeys.MachineId,
        NcPackagePlaceholderKeys.NcReleaseId,
        NcPackagePlaceholderKeys.OffsetLoaderReleaseId,
        NcPackagePlaceholderKeys.EventContext,
        NcPackagePlaceholderKeys.VerificationHook,
        NcPackagePlaceholderKeys.CycleStart,
        NcPackagePlaceholderKeys.CycleEnd
    };

    // CYCLE_START/CYCLE_END are the canonical-protocol equivalent of the legacy V1
    // "(MEIMAD PACKAGE CYCLE START/END V1)" markers that drive real part counting
    // (SqliteProductionRunCycleAccounting). Since 2026-09-18 every canonical template
    // must carry exactly one pair, validated (required, matched, ordered) with its own
    // codes in ValidateCycleMarkerPair, so it is deliberately NOT in UniqueRequiredKeys.
    private static readonly string[] UniqueRequiredKeys =
    [
        NcPackagePlaceholderKeys.ProductionRunId,
        NcPackagePlaceholderKeys.ProductionPackageId,
        NcPackagePlaceholderKeys.MachineId,
        NcPackagePlaceholderKeys.NcReleaseId,
        NcPackagePlaceholderKeys.OffsetLoaderReleaseId,
        NcPackagePlaceholderKeys.EventContext,
        NcPackagePlaceholderKeys.VerificationHook
    ];

    internal static bool IsCanonical(IEnumerable<string> lines) =>
        lines.Any(line => (line ?? string.Empty).Contains("[[MEIMAD:", StringComparison.Ordinal));

    /// <summary>
    /// The immutable-release gate validates only the verification insertion point. Other
    /// package metadata is resolved and validated later, when a concrete package is built.
    /// Ordinary O/G/M codes and unrelated macro calls are deliberately outside this check.
    /// </summary>
    internal static NcPackageTemplateValidation ValidateReleaseTemplate(
        IEnumerable<string> sourceLines)
    {
        var lines = sourceLines.ToArray();
        var hookLines = new List<int>();
        int? firstExecutable = null;
        for (var index = 0; index < lines.Length; index++)
        {
            var text = lines[index] ?? string.Empty;
            var lineNumber = index + 1;
            var matches = StandaloneToken(NcPackagePlaceholderKeys.VerificationHook)
                .Matches(text);
            if (matches.Count == 1) hookLines.Add(lineNumber);
            if (firstExecutable is null && !IsHeaderOrCommentOrPlaceholder(text))
                firstExecutable = lineNumber;
        }

        if (hookLines.Count == 0)
            throw Invalid("verification_placeholder_required",
                "The NC source template must contain exactly one standalone [[MEIMAD:VERIFICATION_HOOK]].");
        if (hookLines.Count != 1)
            throw Invalid("verification_placeholder_duplicate",
                "The NC source template must contain exactly one standalone [[MEIMAD:VERIFICATION_HOOK]].");
        if (firstExecutable is not null && firstExecutable < hookLines[0])
            throw Invalid("verification_placeholder_not_first",
                "[[MEIMAD:VERIFICATION_HOOK]] must precede the first executable NC block.");

        return new(CurrentProtocolVersion, hookLines[0],
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [NcPackagePlaceholderKeys.VerificationHook] = 1
            });
    }

    internal static NcPackageTemplateValidation ValidateCanonical(IEnumerable<string> sourceLines)
    {
        var lines = sourceLines.ToArray();
        var counts = KnownKeys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
        int? hookLine = null;
        int? firstExecutable = null;

        for (var index = 0; index < lines.Length; index++)
        {
            var text = lines[index] ?? string.Empty;
            var lineNumber = index + 1;
            var matches = Token().Matches(text);
            foreach (Match match in matches)
            {
                var key = match.Groups["key"].Value;
                if (!KnownKeys.Contains(key))
                    throw Invalid("production_package_placeholder_unknown",
                        $"Unknown Meimad placeholder '{key}' on line {lineNumber}.");
                counts[key]++;
                if (key == NcPackagePlaceholderKeys.VerificationHook) hookLine = lineNumber;
            }

            var tokenStarts = Regex.Matches(text, @"\[\[MEIMAD:", RegexOptions.CultureInvariant).Count;
            if (tokenStarts != matches.Count)
                throw Invalid("production_package_placeholder_malformed",
                    $"Malformed Meimad placeholder syntax on line {lineNumber}.");

            if (firstExecutable is null && !IsHeaderOrCommentOrPlaceholder(text))
                firstExecutable = lineNumber;
        }

        foreach (var repeatable in new[]
                 {
                     NcPackagePlaceholderKeys.PartName,
                     NcPackagePlaceholderKeys.OperationName
                 })
        {
            if (counts[repeatable] == 0)
                throw Invalid("production_package_placeholder_required",
                    $"Canonical NC template is missing required [[MEIMAD:{repeatable}]].");
        }

        foreach (var unique in UniqueRequiredKeys)
        {
            if (counts[unique] == 0)
                throw Invalid("production_package_placeholder_required",
                    $"Canonical NC template is missing required [[MEIMAD:{unique}]].");
            if (counts[unique] != 1)
                throw Invalid("production_package_placeholder_duplicate",
                    $"Canonical NC template must contain exactly one [[MEIMAD:{unique}]].");
        }

        if (!StandaloneToken(NcPackagePlaceholderKeys.VerificationHook)
                .IsMatch(lines[hookLine!.Value - 1]))
            throw Invalid("production_package_placeholder_location_invalid",
                "[[MEIMAD:VERIFICATION_HOOK]] must occupy its own NC line.");
        if (firstExecutable is not null && firstExecutable < hookLine)
            throw Invalid("production_package_placeholder_location_invalid",
                "[[MEIMAD:VERIFICATION_HOOK]] must precede the first executable NC block.");

        var eventLine = Array.FindIndex(lines,
            line => StandaloneToken(NcPackagePlaceholderKeys.EventContext).IsMatch(line ?? string.Empty));
        if (eventLine < 0)
            throw Invalid("production_package_placeholder_location_invalid",
                "[[MEIMAD:EVENT_CONTEXT]] must occupy its own NC line.");

        ValidateCycleMarkerPair(lines, counts);

        if (lines.Any(line => ActiveVerification().IsMatch(line ?? string.Empty)))
            throw Invalid("verification_executable_not_allowed_in_template",
                "Canonical NC templates must contain VERIFICATION_HOOK, not active verification code.");

        return new(CurrentProtocolVersion, hookLine.Value, counts);
    }

    internal static MatchCollection Tokens(string line) => Token().Matches(line);

    private static void ValidateCycleMarkerPair(string?[] lines, IReadOnlyDictionary<string, int> counts)
    {
        var startCount = counts[NcPackagePlaceholderKeys.CycleStart];
        var endCount = counts[NcPackagePlaceholderKeys.CycleEnd];
        if (startCount > 1 || endCount > 1)
            throw Invalid("production_package_placeholder_duplicate",
                "Canonical NC template must contain at most one [[MEIMAD:CYCLE_START]] and one [[MEIMAD:CYCLE_END]].");
        if (startCount == 0 && endCount == 0)
            throw Invalid("production_package_cycle_marker_required",
                "Canonical NC template must contain one [[MEIMAD:CYCLE_START]] and one [[MEIMAD:CYCLE_END]] around the physical part cycle.");
        if (startCount != endCount)
            throw Invalid("production_package_cycle_marker_unpaired",
                "[[MEIMAD:CYCLE_START]] and [[MEIMAD:CYCLE_END]] must both be present.");

        var startLine = Array.FindIndex(lines,
            line => StandaloneToken(NcPackagePlaceholderKeys.CycleStart).IsMatch(line ?? string.Empty));
        var endLine = Array.FindIndex(lines,
            line => StandaloneToken(NcPackagePlaceholderKeys.CycleEnd).IsMatch(line ?? string.Empty));
        if (startLine < 0)
            throw Invalid("production_package_placeholder_location_invalid",
                "[[MEIMAD:CYCLE_START]] must occupy its own NC line.");
        if (endLine < 0)
            throw Invalid("production_package_placeholder_location_invalid",
                "[[MEIMAD:CYCLE_END]] must occupy its own NC line.");
        if (startLine >= endLine)
            throw Invalid("production_package_cycle_marker_order_invalid",
                "[[MEIMAD:CYCLE_START]] must precede [[MEIMAD:CYCLE_END]].");
    }

    private static bool IsHeaderOrCommentOrPlaceholder(string line)
    {
        var value = line.Trim();
        if (value.Length == 0 || value == "%") return true;
        if (value.Contains("[[MEIMAD:", StringComparison.Ordinal)) return true;
        if (FullLineComment().IsMatch(value)) return true;
        return ProgramHeader().IsMatch(value);
    }

    private static Regex StandaloneToken(string key) =>
        new($@"^\s*\[\[MEIMAD:{Regex.Escape(key)}\]\]\s*;?\s*$",
            RegexOptions.CultureInvariant);

    private static ProductionPackageBuildException Invalid(string code, string message) => new(code, message);

    [GeneratedRegex(@"\[\[MEIMAD:(?<key>[A-Z][A-Z0-9_]*)\]\]", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex(@"\(\s*MEIMAD\s+VERIFY\s+V1\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActiveVerification();

    [GeneratedRegex(@"^\s*\([^)]*\)\s*;?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex FullLineComment();

    [GeneratedRegex(@"^\s*O\d{1,8}\b(?:\s*\([^)]*\))?\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProgramHeader();
}
