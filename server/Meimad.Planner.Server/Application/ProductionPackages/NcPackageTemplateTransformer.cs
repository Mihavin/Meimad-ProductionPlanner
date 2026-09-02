using System.Globalization;
using System.Text;
using Meimad.Planner.Server.Application.GCode;

namespace Meimad.Planner.Server.Application.ProductionPackages;

internal sealed record NcPackageTransformOptions(
    bool VerificationEnabled,
    int VerifyProgramNumber,
    int MacroVersion,
    int EventSequenceVariable);

internal sealed record NcPackageResolvedValues(
    string PartName,
    string OperationName,
    string ProductionRunId,
    string ProductionPackageId,
    string MachineId,
    string NcReleaseId,
    string? OffsetLoaderReleaseId);

internal static class NcPackageTemplateTransformer
{
    internal static byte[] TransformCanonical(
        IEnumerable<string> sourceLines,
        NcPackageTransformOptions options,
        NcPackageResolvedValues values,
        int ncIdentityToken,
        out int protocolVersion)
    {
        var lines = sourceLines.ToArray();
        var validation = NcPackagePlaceholderSchema.ValidateCanonical(lines);
        protocolVersion = validation.ProtocolVersion;
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NcPackagePlaceholderKeys.PartName] = NcText(values.PartName),
            [NcPackagePlaceholderKeys.OperationName] = NcText(values.OperationName),
            [NcPackagePlaceholderKeys.ProductionRunId] = NcText(values.ProductionRunId),
            [NcPackagePlaceholderKeys.ProductionPackageId] = NcText(values.ProductionPackageId),
            [NcPackagePlaceholderKeys.MachineId] = NcText(values.MachineId),
            [NcPackagePlaceholderKeys.NcReleaseId] = NcText(values.NcReleaseId),
            [NcPackagePlaceholderKeys.OffsetLoaderReleaseId] =
                NcText(values.OffsetLoaderReleaseId ?? "NOT_APPLICABLE")
        };
        var output = new List<string>(lines.Length + 16);
        foreach (var source in lines)
        {
            var line = source ?? string.Empty;
            if (line.Contains($"[[MEIMAD:{NcPackagePlaceholderKeys.VerificationHook}]]",
                    StringComparison.Ordinal))
            {
                if (options.VerificationEnabled)
                    output.Add(FormattableString.Invariant(
                        $"G65 P{options.VerifyProgramNumber} A{ncIdentityToken}. (MEIMAD VERIFY V1)"));
                continue;
            }
            if (line.Contains($"[[MEIMAD:{NcPackagePlaceholderKeys.EventContext}]]",
                    StringComparison.Ordinal))
            {
                output.Add("(MEIMAD EVENT CONTEXT V2)");
                output.Add($"DPRNT[MEIMAD/V/2/CONTEXT/PACKAGE/{NcText(values.ProductionPackageId)}/RUN/{NcText(values.ProductionRunId)}/MACHINE/{NcText(values.MachineId)}/NCRELEASE/{NcText(values.NcReleaseId)}/MACROVERSION/{options.MacroVersion}/PROGRAM/{ncIdentityToken}]");
                continue;
            }

            foreach (var replacement in replacements)
                line = line.Replace($"[[MEIMAD:{replacement.Key}]]", replacement.Value,
                    StringComparison.Ordinal);
            output.Add(line);
        }

        var rendered = string.Join("\r\n", output) + "\r\n";
        if (rendered.Contains("[[MEIMAD:", StringComparison.Ordinal))
            throw new ProductionPackageBuildException(
                "production_package_placeholder_unresolved",
                "The runnable NC still contains an unresolved canonical Meimad placeholder.");
        if (!options.VerificationEnabled
            && rendered.Contains("MEIMAD VERIFY V1", StringComparison.OrdinalIgnoreCase))
            throw new ProductionPackageBuildException(
                "production_package_verification_not_removed",
                "Verification-disabled runnable NC contains active verification content.");
        return Encoding.ASCII.GetBytes(rendered);
    }

    /// <summary>Explicit compatibility transformer for immutable legacy V1 releases.</summary>
    internal static byte[] Transform(
        IEnumerable<string> sourceLines,
        NcPackageTransformOptions options,
        out int ncIdentityToken)
    {
        var lines = sourceLines.ToArray();
        var placeholder = NcVerificationHookParser.ParseRequired(lines);
        ncIdentityToken = placeholder.NcIdentityToken;
        var output = new List<string>(lines.Length + 24);
        foreach (var line in lines)
        {
            if (NcVerificationHookParser.PackageVerifyPlaceholder().IsMatch(line))
            {
                if (options.VerificationEnabled)
                    output.Add(FormattableString.Invariant(
                        $"G65 P{options.VerifyProgramNumber} A{ncIdentityToken}. (MEIMAD VERIFY V1)"));
                continue;
            }

            if (NcVerificationHookParser.PackageCycleStartPlaceholder().IsMatch(line))
            {
                if (options.VerificationEnabled)
                    AppendCycle(output, "CST", "S", ncIdentityToken, options);
                continue;
            }

            if (NcVerificationHookParser.PackageCycleEndPlaceholder().IsMatch(line))
            {
                if (options.VerificationEnabled)
                    AppendCycle(output, "CEN", "E", ncIdentityToken, options);
                continue;
            }

            output.Add(line);
        }

        var rendered = string.Join("\r\n", output) + "\r\n";
        if (rendered.Contains("MEIMAD PACKAGE ", StringComparison.OrdinalIgnoreCase))
            throw new ProductionPackageBuildException(
                "production_package_placeholder_unresolved",
                "The runnable NC still contains an unresolved Meimad package placeholder.");
        if (!options.VerificationEnabled
            && rendered.Contains("MEIMAD VERIFY V1", StringComparison.OrdinalIgnoreCase))
            throw new ProductionPackageBuildException(
                "production_package_verification_not_removed",
                "Verification-disabled runnable NC contains active verification content.");
        return Encoding.ASCII.GetBytes(rendered);
    }

    private static string NcText(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            throw new ProductionPackageBuildException(
                "production_package_authoritative_value_missing",
                "A required authoritative package value is empty.");
        return new string(trimmed.Select(character =>
            character is >= ' ' and <= '~' && character is not '[' and not ']' and not '(' and not ')'
                ? character
                : '_').ToArray());
    }

    private static void AppendCycle(
        ICollection<string> output,
        string eventCode,
        string idSuffix,
        int ncId,
        NcPackageTransformOptions options)
    {
        var variable = options.EventSequenceVariable.ToString(CultureInfo.InvariantCulture);
        output.Add("G103 P1");
        output.Add($"#30=ROUND[#{variable}]");
        output.Add($"IF [ABS[#{variable}-#30] GT 0.0001] THEN #30=0.");
        output.Add("IF [#30 LT 0.] THEN #30=0.");
        output.Add("IF [#30 GE 899999.] THEN #30=0.");
        output.Add("#30=#30+1.");
        output.Add($"#{variable}=#30");
        output.Add($"DPRNT[MEIMAD/V/1/EVENT/{eventCode}/ID/NC-{ncId}-{idSuffix}-#3001[80]/SEQ/#30[60]/MACROVERSION/{options.MacroVersion}/PROGRAM/{ncId}]");
        output.Add("G103 P0");
    }
}

internal sealed class ProductionPackageBuildException(string code, string message)
    : Exception(message)
{
    internal string Code { get; } = code;
}
