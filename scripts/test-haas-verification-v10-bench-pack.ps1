$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$temporary = Join-Path $repositoryRoot ('.diagnostics\haas-v10-test-' + [Guid]::NewGuid().ToString('N'))
$localConfig = Join-Path ([IO.Path]::GetTempPath()) ('meimad-haas-v10-' + [Guid]::NewGuid().ToString('N') + '.local.json')
try {
    & (Join-Path $PSScriptRoot 'new-haas-verification-local-config.ps1') `
        -MachineId 'machine-v10-test' -MachineLabel 'V10-BENCH' `
        -OutputPath $localConfig -MacroVersion 10 -SampleNcIdentity 654321 `
        -SampleOffsetReleaseToken 483920

    & (Join-Path $PSScriptRoot 'new-haas-verification-v10-bench-pack.ps1') `
        -ConfigPath $localConfig -OutputDirectory $temporary `
        -AcknowledgeBenchOnlyCandidate

    $all = (Get-ChildItem -LiteralPath $temporary -File |
        ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
    $challenge = Get-Content -LiteralPath (Join-Path $temporary 'O09001-CHALLENGE-V10.CNC') -Raw
    $verify = Get-Content -LiteralPath (Join-Path $temporary 'O09002-VERIFY-INPUT-V10.CNC') -Raw
    $finalizer = Get-Content -LiteralPath (Join-Path $temporary 'O09003-VERIFY-FINALIZER-V10.CNC') -Raw
    $cycleTest = Get-Content -LiteralPath (Join-Path $temporary 'O01992-TEST-CYCLE-COUNT.CNC') -Raw
    $manifest = Get-Content -LiteralPath (Join-Path $temporary 'manifest.json') -Raw | ConvertFrom-Json

    if ($all -match '(?i)MACHINE.?SECRET|DERIVED.?MACHINE.?KEY|HMAC|API.?KEY|AUTH.?TOKEN') {
        throw 'V10 artifacts contain a forbidden Machine credential concept.'
    }
    if ($challenge -notmatch 'EVENT/OLC' -or $verify -notmatch 'EVENT/SVR' -or
        $finalizer -notmatch 'EVENT/SVS' -or $finalizer -notmatch 'EVENT/SVF') {
        throw 'V10 must emit OLC, SVR, SVS, and SVF lifecycle evidence.'
    }
    if ($challenge -match 'SEQ NOT INIT|SEQ EXHAUSTED' -or
        $verify -match 'SEQ NOT INIT|SEQ EXHAUSTED' -or
        $finalizer -match 'SEQ NOT INIT|SEQ EXHAUSTED') {
        throw 'V10 sequence evidence must never block verification.'
    }
    if ($verify -notmatch 'TIMEOUT STARTS ONLY AFTER THE SVR NC-START EVENT') {
        throw 'V10 must start its timeout only at the NC verification hook.'
    }
    if ($verify -notmatch 'SUCCESS CACHE AVOIDS A SECOND PROMPT') {
        throw 'V10 must avoid repeat prompts for the same successful binding.'
    }
    if ($cycleTest -notmatch 'EVENT/CST' -or $cycleTest -notmatch 'EVENT/CEN' -or
        $cycleTest -notmatch 'G65 P9002 A654321') {
        throw 'V10 cycle-count test must verify the NC identity and emit one START/END pair.'
    }
    if (([regex]::Matches($cycleTest, 'EVENT/CST')).Count -ne 1 -or
        ([regex]::Matches($cycleTest, 'EVENT/CEN')).Count -ne 1) {
        throw 'V10 cycle-count test must contain exactly one START and one END event.'
    }
    if ($manifest.eventSequence.role -ne 'EVIDENCE_ONLY' -or
        $manifest.verificationLifecycle -notmatch 'ARMED \(NO TIMEOUT\)') {
        throw 'V10 manifest must document evidence-only sequence and untimed ARMED state.'
    }
    foreach ($program in @($challenge, $verify, $finalizer, $cycleTest)) {
        if ($program -match '\b(?:G0?[0123]|M0?3|M0?4|M0?6|M0?8)\b') {
            throw 'A v10 no-motion artifact contains a motion, spindle, tool-change, or coolant command.'
        }
    }
    Write-Host 'Haas verification v10 bench-pack tests passed.'
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
    if (Test-Path -LiteralPath $localConfig) { Remove-Item -LiteralPath $localConfig -Force }
}
