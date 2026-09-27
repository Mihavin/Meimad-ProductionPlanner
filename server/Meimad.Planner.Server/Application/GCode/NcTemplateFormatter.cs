using System.Text.RegularExpressions;
using Meimad.Planner.Server.Application.ProductionPackages;

namespace Meimad.Planner.Server.Application.GCode;

internal sealed record NcTemplateValidationResult(bool IsValid, string? Code, string? Message);

internal sealed record NcTemplateFormatResult(
    string Text,
    string NcDialect,
    bool Changed,
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> Warnings,
    NcTemplateValidationResult Validation);

/// <summary>
/// "Apply Meimad Planner Format": turns an ordinary CAM/hand-written NC program into a canonical
/// protocol-v2 source template (docs/postprocessor-production-package-contract.md and
/// docs/nc-postprocessor-and-macro-specification.md, section 1 and 6). It only inserts or moves
/// Meimad placeholder lines and the control's print-channel statements; it never edits cutting
/// code and never resolves a placeholder. Elements already present are kept, so applying the
/// format twice changes nothing. The result is validated with the same
/// <see cref="NcPackagePlaceholderSchema.ValidateCanonical"/> check Package Creator uses.
/// </summary>
internal sealed partial class NcTemplateFormatter
{
    internal const int MaximumTextCharacters = 16 * 1024 * 1024;

    private static readonly (string Key, string Label)[] HeaderKeys =
    [
        (NcPackagePlaceholderKeys.PartName, "PART"),
        (NcPackagePlaceholderKeys.OperationName, "OPERATION"),
        (NcPackagePlaceholderKeys.ProductionRunId, "RUN"),
        (NcPackagePlaceholderKeys.ProductionPackageId, "PACKAGE"),
        (NcPackagePlaceholderKeys.MachineId, "MACHINE"),
        (NcPackagePlaceholderKeys.NcReleaseId, "NC RELEASE"),
        (NcPackagePlaceholderKeys.OffsetLoaderReleaseId, "OFFSET LOADER")
    ];

