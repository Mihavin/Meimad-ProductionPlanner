using System.Globalization;
using Meimad.Planner.Server.Domain.Machines;

namespace Meimad.Planner.Server.Application.GCode;

/// <summary>
/// Control families whose syntax the Server injects into runnable NC and generated Offset
/// Loaders. The postprocessor template is dialect-neutral; only the Server-generated hook,
/// event output, cycle blocks, Offset Loader program, and the variable ranges accepted by the
/// verification configuration differ per control. The Machine carries its dialect.
/// </summary>
internal static class NcDialects
{
    internal const string HaasNgc = MachineNcDialects.HaasNgc;
    internal const string FanucMacroB = MachineNcDialects.FanucMacroB;
    internal const string MazakMatrixEia = MachineNcDialects.MazakMatrixEia;
    internal const string OkumaOsp = MachineNcDialects.OkumaOsp;

    internal static readonly IReadOnlyList<string> All = [HaasNgc, FanucMacroB, MazakMatrixEia, OkumaOsp];

    internal static bool IsSupported(string? value) =>
        value is HaasNgc or FanucMacroB or MazakMatrixEia or OkumaOsp;

    internal static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? HaasNgc : value.Trim().ToUpperInvariant();

    internal static NcDialectProfile Profile(string? value) => Normalize(value) switch
    {
        HaasNgc => HaasNgcDialect.Instance,
        FanucMacroB => FanucMacroBDialect.Fanuc,
        MazakMatrixEia => FanucMacroBDialect.Mazak,
        OkumaOsp => OkumaOspDialect.Instance,
        var other => throw new ArgumentOutOfRangeException(nameof(value), $"Unknown NC dialect '{other}'.")
    };
}

internal sealed record NcVariableRange(int Minimum, int Maximum)
{
    internal bool Contains(int value) => value >= Minimum && value <= Maximum;
}

/// <summary>Everything the Server renders or validates that depends on the control family.</summary>
internal abstract class NcDialectProfile
{
    internal abstract string Id { get; }
    internal abstract string DisplayName { get; }

    /// <summary>Range for the nonce, verification-state, release-token, and event-sequence mappings.</summary>
    internal abstract NcVariableRange PersistentVariables { get; }
    internal abstract string PersistentVariablesDescription { get; }
    internal abstract bool IsResponseVariable(int value);
    internal abstract string ResponseVariableDescription { get; }

    /// <summary>Alias normalization before collision checks; identity except for Haas M109 aliases.</summary>
    internal virtual int CanonicalVariable(int value) => value;

    /// <summary>How the control writes the variable with this configured number, e.g. <c>#10504</c> or <c>VC5</c>.</summary>
    internal abstract string VariableName(int number);

    internal abstract string VerificationHook(int verifyProgramNumber, int ncIdentityToken);

    /// <summary>The event-context block: a comment plus the control's statement(s) that print <paramref name="line"/>.</summary>
    internal abstract IReadOnlyList<string> EventContext(string line);

    /// <summary>
    /// The part-counting block for CYCLE_START (<c>CST</c>/<c>S</c>) or CYCLE_END (<c>CEN</c>/<c>E</c>):
    /// sanitize and increment the persistent sequence variable, then print the wire-format event line.
    /// </summary>
    internal abstract IReadOnlyList<string> CycleEvent(
        string eventCode, string idSuffix, int ncIdentityToken, int macroVersion, int sequenceVariable);

    /// <summary>The package-specific Offset Loader program around the Server-authored comment lines.</summary>
    internal abstract IReadOnlyList<string> OffsetLoader(
        IReadOnlyList<string> comments, int challengeProgramNumber, int releaseToken, int ncIdentityToken);

    internal abstract string OffsetLoaderLogicalPath { get; }

    /// <summary>Characters the control's print statement cannot carry are replaced before rendering.</summary>
    internal virtual string SanitizePrintedText(string value) => value;

    protected static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Haas NGC: DPRNT to the Setting 261 destination, G103 look-ahead barrier, #10000-#10999 persistent variables.</summary>
internal sealed class HaasNgcDialect : NcDialectProfile
{
    internal static readonly HaasNgcDialect Instance = new();

