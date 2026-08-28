[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Z0-9-]{1,40}$')]
    [string] $MachineLabel,

    [string] $OutputDirectory,

    [ValidateRange(1, 8999)] [int] $DirectTimerProgramNumber = 1980,
    [ValidateRange(1, 8999)] [int] $FinalizerCallerProgramNumber = 1981,
    [ValidateRange(1, 8999)] [int] $TimerFinalizerProgramNumber = 1982,
    [ValidateRange(1, 8999)] [int] $CounterProbeProgramNumber = 1983,
    [ValidateRange(1, 8999)] [int] $CounterInitializerProgramNumber = 1984,
    [ValidateRange(500, 10549)] [int] $ResponseVariable = 10500,
    [ValidateRange(10000, 10999)] [int] $PersistentCounterVariable = 10504,
    [ValidateRange(1, 899997)] [int] $InitialCounterValue = 1,
    [ValidateRange(5000, 60000)] [int] $MinimumWaitMilliseconds = 15000,
    [switch] $AcknowledgeNoMotionRealMachineTests,
    [switch] $AcknowledgeOneTimePersistentCounterInitialization,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $AcknowledgeNoMotionRealMachineTests) {
    throw @'
Generation is disabled by default. These are real-controller engineering probes.
They contain no motion, spindle, tool, coolant, probing, or offset writes, but they
do use M109, DPRNT, G65, alarms, and a persistent macro variable. Supply
-AcknowledgeNoMotionRealMachineTests only after reviewing the source and procedure.
'@
}
if (-not $AcknowledgeOneTimePersistentCounterInitialization) {
    throw @'
The selected PERSISTENT_COUNTER design requires one recorded initialization write.
Supply -AcknowledgeOneTimePersistentCounterInitialization only after confirming the
counter variable is collision-free and its current value is empty. This switch is
not authorization to overwrite a counter that already has history.
'@
}

$programs = @(
    $DirectTimerProgramNumber,
    $FinalizerCallerProgramNumber,
    $TimerFinalizerProgramNumber,
    $CounterProbeProgramNumber,
    $CounterInitializerProgramNumber)