    internal NcTemplateFormatResult Apply(string? text, string? ncDialect)
    {
        var source = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (source.Length > MaximumTextCharacters)
        {
            throw new GCodeValidationException("text", "too_large",
                $"The NC program is larger than {MaximumTextCharacters / (1024 * 1024)} MB of text.");
        }
        var dialect = NcDialects.Normalize(ncDialect);
        if (!NcDialects.IsSupported(dialect))
        {
            throw new GCodeValidationException("ncDialect", "unsupported",
                $"NC dialect must be one of {string.Join(", ", NcDialects.All)}.");
        }

        var endsWithNewline = source.EndsWith('\n');
        var lines = (endsWithNewline ? source[..^1] : source).Split('\n').ToList();
        if (source.Length == 0) lines.Clear();
        var changes = new List<string>();
        var warnings = new List<string>();

        ConvertLegacyMarkers(lines, changes);
        if (lines.Any(line => TransferHeader().IsMatch(line)))
        {
            warnings.Add("A \"$NAME.MIN%\" transfer header line was found. Release the program body without it: the release check treats it as the first executable block.");
        }

        var counts = CountKeys(lines);
        foreach (var key in counts.Where(entry => entry.Value > 1
                     && entry.Key is not NcPackagePlaceholderKeys.PartName and not NcPackagePlaceholderKeys.OperationName))
        {
            warnings.Add($"[[MEIMAD:{key.Key}]] occurs {key.Value} times; remove the duplicates by hand, the format cannot choose which one is intended.");
        }

        // 1. Identity header comments after "%" and the O program header.
        var headerIndex = HeaderInsertIndex(lines);
        var header = new List<string>();
        foreach (var (key, label) in HeaderKeys)
        {
            var present = key == NcPackagePlaceholderKeys.PartName
                ? lines.Any(line => FullLineComment().IsMatch(line) && line.Contains(Token(key), StringComparison.Ordinal))
                : counts[key] > 0;
            if (!present) header.Add($"({label}: {Token(key)})");
        }
        if (header.Count > 0)
        {
            lines.InsertRange(headerIndex, header);
            changes.Add($"Added identity header comments: {string.Join(", ", header.Select(Label))}.");
        }

        // 2. Verification hook, print channel, event context, part-number line, cycle start —
        //    immediately before the first executable block.
        var fanucChannel = dialect == NcDialects.FanucMacroB;
        var okuma = dialect == NcDialects.OkumaOsp;
        var hookIndex = FindStandalone(lines, NcPackagePlaceholderKeys.VerificationHook);
        var firstExecutable = FirstExecutableIndex(lines);
        if (hookIndex >= 0 && hookIndex > firstExecutable)
        {
            lines.RemoveAt(hookIndex);
            hookIndex = -1;
            firstExecutable = FirstExecutableIndex(lines);
            changes.Add("Moved [[MEIMAD:VERIFICATION_HOOK]] before the first executable block.");
        }

        var block = new List<string>();
        if (hookIndex < 0 && counts[NcPackagePlaceholderKeys.VerificationHook] <= 1)
        {
            block.Add(Token(NcPackagePlaceholderKeys.VerificationHook));
            changes.Add("Added [[MEIMAD:VERIFICATION_HOOK]] before the first executable block.");
        }

        var popenIndex = lines.FindIndex(line => ChannelOpen().IsMatch(line));
        var addedPopen = false;
        if (fanucChannel && popenIndex < 0)
        {
            block.Add("POPEN");
            addedPopen = true;
            changes.Add("Added POPEN (FANUC opens the print channel before any DPRNT).");
        }

        var context = new List<string>();
        if (counts[NcPackagePlaceholderKeys.EventContext] == 0)
        {
            context.Add(Token(NcPackagePlaceholderKeys.EventContext));
            changes.Add("Added [[MEIMAD:EVENT_CONTEXT]].");
        }
        if (!lines.Any(line => PartPrintLine().IsMatch(line)))
        {
            if (okuma)
            {
                context.Add($"PUT '{Token(NcPackagePlaceholderKeys.PartName)}'");
                context.Add("WRITE C");
                changes.Add("Added the part-number output line (PUT / WRITE C).");
            }
            else
            {
                context.Add($"DPRNT[{Token(NcPackagePlaceholderKeys.PartName)}]");
                changes.Add("Added the part-number output line DPRNT[[[MEIMAD:PART_NAME]]].");
            }
        }

        var endCount = counts[NcPackagePlaceholderKeys.CycleEnd];
        var addStart = counts[NcPackagePlaceholderKeys.CycleStart] == 0;
        var afterPrint = new List<string>(context);
        if (addStart) afterPrint.Add(Token(NcPackagePlaceholderKeys.CycleStart));
        if (fanucChannel && popenIndex >= 0)
        {
            // An existing POPEN keeps its place: the hook goes before the first executable
            // block, the print lines and the cycle start directly after POPEN, because the
            // Server-generated cycle event is itself a DPRNT.
            lines.InsertRange(firstExecutable, block);
            if (popenIndex >= firstExecutable) popenIndex += block.Count;
            else warnings.Add("POPEN comes after other executable blocks; check that no machining precedes [[MEIMAD:CYCLE_START]].");
            lines.InsertRange(popenIndex + 1, afterPrint);
        }
        else
        {
            block.AddRange(afterPrint);
            lines.InsertRange(firstExecutable, block);
        }
        var cycleStartIndex = FindStandalone(lines, NcPackagePlaceholderKeys.CycleStart);
        if (addStart)
        {
            changes.Add("Added [[MEIMAD:CYCLE_START]] before the first machining block.");
        }

        // 3. Cycle end (and PCLOS) on the successful path before the main program's end.
        if (endCount == 0)
        {
            var (endIndex, endWord) = ProgramEnd(lines, Math.Max(0, cycleStartIndex + 1));
            var closeIndex = PrecedingChannelClose(lines, endIndex, cycleStartIndex);
            var insertAt = closeIndex >= 0 ? closeIndex : endIndex;
            var ending = new List<string> { Token(NcPackagePlaceholderKeys.CycleEnd) };
            if (addedPopen && closeIndex < 0) ending.Add("PCLOS");
            lines.InsertRange(insertAt, ending);
            changes.Add(endWord is null
                ? "Added [[MEIMAD:CYCLE_END]] at the end of the program (no M30/M02/M99 was found)."
                : $"Added [[MEIMAD:CYCLE_END]] before {endWord}.");
            if (ending.Count > 1) changes.Add("Added PCLOS before the program end.");
            if (endWord == "M99")
            {
                warnings.Add("The main program ends with M99. Check that [[MEIMAD:CYCLE_END]] is on the successful path of exactly one physical cycle.");
            }
            if (endWord is null)
            {
                warnings.Add("No M30, M02 or M99 program end was found; check the [[MEIMAD:CYCLE_END]] placement.");
            }
        }
        else if (addedPopen)
        {
            var (endIndex, _) = ProgramEnd(lines, FindStandalone(lines, NcPackagePlaceholderKeys.CycleEnd) + 1);
            lines.Insert(endIndex, "PCLOS");
            changes.Add("Added PCLOS before the program end.");
        }

        if (fanucChannel && !addedPopen && !lines.Any(line => ChannelClose().IsMatch(line)))
        {
            warnings.Add("The program opens the print channel with POPEN but never closes it with PCLOS before the program end.");
        }

        var output = string.Join('\n', lines) + (endsWithNewline ? "\n" : string.Empty);
        return new NcTemplateFormatResult(
            output,
            dialect,
            !string.Equals(output, source, StringComparison.Ordinal),
            changes,
            warnings,
            Validate(lines));
    }