    internal override string Id => NcDialects.HaasNgc;
    internal override string DisplayName => "Haas NGC";
    internal override NcVariableRange PersistentVariables { get; } = new(10000, 10999);
    internal override string PersistentVariablesDescription => "#10000-#10999";
    internal override bool IsResponseVariable(int value) => value is >= 500 and <= 549 or >= 10500 and <= 10549;
    internal override string ResponseVariableDescription => "the Haas M109 range #500-#549 or #10500-#10549";
    internal override int CanonicalVariable(int value) => value is >= 500 and <= 549 ? value + 10000 : value;
    internal override string VariableName(int number) => "#" + Invariant(number);

    internal override string VerificationHook(int verifyProgramNumber, int ncIdentityToken) =>
        $"G65 P{Invariant(verifyProgramNumber)} A{Invariant(ncIdentityToken)}. (MEIMAD VERIFY V1)";

    internal override IReadOnlyList<string> EventContext(string line) =>
        ["(MEIMAD EVENT CONTEXT V2)", $"DPRNT[{line}]"];

    internal override IReadOnlyList<string> CycleEvent(
        string eventCode, string idSuffix, int ncIdentityToken, int macroVersion, int sequenceVariable)
    {
        var lines = new List<string> { "G103 P1" };
        lines.AddRange(MacroBCycleBody(eventCode, idSuffix, ncIdentityToken, macroVersion, sequenceVariable));
        lines.Add("G103 P0");
        return lines;
    }

    internal override IReadOnlyList<string> OffsetLoader(
        IReadOnlyList<string> comments, int challengeProgramNumber, int releaseToken, int ncIdentityToken) =>
        MacroBOffsetLoader(comments, challengeProgramNumber, releaseToken, ncIdentityToken);

    internal override string OffsetLoaderLogicalPath => "offset-loader/O01990.nc";

    /// <summary>Shared by every custom-macro-B control; only the look-ahead barrier around it is Haas-specific.</summary>
    internal static IEnumerable<string> MacroBCycleBody(
        string eventCode, string idSuffix, int ncIdentityToken, int macroVersion, int sequenceVariable)
    {
        var variable = Invariant(sequenceVariable);
        var ncId = Invariant(ncIdentityToken);
        yield return $"#30=ROUND[#{variable}]";
        yield return $"IF [ABS[#{variable}-#30] GT 0.0001] THEN #30=0.";
        yield return "IF [#30 LT 0.] THEN #30=0.";
        yield return "IF [#30 GE 899999.] THEN #30=0.";
        yield return "#30=#30+1.";
        yield return $"#{variable}=#30";
        yield return $"DPRNT[MEIMAD/V/1/EVENT/{eventCode}/ID/NC-{ncId}-{idSuffix}-#3001[80]/SEQ/#30[60]/MACROVERSION/{Invariant(macroVersion)}/PROGRAM/{ncId}]";
    }

    internal static IReadOnlyList<string> MacroBOffsetLoader(
        IReadOnlyList<string> comments, int challengeProgramNumber, int releaseToken, int ncIdentityToken)
    {
        var lines = new List<string> { "%", "O01990 (MEIMAD PACKAGE OFFSET LOADER)" };
        lines.AddRange(comments);
        lines.Add($"G65 P{Invariant(challengeProgramNumber)} A{Invariant(releaseToken)}. B{Invariant(ncIdentityToken)}.");
        lines.Add("M30");
        lines.Add("%");
        lines.Add(string.Empty);
        return lines;
    }
}

/// <summary>
/// FANUC 0i/30i/31i custom macro B and the Mazak Matrix EIA/ISO macro, which share the DPRNT,
/// G65, and #-variable syntax: persistent variables are #500-#999 and there is no G103, so the
/// cycle block has no look-ahead barrier (register a non-buffered M-code on the control if
/// event order matters).
/// </summary>
internal sealed class FanucMacroBDialect : NcDialectProfile
{
    internal static readonly FanucMacroBDialect Fanuc = new(NcDialects.FanucMacroB, "FANUC custom macro B (0i, 30i, 31i)");
    internal static readonly FanucMacroBDialect Mazak = new(NcDialects.MazakMatrixEia, "Mazak Matrix EIA/ISO macro");