if (($programs | Select-Object -Unique).Count -ne $programs.Count) {
    throw 'All five engineering-test program numbers must be distinct.'
}
if (-not (($ResponseVariable -ge 500 -and $ResponseVariable -le 549) -or
          ($ResponseVariable -ge 10500 -and $ResponseVariable -le 10549))) {
    throw 'ResponseVariable must be in the Haas M109 range 500-549 or 10500-10549.'
}
$canonicalResponse = if ($ResponseVariable -le 549) { $ResponseVariable + 10000 } else { $ResponseVariable }
if ($canonicalResponse -eq $PersistentCounterVariable) {
    throw 'ResponseVariable and PersistentCounterVariable collide after Haas legacy aliases are normalized.'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $OutputDirectory = Join-Path $repositoryRoot ".diagnostics\haas-ngc-engineering-tests\$MachineLabel"
}
$outputFullPath = [IO.Path]::GetFullPath($OutputDirectory)
$repositoryFullPath = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$diagnosticsPrefix = (Join-Path $repositoryFullPath '.diagnostics').TrimEnd('\') + '\'
if (-not $outputFullPath.StartsWith($diagnosticsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Real-machine engineering test packs may be generated only below .diagnostics.'
}
[IO.Directory]::CreateDirectory($outputFullPath) | Out-Null

function Render-Template {
    param([string] $Template, [hashtable] $Values)
    $rendered = $Template
    foreach ($entry in $Values.GetEnumerator()) {
        $rendered = $rendered.Replace("{{$($entry.Key)}}", [string]$entry.Value)
    }
    if ($rendered -match '\{\{[^}]+\}\}') {
        throw "Unresolved engineering-test placeholder: $($Matches[0])"
    }
    return $rendered.Replace("`r`n", "`n").Replace("`r", "`n").Replace("`n", "`r`n")
}

function Write-NewAsciiFile {
    param([string] $Path, [string] $Contents)
    if ((Test-Path -LiteralPath $Path) -and -not $Force) {
        throw "Refusing to overwrite '$Path'. Use -Force after reviewing the target."
    }
    [IO.File]::WriteAllText($Path, $Contents, [Text.Encoding]::ASCII)
}

$values = @{
    MACHINE = $MachineLabel
    DIRECT = ('{0:D4}' -f $DirectTimerProgramNumber)
    CALLER = ('{0:D4}' -f $FinalizerCallerProgramNumber)
    FINALIZER = ('{0:D4}' -f $TimerFinalizerProgramNumber)
    COUNTER = ('{0:D4}' -f $CounterProbeProgramNumber)
    INITIALIZER = ('{0:D4}' -f $CounterInitializerProgramNumber)
    RESPONSE_VAR = $ResponseVariable
    COUNTER_VAR = $PersistentCounterVariable
    INITIAL_COUNTER = $InitialCounterValue
    MINIMUM_WAIT_MS = $MinimumWaitMilliseconds
}

$directTimer = @'
%
O0{{DIRECT}} (MEIMAD NGC M109 DIRECT TIMER TEST)
(NO MOTION - WAIT AT LEAST THE AGREED INTERVAL BEFORE ENTERING DIGIT 7)
G103 P1
;
#1=ROUND[#3001]
#{{RESPONSE_VAR}}=#0
N100 M109 P{{RESPONSE_VAR}} (ENTER DIGIT 7 AFTER WAIT)
IF [#{{RESPONSE_VAR}} EQ #0] GOTO100
G103 P1
;
;
;
#2=ROUND[#3001]
;
#3=#2-#1
DPRNT[MEIMADENG/V/1/TEST/M109DIRECT/MACHINE/{{MACHINE}}/STARTMS/#1[80]/ENDMS/#2[80]/ELAPSEDMS/#3[80]/INPUT/#{{RESPONSE_VAR}}[30]]
#{{RESPONSE_VAR}}=#0
IF [#3 LT {{MINIMUM_WAIT_MS}}.] GOTO900
G103 P0
M30
N900 G103 P0
#3000=920 (M109 DIRECT TIMER EARLY)
M30
%
'@

$finalizerCaller = @'
%
O0{{CALLER}} (MEIMAD NGC M109 G65 FINALIZER CALLER TEST)
(NO MOTION - WAIT AT LEAST THE AGREED INTERVAL BEFORE ENTERING DIGIT 7)
G103 P1
;
#1=ROUND[#3001]
#{{RESPONSE_VAR}}=#0
N100 M109 P{{RESPONSE_VAR}} (ENTER DIGIT 7 AFTER WAIT)
IF [#{{RESPONSE_VAR}} EQ #0] GOTO100
G103 P1
;
;
;
G65 P{{FINALIZER}} A#1 B#{{RESPONSE_VAR}}
#{{RESPONSE_VAR}}=#0
DPRNT[MEIMADENG/V/1/TEST/FINALIZERRETURNED/MACHINE/{{MACHINE}}]
G103 P0
M30
%
'@

$timerFinalizer = @'
%
O0{{FINALIZER}} (MEIMAD NGC SEPARATE G65 TIMER FINALIZER TEST)
(A START TIMER - B M109 CHARACTER CODE - NO MOTION)
G103 P1
;
;
;
#3=ROUND[#3001]
;
IF [#1 EQ #0] GOTO900
IF [#2 EQ #0] GOTO900
#4=ROUND[#1]
#5=ROUND[#2]
#6=#3-#4
DPRNT[MEIMADENG/V/1/TEST/G65FINALIZER/MACHINE/{{MACHINE}}/STARTMS/#4[80]/ENDMS/#3[80]/ELAPSEDMS/#6[80]/INPUT/#5[30]]
IF [#6 LT 0.] GOTO910
IF [#6 LT {{MINIMUM_WAIT_MS}}.] GOTO920
G103 P0
M99
N900 G103 P0
#3000=921 (FINALIZER INPUT MISSING)
M99
N910 G103 P0
#3000=922 (TIMER MOVED BACKWARD)
M99
N920 G103 P0
#3000=923 (FINALIZER TIMER EARLY)
M99
%
'@

$counterProbe = @'
%
O0{{COUNTER}} (MEIMAD PERSISTENT COUNTER INCREMENT TEST)
(NO MOTION - COUNTER IS EVIDENCE ONLY AND NEVER WORKFLOW AUTHORITY)
G103 P1
;
IF [#{{COUNTER_VAR}} EQ #0] GOTO900
#1=ROUND[#{{COUNTER_VAR}}]
IF [ABS[#{{COUNTER_VAR}}-#1] GT 0.0001] GOTO910
IF [#1 LT 1.] GOTO910
IF [#1 GE 899999.] GOTO920
#{{COUNTER_VAR}}=#1+1.
;
;
#2=ROUND[#{{COUNTER_VAR}}]
DPRNT[MEIMADENG/V/1/TEST/PERSISTENTCOUNTER/MACHINE/{{MACHINE}}/BEFORE/#1[60]/AFTER/#2[60]]
G103 P0
M30
N900 G103 P0
#3000=924 (COUNTER NOT INITIALIZED)
M30
N910 G103 P0
#3000=925 (COUNTER VALUE INVALID)
M30
N920 G103 P0
#3000=926 (COUNTER EXHAUSTED)
M30
%
'@

$counterInitializer = @'
%
O0{{INITIALIZER}} (MEIMAD ONE-TIME PERSISTENT COUNTER INITIALIZER)
(NO MOTION - RUN ONCE ONLY AFTER EMPTY VALUE AND COLLISION APPROVAL)
(DO NOT RUN AFTER ANY COUNTER HISTORY EXISTS)
G103 P1
;
IF [#{{COUNTER_VAR}} NE #0] GOTO900
#{{COUNTER_VAR}}={{INITIAL_COUNTER}}.
;
;
#1=ROUND[#{{COUNTER_VAR}}]
DPRNT[MEIMADENG/V/1/TEST/COUNTERINITIALIZED/MACHINE/{{MACHINE}}/VALUE/#1[60]]
G103 P0
M30
N900 G103 P0
#3000=927 (COUNTER ALREADY SET)
M30
%
'@

$readme = @'
MEIMAD HAAS NGC REAL-MACHINE ENGINEERING TEST PACK
==================================================

STATUS: NO-MOTION ENGINEERING TESTS - NOT PRODUCTION MACROS

These programs answer the unresolved M109/look-ahead/finalizer and persistent
counter questions on one exact controller. They contain no movement, spindle,
feed, tool change, coolant, probing, offset write, production-cycle event, Machine
secret, nonce, NC identity, or verification response algorithm.

DO NOT LOAD OR RUN until all five O-numbers and both variable numbers are confirmed
free, the persistent counter is approved collision-free, the Server verification
setting is disabled, and the procedure document has been reviewed. Restore Setting
23 after loading if the site uses protected-program access. Stop on any unexpected
alarm, return, value, or DPRNT record.

Files:
- O0{{DIRECT}} tests a fresh #3001 read in the same M109 program context.
- O0{{CALLER}} calls O0{{FINALIZER}} after M109 and blank barriers.
- O0{{FINALIZER}} reads #3001 in the separate G65 context.
- O0{{INITIALIZER}} performs the selected counter's one-time positive initialization.
- O0{{COUNTER}} increments and reports the persistent evidence counter.

Selected sequence design: PERSISTENT_COUNTER.
Initial value: {{INITIAL_COUNTER}} (zero/unset is deliberately invalid).
Minimum operator wait used by timer tests: {{MINIMUM_WAIT_MS}} ms.

Use docs/haas-ngc-engineering-machine-tests.md for the exact order, hard stops,
expected records, Reset/E-stop/Single Block/Block Delete/reboot matrix, and result
record. This pack does not test #3001 assignment or force a timer wrap; v6 fails
closed if the timer moves backward, while sequence continuity comes only from the
persistent counter.
'@

$results = @'
# Haas NGC engineering-test result record

- Machine: {{MACHINE}}
- Controller / version:
- Controller serial:
- Test date / work order:
- Observer:
- O-numbers confirmed free:
- Response variable / collision approval: #{{RESPONSE_VAR}} /
- Persistent counter / collision approval: #{{COUNTER_VAR}} /
- Pack manifest SHA-256:

| Test | Result (`PASS`/`FAIL`/`NOT_TESTED`) | Exact observed value/alarm | Evidence file |
|---|---|---|---|
| Direct M109 fresh timer read | NOT_TESTED | | |
| Separate G65 finalizer fresh timer read | NOT_TESTED | | |
| Reset at M109 cannot return | NOT_TESTED | | |
| E-stop at M109 cannot return | NOT_TESTED | | |
| Single Block executes finalizer exactly once | NOT_TESTED | | |
| Block Delete ON cannot skip protection | NOT_TESTED | | |
| Mode-change behavior at M109 | NOT_TESTED | | |
| Counter one-time initialization | NOT_TESTED | | |
| Counter consecutive increments | NOT_TESTED | | |
| Counter retained after Reset | NOT_TESTED | | |
| Counter retained after E-stop | NOT_TESTED | | |
| Counter retained after controller reboot | NOT_TESTED | | |
| First post-reboot increment is exact next value | NOT_TESTED | | |

Decision: NOT_READY

Change the decision to `READY` only when every required row passes with evidence
and every header field is complete. Never record a secret, nonce, response code,
or protected arithmetic.
'@

$templates = [ordered]@{
    ('O0{0:D4}-M109-DIRECT-TIMER.CNC' -f $DirectTimerProgramNumber) = $directTimer
    ('O0{0:D4}-M109-G65-CALLER.CNC' -f $FinalizerCallerProgramNumber) = $finalizerCaller
    ('O0{0:D4}-G65-TIMER-FINALIZER.CNC' -f $TimerFinalizerProgramNumber) = $timerFinalizer
    ('O0{0:D4}-PERSISTENT-COUNTER-PROBE.CNC' -f $CounterProbeProgramNumber) = $counterProbe
    ('O0{0:D4}-ONE-TIME-COUNTER-INITIALIZER.CNC' -f $CounterInitializerProgramNumber) = $counterInitializer
    'README-REAL-MACHINE-TEST.txt' = $readme
    'RESULTS-TEMPLATE.md' = $results
}

foreach ($entry in $templates.GetEnumerator()) {
    Write-NewAsciiFile (Join-Path $outputFullPath $entry.Key) (Render-Template $entry.Value $values)
}

$manifestFiles = foreach ($name in $templates.Keys) {
    $path = Join-Path $outputFullPath $name
    [ordered]@{
        file = $name
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$manifest = [ordered]@{
    status = 'NO_MOTION_ENGINEERING_TESTS_NOT_PRODUCTION'
    productionReady = $false
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    machineLabel = $MachineLabel
    selectedSequenceDesign = 'PERSISTENT_COUNTER'
    programs = $programs
    responseVariable = $ResponseVariable
    persistentCounterVariable = $PersistentCounterVariable
    initialCounterValue = $InitialCounterValue
    minimumWaitMilliseconds = $MinimumWaitMilliseconds
    systemTimerAssignmentIncluded = $false
    files = @($manifestFiles)
    requiredBeforeLoad = @(
        'five program numbers confirmed free',
        'response and persistent-counter variables confirmed collision-free',
        'persistent counter confirmed empty before one-time initialization',
        'Server verification disabled',
        'named internal CNC engineer and Meimad observer approve test window')
}
$manifestPath = Join-Path $outputFullPath 'manifest.json'
Write-NewAsciiFile $manifestPath (($manifest | ConvertTo-Json -Depth 6) + "`r`n")

$checksumLines = foreach ($name in @($templates.Keys) + 'manifest.json') {
    $hash = (Get-FileHash -LiteralPath (Join-Path $outputFullPath $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$name"
}
Write-NewAsciiFile (Join-Path $outputFullPath 'SHA256SUMS.txt') (($checksumLines -join "`r`n") + "`r`n")

Write-Host "Generated no-motion NGC engineering test pack: $outputFullPath"
Write-Warning 'REAL MACHINE TESTS ONLY. Verification stays disabled; run strictly from the reviewed procedure.'
