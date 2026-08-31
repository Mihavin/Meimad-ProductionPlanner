[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ConfigPath,
    [string] $OutputDirectory,
    [switch] $AcknowledgeBenchOnlyCandidate,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $AcknowledgeBenchOnlyCandidate) {
    throw 'V10 is a no-motion bench candidate. Use -AcknowledgeBenchOnlyCandidate to generate review artifacts.'
}

function Int-InRange([string] $name, [object] $value, [int] $minimum, [int] $maximum) {
    $integer = [int64]$value
    if ([double]$value -ne $integer -or $integer -lt $minimum -or $integer -gt $maximum) {
        throw "$name must be an integer from $minimum through $maximum."
    }
    [int]$integer
}
function Render([string] $template, [hashtable] $values) {
    foreach ($entry in $values.GetEnumerator()) {
        $template = $template.Replace("{{$($entry.Key)}}", [string]$entry.Value)
    }
    if ($template -match '\{\{[^}]+\}\}') { throw "Unresolved template value: $($Matches[0])" }
    $template.Replace("`r`n", "`n").Replace("`r", "`n").Replace("`n", "`r`n")
}
function Write-Ascii([string] $path, [string] $contents) {
    if ((Test-Path -LiteralPath $path) -and -not $Force) {
        throw "Refusing to overwrite '$path'. Use -Force after reviewing it."
    }
    [IO.File]::WriteAllText($path, $contents, [Text.Encoding]::ASCII)
}

