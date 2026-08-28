$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$generator = Join-Path $PSScriptRoot 'new-haas-ngc-engineering-test-pack.ps1'
$auditScript = Join-Path $PSScriptRoot 'audit-haas-ngc-engineering-results.ps1'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repositoryRoot ('.diagnostics\haas-ngc-engineering-test-' + [Guid]::NewGuid().ToString('N'))

$rejected = $false
try { & $generator -MachineLabel 'VF3SS-TEST' -OutputDirectory $output | Out-Null }
catch { $rejected = $_.Exception.Message -match 'disabled by default' }
if (-not $rejected) { throw 'Engineering pack generation must require explicit real-machine acknowledgement.' }

$rejected = $false
try {
    & $generator -MachineLabel 'VF3SS-TEST' -OutputDirectory $output `
        -AcknowledgeNoMotionRealMachineTests | Out-Null
}
catch { $rejected = $_.Exception.Message -match 'one recorded initialization write' }
if (-not $rejected) { throw 'Engineering pack generation must require counter-initialization acknowledgement.' }

$aliasOutput = Join-Path $repositoryRoot ('.diagnostics\haas-ngc-alias-test-' + [Guid]::NewGuid().ToString('N'))
$rejected = $false
try {
    & $generator -MachineLabel 'VF3SS-TEST' -OutputDirectory $aliasOutput `
        -ResponseVariable 504 -PersistentCounterVariable 10504 `
        -AcknowledgeNoMotionRealMachineTests `
        -AcknowledgeOneTimePersistentCounterInitialization | Out-Null
}
catch { $rejected = $_.Exception.Message -match 'legacy aliases' }
if (-not $rejected) { throw 'Engineering pack must reject Haas #504/#10504 alias collision.' }

& $generator -MachineLabel 'VF3SS-TEST' -OutputDirectory $output `
    -AcknowledgeNoMotionRealMachineTests `
    -AcknowledgeOneTimePersistentCounterInitialization

$expected = @(
    'O01980-M109-DIRECT-TIMER.CNC',
    'O01981-M109-G65-CALLER.CNC',
    'O01982-G65-TIMER-FINALIZER.CNC',
    'O01983-PERSISTENT-COUNTER-PROBE.CNC',
    'O01984-ONE-TIME-COUNTER-INITIALIZER.CNC',
    'README-REAL-MACHINE-TEST.txt',
    'RESULTS-TEMPLATE.md',
    'manifest.json',
    'SHA256SUMS.txt')
foreach ($name in $expected) {
    if (-not (Test-Path -LiteralPath (Join-Path $output $name))) {
        throw "Engineering test pack is missing $name."
    }
}

$ncFiles = @(Get-ChildItem -LiteralPath $output -Filter '*.CNC')
if ($ncFiles.Count -ne 5) { throw 'Engineering pack must contain exactly five NC programs.' }
foreach ($file in $ncFiles) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    if ($text -match '(?im)^\s*(?:G0?[0123](?=\s|$)|M0?[3468](?=\s|$)|T\d+\b)') {
        throw "$($file.Name) contains a forbidden motion/spindle/tool/coolant block."
    }
    if ($text -match '(?im)\b(?:G43|G44|G49|G54|G55|G56|G57|G58|G59)\b') {
        throw "$($file.Name) contains a forbidden tool/work-offset command."
    }
    if ($text -match '(?im)#3001\s*=') {
        throw "$($file.Name) must not assign the Haas system timer."
    }
    if ($text -match '(?im)\b(?:CST|CEN|OLC|SVS|SVF)\b') {
        throw "$($file.Name) must not emit production or verification protocol events."
    }
}

$direct = Get-Content -LiteralPath (Join-Path $output 'O01980-M109-DIRECT-TIMER.CNC') -Raw
$caller = Get-Content -LiteralPath (Join-Path $output 'O01981-M109-G65-CALLER.CNC') -Raw
$finalizer = Get-Content -LiteralPath (Join-Path $output 'O01982-G65-TIMER-FINALIZER.CNC') -Raw
$counter = Get-Content -LiteralPath (Join-Path $output 'O01983-PERSISTENT-COUNTER-PROBE.CNC') -Raw
$initializer = Get-Content -LiteralPath (Join-Path $output 'O01984-ONE-TIME-COUNTER-INITIALIZER.CNC') -Raw
$readme = Get-Content -LiteralPath (Join-Path $output 'README-REAL-MACHINE-TEST.txt') -Raw
$results = Get-Content -LiteralPath (Join-Path $output 'RESULTS-TEMPLATE.md') -Raw
$manifest = Get-Content -LiteralPath (Join-Path $output 'manifest.json') -Raw | ConvertFrom-Json

