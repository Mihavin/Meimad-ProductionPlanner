[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ConfigPath,
    [string] $OutputDirectory,
    [ValidateSet(7, 8, 9)] [int] $CandidateMacroVersion = 7,
    [ValidateSet(0, 1)] [int] $FailureDprntDwellSeconds = 0,
    [switch] $AcknowledgeBenchOnlyCandidate,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $AcknowledgeBenchOnlyCandidate) {
    throw @"
Generation is disabled by default. Macro v$CandidateMacroVersion is an internally reviewable,
no-motion bench candidate. It has not passed the required physical CNC retest.
Use -AcknowledgeBenchOnlyCandidate only to generate review artifacts below
.diagnostics. This does not authorize controller loading, Server enablement, or
production use.
"@
}

if (($CandidateMacroVersion -eq 7 -and $FailureDprntDwellSeconds -ne 0) -or
    ($CandidateMacroVersion -in @(8, 9) -and $FailureDprntDwellSeconds -ne 1)) {
    throw 'Macro v7 requires zero failure dwell; macro v8/v9 requires the reviewed one-second failure DPRNT dwell.'
}

function Require-IntegerRange {
    param([string] $Name, [object] $Value, [int] $Minimum, [int] $Maximum)
    if ($null -eq $Value -or $Value -isnot [ValueType]) {
        throw "$Name must be an integer from $Minimum through $Maximum."
    }
    $integer = [int64]$Value
    if ([double]$Value -ne $integer -or $integer -lt $Minimum -or $integer -gt $Maximum) {
        throw "$Name must be an integer from $Minimum through $Maximum."
    }
    return [int]$integer
}

function Render-Template {
    param([string] $Template, [hashtable] $Values)
    $rendered = $Template
    foreach ($entry in $Values.GetEnumerator()) {
        $rendered = $rendered.Replace("{{$($entry.Key)}}", [string]$entry.Value)
    }
    if ($rendered -match '\{\{[^}]+\}\}') {
        throw "An internal macro-template placeholder was not resolved: $($Matches[0])"
    }
    return $rendered.Replace("`r`n", "`n").Replace("`r", "`n").Replace("`n", "`r`n")
}

function Write-NewAsciiFile {
    param([string] $Path, [string] $Contents, [bool] $AllowOverwrite)
    if ((Test-Path -LiteralPath $Path) -and -not $AllowOverwrite) {
        throw "Refusing to overwrite '$Path'. Use -Force after reviewing the target."
    }
    [IO.File]::WriteAllText($Path, $Contents, [Text.Encoding]::ASCII)
}

$resolvedConfig = (Resolve-Path -LiteralPath $ConfigPath).Path
$config = Get-Content -LiteralPath $resolvedConfig -Raw | ConvertFrom-Json
$machineLabel = [string]$config.machineLabel
if ($machineLabel -notmatch '^[A-Z0-9-]{1,40}$') {
    throw 'machineLabel must use 1-40 uppercase letters, digits, or hyphens.'
}