    private FanucMacroBDialect(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    internal override string Id { get; }
    internal override string DisplayName { get; }
    internal override NcVariableRange PersistentVariables { get; } = new(500, 999);
    internal override string PersistentVariablesDescription => "#500-#999";
    internal override bool IsResponseVariable(int value) => value is >= 500 and <= 999;
    internal override string ResponseVariableDescription => "the persistent range #500-#999";
    internal override string VariableName(int number) => "#" + Invariant(number);

    internal override string VerificationHook(int verifyProgramNumber, int ncIdentityToken) =>
        $"G65 P{Invariant(verifyProgramNumber)} A{Invariant(ncIdentityToken)}. (MEIMAD VERIFY V1)";

    internal override IReadOnlyList<string> EventContext(string line) =>
        ["(MEIMAD EVENT CONTEXT V2)", $"DPRNT[{line}]"];

    internal override IReadOnlyList<string> CycleEvent(
        string eventCode, string idSuffix, int ncIdentityToken, int macroVersion, int sequenceVariable) =>
        HaasNgcDialect.MacroBCycleBody(eventCode, idSuffix, ncIdentityToken, macroVersion, sequenceVariable).ToArray();

    internal override IReadOnlyList<string> OffsetLoader(
        IReadOnlyList<string> comments, int challengeProgramNumber, int releaseToken, int ncIdentityToken) =>
        HaasNgcDialect.MacroBOffsetLoader(comments, challengeProgramNumber, releaseToken, ncIdentityToken);

    internal override string OffsetLoaderLogicalPath => "offset-loader/O01990.nc";
}

/// <summary>
/// Okuma OSP-P200/P300 User Task 2: subprograms are called with <c>CALL Onnnn PA=..</c>, common
/// variables are <c>VC1-VC200</c>, conditions branch to sequence names, and text is printed with
/// <c>PUT</c> statements flushed by <c>WRITE C</c>. There is no DPRNT and no G103.
/// </summary>
internal sealed class OkumaOspDialect : NcDialectProfile
{
    internal static readonly OkumaOspDialect Instance = new();

    internal override string Id => NcDialects.OkumaOsp;
    internal override string DisplayName => "Okuma OSP-P200/P300 (User Task 2)";
    internal override NcVariableRange PersistentVariables { get; } = new(1, 200);
    internal override string PersistentVariablesDescription => "common variables VC1-VC200 (enter the number only)";
    internal override bool IsResponseVariable(int value) => value is >= 1 and <= 200;
    internal override string ResponseVariableDescription => "the common-variable range VC1-VC200";
    internal override string VariableName(int number) => "VC" + Invariant(number);

    internal override string VerificationHook(int verifyProgramNumber, int ncIdentityToken) =>
        $"CALL O{Invariant(verifyProgramNumber)} PA={Invariant(ncIdentityToken)} (MEIMAD VERIFY V1)";

    internal override IReadOnlyList<string> EventContext(string line) =>
        ["(MEIMAD EVENT CONTEXT V2)", $"PUT '{line}'", "WRITE C"];

    internal override IReadOnlyList<string> CycleEvent(
        string eventCode, string idSuffix, int ncIdentityToken, int macroVersion, int sequenceVariable)
    {
        var variable = VariableName(sequenceVariable);
        var ncId = Invariant(ncIdentityToken);
        var reset = $"NMD{idSuffix}1";
        var increment = $"NMD{idSuffix}2";
        return
        [
            $"{variable}=ROUND[{variable}]",
            $"IF [{variable} LT 0] {reset}",
            $"IF [{variable} GE 899999] {reset}",
            $"GOTO {increment}",
            $"{reset} {variable}=0",
            $"{increment} {variable}={variable}+1",
            $"PUT 'MEIMAD/V/1/EVENT/{eventCode}/ID/NC-{ncId}-{idSuffix}-'",
            $"PUT {variable},6,0",
            "PUT '/SEQ/'",
            $"PUT {variable},6,0",
            $"PUT '/MACROVERSION/{Invariant(macroVersion)}/PROGRAM/{ncId}'",
            "WRITE C"
        ];
    }

    internal override IReadOnlyList<string> OffsetLoader(
        IReadOnlyList<string> comments, int challengeProgramNumber, int releaseToken, int ncIdentityToken)
    {
        // An O-name line at the top of an OSP .MIN file would turn the whole file into a
        // subprogram, so the loader is a plain main program: comments, one CALL, M02.
        var lines = new List<string> { "(MEIMAD PACKAGE OFFSET LOADER)" };
        lines.AddRange(comments);
        lines.Add($"CALL O{Invariant(challengeProgramNumber)} PA={Invariant(releaseToken)} PB={Invariant(ncIdentityToken)}");
        lines.Add("M02");
        lines.Add(string.Empty);
        return lines;
    }

    internal override string OffsetLoaderLogicalPath => "offset-loader/O1990.MIN";

    internal override string SanitizePrintedText(string value) => value.Replace('\'', '_');
}