    internal static NcTemplateValidationResult Validate(IEnumerable<string> lines)
    {
        try
        {
            NcPackagePlaceholderSchema.ValidateCanonical(lines);
            return new NcTemplateValidationResult(true, null, null);
        }
        catch (ProductionPackageBuildException exception)
        {
            return new NcTemplateValidationResult(false, exception.Code, exception.Message);
        }
    }

    private static void ConvertLegacyMarkers(List<string> lines, List<string> changes)
    {
        var converted = 0;
        var hookSeen = lines.Any(line => line.Contains(Token(NcPackagePlaceholderKeys.VerificationHook), StringComparison.Ordinal));
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (NcVerificationHookParser.PackageVerifyPlaceholder().IsMatch(line) || LegacyActiveHook().IsMatch(line))
            {
                if (hookSeen)
                {
                    lines.RemoveAt(index--);
                }
                else
                {
                    lines[index] = Token(NcPackagePlaceholderKeys.VerificationHook);
                    hookSeen = true;
                }
                converted++;
            }
            else if (NcVerificationHookParser.PackageCycleStartPlaceholder().IsMatch(line))
            {
                lines[index] = Token(NcPackagePlaceholderKeys.CycleStart);
                converted++;
            }
            else if (NcVerificationHookParser.PackageCycleEndPlaceholder().IsMatch(line))
            {
                lines[index] = Token(NcPackagePlaceholderKeys.CycleEnd);
                converted++;
            }
        }
        if (converted > 0)
        {
            changes.Add($"Converted {converted} legacy V1 marker line(s) to canonical [[MEIMAD:...]] placeholders; the Server now assigns the NC identity at release.");
        }
    }

    private static Dictionary<string, int> CountKeys(IEnumerable<string> lines)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in new[]
                 {
                     NcPackagePlaceholderKeys.PartName, NcPackagePlaceholderKeys.OperationName,
                     NcPackagePlaceholderKeys.ProductionRunId, NcPackagePlaceholderKeys.ProductionPackageId,
                     NcPackagePlaceholderKeys.MachineId, NcPackagePlaceholderKeys.NcReleaseId,
                     NcPackagePlaceholderKeys.OffsetLoaderReleaseId, NcPackagePlaceholderKeys.EventContext,
                     NcPackagePlaceholderKeys.VerificationHook, NcPackagePlaceholderKeys.CycleStart,
                     NcPackagePlaceholderKeys.CycleEnd
                 })
        {
            counts[key] = 0;
        }
        foreach (var line in lines)
        {
            foreach (Match match in NcPackagePlaceholderSchema.Tokens(line))
            {
                var key = match.Groups["key"].Value;
                if (counts.ContainsKey(key)) counts[key]++;
            }
        }
        return counts;
    }

    private static int HeaderInsertIndex(List<string> lines)
    {
        var index = 0;
        while (index < lines.Count && (lines[index].Trim().Length == 0 || lines[index].Trim() == "%")) index++;
        if (index < lines.Count && ProgramHeader().IsMatch(lines[index])) return index + 1;
        // No O header (Okuma .MIN, or a bare program): the header goes after a leading "%".
        index = 0;
        while (index < lines.Count && lines[index].Trim() == "%") index++;
        return index;
    }

    private static int FirstExecutableIndex(List<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var value = lines[index].Trim();
            if (value.Length == 0 || value == "%" || value.Contains("[[MEIMAD:", StringComparison.Ordinal)
                || FullLineComment().IsMatch(value) || ProgramHeader().IsMatch(value))
            {
                continue;
            }
            return index;
        }
        // Only comments: insert before a trailing "%" if there is one.
        var last = lines.Count;
        while (last > 0 && (lines[last - 1].Trim() == "%" || lines[last - 1].Trim().Length == 0)) last--;
        return last;
    }

    private static int FindStandalone(List<string> lines, string key) =>
        lines.FindIndex(line => Regex.IsMatch(line, $@"^\s*\[\[MEIMAD:{key}\]\]\s*;?\s*$", RegexOptions.CultureInvariant));

    /// <summary>First main-program end after <paramref name="start"/>: M30 or M02, else M99, else the end.</summary>
    private static (int Index, string? Word) ProgramEnd(List<string> lines, int start)
    {
        int? m99 = null;
        for (var index = Math.Max(0, start); index < lines.Count; index++)
        {
            var code = Code(lines[index]);
            if (ProgramStop().Match(code) is { Success: true } stop)
            {
                return (index, stop.Value.Contains("30", StringComparison.Ordinal) ? "M30" : "M02");
            }
            if (m99 is null && SubprogramReturn().IsMatch(code)) m99 = index;
        }
        if (m99 is not null) return (m99.Value, "M99");
        var last = lines.Count;
        while (last > start && (lines[last - 1].Trim() == "%" || lines[last - 1].Trim().Length == 0)) last--;
        return (last, null);
    }

    /// <summary>A PCLOS just before the program end must stay after CYCLE_END (the cycle event is a DPRNT).</summary>
    private static int PrecedingChannelClose(List<string> lines, int endIndex, int afterIndex)
    {
        for (var index = endIndex - 1; index > afterIndex && index >= 0; index--)
        {
            var value = lines[index].Trim();
            if (value.Length == 0 || FullLineComment().IsMatch(value)) continue;
            return ChannelClose().IsMatch(value) ? index : -1;
        }
        return -1;
    }

    private static string Code(string line) =>
        SemicolonComment().Replace(ParenthesisComment().Replace(line, " "), string.Empty).ToUpperInvariant();

    private static string Token(string key) => $"[[MEIMAD:{key}]]";

    private static string Label(string headerLine) => headerLine[1..headerLine.IndexOf(':', StringComparison.Ordinal)];

    [GeneratedRegex(@"^\s*\([^)]*\)\s*;?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex FullLineComment();

    [GeneratedRegex(@"^\s*O\d{1,8}\b(?:\s*\([^)]*\))?\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProgramHeader();

    [GeneratedRegex(@"^\s*(?:N\d+\s*)?POPEN\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChannelOpen();

    [GeneratedRegex(@"^\s*(?:N\d+\s*)?PCLOS\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChannelClose();

    [GeneratedRegex(@"^\s*(?:N\d+\s*)?(?:DPRNT\s*\[\s*\[\[MEIMAD:PART_NAME\]\]\s*\]|PUT\s*'\s*\[\[MEIMAD:PART_NAME\]\]\s*')", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartPrintLine();

    [GeneratedRegex(@"^\s*\$.*%\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TransferHeader();

    // Active V1 verification code; a canonical template must not contain it.
    [GeneratedRegex(@"\(\s*MEIMAD\s+VERIFY\s+V1\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyActiveHook();

    [GeneratedRegex(@"(?<![A-Z])M0*(?:30|2)(?![0-9.])", RegexOptions.CultureInvariant)]
    private static partial Regex ProgramStop();

    [GeneratedRegex(@"(?<![A-Z])M99(?![0-9.])", RegexOptions.CultureInvariant)]
    private static partial Regex SubprogramReturn();

    [GeneratedRegex(@"\([^)]*\)?")]
    private static partial Regex ParenthesisComment();

    [GeneratedRegex(@";.*$")]
    private static partial Regex SemicolonComment();
}