$challengeProgram = Require-IntegerRange challengeProgramNumber $config.challengeProgramNumber 9000 9999
$verifyProgram = Require-IntegerRange verifyProgramNumber $config.verifyProgramNumber 9000 9999
$finalizeProgram = Require-IntegerRange finalizeProgramNumber $config.finalizeProgramNumber 9000 9999
if ((@($challengeProgram, $verifyProgram, $finalizeProgram) | Select-Object -Unique).Count -ne 3) {
    throw 'The three protected program numbers must be distinct.'
}
$nonceVariable = Require-IntegerRange nonceVariable $config.nonceVariable 10000 10999
$responseVariable = Require-IntegerRange responseVariable $config.responseVariable 1 10999
$stateVariable = Require-IntegerRange verificationStateVariable $config.verificationStateVariable 10000 10999
$releaseVariable = Require-IntegerRange releaseTokenVariable $config.releaseTokenVariable 10000 10999
$sequenceVariable = Require-IntegerRange eventSequenceVariable $config.eventSequenceVariable 10000 10999
$canonicalResponseVariable = if ($responseVariable -ge 500 -and $responseVariable -le 549) {
    $responseVariable + 10000
} else { $responseVariable }
if ((@($nonceVariable, $canonicalResponseVariable, $stateVariable, $releaseVariable, $sequenceVariable) |
        Select-Object -Unique).Count -ne 5) {
    throw 'The five configured macro variables must be distinct after Haas legacy aliases are normalized.'
}
if (-not (($responseVariable -ge 500 -and $responseVariable -le 549) -or
          ($responseVariable -ge 10500 -and $responseVariable -le 10549))) {
    throw 'responseVariable must be in an M109-supported range: 500-549 or 10500-10549.'
}
$macroVersion = Require-IntegerRange macroVersion $config.macroVersion $CandidateMacroVersion $CandidateMacroVersion
$responseDigits = Require-IntegerRange responseDigits $config.responseDigits 4 6
$timeoutSeconds = Require-IntegerRange verificationTimeoutSeconds $config.verificationTimeoutSeconds 30 3600
$machineKey = Require-IntegerRange derivedMachineKey $config.derivedMachineKey 100000 999999
$ncIdentity = Require-IntegerRange sampleNcIdentity $config.sampleNcIdentity 100000 999999
$offsetRelease = Require-IntegerRange sampleOffsetReleaseToken $config.sampleOffsetReleaseToken 100000 999999
$testNcProgram = Require-IntegerRange testNcProgramNumber $config.testNcProgramNumber 1 8999
$testOffsetProgram = Require-IntegerRange testOffsetLoaderProgramNumber $config.testOffsetLoaderProgramNumber 1 8999
if ($testNcProgram -eq $testOffsetProgram) { throw 'Test program numbers must be distinct.' }
if ($machineKey -eq 271828 -and -not [bool]$config.allowPublicTestKey) {
    throw '271828 is a public test key and is forbidden outside an explicitly marked isolated bench.'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $OutputDirectory = Join-Path $repositoryRoot ".diagnostics\haas-v$CandidateMacroVersion-bench\$machineLabel"
}
$outputFullPath = [IO.Path]::GetFullPath($OutputDirectory)
$repositoryFullPath = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$diagnosticsPrefix = (Join-Path $repositoryFullPath '.diagnostics').TrimEnd('\') + '\'
if (-not $outputFullPath.StartsWith($diagnosticsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Bench candidates contain a derived Machine key and may be generated only below .diagnostics.'
}
[IO.Directory]::CreateDirectory($outputFullPath) | Out-Null

$values = @{
    CHALLENGE = ('{0:D4}' -f $challengeProgram)
    VERIFY = ('{0:D4}' -f $verifyProgram)
    FINALIZE = ('{0:D4}' -f $finalizeProgram)
    NONCE_VAR = $nonceVariable
    RESPONSE_VAR = $responseVariable
    STATE_VAR = $stateVariable
    RELEASE_VAR = $releaseVariable
    SEQUENCE_VAR = $sequenceVariable
    MACRO_VERSION = $macroVersion
    MACHINE_KEY = $machineKey
    RESPONSE_DIGITS = $responseDigits
    TIMEOUT_MS = ($timeoutSeconds * 1000)
    NC_ID = ('{0:D6}' -f $ncIdentity)
    OFFSET_TOKEN = ('{0:D6}' -f $offsetRelease)
    TEST_NC = ('{0:D4}' -f $testNcProgram)
    TEST_OFFSET = ('{0:D4}' -f $testOffsetProgram)
    MACHINE_LABEL = $machineLabel
    FAILURE_DWELL = if ($FailureDprntDwellSeconds -eq 1) {
        "`r`nG04 P1. (ALLOW SVF DPRNT TRANSMISSION BEFORE FAIL-CLOSED ALARM)"
    } else { '' }
}

$challengeTemplate = @'
%
O0{{CHALLENGE}} (MEIMAD PROTECTED CHALLENGE V{{MACRO_VERSION}})
(NO MOTION BENCH CANDIDATE - INTERNAL REVIEW AND PHYSICAL RETEST REQUIRED)
(A OFFSET RELEASE TOKEN - B EXPECTED NC IDENTITY)
G103 P1
;
#{{STATE_VAR}}=#0
#{{RESPONSE_VAR}}=#0
#{{NONCE_VAR}}=#0
#{{RELEASE_VAR}}=#0
IF [#1 EQ #0] GOTO900
IF [#2 EQ #0] GOTO900
#20=ROUND[#1]
#21=ROUND[#2]
IF [ABS[#1-#20] GT 0.0001] GOTO900
IF [ABS[#2-#21] GT 0.0001] GOTO900
IF [#20 LT 100000.] GOTO900
IF [#20 GT 999999.] GOTO900
IF [#21 LT 100000.] GOTO900
IF [#21 GT 999999.] GOTO900
IF [#{{SEQUENCE_VAR}} EQ #0] GOTO920
#30=ROUND[#{{SEQUENCE_VAR}}]
IF [ABS[#{{SEQUENCE_VAR}}-#30] GT 0.0001] GOTO920
IF [#30 LT 0.] GOTO920
(RESERVE ONE FOLLOWING RESULT EVENT BEFORE SEQUENCE EXHAUSTION)
IF [#30 GT 899997.] GOTO921
#{{SEQUENCE_VAR}}=#30+1.
;
#30=ROUND[#{{SEQUENCE_VAR}}]
#{{NONCE_VAR}}=99999.+#30
#{{RELEASE_VAR}}=#20
#{{STATE_VAR}}=ROUND[#3001]+1.
;
DPRNT[MEIMAD/V/1/EVENT/OLC/ID/OLC-{{MACHINE_LABEL}}-#30[60]/SEQ/#30[60]/MACROVERSION/{{MACRO_VERSION}}/PROGRAM/#21[60]/OFFSETRELEASE/#{{RELEASE_VAR}}[60]/NONCE/#{{NONCE_VAR}}[60]]
G103 P0
M99
N900 #{{STATE_VAR}}=#0
#{{RESPONSE_VAR}}=#0
#{{NONCE_VAR}}=#0
#{{RELEASE_VAR}}=#0
G103 P0
#3000=901 (MEIMAD CHALLENGE INPUT)
M99
N920 G103 P0
#3000=905 (MEIMAD SEQ NOT INIT)
M99
N921 G103 P0
#3000=906 (MEIMAD SEQ EXHAUSTED)
M99
%
'@

$foldBlocks = [Text.StringBuilder]::new()
foreach ($source in @('#29', '#32', '#20', "$machineKey.", '314159.')) {
    [void]$foldBlocks.AppendLine("#26=$source")
    [void]$foldBlocks.AppendLine('#27=100000.')
    [void]$foldBlocks.AppendLine('WHILE [#27 GE 1.] DO1')
    [void]$foldBlocks.AppendLine('#28=FIX[#26/#27]-FIX[#26/[#27*10.]]*10.')
    [void]$foldBlocks.AppendLine('#23=[#23-FIX[#23/90909.]*90909.]*11.+#28')
    [void]$foldBlocks.AppendLine('#27=FIX[#27/10.]')
    [void]$foldBlocks.AppendLine('END1')
}
$values.FOLD_BLOCKS = $foldBlocks.ToString().TrimEnd("`r", "`n")

$digitEntry = [Text.StringBuilder]::new()
for ($index = 1; $index -le $responseDigits; $index++) {
    $label = 100 + $index
    [void]$digitEntry.AppendLine("#$responseVariable=#0")
    [void]$digitEntry.AppendLine("N$label M109 P$responseVariable (MEIMAD DIGIT $index OF $responseDigits)")
    [void]$digitEntry.AppendLine("IF [#$responseVariable EQ #0] GOTO$label")
    [void]$digitEntry.AppendLine("IF [#$responseVariable LT 48.] GOTO900")
    [void]$digitEntry.AppendLine("IF [#$responseVariable GT 57.] GOTO900")
    [void]$digitEntry.AppendLine("#31=#31*10.+[#$responseVariable-48.]")
    [void]$digitEntry.AppendLine("#$responseVariable=#0")
}
$values.DIGIT_ENTRY = $digitEntry.ToString().TrimEnd("`r", "`n")

$verifyTemplate = @'
%
O0{{VERIFY}} (MEIMAD PROTECTED VERIFY INPUT V{{MACRO_VERSION}})
(NO MOTION BENCH CANDIDATE - INTERNAL REVIEW AND PHYSICAL RETEST REQUIRED)
(A IMMUTABLE SIX DIGIT NC IDENTITY)
(MACHINE KEY IS LOCAL PROTECTED DATA - NEVER DPRNT)
G103 P1
;
IF [#1 EQ #0] GOTO910
#20=ROUND[#1]
IF [ABS[#1-#20] GT 0.0001] GOTO910
IF [#20 LT 100000.] GOTO910
IF [#20 GT 999999.] GOTO910
IF [#{{STATE_VAR}} EQ #0] GOTO910
IF [#{{NONCE_VAR}} EQ #0] GOTO910
IF [#{{RELEASE_VAR}} EQ #0] GOTO910
#21=ROUND[#{{STATE_VAR}}]-1.
#22=ROUND[#3001]-#21
IF [#22 LT 0.] GOTO910
IF [#22 GT {{TIMEOUT_MS}}.] GOTO910
#29=ROUND[#{{NONCE_VAR}}]
#32=ROUND[#{{RELEASE_VAR}}]
(CONSUME ALL REUSABLE CHALLENGE STATE BEFORE THE FIRST M109 PROMPT)
#{{STATE_VAR}}=#0
#{{RESPONSE_VAR}}=#0
#{{NONCE_VAR}}=#0
#{{RELEASE_VAR}}=#0
#23=7919.
#23=[#23-FIX[#23/90909.]*90909.]*11.+1.
{{FOLD_BLOCKS}}
#24={{RESPONSE_DIGITS}}.
#25=10.
WHILE [#24 GT 1.] DO2
#25=#25*10.
#24=#24-1.
END2
#24=ROUND[#23-FIX[#23/#25]*#25]
#31=0.
{{DIGIT_ENTRY}}
GOTO800
N900 #31=-1.
N800 G103 P1
;
;
(FINALIZER READS TIMER IN A SEPARATE PROTECTED MACRO EXECUTION CONTEXT)
G65 P{{FINALIZE}} A#20 B#29 C#32 D#21 E#24 F#31
G103 P0
M99
N910 #{{STATE_VAR}}=#0
#{{RESPONSE_VAR}}=#0
#{{NONCE_VAR}}=#0
#{{RELEASE_VAR}}=#0
G103 P0
#3000=903 (MEIMAD VERIFY FAILED)
M99
%
'@

$finalizeTemplate = @'
%
O0{{FINALIZE}} (MEIMAD PROTECTED VERIFY FINALIZER V{{MACRO_VERSION}})
(A NC ID - B NONCE - C RELEASE - D START MS - E EXPECTED - F ENTERED)
(NO MOTION BENCH CANDIDATE - INTERNAL REVIEW AND PHYSICAL RETEST REQUIRED)
G103 P1
;
#20=ROUND[#3001]
;
(FINALIZER DEFENSE IN DEPTH CLEARS ALL TEMPORARY HANDSHAKE VARIABLES)
#{{STATE_VAR}}=#0
#{{RESPONSE_VAR}}=#0
#{{NONCE_VAR}}=#0
#{{RELEASE_VAR}}=#0
;
IF [#1 EQ #0] GOTO900
IF [#2 EQ #0] GOTO900
IF [#3 EQ #0] GOTO900
IF [#7 EQ #0] GOTO900
IF [#8 EQ #0] GOTO900
IF [#9 EQ #0] GOTO900
#21=ROUND[#1]
#22=ROUND[#2]
#23=ROUND[#3]
#24=ROUND[#7]
#25=ROUND[#8]
#26=ROUND[#9]
IF [#21 LT 100000.] GOTO900
IF [#21 GT 999999.] GOTO900
IF [#22 LT 100000.] GOTO900
IF [#22 GT 999999.] GOTO900
IF [#23 LT 100000.] GOTO900
IF [#23 GT 999999.] GOTO900
#27=#20-#24
IF [#27 LT 0.] GOTO910
IF [#27 GT {{TIMEOUT_MS}}.] GOTO910
IF [#26 NE #25] GOTO910
IF [#{{SEQUENCE_VAR}} EQ #0] GOTO920
#30=ROUND[#{{SEQUENCE_VAR}}]
IF [ABS[#{{SEQUENCE_VAR}}-#30] GT 0.0001] GOTO920
IF [#30 LT 0.] GOTO920
IF [#30 GE 899999.] GOTO921
#{{SEQUENCE_VAR}}=#30+1.
;
#30=ROUND[#{{SEQUENCE_VAR}}]
DPRNT[MEIMAD/V/1/EVENT/SVS/ID/SVS-{{MACHINE_LABEL}}-#30[60]/SEQ/#30[60]/MACROVERSION/{{MACRO_VERSION}}/PROGRAM/#21[60]/OFFSETRELEASE/#23[60]/NONCE/#22[60]]
G103 P0
M99
N910 IF [#{{SEQUENCE_VAR}} EQ #0] GOTO920
#30=ROUND[#{{SEQUENCE_VAR}}]
IF [ABS[#{{SEQUENCE_VAR}}-#30] GT 0.0001] GOTO920
IF [#30 LT 0.] GOTO920
IF [#30 GE 899999.] GOTO921
#{{SEQUENCE_VAR}}=#30+1.
;
#30=ROUND[#{{SEQUENCE_VAR}}]
DPRNT[MEIMAD/V/1/EVENT/SVF/ID/SVF-{{MACHINE_LABEL}}-#30[60]/SEQ/#30[60]/MACROVERSION/{{MACRO_VERSION}}/PROGRAM/#21[60]/OFFSETRELEASE/#23[60]/NONCE/#22[60]]
G103 P0{{FAILURE_DWELL}}
#3000=903 (MEIMAD VERIFY FAILED)
M99
N900 G103 P0
#3000=904 (MEIMAD FINALIZER INPUT)
M99
N920 G103 P0
#3000=905 (MEIMAD SEQ NOT INIT)
M99
N921 G103 P0
#3000=906 (MEIMAD SEQ EXHAUSTED)
M99
%
'@

$hookTemplate = @'
(PLACE IMMEDIATELY AFTER %, O HEADER, AND FULL-LINE COMMENTS)
(THIS MUST BE THE FIRST EXECUTABLE BLOCK AND MUST APPEAR EXACTLY ONCE)
G65 P{{VERIFY}} A{{NC_ID}}. (MEIMAD VERIFY V1)
'@

$offsetTemplate = @'
(PLACE ONLY AFTER EVERY OFFSET WRITE AND READBACK HAS SUCCEEDED)
G65 P{{CHALLENGE}} A{{OFFSET_TOKEN}}. B{{NC_ID}}.
'@

$cycleTemplate = @'
(AFTER THE FIRST-BLOCK VERIFY HOOK AND BEFORE FIRST MACHINING ACTION)
G103 P1
;
IF [#{{SEQUENCE_VAR}} EQ #0] GOTO905
#30=ROUND[#{{SEQUENCE_VAR}}]
IF [ABS[#{{SEQUENCE_VAR}}-#30] GT 0.0001] GOTO905
IF [#30 LT 0.] GOTO905
(RESERVE THE MATCHING CYCLE END EVENT)
IF [#30 GT 899997.] GOTO906
#{{SEQUENCE_VAR}}=#30+1.
;
#30=ROUND[#{{SEQUENCE_VAR}}]
DPRNT[MEIMAD/V/1/EVENT/CST/ID/NC-{{NC_ID}}-S-#30[60]/SEQ/#30[60]/MACROVERSION/{{MACRO_VERSION}}/PROGRAM/{{NC_ID}}]
G103 P0
(CONTINUE WITH THE FIRST MACHINING ACTION)

(ONLY ON THE NORMAL COMPLETION PATH IMMEDIATELY BEFORE M30)
G103 P1
;
IF [#{{SEQUENCE_VAR}} EQ #0] GOTO905
#30=ROUND[#{{SEQUENCE_VAR}}]
IF [ABS[#{{SEQUENCE_VAR}}-#30] GT 0.0001] GOTO905
IF [#30 LT 0.] GOTO905
IF [#30 GE 899999.] GOTO906
#{{SEQUENCE_VAR}}=#30+1.
;
#30=ROUND[#{{SEQUENCE_VAR}}]
DPRNT[MEIMAD/V/1/EVENT/CEN/ID/NC-{{NC_ID}}-E-#30[60]/SEQ/#30[60]/MACROVERSION/{{MACRO_VERSION}}/PROGRAM/{{NC_ID}}]
G103 P0
GOTO907

N905 G103 P0
#3000=905 (MEIMAD SEQ NOT INIT)
N906 G103 P0
#3000=906 (MEIMAD SEQ EXHAUSTED)
N907 (CONTINUE DIRECTLY TO M30)
'@

$testOffsetTemplate = @'
%
O0{{TEST_OFFSET}} (MEIMAD V{{MACRO_VERSION}} NO-MOTION TEST OFFSET LOADER)
(NO OFFSET WRITES - BENCH ONLY)
G65 P{{CHALLENGE}} A{{OFFSET_TOKEN}}. B{{NC_ID}}.
M30
%
'@

$testNcTemplate = @'
%
O0{{TEST_NC}} (MEIMAD V{{MACRO_VERSION}} NO-MOTION TEST NC PROGRAM)
(THE NEXT LINE IS THE FIRST EXECUTABLE BLOCK AND APPEARS EXACTLY ONCE)
G65 P{{VERIFY}} A{{NC_ID}}. (MEIMAD VERIFY V1)
DPRNT[MEIMADSPIKE/NC/{{NC_ID}}/VERIFICATION/RETURNED]
M30
%
'@

$files = [ordered]@{
    ('O0{0:D4}-CHALLENGE-V{1}.CNC' -f $challengeProgram, $macroVersion) = Render-Template $challengeTemplate $values
    ('O0{0:D4}-VERIFY-INPUT-V{1}.CNC' -f $verifyProgram, $macroVersion) = Render-Template $verifyTemplate $values
    ('O0{0:D4}-VERIFY-FINALIZER-V{1}.CNC' -f $finalizeProgram, $macroVersion) = Render-Template $finalizeTemplate $values
    'NC-FIRST-BLOCK-HOOK.CNC.txt' = Render-Template $hookTemplate $values
    'OFFSET-LOADER-FINAL-CALL.CNC.txt' = Render-Template $offsetTemplate $values
    'CYCLE-EVENT-BLOCKS.CNC.txt' = Render-Template $cycleTemplate $values
    ('O0{0:D4}-TEST-OFFSET-LOADER.CNC' -f $testOffsetProgram) = Render-Template $testOffsetTemplate $values
    ('O0{0:D4}-TEST-NC-PROGRAM.CNC' -f $testNcProgram) = Render-Template $testNcTemplate $values
}

foreach ($entry in $files.GetEnumerator()) {
    Write-NewAsciiFile (Join-Path $outputFullPath $entry.Key) $entry.Value ([bool]$Force)
}

$manifestFiles = foreach ($name in $files.Keys) {
    $path = Join-Path $outputFullPath $name
    [ordered]@{
        file = $name
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$manifest = [ordered]@{
    status = 'BENCH_ONLY_INTERNAL_REVIEW_REQUIRED'
    productionReady = $false
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    machineLabel = $machineLabel
    macroVersion = $macroVersion
    protectedPrograms = @($challengeProgram, $verifyProgram, $finalizeProgram)
    protectedVariables = @($nonceVariable, $responseVariable, $stateVariable, $releaseVariable, $sequenceVariable)
    eventSequence = [ordered]@{
        variable = $sequenceVariable
        initialization = 'AUTHORIZED_ONE_TIME_POSITIVE_INTEGER_AFTER_SERVER_SOURCE_HISTORY_REVIEW'
        initialValue = 1
        maximum = 899999
        resetOrWrapAllowed = $false
    }
    timerFinalization = 'SEPARATE_PROTECTED_G65_CONTEXT_AFTER_M109'
    files = @($manifestFiles)
    requiredBeforeControllerLoad = @(
        'qualified CNC controls engineer source review and signature',
        'Meimad production owner source review and signature',
        'three protected program numbers confirmed free',
        'five protected variables confirmed collision-free',
        'approved initial sequence value recorded',
        'bounded no-motion physical retest procedure approved'
    )
    requiredBeforeProduction = @(
        'all physical CNC commissioning checklist rows pass with captured evidence',
        'physical tablet SEND_TO_QC and QC PASS workflow pass',
        'Server verification remains disabled until signed acceptance'
    )
}
$manifestPath = Join-Path $outputFullPath 'manifest.json'
Write-NewAsciiFile $manifestPath (($manifest | ConvertTo-Json -Depth 7) + "`r`n") ([bool]$Force)

Write-Host "Generated internally reviewable v$macroVersion bench pack: $outputFullPath"
Write-Warning 'BENCH ONLY. Do not load, enable, or use for production until internal review and physical commissioning are complete.'
