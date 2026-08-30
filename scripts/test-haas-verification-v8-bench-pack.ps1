$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$temporary = Join-Path $repositoryRoot ('.diagnostics\haas-v8-test-' + [Guid]::NewGuid().ToString('N'))
$localConfig = Join-Path ([IO.Path]::GetTempPath()) ('meimad-haas-v8-' + [Guid]::NewGuid().ToString('N') + '.local.json')
try {
    $secret = ConvertTo-SecureString 'PUBLIC-V8-TEST-SECRET-ONLY' -AsPlainText -Force
    & (Join-Path $PSScriptRoot 'new-haas-verification-local-config.ps1') `
        -MachineId 'machine-v8-test' -MachineLabel 'V8-BENCH' `
        -OutputPath $localConfig -VerificationSecret $secret `
        -MacroVersion 8 -SampleNcIdentity 654321 `
        -SampleOffsetReleaseToken 483920

    $blocked = $false
    try {
        & (Join-Path $PSScriptRoot 'new-haas-verification-v8-bench-pack.ps1') `
            -ConfigPath $localConfig -OutputDirectory $temporary
    }
    catch { $blocked = $_.Exception.Message -match 'disabled by default' }
    if (-not $blocked) {
        throw 'V8 generation must require explicit bench-only acknowledgement.'
    }

    & (Join-Path $PSScriptRoot 'new-haas-verification-v8-bench-pack.ps1') `
        -ConfigPath $localConfig -OutputDirectory $temporary `
        -AcknowledgeBenchOnlyCandidate

    $challenge = Get-Content -LiteralPath (Join-Path $temporary 'O09001-CHALLENGE-V8.CNC') -Raw
    $verify = Get-Content -LiteralPath (Join-Path $temporary 'O09002-VERIFY-INPUT-V8.CNC') -Raw
    $finalizer = Get-Content -LiteralPath (Join-Path $temporary 'O09003-VERIFY-FINALIZER-V8.CNC') -Raw
    $testNc = Get-Content -LiteralPath (Join-Path $temporary 'O01990-TEST-NC-PROGRAM.CNC') -Raw
    $manifest = Get-Content -LiteralPath (Join-Path $temporary 'manifest.json') -Raw | ConvertFrom-Json

    if ($challenge -notmatch 'MACROVERSION/8' -or
        $finalizer -notmatch 'MACROVERSION/8') {
        throw 'V8 artifacts must report only macro version 8.'
    }
    if ($finalizer -notmatch '(?s)EVENT/SVF/.+G103 P0\s+G04 P1\. \(ALLOW SVF DPRNT TRANSMISSION BEFORE FAIL-CLOSED ALARM\)\s+#3000=903') {
        throw 'V8 must place the one-second transmission dwell between SVF/G103 and alarm 903.'
    }
    if (($finalizer | Select-String -Pattern 'G04 P1\.' -AllMatches).Matches.Count -ne 1) {
        throw 'V8 finalizer must contain exactly one reviewed failure DPRNT dwell.'
    }
    if (($verify | Select-String -Pattern 'M109 P10500' -AllMatches).Matches.Count -ne 6) {
        throw 'V8 input macro must prompt for exactly six digits.'
    }
    $acceptedDigitClearPattern = '(?m)^#31=#31\*10\.\+\[#10500-48\.\]\r?\n#10500=#0\r?$'
    if ([regex]::Matches($verify, $acceptedDigitClearPattern).Count -ne 6) {
        throw 'V8 must preserve the per-digit response-variable cleanup.'
    }
    $testExecutable = @($testNc -split "`r?`n" | Where-Object {
        $line = $_.Trim()
        $line -and $line -ne '%' -and $line -notmatch '^O\d+' -and $line -notmatch '^\('
    })
    if ($testExecutable[0] -ne 'G65 P9002 A654321. (MEIMAD VERIFY V1)') {
        throw 'V8 test NC must preserve the exact generic first-block hook.'
    }
    foreach ($program in @($challenge, $verify, $finalizer, $testNc)) {
        if ($program -match '\b(?:G0?[0123]|M0?3|M0?4|M0?6|M0?8)\b') {
            throw 'A v8 no-motion artifact contains a motion, spindle, tool-change, or coolant command.'
        }
    }
    if ($manifest.macroVersion -ne 8 -or
        $manifest.status -ne 'BENCH_ONLY_INTERNAL_REVIEW_REQUIRED' -or
        $manifest.productionReady) {
        throw 'V8 manifest identity or bench-only disposition is invalid.'
    }
    if ($manifest.files.Count -ne 8) {
        throw 'V8 manifest must hash all eight artifacts.'
    }
    foreach ($file in $manifest.files) {
        $actual = (Get-FileHash -LiteralPath (Join-Path $temporary $file.file) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $file.sha256) {
            throw "Hash mismatch for $($file.file)."
        }
    }

    Write-Host 'Haas verification v8 bench-pack tests passed.'
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
    if (Test-Path -LiteralPath $localConfig) {
        Remove-Item -LiteralPath $localConfig -Force
    }
}
