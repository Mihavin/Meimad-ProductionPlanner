using System.Globalization;
using System.Text;

namespace Meimad.Planner.Server.Application.GCode;

/// <summary>
/// What the protected verification subprograms of one Machine are generated from: the program
/// numbers and variable numbers of its verification configuration (or the dialect's documented
/// defaults when none is configured yet), the macro version the Server expects, the response
/// width and the setup-verification timeout.
/// </summary>
internal sealed record NcVerificationMacroSettings(
    int ChallengeProgramNumber,
    int VerifyProgramNumber,
    int FinalizeProgramNumber,
    int NonceVariable,
    int ResponseVariable,
    int VerificationStateVariable,
    int ReleaseTokenVariable,
    int EventSequenceVariable,
    int MacroVersion,
    int ResponseCodeDigits,
    int VerificationTimeoutSeconds,
    bool FromConfiguration);

internal sealed record NcVerificationMacroFile(string FileName, string Text);

/// <summary>The generated subprograms of one Machine plus a README describing their installation.</summary>
internal sealed record NcVerificationMacroPackage(
    string MachineTag,
    string Dialect,
    NcVerificationMacroSettings Settings,
    IReadOnlyList<NcVerificationMacroFile> Files,
    string Readme);

/// <summary>
/// Renders the challenge (O9001), verify (O9002) and finalizer (O9003) subprograms per control
/// family. Every control implements the same protocol as the commissioned Haas NGC V10 macros:
/// the challenge prints <c>OLC</c> and arms the temporary variables, the verify subprogram prints
/// <c>SVR</c>, asks the operator for the response code and calls the finalizer, which compares the
/// code with the fold of nonce, release token and NC identity (version 1, finalization 314159) and
/// prints <c>SVS</c> or <c>SVF</c>. Only the syntax differs: Haas has the G103 look-ahead barrier and
/// M109 operator input; FANUC and Mazak need POPEN/PCLOS around every DPRNT and take the code
/// from a macro variable after a #3006 message stop; Okuma OSP prints with PUT/WRITE C and
/// branches to sequence names.
/// </summary>
internal static class NcVerificationMacroGenerator
{
    private const string LineEnding = "\r\n";

    internal static NcVerificationMacroPackage Generate(
        NcDialectProfile dialect, NcVerificationMacroSettings settings, string machineNumber, string machineName)
    {
        var tag = MachineTag(dialect, machineNumber);
        var files = dialect.VerificationMacros(settings, tag);
        return new NcVerificationMacroPackage(tag, dialect.Id, settings, files, Readme(dialect, settings, tag, machineNumber, machineName, files));
    }

    /// <summary>Documented defaults for a Machine without a verification configuration: the numbers from the postprocessor specification.</summary>
    internal static NcVerificationMacroSettings DefaultSettings(NcDialectProfile dialect) => dialect.Id switch
    {
        NcDialects.HaasNgc => new(9001, 9002, 9003, 10501, 10500, 10502, 10503, 10504, 10, 6, 120, false),
        NcDialects.OkumaOsp => new(9001, 9002, 9003, 1, 2, 3, 4, 5, 10, 6, 120, false),
        _ => new(9001, 9002, 9003, 501, 500, 502, 503, 504, 10, 6, 120, false)
    };

