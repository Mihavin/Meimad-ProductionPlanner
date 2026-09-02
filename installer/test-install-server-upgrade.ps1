$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installerRoot = Split-Path -Parent $PSCommandPath
$script = Join-Path $installerRoot 'install-server-upgrade.ps1'
$result = (& $script -ValidateOnly) | ConvertFrom-Json
if ($result.status -cne 'READY_FOR_ELEVATED_INSTALL' -or
    $result.msiVersion -isnot [string] -or
    $result.msiVersion -cne '0.1.78' -or
    $result.enabledVerificationMachines -ne 0 -or
    -not $result.administratorRequired -or
    [string]$result.sha256 -notmatch '^[0-9A-F]{64}$') {
    throw 'Server upgrade preflight did not prove a checksummed, disabled-verification handoff.'
}

$source = Get-Content -LiteralPath $script -Raw
foreach ($required in @(
    'Refusing Server upgrade while CNC verification is enabled',
    'Administrator elevation is required',
    'sc.exe qfailure',
    "status = 'INSTALLED_AND_VERIFIED'")) {
    if ($source.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "Server upgrade tool is missing fail-closed marker: $required"
    }
}
if ($source -match '(?i)-Verb\s+RunAs') {
    throw 'Server upgrade tool must not self-elevate or bypass an explicit administrator session.'
}

Write-Host 'Server upgrade preflight tests passed.'
