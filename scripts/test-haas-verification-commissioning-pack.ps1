$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ("meimad-haas-pack-" + [Guid]::NewGuid().ToString('N'))
$localConfig = "$temporary.local.json"
$machineZip = "$temporary.zip"
try {
    $testSecretText = 'PUBLIC-TEST-SECRET-DO-NOT-USE'
    $testSecret = ConvertTo-SecureString $testSecretText -AsPlainText -Force
    & (Join-Path $PSScriptRoot 'new-haas-verification-local-config.ps1') `
        -MachineId 'machine-test-1' -MachineLabel 'BENCH-VF3' `
        -OutputPath $localConfig -VerificationSecret $testSecret `
        -SampleNcIdentity 654321 -SampleOffsetReleaseToken 483920 -MacroVersion 5
    $localText = Get-Content -LiteralPath $localConfig -Raw
    if ($localText.Contains($testSecretText)) { throw 'Local config leaked the verification secret.' }
    $localValue = $localText | ConvertFrom-Json
    if ($localValue.derivedMachineKey -lt 100000 -or $localValue.derivedMachineKey -gt 999999) {
        throw 'Derived Machine key is outside the required six-digit range.'
    }
    if ($localValue.macroVersion -ne 5) {
        throw 'The quarantined audit fixture must remain pinned to macro version 5.'
    }
    $blockedByDefault = $false
    try {
        & (Join-Path $PSScriptRoot 'new-haas-verification-commissioning-pack.ps1') `
            -ConfigPath $localConfig -OutputDirectory $temporary
    }
    catch {
        $blockedByDefault = $_.Exception.Message -match 'disabled by default'
    }
    if (-not $blockedByDefault) {
        throw 'Quarantined macro generation must fail unless audit-only reproduction is acknowledged.'
    }
    & (Join-Path $PSScriptRoot 'new-haas-verification-commissioning-pack.ps1') `
        -ConfigPath $localConfig `
        -OutputDirectory $temporary `
        -AcknowledgeQuarantinedAuditOnly

    $challenge = Get-Content -LiteralPath (Join-Path $temporary 'O09001-CHALLENGE.CNC') -Raw
    $verify = Get-Content -LiteralPath (Join-Path $temporary 'O09002-VERIFY.CNC') -Raw
    $hook = Get-Content -LiteralPath (Join-Path $temporary 'NC-FIRST-BLOCK-HOOK.CNC.txt') -Raw
    $offset = Get-Content -LiteralPath (Join-Path $temporary 'OFFSET-LOADER-FINAL-CALL.CNC.txt') -Raw
    $testOffset = Get-Content -LiteralPath (Join-Path $temporary 'O01991-TEST-OFFSET-LOADER.CNC') -Raw
    $testNc = Get-Content -LiteralPath (Join-Path $temporary 'O01990-TEST-NC-PROGRAM.CNC') -Raw
    $manifest = Get-Content -LiteralPath (Join-Path $temporary 'manifest.json') -Raw | ConvertFrom-Json

    if ($challenge -notmatch 'EVENT/OLC/.+MACROVERSION/5/PROGRAM/.+/OFFSETRELEASE/.+/NONCE/') {
        throw 'Challenge macro does not contain the strict OLC field order.'
    }
    if ($verify -notmatch 'EVENT/SVS/' -or $verify -notmatch 'EVENT/SVF/') {
        throw 'Verify macro must emit both success and failure evidence.'
    }
    if ($verify -match '#10502=0\.-#20' -or
        $verify -notmatch 'EVENT/SVS/.+[\r\n]+#10502=#0') {
        throw 'Success must clear handshake state instead of persisting reusable authority.'
    }
    if (($verify | Select-String -Pattern 'M109 P10500' -AllMatches).Matches.Count -ne 6) {
        throw 'Verify macro must request exactly six configured M109 digits.'
    }
    $consumePattern = '(?s)#29=ROUND\[#10501\].*#32=ROUND\[#10503\].*' +
        '\(CONSUME PERSISTENT CHALLENGE BEFORE OPERATOR INPUT\).*' +
        '#10502=#0.*#10500=#0.*#10501=#0.*#10503=#0.*' +
        '#26=#29.*#26=#32.*M109 P10500'
    if ($verify -notmatch $consumePattern) {
        throw 'Verify macro must consume persistent challenge authority and use local copies before M109 input.'
    }
    $lastInput = $verify.LastIndexOf('M109 P10500', [StringComparison]::Ordinal)
    $postInputTimeout = $verify.IndexOf('#22=ROUND[#3001]-#21', $lastInput, [StringComparison]::Ordinal)
    $responseComparison = $verify.IndexOf('IF [ROUND[#31] NE ROUND[#24]] GOTO910', [StringComparison]::Ordinal)
    if ($lastInput -lt 0 -or $postInputTimeout -lt $lastInput -or
        $responseComparison -lt $postInputTimeout) {
        throw 'Verify macro must recheck challenge age after M109 entry and before response comparison.'
    }
    if ($hook.TrimEnd() -notmatch 'G65 P9002 A654321\. \(MEIMAD VERIFY V1\)$') {
        throw 'Generated hook does not match the Server-accepted syntax.'
    }
    if ($offset -notmatch 'G65 P9001 A483920\. B654321\.') {
        throw 'Generated Offset Loader call does not bind both six-digit tokens.'
    }
    if ($testOffset -notmatch 'G65 P9001 A483920\. B654321\.[\r\n]+M30' -or
        $testOffset -match '\bG10\b') {
        throw 'Test Offset Loader must call the challenge last and must not write offsets.'
    }
    $testNcExecutable = @($testNc -split "`r?`n" | Where-Object {
        $value = $_.Trim()
        $value -and $value -ne '%' -and $value -notmatch '^O\d+' -and $value -notmatch '^\('
    })
    if ($testNcExecutable[0] -ne 'G65 P9002 A654321. (MEIMAD VERIFY V1)') {
        throw 'The verification hook is not the first executable test-NC block.'
    }
    if ($testNc -match 'EVENT/(?:CST|CEN)' -or
        $testNc -match '\b(?:G0?[0123]|M0?3|M0?4|M0?6|M0?8)\b') {
        throw 'Test NC must not contain production-cycle events or cutting commands.'
    }
    if ($challenge -match '\b(?:G0?[0123]|M0?3|M0?4|M0?6|M0?8)\b' -or
        $verify -match '\b(?:G0?[0123]|M0?3|M0?4|M0?6|M0?8)\b') {
        throw 'Protected candidates unexpectedly contain a motion/spindle/tool/coolant command.'
    }
    if ($manifest.status -ne 'QUARANTINED_PHYSICAL_TIMEOUT_FAILURE') {
        throw 'Manifest must retain the physical-timeout quarantine status.'
    }
    if ($manifest.files.Count -ne 7) { throw 'Manifest must hash all seven generated artifacts.' }
    foreach ($file in $manifest.files) {
        $actual = (Get-FileHash -LiteralPath (Join-Path $temporary $file.file) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $file.sha256) { throw "Hash mismatch for $($file.file)." }
    }
    $machinePackageBlockedByDefault = $false
    try {
        & (Join-Path $PSScriptRoot 'new-haas-machine-specific-package.ps1') `
            -ConfigPath $localConfig -OutputZip $machineZip
    }
    catch {
        $machinePackageBlockedByDefault = $_.Exception.Message -match 'disabled by default'
    }
    if (-not $machinePackageBlockedByDefault) {
        throw 'Quarantined Machine-specific ZIP creation must fail without audit-only acknowledgement.'
    }
    $bundleBuildBlockedByDefault = $false
    try {
        & (Join-Path $PSScriptRoot 'build-haas-verification-packages.ps1')
    }
    catch {
        $bundleBuildBlockedByDefault = $_.Exception.Message -match 'disabled by default'
    }
    if (-not $bundleBuildBlockedByDefault) {
        throw 'Quarantined bundle creation must fail without audit-only acknowledgement.'
    }
    & (Join-Path $PSScriptRoot 'new-haas-machine-specific-package.ps1') `
        -ConfigPath $localConfig -OutputZip $machineZip `
        -AcknowledgeQuarantinedAuditOnly
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($machineZip)
    try {
        $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        if ($names -notcontains 'O09001-CHALLENGE.CNC' -or
            $names -notcontains 'O09002-VERIFY.CNC' -or
            $names -notcontains 'O01991-TEST-OFFSET-LOADER.CNC' -or
            $names -notcontains 'O01990-TEST-NC-PROGRAM.CNC' -or
            $names -notcontains 'README-MACHINE-CANDIDATE.txt') {
            throw 'Machine-specific package is incomplete.'
        }
        if ($names | Where-Object { $_ -match '\.local\.json$' }) {
            throw 'Machine-specific package leaked the local configuration.'
        }
    }
    finally { $archive.Dispose() }
    if (-not (Test-Path -LiteralPath "$machineZip.sha256")) {
        throw 'Machine-specific package checksum is missing.'
    }
    Write-Host 'Haas verification commissioning-pack tests passed.'
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
    if (Test-Path -LiteralPath $localConfig) {
        Remove-Item -LiteralPath $localConfig -Force
    }
    if (Test-Path -LiteralPath $machineZip) {
        Remove-Item -LiteralPath $machineZip -Force
    }
    if (Test-Path -LiteralPath "$machineZip.sha256") {
        Remove-Item -LiteralPath "$machineZip.sha256" -Force
    }
}