    /// <summary>The event ID prefix: dialect family and Machine number, safe for the wire format.</summary>
    internal static string MachineTag(NcDialectProfile dialect, string machineNumber)
    {
        var family = dialect.Id switch
        {
            NcDialects.HaasNgc => "HAAS",
            NcDialects.FanucMacroB => "FANUC",
            NcDialects.MazakMatrixEia => "MAZAK",
            _ => "OKUMA"
        };
        var number = new string(machineNumber.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return number.Length == 0 ? family : $"{family}-{number}";
    }

    internal static string Join(IEnumerable<string> lines) => string.Join(LineEnding, lines) + LineEnding;

    private static string Readme(
        NcDialectProfile dialect, NcVerificationMacroSettings settings, string tag, string machineNumber,
        string machineName, IReadOnlyList<NcVerificationMacroFile> files)
    {
        var lines = new List<string>
        {
            $"MEIMAD PROTECTED VERIFICATION SUBPROGRAMS - MACHINE {machineNumber} {machineName}",
            $"Control family: {dialect.DisplayName} ({dialect.Id})",
            $"Macro version: {settings.MacroVersion} (the Server expects this version in every event)",
            $"Event ID prefix: {tag}",
            settings.FromConfiguration
                ? "Variables and program numbers: from the Machine's verification configuration in Meimad Setup."
                : "Variables and program numbers: DIALECT DEFAULTS - no verification configuration exists for this Machine yet. Enter the same numbers in Meimad Setup before enabling Server Verification.",
            string.Empty,
            "Files:"
        };
        lines.AddRange(files.Select(file => $"  {file.FileName}"));
        lines.AddRange(
        [
            string.Empty,
            "Programs:",
            $"  Challenge (called by the package Offset Loader with the release token and NC identity): O{settings.ChallengeProgramNumber}",
            $"  Verify (called by the runnable NC hook with the NC identity):                           O{settings.VerifyProgramNumber}",
            $"  Finalizer (called by the verify program; compares the operator's response):          O{settings.FinalizeProgramNumber}",
            string.Empty,
            "Variables (never planning or workflow authority; the Server is authoritative):",
            $"  Nonce:               {dialect.VariableName(settings.NonceVariable)}",
            $"  Operator response:   {dialect.VariableName(settings.ResponseVariable)}",
            $"  Verification state:  {dialect.VariableName(settings.VerificationStateVariable)}",
            $"  Release token:       {dialect.VariableName(settings.ReleaseTokenVariable)}",
            $"  Event sequence:      {dialect.VariableName(settings.EventSequenceVariable)}",
            $"  Response width:      {settings.ResponseCodeDigits} digits",
            $"  Verify-to-finalize:  {settings.VerificationTimeoutSeconds} s (the Server enforces its own timeout as well)",
            string.Empty
        ]);
        lines.AddRange(dialect.VerificationMacroNotes(settings));
        lines.AddRange(
        [
            string.Empty,
            "Commissioning: load the files into the control's protected program area, run the Offset Loader of a",
            "bench Production Package with no tool in the spindle and no motion, and confirm in Meimad that OLC,",
            "SVR and SVS/SVF arrive with the expected identity before enabling Server Verification for the Machine.",
            "Keep the macro version in Meimad Setup equal to the version in these files."
        ]);
        return Join(lines);
    }

    internal static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The custom macro B renderings shared by Haas NGC (with G103 and M109) and FANUC/Mazak (POPEN/PCLOS and #3006).</summary>
internal static class NcMacroBVerificationMacros
{
    internal static IReadOnlyList<NcVerificationMacroFile> Render(NcVerificationMacroSettings s, string tag, bool haas)
    {
        var prefix = haas ? "O0" : "O";
        var extension = haas ? ".nc" : ".NC";
        return
        [
            new($"{prefix}{s.ChallengeProgramNumber}{extension}", NcVerificationMacroGenerator.Join(Challenge(s, tag, haas))),
            new($"{prefix}{s.VerifyProgramNumber}{extension}", NcVerificationMacroGenerator.Join(Verify(s, tag, haas))),
            new($"{prefix}{s.FinalizeProgramNumber}{extension}", NcVerificationMacroGenerator.Join(Finalizer(s, tag, haas)))
        ];
    }

    private static string V(int number) => "#" + NcVerificationMacroGenerator.Invariant(number);
    private static string N(int number) => NcVerificationMacroGenerator.Invariant(number);

    /// <summary>A DPRNT line, wrapped in POPEN/PCLOS on FANUC-family controls.</summary>
    private static IEnumerable<string> Print(string body, bool haas)
    {
        if (!haas) yield return "POPEN";
        yield return $"DPRNT[{body}]";
        if (!haas) yield return "PCLOS";
    }

    private static IEnumerable<string> Barrier(bool haas, bool start)
    {
        if (haas) yield return start ? "G103 P1" : "G103 P0";
    }

    /// <summary>Sanitize and increment the persistent sequence variable into #30 (identical to the cycle block).</summary>
    private static IEnumerable<string> NextSequence(NcVerificationMacroSettings s)
    {
        var q = V(s.EventSequenceVariable);
        yield return $"#30=ROUND[{q}]";
        yield return $"IF [ABS[{q}-#30] GT 0.0001] THEN #30=0.";
        yield return "IF [#30 LT 0.] THEN #30=0.";
        yield return "IF [#30 GE 899999.] THEN #30=0.";
        yield return "#30=#30+1.";
        yield return $"{q}=#30";
    }

    private static IEnumerable<string> ClearTemporaries(NcVerificationMacroSettings s, bool includeReleaseToken)
    {
        yield return $"{V(s.VerificationStateVariable)}=#0";
        yield return $"{V(s.ResponseVariable)}=#0";
        yield return $"{V(s.NonceVariable)}=#0";
        if (includeReleaseToken) yield return $"{V(s.ReleaseTokenVariable)}=#0";
    }

    private static IEnumerable<string> Challenge(NcVerificationMacroSettings s, string tag, bool haas)
    {
        var n = V(s.NonceVariable);
        var k = V(s.ReleaseTokenVariable);
        var st = V(s.VerificationStateVariable);
        yield return "%";
        yield return $"{(haas ? "O0" : "O")}{N(s.ChallengeProgramNumber)} (MEIMAD PROTECTED CHALLENGE V{N(s.MacroVersion)})";
        yield return "(A OFFSET RELEASE TOKEN - B EXPECTED NC IDENTITY - NO MOTION)";
        foreach (var line in Barrier(haas, true)) yield return line;
        foreach (var line in ClearTemporaries(s, true)) yield return line;
        yield return "IF [#1 EQ #0] GOTO900";
        yield return "IF [#2 EQ #0] GOTO900";
        yield return "#20=ROUND[#1]";
        yield return "#21=ROUND[#2]";
        yield return "IF [ABS[#1-#20] GT 0.0001] GOTO900";
        yield return "IF [ABS[#2-#21] GT 0.0001] GOTO900";
        yield return "IF [#20 LT 100000.] GOTO900";
        yield return "IF [#20 GT 999999.] GOTO900";
        yield return "IF [#21 LT 100000.] GOTO900";
        yield return "IF [#21 GT 999999.] GOTO900";
        foreach (var line in NextSequence(s)) yield return line;
        yield return "#29=ROUND[#3001]";
        yield return "#29=#29-FIX[#29/900000.]*900000.";
        yield return $"{n}=100000.+#29";
        yield return $"{k}=#20";
        yield return $"{st}=1.";
        foreach (var line in Print($"MEIMAD/V/1/EVENT/OLC/ID/OLC-{tag}-#20[60]-{n}[60]/SEQ/#30[60]/MACROVERSION/{N(s.MacroVersion)}/PROGRAM/#21[60]/OFFSETRELEASE/#20[60]/NONCE/{n}[60]", haas))
            yield return line;
        foreach (var line in Barrier(haas, false)) yield return line;
        yield return "M99";
        yield return $"N900 {st}=#0";
        yield return $"{V(s.ResponseVariable)}=#0";
        yield return $"{n}=#0";
        yield return $"{k}=#0";
        foreach (var line in Barrier(haas, false)) yield return line;
        yield return "#3000=901 (MEIMAD CHALLENGE INPUT)";
        yield return "M99";
        yield return string.Empty;
        yield return "%";
    }

    private static IEnumerable<string> Verify(NcVerificationMacroSettings s, string tag, bool haas)
    {
        var r = V(s.ResponseVariable);
        var n = V(s.NonceVariable);
        var k = V(s.ReleaseTokenVariable);
        var st = V(s.VerificationStateVariable);
        yield return "%";
        yield return $"{(haas ? "O0" : "O")}{N(s.VerifyProgramNumber)} (MEIMAD PROTECTED VERIFY INPUT V{N(s.MacroVersion)} - NO MOTION)";
        foreach (var line in Barrier(haas, true)) yield return line;
        yield return "IF [#1 EQ #0] GOTO910";
        yield return "#20=ROUND[#1]";
        yield return "IF [ABS[#1-#20] GT 0.0001] GOTO910";
        yield return "IF [#20 LT 100000.] GOTO910";
        yield return "IF [#20 GT 999999.] GOTO910";
        yield return "(SUCCESS CACHE AVOIDS A SECOND PROMPT FOR THE SAME EXACT BINDING)";
        yield return $"IF [{st} NE #20] GOTO10";
        yield return $"IF [{n} NE #0] GOTO10";
        yield return $"IF [{k} LT 100000.] GOTO10";
        yield return $"IF [{k} GT 999999.] GOTO10";
        foreach (var line in Barrier(haas, false)) yield return line;
        yield return "M99";
        yield return $"N10 IF [{st} NE 1.] GOTO910";
        yield return $"IF [{n} EQ #0] GOTO910";
        yield return $"IF [{k} EQ #0] GOTO910";
        yield return $"#29=ROUND[{n}]";
        yield return $"#32=ROUND[{k}]";
        foreach (var line in NextSequence(s)) yield return line;
        foreach (var line in Print($"MEIMAD/V/1/EVENT/SVR/ID/SVR-{tag}-#32[60]-#29[60]/SEQ/#30[60]/MACROVERSION/{N(s.MacroVersion)}/PROGRAM/#20[60]/OFFSETRELEASE/#32[60]/NONCE/#29[60]", haas))
            yield return line;
        yield return "(TIMEOUT STARTS ONLY AFTER THE SVR NC-START EVENT)";
        yield return "#21=ROUND[#3001]+1.";
        yield return $"{st}=#0";
        yield return $"{r}=#0";
        yield return $"{n}=#0";
        // Fold of version 1, nonce, release token, NC identity and the finalization constant.
        yield return "#23=7919.";
        yield return "#23=[#23-FIX[#23/90909.]*90909.]*11.+1.";
        foreach (var source in new[] { "#29", "#32", "#20", "314159." })
        {
            yield return $"#26={source}";
            yield return "#27=100000.";
            yield return "WHILE [#27 GE 1.] DO1";
            yield return "#28=FIX[#26/#27]-FIX[#26/[#27*10.]]*10.";
            yield return "#23=[#23-FIX[#23/90909.]*90909.]*11.+#28";
            yield return "#27=FIX[#27/10.]";
            yield return "END1";
        }
        yield return $"#24={N(s.ResponseCodeDigits)}.";
        yield return "#25=10.";
        yield return "WHILE [#24 GT 1.] DO2";
        yield return "#25=#25*10.";
        yield return "#24=#24-1.";
        yield return "END2";
        yield return "#24=ROUND[#23-FIX[#23/#25]*#25]";
        yield return "#31=0.";
        yield return $"{r}=#0";
        if (haas)
        {
            // M109 returns one ASCII character per call; a non-digit fails the verification.
            for (var digit = 1; digit <= s.ResponseCodeDigits; digit++)
            {
                yield return $"N{100 + digit} M109 P{N(s.ResponseVariable)} (MEIMAD DIGIT {digit} OF {N(s.ResponseCodeDigits)})";
                yield return $"IF [{r} EQ #0] GOTO{100 + digit}";
                yield return $"IF [{r} LT 48.] GOTO900";
                yield return $"IF [{r} GT 57.] GOTO900";
                yield return $"#31=#31*10.+[{r}-48.]";
                yield return $"{r}=#0";
                if (digit < s.ResponseCodeDigits) yield return $"{r}=#0";
            }
        }
        else
        {
            // No M109 on this control: the message stop asks the operator to enter the whole code
            // into the response variable on the macro-variable screen, then press cycle start.
            yield return $"#3006=1 (MEIMAD CODE TO {V(s.ResponseVariable)} THEN START)";
            yield return $"IF [{r} EQ #0] GOTO900";
            yield return $"#31=ROUND[{r}]";
            yield return $"IF [ABS[{r}-#31] GT 0.0001] GOTO900";
            yield return "IF [#31 LT 0.] GOTO900";
            yield return "IF [#31 GE #25] GOTO900";
            yield return $"{r}=#0";
        }
        yield return "GOTO800";
        yield return "N900 #31=-1.";
        yield return $"N800 {(haas ? "G103 P1" : $"{r}=#0")}";
        yield return $"G65 P{N(s.FinalizeProgramNumber)} A#20 B#29 C#32 D#21 E#24 F#31";
        foreach (var line in Barrier(haas, false)) yield return line;
        yield return "M99";
        yield return $"N910 {st}=#0";
        yield return $"{r}=#0";
        yield return $"{n}=#0";
        yield return $"{k}=#0";
        foreach (var line in Barrier(haas, false)) yield return line;
        yield return "#3000=903 (MEIMAD VERIFY FAILED)";
        yield return "M99";
        yield return string.Empty;
        yield return "%";
    }

    private static IEnumerable<string> Finalizer(NcVerificationMacroSettings s, string tag, bool haas)
    {
        var r = V(s.ResponseVariable);
        var n = V(s.NonceVariable);
        var k = V(s.ReleaseTokenVariable);
        var st = V(s.VerificationStateVariable);
        var timeoutMilliseconds = N(Math.Clamp(s.VerificationTimeoutSeconds, 30, 3600) * 1000);
        yield return "%";
        yield return $"{(haas ? "O0" : "O")}{N(s.FinalizeProgramNumber)} (MEIMAD PROTECTED FINALIZER V{N(s.MacroVersion)} - NO MOTION)";
        foreach (var line in Barrier(haas, true)) yield return line;
        yield return "#20=ROUND[#3001]";
        yield return $"{st}=#0";
        yield return $"{r}=#0";
        yield return $"{n}=#0";
        yield return "IF [#1 EQ #0] GOTO900";
        yield return "IF [#2 EQ #0] GOTO900";
        yield return "IF [#3 EQ #0] GOTO900";
        yield return "IF [#7 EQ #0] GOTO900";
        yield return "IF [#8 EQ #0] GOTO900";
        yield return "IF [#9 EQ #0] GOTO900";
        yield return "#21=ROUND[#1]";
        yield return "#22=ROUND[#2]";
        yield return "#23=ROUND[#3]";
        yield return "#24=ROUND[#7]";
        yield return "#25=ROUND[#8]";
        yield return "#26=ROUND[#9]";
        yield return "IF [#21 LT 100000.] GOTO900";
        yield return "IF [#21 GT 999999.] GOTO900";
        yield return "IF [#22 LT 100000.] GOTO900";
        yield return "IF [#22 GT 999999.] GOTO900";
        yield return "IF [#23 LT 100000.] GOTO900";
        yield return "IF [#23 GT 999999.] GOTO900";
        yield return "#27=#20-#24";
        yield return "IF [#27 LT 0.] GOTO910";
        yield return $"IF [#27 GT {timeoutMilliseconds}.] GOTO910";
        yield return "IF [#26 NE #25] GOTO910";
        foreach (var line in NextSequence(s)) yield return line;
        foreach (var line in Print($"MEIMAD/V/1/EVENT/SVS/ID/SVS-{tag}-#23[60]-#22[60]/SEQ/#30[60]/MACROVERSION/{N(s.MacroVersion)}/PROGRAM/#21[60]/OFFSETRELEASE/#23[60]/NONCE/#22[60]", haas))
            yield return line;
        yield return $"{st}=#21";
        yield return $"{k}=#23";
        foreach (var line in Barrier(haas, false)) yield return line;
        yield return "M99";
        yield return $"N910 {st}=#0";
        yield return $"{k}=#0";
        foreach (var line in NextSequence(s)) yield return line;
        foreach (var line in Print($"MEIMAD/V/1/EVENT/SVF/ID/SVF-{tag}-#23[60]-#22[60]/SEQ/#30[60]/MACROVERSION/{N(s.MacroVersion)}/PROGRAM/#21[60]/OFFSETRELEASE/#23[60]/NONCE/#22[60]", haas))
            yield return line;
        foreach (var line in Barrier(haas, false)) yield return line;
        yield return "G04 P1. (ALLOW SVF DPRNT TRANSMISSION BEFORE FAIL-CLOSED ALARM)";
        yield return "#3000=903 (MEIMAD VERIFY FAILED)";
        yield return "M99";
        yield return $"N900 {k}=#0";
        foreach (var line in Barrier(haas, false)) yield return line;
        yield return "#3000=904 (MEIMAD FINALIZER INPUT)";
        yield return "M99";
        yield return string.Empty;
        yield return "%";
    }
}

/// <summary>
/// Okuma OSP-P200/P300 User Task 2 rendering: one <c>.SUB</c> library with the three subprograms.
/// OSP has no DPRNT, no #-variables, no G65, no M109 and no macro alarm: text is printed with
/// PUT/WRITE C, arguments arrive as PA/PB/..., branches go to sequence names, the operator enters
/// the response into a common variable after M00, and a failed check ends in M00 with a message
/// line in the log. Scratch values use the common variables VC190-VC199, which must stay free.
/// </summary>
internal static class NcOkumaVerificationMacros
{
    private const int Scratch = 190;

    internal static IReadOnlyList<NcVerificationMacroFile> Render(NcVerificationMacroSettings s, string tag)
    {
        var lines = new List<string>();
        lines.AddRange(Challenge(s, tag));
        lines.AddRange(Verify(s, tag));
        lines.AddRange(Finalizer(s, tag));
        return [new("MEIMAD.SUB", NcVerificationMacroGenerator.Join(lines))];
    }

    private static string VC(int number) => "VC" + NcVerificationMacroGenerator.Invariant(number);
    private static string N(int number) => NcVerificationMacroGenerator.Invariant(number);

    private static IEnumerable<string> NextSequence(NcVerificationMacroSettings s, string sequenceName)
    {
        var q = VC(s.EventSequenceVariable);
        var scratch = VC(Scratch);
        yield return $"{scratch}=ROUND[{q}]";
        yield return $"IF [{scratch} LT 0] N{sequenceName}1";
        yield return $"IF [{scratch} GE 899999] N{sequenceName}1";
        yield return $"GOTO N{sequenceName}2";
        yield return $"N{sequenceName}1 {scratch}=0";
        yield return $"N{sequenceName}2 {scratch}={scratch}+1";
        yield return $"{q}={scratch}";
    }

    /// <summary>The event line printed piece by piece: text in quotes, numbers with six digits and no decimals.</summary>
    private static IEnumerable<string> Print(string code, string tag, string first, string second, string macroVersion, string program)
    {
        yield return $"PUT 'MEIMAD/V/1/EVENT/{code}/ID/{code}-{tag}-'";
        yield return $"PUT {first},6,0";
        yield return "PUT '-'";
        yield return $"PUT {second},6,0";
        yield return "PUT '/SEQ/'";
        yield return $"PUT {VC(Scratch)},6,0";
        yield return $"PUT '/MACROVERSION/{macroVersion}/PROGRAM/'";
        yield return $"PUT {program},6,0";
        yield return "PUT '/OFFSETRELEASE/'";
        yield return $"PUT {first},6,0";
        yield return "PUT '/NONCE/'";
        yield return $"PUT {second},6,0";
        yield return "WRITE C";
    }

    private static IEnumerable<string> Failure(string message)
    {
        yield return $"PUT '{message}'";
        yield return "WRITE C";
        yield return $"({message})";
        yield return "M00";
        yield return "RTS";
    }

    private static IEnumerable<string> Challenge(NcVerificationMacroSettings s, string tag)
    {
        var n = VC(s.NonceVariable);
        var k = VC(s.ReleaseTokenVariable);
        var st = VC(s.VerificationStateVariable);
        yield return $"O{N(s.ChallengeProgramNumber)}";
        yield return $"(MEIMAD PROTECTED CHALLENGE V{N(s.MacroVersion)} - PA RELEASE TOKEN PB NC IDENTITY - NO MOTION)";
        yield return $"{st}=0";
        yield return $"{VC(s.ResponseVariable)}=0";
        yield return $"{n}=0";
        yield return $"{k}=0";
        yield return "IF [PA LT 100000] NCHE";
        yield return "IF [PA GT 999999] NCHE";
        yield return "IF [PB LT 100000] NCHE";
        yield return "IF [PB GT 999999] NCHE";
        foreach (var line in NextSequence(s, "CHQ")) yield return line;
        // OSP exposes no millisecond clock to User Task: the nonce is a rolling value from the
        // sequence, the previous release token and the NC identity, kept in the six-digit range.
        yield return $"{VC(Scratch + 1)}=MOD[[{VC(Scratch)}*7919+PB*13+PA*7],900000]";
        yield return $"{n}=100000+{VC(Scratch + 1)}";
        yield return $"{k}=PA";
        yield return $"{st}=1";
        foreach (var line in Print("OLC", tag, "PA", n, N(s.MacroVersion), "PB")) yield return line;
        yield return "RTS";
        yield return $"NCHE {st}=0";
        yield return $"{n}=0";
        yield return $"{k}=0";
        foreach (var line in Failure("MEIMAD CHALLENGE INPUT ERROR")) yield return line;
    }

    private static IEnumerable<string> Verify(NcVerificationMacroSettings s, string tag)
    {
        var r = VC(s.ResponseVariable);
        var n = VC(s.NonceVariable);
        var k = VC(s.ReleaseTokenVariable);
        var st = VC(s.VerificationStateVariable);
        var state = VC(Scratch + 2);
        var digit = VC(Scratch + 3);
        var divisor = VC(Scratch + 4);
        var source = VC(Scratch + 5);
        var nonce = VC(Scratch + 6);
        var release = VC(Scratch + 7);
        var expected = VC(Scratch + 8);
        var modulus = VC(Scratch + 9);
        yield return $"O{N(s.VerifyProgramNumber)}";
        yield return $"(MEIMAD PROTECTED VERIFY INPUT V{N(s.MacroVersion)} - PA NC IDENTITY - NO MOTION)";
        yield return "IF [PA LT 100000] NVFE";
        yield return "IF [PA GT 999999] NVFE";
        yield return "(SUCCESS CACHE AVOIDS A SECOND PROMPT FOR THE SAME EXACT BINDING)";
        yield return $"IF [{st} NE PA] NVR1";
        yield return $"IF [{n} NE 0] NVR1";
        yield return $"IF [{k} LT 100000] NVR1";
        yield return $"IF [{k} GT 999999] NVR1";
        yield return "RTS";
        yield return $"NVR1 IF [{st} NE 1] NVFE";
        yield return $"IF [{n} EQ 0] NVFE";
        yield return $"IF [{k} EQ 0] NVFE";
        yield return $"{nonce}=ROUND[{n}]";
        yield return $"{release}=ROUND[{k}]";
        foreach (var line in NextSequence(s, "VRQ")) yield return line;
        foreach (var line in Print("SVR", tag, release, nonce, N(s.MacroVersion), "PA")) yield return line;
        yield return $"{st}=0";
        yield return $"{r}=0";
        yield return $"{n}=0";
        // Fold of version 1, nonce, release token, NC identity and the finalization constant.
        yield return $"{state}=7919";
        yield return $"{state}=MOD[{state},90909]*11+1";
        var index = 0;
        foreach (var value in new[] { nonce, release, "PA", "314159" })
        {
            index++;
            yield return $"{source}={value}";
            yield return $"{divisor}=100000";
            yield return $"NVF{index}A IF [{divisor} LT 1] NVF{index}B";
            yield return $"{digit}=FIX[{source}/{divisor}]-FIX[{source}/[{divisor}*10]]*10";
            yield return $"{state}=MOD[{state},90909]*11+{digit}";
            yield return $"{divisor}=FIX[{divisor}/10]";
            yield return $"GOTO NVF{index}A";
            yield return $"NVF{index}B {digit}=0";
        }
        yield return $"{modulus}=1";
        yield return $"{digit}={N(s.ResponseCodeDigits)}";
        yield return $"NVFM IF [{digit} LE 0] NVFN";
        yield return $"{modulus}={modulus}*10";
        yield return $"{digit}={digit}-1";
        yield return "GOTO NVFM";
        yield return $"NVFN {expected}=MOD[{state},{modulus}]";
        // The operator enters the code into the response variable, then presses cycle start.
        yield return $"{r}=0";
        yield return $"PUT 'MEIMAD ENTER {N(s.ResponseCodeDigits)} DIGIT CODE IN {r} THEN CYCLE START'";
        yield return "WRITE C";
        yield return $"(MEIMAD ENTER {N(s.ResponseCodeDigits)} DIGIT CODE IN {r} THEN CYCLE START)";
        yield return "M00";
        yield return $"{VC(Scratch + 1)}=ROUND[{r}]";
        yield return $"IF [{VC(Scratch + 1)} LT 0] NVRF";
        yield return $"IF [{VC(Scratch + 1)} GE {modulus}] NVRF";
        yield return "GOTO NVRC";
        yield return $"NVRF {VC(Scratch + 1)}=-1";
        yield return $"NVRC {r}=0";
        yield return $"CALL O{N(s.FinalizeProgramNumber)} PA=PA PB={nonce} PC={release} PE={expected} PF={VC(Scratch + 1)}";
        yield return "RTS";
        yield return $"NVFE {st}=0";
        yield return $"{r}=0";
        yield return $"{n}=0";
        yield return $"{k}=0";
        foreach (var line in Failure("MEIMAD VERIFY FAILED")) yield return line;
    }

    private static IEnumerable<string> Finalizer(NcVerificationMacroSettings s, string tag)
    {
        var r = VC(s.ResponseVariable);
        var n = VC(s.NonceVariable);
        var k = VC(s.ReleaseTokenVariable);
        var st = VC(s.VerificationStateVariable);
        yield return $"O{N(s.FinalizeProgramNumber)}";
        yield return $"(MEIMAD PROTECTED FINALIZER V{N(s.MacroVersion)} - PA NC IDENTITY PB NONCE PC RELEASE TOKEN PE EXPECTED PF ENTERED - NO MOTION)";
        yield return $"{st}=0";
        yield return $"{r}=0";
        yield return $"{n}=0";
        yield return "IF [PA LT 100000] NFNE";
        yield return "IF [PA GT 999999] NFNE";
        yield return "IF [PB LT 100000] NFNE";
        yield return "IF [PB GT 999999] NFNE";
        yield return "IF [PC LT 100000] NFNE";
        yield return "IF [PC GT 999999] NFNE";
        yield return "IF [PF NE PE] NFNF";
        foreach (var line in NextSequence(s, "FSQ")) yield return line;
        foreach (var line in Print("SVS", tag, "PC", "PB", N(s.MacroVersion), "PA")) yield return line;
        yield return $"{st}=PA";
        yield return $"{k}=PC";
        yield return "RTS";
        yield return $"NFNF {st}=0";
        yield return $"{k}=0";
        foreach (var line in NextSequence(s, "FFQ")) yield return line;
        foreach (var line in Print("SVF", tag, "PC", "PB", N(s.MacroVersion), "PA")) yield return line;
        foreach (var line in Failure("MEIMAD VERIFY FAILED")) yield return line;
        yield return $"NFNE {k}=0";
        foreach (var line in Failure("MEIMAD FINALIZER INPUT ERROR")) yield return line;
    }
}