$config = Get-Content -LiteralPath (Resolve-Path -LiteralPath $ConfigPath).Path -Raw | ConvertFrom-Json
$machineLabel = [string]$config.machineLabel
if ($machineLabel -notmatch '^[A-Z0-9-]{1,40}$') { throw 'machineLabel is invalid.' }
$challenge = Int-InRange challengeProgramNumber $config.challengeProgramNumber 9000 9999
$verify = Int-InRange verifyProgramNumber $config.verifyProgramNumber 9000 9999
$finalizer = Int-InRange finalizeProgramNumber $config.finalizeProgramNumber 9000 9999
if ((@($challenge,$verify,$finalizer) | Select-Object -Unique).Count -ne 3) { throw 'Protected programs must be distinct.' }
$nonceVar = Int-InRange nonceVariable $config.nonceVariable 10000 10999
$responseVar = Int-InRange responseVariable $config.responseVariable 1 10999
$stateVar = Int-InRange verificationStateVariable $config.verificationStateVariable 10000 10999
$releaseVar = Int-InRange releaseTokenVariable $config.releaseTokenVariable 10000 10999
$sequenceVar = Int-InRange eventSequenceVariable $config.eventSequenceVariable 10000 10999
$canonicalResponse = if ($responseVar -in 500..549) { $responseVar + 10000 } else { $responseVar }
if ($canonicalResponse -notin 10500..10549) { throw 'responseVariable must be in the Haas M109 range.' }
if ((@($nonceVar,$canonicalResponse,$stateVar,$releaseVar,$sequenceVar) | Select-Object -Unique).Count -ne 5) {
    throw 'Macro variables must be distinct after Haas alias normalization.'
}
if ((Int-InRange macroVersion $config.macroVersion 10 10) -ne 10) { throw 'V10 configuration required.' }
$digits = Int-InRange responseDigits $config.responseDigits 4 6
$timeoutMs = (Int-InRange verificationTimeoutSeconds $config.verificationTimeoutSeconds 30 3600) * 1000
$ncId = Int-InRange sampleNcIdentity $config.sampleNcIdentity 100000 999999
$offsetToken = Int-InRange sampleOffsetReleaseToken $config.sampleOffsetReleaseToken 100000 999999
$testNc = Int-InRange testNcProgramNumber $config.testNcProgramNumber 1 8999
$testOffset = Int-InRange testOffsetLoaderProgramNumber $config.testOffsetLoaderProgramNumber 1 8999
$testCycle = Int-InRange testCycleProgramNumber $config.testCycleProgramNumber 1 8999
if ((@($testNc,$testOffset,$testCycle) | Select-Object -Unique).Count -ne 3) { throw 'Test programs must be distinct.' }

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot ".diagnostics\haas-v10-bench\$machineLabel"
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
$allowedPrefix = (Join-Path ([IO.Path]::GetFullPath($repositoryRoot)) '.diagnostics').TrimEnd('\') + '\'
if (-not $output.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Bench artifacts may be generated only below .diagnostics.'
}
[IO.Directory]::CreateDirectory($output) | Out-Null

$values = @{
    CHALLENGE=('{0:D4}' -f $challenge); VERIFY=('{0:D4}' -f $verify); FINALIZER=('{0:D4}' -f $finalizer)
    NONCE_VAR=$nonceVar; RESPONSE_VAR=$responseVar; STATE_VAR=$stateVar; RELEASE_VAR=$releaseVar; SEQUENCE_VAR=$sequenceVar
    DIGITS=$digits; TIMEOUT_MS=$timeoutMs; NC_ID=('{0:D6}' -f $ncId); OFFSET_TOKEN=('{0:D6}' -f $offsetToken)
    TEST_NC=('{0:D4}' -f $testNc); TEST_OFFSET=('{0:D4}' -f $testOffset); TEST_CYCLE=('{0:D4}' -f $testCycle)
    MACHINE_LABEL=$machineLabel
}
$sequenceBlock = @'
#30=ROUND[#{{SEQUENCE_VAR}}]
IF [ABS[#{{SEQUENCE_VAR}}-#30] GT 0.0001] THEN #30=0.
IF [#30 LT 0.] THEN #30=0.
IF [#30 GE 899999.] THEN #30=0.
#30=#30+1.
#{{SEQUENCE_VAR}}=#30
'@
$values.SEQUENCE_BLOCK = Render $sequenceBlock $values
$fold = [Text.StringBuilder]::new()
foreach ($source in @('#29','#32','#20','314159.')) {
    [void]$fold.AppendLine("#26=$source")
    [void]$fold.AppendLine('#27=100000.')
    [void]$fold.AppendLine('WHILE [#27 GE 1.] DO1')
    [void]$fold.AppendLine('#28=FIX[#26/#27]-FIX[#26/[#27*10.]]*10.')
    [void]$fold.AppendLine('#23=[#23-FIX[#23/90909.]*90909.]*11.+#28')
    [void]$fold.AppendLine('#27=FIX[#27/10.]')
    [void]$fold.AppendLine('END1')
}
$values.FOLD_BLOCKS = $fold.ToString().TrimEnd("`r","`n")
$digitEntry = [Text.StringBuilder]::new()
for ($index=1; $index -le $digits; $index++) {
    $label=100+$index
    [void]$digitEntry.AppendLine("#$responseVar=#0")
    [void]$digitEntry.AppendLine("N$label M109 P$responseVar (MEIMAD DIGIT $index OF $digits)")
    [void]$digitEntry.AppendLine("IF [#$responseVar EQ #0] GOTO$label")
    [void]$digitEntry.AppendLine("IF [#$responseVar LT 48.] GOTO900")
    [void]$digitEntry.AppendLine("IF [#$responseVar GT 57.] GOTO900")
    [void]$digitEntry.AppendLine("#31=#31*10.+[#$responseVar-48.]")
    [void]$digitEntry.AppendLine("#$responseVar=#0")
}
$values.DIGIT_ENTRY=$digitEntry.ToString().TrimEnd("`r","`n")

$challengeTemplate=@'
%
O0{{CHALLENGE}} (MEIMAD PROTECTED CHALLENGE V10)
(A OFFSET RELEASE TOKEN - B EXPECTED NC IDENTITY - NO MOTION)
G103 P1
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
{{SEQUENCE_BLOCK}}
#29=ROUND[#3001]
#29=#29-FIX[#29/900000.]*900000.
#{{NONCE_VAR}}=100000.+#29
#{{RELEASE_VAR}}=#20
#{{STATE_VAR}}=1.
DPRNT[MEIMAD/V/1/EVENT/OLC/ID/OLC-{{MACHINE_LABEL}}-#20[60]-#{{NONCE_VAR}}[60]/SEQ/#30[60]/MACROVERSION/10/PROGRAM/#21[60]/OFFSETRELEASE/#20[60]/NONCE/#{{NONCE_VAR}}[60]]
G103 P0
M99
N900 #{{STATE_VAR}}=#0
#{{RESPONSE_VAR}}=#0
#{{NONCE_VAR}}=#0
#{{RELEASE_VAR}}=#0
G103 P0
#3000=901 (MEIMAD CHALLENGE INPUT)
M99
%
'@
$verifyTemplate=@'
%
O0{{VERIFY}} (MEIMAD PROTECTED VERIFY INPUT V10 - NO MOTION)
G103 P1
IF [#1 EQ #0] GOTO910
#20=ROUND[#1]
IF [ABS[#1-#20] GT 0.0001] GOTO910
IF [#20 LT 100000.] GOTO910
IF [#20 GT 999999.] GOTO910
(SUCCESS CACHE AVOIDS A SECOND PROMPT FOR THE SAME EXACT BINDING)
IF [#{{STATE_VAR}} NE #20] GOTO10
IF [#{{NONCE_VAR}} NE #0] GOTO10
IF [#{{RELEASE_VAR}} LT 100000.] GOTO10
IF [#{{RELEASE_VAR}} GT 999999.] GOTO10
G103 P0
M99
N10 IF [#{{STATE_VAR}} NE 1.] GOTO910
IF [#{{NONCE_VAR}} EQ #0] GOTO910
IF [#{{RELEASE_VAR}} EQ #0] GOTO910
#29=ROUND[#{{NONCE_VAR}}]
#32=ROUND[#{{RELEASE_VAR}}]
{{SEQUENCE_BLOCK}}
DPRNT[MEIMAD/V/1/EVENT/SVR/ID/SVR-{{MACHINE_LABEL}}-#32[60]-#29[60]/SEQ/#30[60]/MACROVERSION/10/PROGRAM/#20[60]/OFFSETRELEASE/#32[60]/NONCE/#29[60]]
(TIMEOUT STARTS ONLY AFTER THE SVR NC-START EVENT)
#21=ROUND[#3001]+1.
#{{STATE_VAR}}=#0
#{{RESPONSE_VAR}}=#0
#{{NONCE_VAR}}=#0
#23=7919.
#23=[#23-FIX[#23/90909.]*90909.]*11.+1.
{{FOLD_BLOCKS}}
#24={{DIGITS}}.
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
G65 P{{FINALIZER}} A#20 B#29 C#32 D#21 E#24 F#31
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
$finalizerTemplate=@'
%
O0{{FINALIZER}} (MEIMAD PROTECTED FINALIZER V10 - NO MOTION)
G103 P1
#20=ROUND[#3001]
#{{STATE_VAR}}=#0
#{{RESPONSE_VAR}}=#0
#{{NONCE_VAR}}=#0
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
{{SEQUENCE_BLOCK}}
DPRNT[MEIMAD/V/1/EVENT/SVS/ID/SVS-{{MACHINE_LABEL}}-#23[60]-#22[60]/SEQ/#30[60]/MACROVERSION/10/PROGRAM/#21[60]/OFFSETRELEASE/#23[60]/NONCE/#22[60]]
#{{STATE_VAR}}=#21
#{{RELEASE_VAR}}=#23
G103 P0
M99
N910 #{{STATE_VAR}}=#0
#{{RELEASE_VAR}}=#0
{{SEQUENCE_BLOCK}}
DPRNT[MEIMAD/V/1/EVENT/SVF/ID/SVF-{{MACHINE_LABEL}}-#23[60]-#22[60]/SEQ/#30[60]/MACROVERSION/10/PROGRAM/#21[60]/OFFSETRELEASE/#23[60]/NONCE/#22[60]]
G103 P0
G04 P1. (ALLOW SVF DPRNT TRANSMISSION BEFORE FAIL-CLOSED ALARM)
#3000=903 (MEIMAD VERIFY FAILED)
M99
N900 #{{RELEASE_VAR}}=#0
G103 P0
#3000=904 (MEIMAD FINALIZER INPUT)
M99
%
'@
$hookTemplate=@'
(FIRST EXECUTABLE BLOCK - EXACTLY ONCE)
G65 P{{VERIFY}} A{{NC_ID}}. (MEIMAD VERIFY V1)
'@
$offsetTemplate=@'
(ONLY AFTER EVERY OFFSET WRITE AND READBACK SUCCEEDS)
G65 P{{CHALLENGE}} A{{OFFSET_TOKEN}}. B{{NC_ID}}.
'@
$cycleTemplate=@'
(AFTER VERIFY HOOK AND BEFORE FIRST MACHINING ACTION)
G103 P1
{{SEQUENCE_BLOCK}}
DPRNT[MEIMAD/V/1/EVENT/CST/ID/NC-{{NC_ID}}-S-#3001[80]/SEQ/#30[60]/MACROVERSION/10/PROGRAM/{{NC_ID}}]
G103 P0
(ON NORMAL COMPLETION IMMEDIATELY BEFORE M30)
G103 P1
{{SEQUENCE_BLOCK}}
DPRNT[MEIMAD/V/1/EVENT/CEN/ID/NC-{{NC_ID}}-E-#3001[80]/SEQ/#30[60]/MACROVERSION/10/PROGRAM/{{NC_ID}}]
G103 P0
'@
$values.CYCLE_BLOCKS=(Render $cycleTemplate $values).TrimEnd("`r","`n")
$testOffsetTemplate="%`nO0{{TEST_OFFSET}} (MEIMAD V10 NO-MOTION TEST OFFSET LOADER)`nG65 P{{CHALLENGE}} A{{OFFSET_TOKEN}}. B{{NC_ID}}.`nM30`n%`n"
$testNcTemplate="%`nO0{{TEST_NC}} (MEIMAD V10 NO-MOTION TEST NC)`nG65 P{{VERIFY}} A{{NC_ID}}. (MEIMAD VERIFY V1)`nDPRNT[MEIMADSPIKE/NC/{{NC_ID}}/VERIFICATION/RETURNED]`nM30`n%`n"
$testCycleTemplate="%`nO0{{TEST_CYCLE}} (MEIMAD V10 NO-MOTION CYCLE COUNT TEST)`nG65 P{{VERIFY}} A{{NC_ID}}. (MEIMAD VERIFY V1)`n{{CYCLE_BLOCKS}}`nM30`n%`n"

$files=[ordered]@{
    ('O0{0:D4}-CHALLENGE-V10.CNC' -f $challenge)=Render $challengeTemplate $values
    ('O0{0:D4}-VERIFY-INPUT-V10.CNC' -f $verify)=Render $verifyTemplate $values
    ('O0{0:D4}-VERIFY-FINALIZER-V10.CNC' -f $finalizer)=Render $finalizerTemplate $values
    'NC-FIRST-BLOCK-HOOK.CNC.txt'=Render $hookTemplate $values
    'OFFSET-LOADER-FINAL-CALL.CNC.txt'=Render $offsetTemplate $values
    'CYCLE-EVENT-BLOCKS.CNC.txt'=Render $cycleTemplate $values
    ('O0{0:D4}-TEST-OFFSET-LOADER.CNC' -f $testOffset)=Render $testOffsetTemplate $values
    ('O0{0:D4}-TEST-NC-PROGRAM.CNC' -f $testNc)=Render $testNcTemplate $values
    ('O0{0:D4}-TEST-CYCLE-COUNT.CNC' -f $testCycle)=Render $testCycleTemplate $values
}
foreach($entry in $files.GetEnumerator()){ Write-Ascii (Join-Path $output $entry.Key) $entry.Value }
$manifestFiles=@($files.Keys | ForEach-Object {
    $path=Join-Path $output $_
    [ordered]@{file=$_;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$manifest=[ordered]@{
    status='BENCH_ONLY_INTERNAL_REVIEW_REQUIRED';productionReady=$false
    generatedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');machineLabel=$machineLabel;macroVersion=10
    protectedPrograms=@($challenge,$verify,$finalizer)
    protectedVariables=@($nonceVar,$responseVar,$stateVar,$releaseVar,$sequenceVar)
    eventSequence=[ordered]@{variable=$sequenceVar;role='EVIDENCE_ONLY';initialization='AUTOMATIC_ZERO_OR_INVALID_RECOVERY';resetOrWrapAllowed=$true}
    verificationLifecycle='OFFSET_LOADER_COMPLETED -> ARMED (NO TIMEOUT) -> SVR FIRST NC START -> PENDING -> SUCCEEDED'
    files=$manifestFiles
    requiredBeforeProduction=@('source review','bounded no-motion physical commissioning','Server verification remains disabled until acceptance')
}
Write-Ascii (Join-Path $output 'manifest.json') (($manifest|ConvertTo-Json -Depth 7)+"`r`n")
Write-Host "Generated internally reviewable V10 bench pack: $output"
Write-Warning 'BENCH ONLY. Do not load or enable until source review and physical commissioning pass.'