if ($direct.IndexOf('M109 P10500', [StringComparison]::Ordinal) -gt
    $direct.IndexOf('#2=ROUND[#3001]', [StringComparison]::Ordinal)) {
    throw 'Direct timer read is not sourced after M109 in the program text.'
}
if ($direct -notmatch 'IF \[#3 LT 15000\.\] GOTO900' -or
    $direct -notmatch 'TEST/M109DIRECT') {
    throw 'Direct timer test is missing the independent-wait fail-closed check.'
}
if ($caller -notmatch 'M109 P10500[\s\S]+G103 P1\r?\n;\r?\n;\r?\n;\r?\nG65 P1982' -or
    $caller -notmatch 'TEST/FINALIZERRETURNED') {
    throw 'G65 caller is missing the M109 barrier or return evidence.'
}
if ($finalizer -notmatch '#3=ROUND\[#3001\]' -or
    $finalizer -notmatch 'IF \[#6 LT 0\.\] GOTO910' -or
    $finalizer -notmatch 'IF \[#6 LT 15000\.\] GOTO920') {
    throw 'Separate finalizer must read a fresh timer and fail closed on backward/early time.'
}
if ($counter -notmatch 'IF \[#10504 EQ #0\] GOTO900' -or
    $counter -notmatch 'IF \[#1 LT 1\.\] GOTO910' -or
    $counter -notmatch 'IF \[#1 GE 899999\.\] GOTO920' -or
    $counter -notmatch '#10504=#1\+1\.' -or
    $counter -match '(?i)wrap') {
    throw 'Persistent counter probe must reject unset/invalid/exhausted values and never wrap.'
}
if ($initializer -notmatch 'IF \[#10504 NE #0\] GOTO900' -or
    $initializer -notmatch '#10504=1\.' -or
    $initializer -match '#10504=0\.') {
    throw 'Counter initializer must be one-time and use a positive value.'
}
foreach ($marker in @(
    'Selected sequence design: PERSISTENT_COUNTER.',
    'zero/unset is deliberately invalid',
    'does not test #3001 assignment or force a timer wrap')) {
    if ($readme.IndexOf($marker, [StringComparison]::Ordinal) -lt 0) {
        throw "Engineering README is missing: $marker"
    }
}
$resultAudit = ((& $auditScript -ResultsPath (Join-Path $output 'RESULTS-TEMPLATE.md')) | ConvertFrom-Json)
if ($resultAudit.status -ne 'NOT_READY' -or $resultAudit.checks.total -ne 13 -or
    $resultAudit.checks.notTested -ne 13 -or $resultAudit.missingHeaders.Count -ne 8 -or
    -not $resultAudit.declarationConsistent) {
    throw 'Blank physical engineering result template was not graded fail-closed.'
}
$rejected = $false
try { & $auditScript -ResultsPath (Join-Path $output 'RESULTS-TEMPLATE.md') -RequirePass | Out-Null }
catch { $rejected = $_.Exception.Message -match 'incomplete or inconsistent' }
if (-not $rejected) { throw 'Physical engineering result gate accepted unperformed tests.' }
foreach ($marker in @(
    'Reset at M109 cannot return',
    'E-stop at M109 cannot return',
    'Counter retained after controller reboot',
    'First post-reboot increment is exact next value')) {
    if ($results.IndexOf($marker, [StringComparison]::Ordinal) -lt 0) {
        throw "Engineering results template is missing: $marker"
    }
}
if ($manifest.status -ne 'NO_MOTION_ENGINEERING_TESTS_NOT_PRODUCTION' -or
    $manifest.productionReady -ne $false -or
    $manifest.selectedSequenceDesign -ne 'PERSISTENT_COUNTER' -or
    $manifest.initialCounterValue -ne 1 -or
    $manifest.systemTimerAssignmentIncluded -ne $false) {
    throw 'Engineering manifest does not preserve the selected fail-closed boundary.'
}

$checksumRows = Get-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt')
if ($checksumRows.Count -ne 8) { throw 'Engineering checksum file must cover seven source files plus the manifest.' }
foreach ($row in $checksumRows) {
    if ($row -notmatch '^([0-9a-f]{64}) \*(.+)$') { throw "Invalid checksum row: $row" }
    $actual = (Get-FileHash -LiteralPath (Join-Path $output $Matches[2]) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Matches[1]) { throw "Checksum mismatch for $($Matches[2])." }
}

$rejected = $false
try {
    & $generator -MachineLabel 'VF3SS-TEST' -OutputDirectory $output `
        -AcknowledgeNoMotionRealMachineTests `
        -AcknowledgeOneTimePersistentCounterInitialization | Out-Null
}
catch { $rejected = $_.Exception.Message -match 'Refusing to overwrite' }
if (-not $rejected) { throw 'Engineering generator must refuse overwrite without -Force.' }

Write-Host 'Haas NGC real-machine engineering test-pack checks passed.'
