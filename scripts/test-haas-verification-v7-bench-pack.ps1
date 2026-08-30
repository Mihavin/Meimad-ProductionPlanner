$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$temporary = Join-Path $repositoryRoot (".diagnostics\haas-v7-test-" + [Guid]::NewGuid().ToString('N'))
$localConfig = Join-Path ([IO.Path]::GetTempPath()) ("meimad-haas-v7-" + [Guid]::NewGuid().ToString('N') + '.local.json')
$aliasConfig = Join-Path ([IO.Path]::GetTempPath()) ("meimad-haas-v7-alias-" + [Guid]::NewGuid().ToString('N') + '.local.json')
try {
    $secret = ConvertTo-SecureString 'PUBLIC-V7-TEST-SECRET-ONLY' -AsPlainText -Force
    $aliasRejected = $false
    try {
        & (Join-Path $PSScriptRoot 'new-haas-verification-local-config.ps1') `
            -MachineId 'machine-v7-test' -MachineLabel 'V7-BENCH' `
            -OutputPath $aliasConfig -VerificationSecret $secret `
            -MacroVersion 7 `
            -NonceVariable 10500 -ResponseVariable 500 `
            -SampleNcIdentity 654321 -SampleOffsetReleaseToken 483920
    }
    catch { $aliasRejected = $_.Exception.Message -match 'legacy aliases' }
    if (-not $aliasRejected) {
        throw 'Local configuration must reject #500/#10500 as the same Haas variable.'
    }
    & (Join-Path $PSScriptRoot 'new-haas-verification-local-config.ps1') `
        -MachineId 'machine-v7-test' -MachineLabel 'V7-BENCH' `
        -OutputPath $localConfig -VerificationSecret $secret `
        -MacroVersion 7 `
        -SampleNcIdentity 654321 -SampleOffsetReleaseToken 483920

    $config = Get-Content -LiteralPath $localConfig -Raw | ConvertFrom-Json
    if ($config.macroVersion -ne 7 -or $config.finalizeProgramNumber -ne 9003 -or
        $config.eventSequenceVariable -ne 10504) {
        throw 'Local v7 configuration is incomplete.'
    }

    $blocked = $false
    try {
        & (Join-Path $PSScriptRoot 'new-haas-verification-v7-bench-pack.ps1') `
            -ConfigPath $localConfig -OutputDirectory $temporary
    }
    catch { $blocked = $_.Exception.Message -match 'disabled by default' }
    if (-not $blocked) { throw 'V7 generation must require explicit bench-only acknowledgement.' }

    & (Join-Path $PSScriptRoot 'new-haas-verification-v7-bench-pack.ps1') `
        -ConfigPath $localConfig -OutputDirectory $temporary `
        -AcknowledgeBenchOnlyCandidate

    $challenge = Get-Content -LiteralPath (Join-Path $temporary 'O09001-CHALLENGE-V7.CNC') -Raw
    $verify = Get-Content -LiteralPath (Join-Path $temporary 'O09002-VERIFY-INPUT-V7.CNC') -Raw
    $finalizer = Get-Content -LiteralPath (Join-Path $temporary 'O09003-VERIFY-FINALIZER-V7.CNC') -Raw
    $cycles = Get-Content -LiteralPath (Join-Path $temporary 'CYCLE-EVENT-BLOCKS.CNC.txt') -Raw
    $testNc = Get-Content -LiteralPath (Join-Path $temporary 'O01990-TEST-NC-PROGRAM.CNC') -Raw
    $manifest = Get-Content -LiteralPath (Join-Path $temporary 'manifest.json') -Raw | ConvertFrom-Json

    if ($challenge -match 'SEQ/#3001' -or $verify -match 'SEQ/#3001' -or
        $finalizer -match 'SEQ/#3001' -or $cycles -match 'SEQ/#3001') {
        throw '#3001 must never be used as the v7 event sequence.'
    }
    if ($challenge -notmatch '#10504=#30\+1\.' -or
        $finalizer -notmatch '#10504=#30\+1\.' -or
        $cycles -notmatch '#10504=#30\+1\.') {
        throw 'Every v7 event family must increment the configured persistent sequence.'
    }
    if ($challenge -notmatch 'EVENT/OLC/.+SEQ/#30\[60\].+MACROVERSION/7.+OFFSETRELEASE/.+/NONCE/') {
        throw 'V7 OLC does not use the strict correlated contract.'
    }
    if ($finalizer -notmatch 'EVENT/SVS/.+PROGRAM/#21\[60\]/OFFSETRELEASE/#23\[60\]/NONCE/#22\[60\]' -or
        $finalizer -notmatch 'EVENT/SVF/.+PROGRAM/#21\[60\]/OFFSETRELEASE/#23\[60\]/NONCE/#22\[60\]') {
        throw 'V7 result events must carry exact NC, release, and nonce correlation.'
    }
    if (($verify | Select-String -Pattern 'M109 P10500' -AllMatches).Matches.Count -ne 6) {
        throw 'V7 input macro must prompt for exactly six digits.'
    }
    $acceptedDigitClearPattern = '(?m)^#31=#31\*10\.\+\[#10500-48\.\]\r?\n#10500=#0\r?$'
    if ([regex]::Matches($verify, $acceptedDigitClearPattern).Count -ne 6) {
        throw 'V7 must clear the M109 response variable immediately after every accepted digit.'
    }
    $clear = $verify.IndexOf('(CONSUME ALL REUSABLE CHALLENGE STATE BEFORE THE FIRST M109 PROMPT)',
        [StringComparison]::Ordinal)
    $firstPrompt = $verify.IndexOf('M109 P10500', [StringComparison]::Ordinal)
    $finalizerCall = $verify.IndexOf('G65 P9003 A#20 B#29 C#32 D#21 E#24 F#31',
        [StringComparison]::Ordinal)
    if ($clear -lt 0 -or $firstPrompt -lt $clear -or $finalizerCall -lt $firstPrompt) {
        throw 'V7 must consume state before input and finalize only after input.'
    }
    if ($finalizer -notmatch '(?s)G103 P1\s*;\s*#20=ROUND\[#3001\]\s*;') {
        throw 'V7 finalizer must read #3001 behind a one-block look-ahead barrier.'
    }
    if ($finalizer -notmatch '(?s)#20=ROUND\[#3001\]\s*;\s*\(FINALIZER DEFENSE IN DEPTH CLEARS ALL TEMPORARY HANDSHAKE VARIABLES\)\s*#10502=#0\s*#10500=#0\s*#10501=#0\s*#10503=#0\s*;\s*IF \[#1 EQ #0\]') {
        throw 'V7 finalizer must clear all temporary handshake variables before any result branch.'
    }
    if ($finalizer -notmatch 'IF \[#8 EQ #0\] GOTO900' -or
        $finalizer -notmatch 'IF \[#9 EQ #0\] GOTO900') {
        throw 'V7 finalizer must reject omitted expected or entered response arguments.'
    }
    if ($finalizer -notmatch 'IF \[#27 LT 0\.\] GOTO910' -or
        $finalizer -notmatch 'IF \[#27 GT 120000\.\] GOTO910') {
        throw 'V7 finalizer must fail on reboot-negative or expired elapsed time.'
    }
    if ($challenge -notmatch '#10501=99999\.\+#30') {
        throw 'V7 nonce must derive from the non-repeating persistent sequence.'
    }
    if ($cycles -notmatch 'EVENT/CST/' -or $cycles -notmatch 'EVENT/CEN/' -or
        $cycles -match '#3901') {
        throw 'V7 cycle snippets must share the persistent sequence and not use the parts counter.'
    }
    if ($cycles -notmatch '(?s)EVENT/CEN/.+G103 P0\s*GOTO907\s*N905.+N906.+N907 \(CONTINUE DIRECTLY TO M30\)') {
        throw 'Normal cycle completion must jump past the fail-closed alarm labels.'
    }
    $executable = @($testNc -split "`r?`n" | Where-Object {
        $line = $_.Trim()
        $line -and $line -ne '%' -and $line -notmatch '^O\d+' -and $line -notmatch '^\('
    })
    if ($executable[0] -ne 'G65 P9002 A654321. (MEIMAD VERIFY V1)') {
        throw 'V7 test NC does not have the exact first executable verification hook.'
    }
    foreach ($program in @($challenge, $verify, $finalizer, $testNc)) {
        if ($program -match '\b(?:G0?[0123]|M0?3|M0?4|M0?6|M0?8)\b') {
            throw 'A v7 no-motion artifact contains a motion, spindle, tool-change, or coolant command.'
        }
    }
    if ($manifest.status -ne 'BENCH_ONLY_INTERNAL_REVIEW_REQUIRED' -or $manifest.productionReady) {
        throw 'V7 manifest must remain explicitly non-production.'
    }
    if ($manifest.eventSequence.resetOrWrapAllowed -or $manifest.eventSequence.maximum -ne 899999 -or
        $manifest.eventSequence.initialValue -ne 1 -or
        $manifest.eventSequence.initialization -ne 'AUTHORIZED_ONE_TIME_POSITIVE_INTEGER_AFTER_SERVER_SOURCE_HISTORY_REVIEW') {
        throw 'V7 manifest does not preserve fail-closed sequence exhaustion.'
    }
    if ($manifest.files.Count -ne 8) { throw 'V7 manifest must hash all eight artifacts.' }
    foreach ($file in $manifest.files) {
        $actual = (Get-FileHash -LiteralPath (Join-Path $temporary $file.file) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $file.sha256) { throw "Hash mismatch for $($file.file)." }
    }

    Write-Host 'Haas verification v7 bench-pack tests passed.'
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
    if (Test-Path -LiteralPath $localConfig) {
        Remove-Item -LiteralPath $localConfig -Force
    }
    if (Test-Path -LiteralPath $aliasConfig) {
        Remove-Item -LiteralPath $aliasConfig -Force
    }
}
