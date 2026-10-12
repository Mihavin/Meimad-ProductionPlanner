using System.Globalization;
using System.Text.RegularExpressions;

namespace Meimad.Planner.Server.Application.MachineEvidence;

/// <summary>
/// Work offset names as the Server stores them: <c>G54</c>-<c>G59</c>, <c>G54.1 P1</c> (FANUC/Mazak
/// additional offsets), <c>G154 P1</c> (Haas additional offsets) and <c>G15 H1</c> (Okuma OSP work
/// coordinate systems). Blanks are ignored on input. The CNC-safe spelling <c>G541P1</c> (no dot,
/// because a dot in DPRNT text is not printable on every control) and <c>G54-1P1</c> mean
/// <c>G54.1 P1</c>. Which names a Machine accepts depends on its NC dialect
/// (<see cref="GCode.NcDialectProfile.NormalizeWorkOffset"/>).
/// </summary>
internal static partial class WorkOffsetNames
{
    /// <summary>The dialect-independent spelling of <paramref name="value"/>, or null when it is no work offset name.</summary>
    internal static WorkOffsetName? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var compact = new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray()).ToUpperInvariant();
        var match = Standard().Match(compact);
        if (match.Success) return new(WorkOffsetFamily.Standard, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        match = Additional().Match(compact);
        if (match.Success) return Number(WorkOffsetFamily.G54Point1, match.Groups[1].Value);
        match = HaasAdditional().Match(compact);
        if (match.Success) return Number(WorkOffsetFamily.G154, match.Groups[1].Value);
        match = HaasAlias().Match(compact);
        if (match.Success) return new(WorkOffsetFamily.HaasAlias, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        match = Okuma().Match(compact);
        if (match.Success) return Number(WorkOffsetFamily.OkumaH, match.Groups[1].Value);
        return null;
    }

    private static WorkOffsetName? Number(WorkOffsetFamily family, string digits) =>
        int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? new(family, number)
            : null;

    [GeneratedRegex("^G(5[4-9])$", RegexOptions.CultureInvariant)]
    private static partial Regex Standard();

    [GeneratedRegex(@"^G54(?:\.1|-1|1)P(\d{1,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex Additional();

    [GeneratedRegex(@"^G154P(\d{1,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex HaasAdditional();

    [GeneratedRegex(@"^G(1[1-2]\d)$", RegexOptions.CultureInvariant)]
    private static partial Regex HaasAlias();

    [GeneratedRegex(@"^(?:G15)?H(\d{1,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex Okuma();
}

internal enum WorkOffsetFamily
{
    /// <summary>G54-G59; <see cref="WorkOffsetName.Number"/> is 54-59.</summary>
    Standard,

    /// <summary>FANUC/Mazak G54.1 Pn.</summary>
    G54Point1,

    /// <summary>Haas G154 Pn.</summary>
    G154,

    /// <summary>Haas G110-G129, the same registers as G154 P1-P20; <see cref="WorkOffsetName.Number"/> is 110-129.</summary>
    HaasAlias,

    /// <summary>Okuma OSP G15 Hn.</summary>
    OkumaH
}

internal sealed record WorkOffsetName(WorkOffsetFamily Family, int Number)
{
    /// <summary>The stored spelling: <c>G54</c>, <c>G54.1 P1</c>, <c>G154 P1</c>, <c>G110</c>, <c>G15 H1</c>.</summary>
    internal string Canonical => Family switch
    {
        WorkOffsetFamily.Standard => "G" + Invariant(Number),
        WorkOffsetFamily.G54Point1 => "G54.1 P" + Invariant(Number),
        WorkOffsetFamily.G154 => "G154 P" + Invariant(Number),
        WorkOffsetFamily.HaasAlias => "G" + Invariant(Number),
        WorkOffsetFamily.OkumaH => "G15 H" + Invariant(Number),
        _ => throw new InvalidOperationException()
    };

    /// <summary>How a DPRNT/PUT text prints the name: no dot and no blank (<c>G541P1</c>, <c>G154P1</c>, <c>G15H1</c>).</summary>
    internal string CncSafe => Canonical.Replace(".", string.Empty, StringComparison.Ordinal)
        .Replace(" ", string.Empty, StringComparison.Ordinal);

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
