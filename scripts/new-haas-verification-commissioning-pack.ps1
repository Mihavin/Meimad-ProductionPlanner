[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ConfigPath,

    [string] $OutputDirectory,

    [switch] $AcknowledgeQuarantinedAuditOnly,

    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $AcknowledgeQuarantinedAuditOnly) {
    throw @'
Generation is disabled by default: this source contains the quarantined macro-v5
design that failed the physical M109 timeout test and uses a non-monotonic #3001
event sequence. It is not loadable CNC code. Use
-AcknowledgeQuarantinedAuditOnly only to reproduce audit fixtures in an isolated
development workspace; it does not authorize packaging, controller loading, or
Server enablement.
'@
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
    [System.IO.File]::WriteAllText($Path, $Contents, [System.Text.Encoding]::ASCII)
}

$resolvedConfig = (Resolve-Path -LiteralPath $ConfigPath).Path
$config = Get-Content -LiteralPath $resolvedConfig -Raw | ConvertFrom-Json

$machineLabel = [string]$config.machineLabel
if ($machineLabel -notmatch '^[A-Z0-9-]{1,40}$') {
    throw 'machineLabel must use 1-40 uppercase letters, digits, or hyphens.'
}
$challengeProgram = Require-IntegerRange challengeProgramNumber $config.challengeProgramNumber 9000 9999
$verifyProgram = Require-IntegerRange verifyProgramNumber $config.verifyProgramNumber 9000 9999
if ($challengeProgram -eq $verifyProgram) { throw 'challengeProgramNumber and verifyProgramNumber must be distinct.' }
$nonceVariable = Require-IntegerRange nonceVariable $config.nonceVariable 10000 10999
$responseVariable = Require-IntegerRange responseVariable $config.responseVariable 1 10999
$stateVariable = Require-IntegerRange verificationStateVariable $config.verificationStateVariable 10000 10999
$releaseVariable = Require-IntegerRange releaseTokenVariable $config.releaseTokenVariable 10000 10999
$canonicalResponseVariable = if ($responseVariable -ge 500 -and $responseVariable -le 549) {
    $responseVariable + 10000
} else { $responseVariable }
$variables = @($nonceVariable, $canonicalResponseVariable, $stateVariable, $releaseVariable)
if (($variables | Select-Object -Unique).Count -ne 4) {
    throw 'The four configured macro variables must be distinct after Haas legacy aliases are normalized.'
}
if (-not (($responseVariable -ge 500 -and $responseVariable -le 549) -or
          ($responseVariable -ge 10500 -and $responseVariable -le 10549))) {
    throw 'responseVariable must be in an M109-supported range: 500-549 or 10500-10549.'
}
$macroVersion = Require-IntegerRange macroVersion $config.macroVersion 1 999999
if ($macroVersion -ne 5) {
    throw 'The quarantined audit reproducer is pinned to macroVersion 5. It must never label the old design as a newer candidate.'
}
$responseDigits = Require-IntegerRange responseDigits $config.responseDigits 4 6
$timeoutSeconds = Require-IntegerRange verificationTimeoutSeconds $config.verificationTimeoutSeconds 30 3600
$machineKey = Require-IntegerRange derivedMachineKey $config.derivedMachineKey 100000 999999
$ncIdentity = Require-IntegerRange sampleNcIdentity $config.sampleNcIdentity 100000 999999
$offsetRelease = Require-IntegerRange sampleOffsetReleaseToken $config.sampleOffsetReleaseToken 100000 999999
$testNcProgram = Require-IntegerRange testNcProgramNumber `
    $(if ($config.PSObject.Properties.Name -contains 'testNcProgramNumber') {
        $config.testNcProgramNumber
    } else { 1990 }) 1 8999
$testOffsetProgram = Require-IntegerRange testOffsetLoaderProgramNumber `
    $(if ($config.PSObject.Properties.Name -contains 'testOffsetLoaderProgramNumber') {
        $config.testOffsetLoaderProgramNumber
    } else { 1991 }) 1 8999
if ($testNcProgram -eq $testOffsetProgram) {
    throw 'testNcProgramNumber and testOffsetLoaderProgramNumber must be distinct.'
}
$publicKeyAllowed = $config.PSObject.Properties.Name -contains 'allowPublicTestKey' -and
    [bool]$config.allowPublicTestKey
