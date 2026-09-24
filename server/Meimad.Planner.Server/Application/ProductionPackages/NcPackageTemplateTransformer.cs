using System.Globalization;
using System.Text;
using Meimad.Planner.Server.Application.GCode;

namespace Meimad.Planner.Server.Application.ProductionPackages;

/// <param name="PartCountingEnabled">
/// Whether the CYCLE_START/CYCLE_END markers expand to the wire-format part-counting events.
/// Null keeps the historical coupling to <paramref name="VerificationEnabled"/>; a Machine with
/// an enabled DPRNT connection counts parts whether or not its verification is enabled.
/// </param>
internal sealed record NcPackageTransformOptions(
    bool VerificationEnabled,
    int VerifyProgramNumber,
    int MacroVersion,
    int EventSequenceVariable,
    string NcDialect = NcDialects.HaasNgc,
    bool? PartCountingEnabled = null)
{
    internal NcDialectProfile Profile => NcDialects.Profile(NcDialect);

    internal bool CountsParts => PartCountingEnabled ?? VerificationEnabled;
}

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
                    output.Add(options.Profile.VerificationHook(options.VerifyProgramNumber, ncIdentityToken));
                continue;
            }
            if (line.Contains($"[[MEIMAD:{NcPackagePlaceholderKeys.EventContext}]]",
                    StringComparison.Ordinal))
            {
                var context = FormattableString.Invariant(
                    $"MEIMAD/V/2/CONTEXT/PACKAGE/{NcText(values.ProductionPackageId)}/RUN/{NcText(values.ProductionRunId)}/MACHINE/{NcText(values.MachineId)}/NCRELEASE/{NcText(values.NcReleaseId)}/MACROVERSION/{options.MacroVersion}/PROGRAM/{ncIdentityToken}");
                output.AddRange(options.Profile.EventContext(options.Profile.SanitizePrintedText(context)));
                continue;
            }
            if (line.Contains($"[[MEIMAD:{NcPackagePlaceholderKeys.CycleStart}]]",
                    StringComparison.Ordinal))
            {
                // Real part counting (SqliteProductionRunCycleAccounting) only understands the
                // wire-format V=1 CST/CEN events emitted here — identical mechanism to the legacy
                // Transform() path below. They are emitted for every Machine whose DPRNT
                // connection can carry them, with or without Server Verification.
                if (options.CountsParts)
                    AppendCycle(output, "CST", "S", ncIdentityToken, options);
                continue;
            }
            if (line.Contains($"[[MEIMAD:{NcPackagePlaceholderKeys.CycleEnd}]]",
                    StringComparison.Ordinal))
            {
                if (options.CountsParts)
                    AppendCycle(output, "CEN", "E", ncIdentityToken, options);
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
                    output.Add(options.Profile.VerificationHook(options.VerifyProgramNumber, ncIdentityToken));
                continue;
            }

            if (NcVerificationHookParser.PackageCycleStartPlaceholder().IsMatch(line))
            {
                if (options.CountsParts)
                    AppendCycle(output, "CST", "S", ncIdentityToken, options);
                continue;
            }

            if (NcVerificationHookParser.PackageCycleEndPlaceholder().IsMatch(line))
            {
                if (options.CountsParts)
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

    /// <summary>The part-counting block is rendered by the Machine's dialect (Haas G103 barrier, macro B, or Okuma OSP).</summary>
    private static void AppendCycle(
        List<string> output,
        string eventCode,
        string idSuffix,
        int ncId,
        NcPackageTransformOptions options) =>
        output.AddRange(options.Profile.CycleEvent(
            eventCode, idSuffix, ncId, options.MacroVersion, options.EventSequenceVariable));
}

internal sealed class ProductionPackageBuildException(string code, string message)
    : Exception(message)
{
    internal string Code { get; } = code;
}