if ($machineKey -eq 271828 -and -not $publicKeyAllowed) {
    throw '271828 is a public test key. Set allowPublicTestKey only for an isolated bench pack.'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $OutputDirectory = Join-Path $repositoryRoot ".diagnostics\haas-commissioning\$machineLabel"
}
$outputFullPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$repositoryFullPath = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$repositoryPrefix = $repositoryFullPath.TrimEnd('\') + '\'
if ($outputFullPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
    -not $outputFullPath.StartsWith((Join-Path $repositoryFullPath '.diagnostics').TrimEnd('\') + '\',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Generated key-bearing macros inside the repository must stay below .diagnostics (git-ignored).'
}
[System.IO.Directory]::CreateDirectory($outputFullPath) | Out-Null

$values = @{
    CHALLENGE = ('{0:D4}' -f $challengeProgram)
    VERIFY = ('{0:D4}' -f $verifyProgram)
    NONCE_VAR = $nonceVariable
    RESPONSE_VAR = $responseVariable
    STATE_VAR = $stateVariable
    RELEASE_VAR = $releaseVariable
    MACRO_VERSION = $macroVersion
    MACHINE_KEY = $machineKey
    RESPONSE_DIGITS = $responseDigits
    TIMEOUT_MS = ($timeoutSeconds * 1000)
    NC_ID = ('{0:D6}' -f $ncIdentity)
    OFFSET_TOKEN = ('{0:D6}' -f $offsetRelease)
    TEST_NC = ('{0:D4}' -f $testNcProgram)
    TEST_OFFSET = ('{0:D4}' -f $testOffsetProgram)
    MACHINE_LABEL = $machineLabel
}

$challengeTemplate = @'
%
O0{{CHALLENGE}} (MEIMAD PROTECTED CHALLENGE V1)
(COMMISSIONING CANDIDATE - NO MOTION - ENGINEERING APPROVAL REQUIRED)
(A OFFSET RELEASE TOKEN - B EXPECTED NC IDENTITY)
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
#{{NONCE_VAR}}=100000.+FIX[#3001-FIX[#3001/900000.]*900000.]
#{{RELEASE_VAR}}=#20
#{{STATE_VAR}}=ROUND[#3001]+1.
#30=ROUND[#3001]
DPRNT[MEIMAD/V/1/EVENT/OLC/ID/OLC-#3011[10]-#3012[10]-#30[10]/SEQ/#30[10]/MACROVERSION/{{MACRO_VERSION}}/PROGRAM/#21[60]/OFFSETRELEASE/#{{RELEASE_VAR}}[60]/NONCE/#{{NONCE_VAR}}[60]]
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

$digitEntry = [System.Text.StringBuilder]::new()
for ($index = 1; $index -le $responseDigits; $index++) {
    [void]$digitEntry.AppendLine("M109 P$responseVariable (MEIMAD DIGIT $index OF $responseDigits)")
    [void]$digitEntry.AppendLine("IF [#$responseVariable EQ #0] GOTO910")
    [void]$digitEntry.AppendLine("IF [#$responseVariable LT 48.] GOTO910")
    [void]$digitEntry.AppendLine("IF [#$responseVariable GT 57.] GOTO910")
    [void]$digitEntry.AppendLine("#31=#31*10.+[#$responseVariable-48.]")
    [void]$digitEntry.AppendLine("#$responseVariable=#0")
}
$values.DIGIT_ENTRY = $digitEntry.ToString().TrimEnd("`r", "`n")

$foldBlocks = [System.Text.StringBuilder]::new()
# The persistent challenge values are copied into G65-local variables #29/#32
# and invalidated before operator input. Folding must never read the persistent
# variables after that consumption boundary.
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

$verifyTemplate = @'
%
O0{{VERIFY}} (MEIMAD PROTECTED VERIFY V1)
(COMMISSIONING CANDIDATE - NO MOTION - ENGINEERING APPROVAL REQUIRED)
(A IMMUTABLE SIX DIGIT NC IDENTITY)
(MACHINE KEY IS LOCAL PROTECTED DATA - NEVER DPRNT)
G103 P1
IF [#1 EQ #0] GOTO900
#20=ROUND[#1]
IF [ABS[#1-#20] GT 0.0001] GOTO900
IF [#20 LT 100000.] GOTO900
IF [#20 GT 999999.] GOTO900
IF [#{{STATE_VAR}} EQ #0] GOTO910
IF [#{{NONCE_VAR}} EQ #0] GOTO910
IF [#{{RELEASE_VAR}} EQ #0] GOTO910
IF [ROUND[#{{STATE_VAR}}] LE 0.] GOTO910
#21=ROUND[#{{STATE_VAR}}]-1.
#22=ROUND[#3001]-#21
IF [#22 LT 0.] GOTO910
IF [#22 GT {{TIMEOUT_MS}}.] GOTO910
#29=ROUND[#{{NONCE_VAR}}]
#32=ROUND[#{{RELEASE_VAR}}]
(CONSUME PERSISTENT CHALLENGE BEFORE OPERATOR INPUT)
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
#22=ROUND[#3001]-#21
IF [#22 LT 0.] GOTO910
IF [#22 GT {{TIMEOUT_MS}}.] GOTO910
IF [ROUND[#31] NE ROUND[#24]] GOTO910
#30=ROUND[#3001]
DPRNT[MEIMAD/V/1/EVENT/SVS/ID/SVS-#3011[10]-#3012[10]-#30[10]/SEQ/#30[10]/MACROVERSION/{{MACRO_VERSION}}/PROGRAM/#20[60]]
#{{STATE_VAR}}=#0
#{{RESPONSE_VAR}}=#0
#{{NONCE_VAR}}=#0
#{{RELEASE_VAR}}=#0
G103 P0
M99
N900 #{{RESPONSE_VAR}}=#0
#{{STATE_VAR}}=#0
#{{NONCE_VAR}}=#0
#{{RELEASE_VAR}}=#0
G103 P0
#3000=902 (MEIMAD NC ID INVALID)
M99
N910 #30=ROUND[#3001]
DPRNT[MEIMAD/V/1/EVENT/SVF/ID/SVF-#3011[10]-#3012[10]-#30[10]/SEQ/#30[10]/MACROVERSION/{{MACRO_VERSION}}/PROGRAM/#20[60]]
#{{STATE_VAR}}=#0
#{{RESPONSE_VAR}}=#0
#{{NONCE_VAR}}=#0
#{{RELEASE_VAR}}=#0
G103 P0
#3000=903 (MEIMAD VERIFY FAILED)
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
#30=ROUND[#3901*2.]
#31=#30+1.
DPRNT[MEIMAD/V/1/EVENT/CST/ID/NC-{{NC_ID}}-S-#3011[10]-#3012[10]-#30[10]/SEQ/#30[10]/MACROVERSION/{{MACRO_VERSION}}/PROGRAM/{{NC_ID}}]
G103 P0

(ONLY ON THE NORMAL COMPLETION PATH IMMEDIATELY BEFORE M30)
G103 P1
DPRNT[MEIMAD/V/1/EVENT/CEN/ID/NC-{{NC_ID}}-E-#3011[10]-#3012[10]-#31[10]/SEQ/#31[10]/MACROVERSION/{{MACRO_VERSION}}/PROGRAM/{{NC_ID}}]
G103 P0
'@

$testOffsetTemplate = @'
%
O0{{TEST_OFFSET}} (MEIMAD NO-MOTION TEST OFFSET LOADER)
(BENCH COMMISSIONING ONLY - NO OFFSET WRITES)
(EXPECTED NC ID {{NC_ID}})
(OFFSET RELEASE TOKEN {{OFFSET_TOKEN}})
(THE CHALLENGE CALL IS THE LAST EXECUTABLE ACTION BEFORE M30)
G65 P{{CHALLENGE}} A{{OFFSET_TOKEN}}. B{{NC_ID}}.
M30
%
'@

$testNcTemplate = @'
%
O0{{TEST_NC}} (MEIMAD NO-MOTION TEST NC PROGRAM)
(BENCH COMMISSIONING ONLY - NO MOTION - NO PRODUCTION CYCLE EVENTS)
(THE NEXT LINE IS THE FIRST EXECUTABLE BLOCK AND MUST APPEAR EXACTLY ONCE)
G65 P{{VERIFY}} A{{NC_ID}}. (MEIMAD VERIFY V1)
DPRNT[MEIMADSPIKE/NC/{{NC_ID}}/VERIFICATION/RETURNED]
M30
%
'@

$files = [ordered]@{
    ('O0{0:D4}-CHALLENGE.CNC' -f $challengeProgram) = Render-Template $challengeTemplate $values
    ('O0{0:D4}-VERIFY.CNC' -f $verifyProgram) = Render-Template $verifyTemplate $values
    'NC-FIRST-BLOCK-HOOK.CNC.txt' = Render-Template $hookTemplate $values
    'OFFSET-LOADER-FINAL-CALL.CNC.txt' = Render-Template $offsetTemplate $values
    'CYCLE-EVENT-BLOCKS.CNC.txt' = Render-Template $cycleTemplate $values
    ('O0{0:D4}-TEST-OFFSET-LOADER.CNC' -f $testOffsetProgram) = `
        Render-Template $testOffsetTemplate $values
    ('O0{0:D4}-TEST-NC-PROGRAM.CNC' -f $testNcProgram) = `
        Render-Template $testNcTemplate $values
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
    status = 'QUARANTINED_PHYSICAL_TIMEOUT_FAILURE'
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    machineLabel = $machineLabel
    macroVersion = $macroVersion
    challengeProgramNumber = $challengeProgram
    verifyProgramNumber = $verifyProgram
    responseDigits = $responseDigits
    verificationTimeoutSeconds = $timeoutSeconds
    testOffsetLoaderProgramNumber = $testOffsetProgram
    testNcProgramNumber = $testNcProgram
    inputMethod = 'M109_SINGLE_DIGIT'
    files = @($manifestFiles)
    requiredApproval = @(
        'qualified CNC controls engineer and Meimad owner review',
        'collision-free protected program and variable mapping',
        'Setting 23 and operator-access validation',
        'physical seven-vector arithmetic test',
        'Reset alarm power-cycle and timeout fail-closed tests',
        'captured strict DPRNT line validation',
        'dry-run interlock proof before any cutting trial'
    )
}
$manifestPath = Join-Path $outputFullPath 'manifest.json'
Write-NewAsciiFile $manifestPath (($manifest | ConvertTo-Json -Depth 6) + "`r`n") ([bool]$Force)

Write-Host "Generated quarantined commissioning pack: $outputFullPath"
Write-Warning 'QUARANTINED after a physical timeout failure. Generated protected macros contain a derived Machine key.'
Write-Warning 'Do not load or run these macros. A reviewed replacement design is required.'
