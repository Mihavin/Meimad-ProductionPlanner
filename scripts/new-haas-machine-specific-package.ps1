[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ConfigPath,

    [Parameter(Mandatory = $true)]
    [string] $OutputZip,

    [switch] $AcknowledgeQuarantinedAuditOnly,

    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $AcknowledgeQuarantinedAuditOnly) {
    throw @'
Machine-specific packaging is disabled by default because the contained macro-v5
design failed physical timeout and sequence acceptance. Use
-AcknowledgeQuarantinedAuditOnly only to reproduce a quarantined audit artifact;
it does not authorize controller loading or Server enablement.
'@
}

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$resolvedConfig = (Resolve-Path -LiteralPath $ConfigPath).Path
if ($resolvedConfig -notmatch '\.local\.json$') {
    throw 'ConfigPath must be a local *.local.json commissioning configuration.'
}
$config = Get-Content -LiteralPath $resolvedConfig -Raw | ConvertFrom-Json
if ([int]$config.derivedMachineKey -eq 271828) {
    throw 'The public test key belongs only in the BENCH-ONLY package.'
}
$machineLabel = [string]$config.machineLabel
if ($machineLabel -notmatch '^[A-Z0-9-]{1,40}$') {
    throw 'machineLabel must use 1-40 uppercase letters, digits, or hyphens.'
}
$testNcProgram = if ($config.PSObject.Properties.Name -contains 'testNcProgramNumber') {
    [int]$config.testNcProgramNumber
} else { 1990 }
$testOffsetProgram = if ($config.PSObject.Properties.Name -contains 'testOffsetLoaderProgramNumber') {
    [int]$config.testOffsetLoaderProgramNumber
} else { 1991 }

$resolvedZip = [IO.Path]::GetFullPath($OutputZip)
if ([IO.Path]::GetExtension($resolvedZip) -ne '.zip') { throw 'OutputZip must end in .zip.' }
$repositoryPrefix = $repositoryRoot.TrimEnd('\') + '\'
$artifactPrefix = ([IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))).TrimEnd('\') + '\'
if ($resolvedZip.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
    -not $resolvedZip.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'A sensitive generated ZIP inside the repository must stay below artifacts (git-ignored).'
}
if ((Test-Path -LiteralPath $resolvedZip) -and -not $Force) {
    throw "Refusing to overwrite '$resolvedZip'. Use -Force after reviewing the target."
}
$zipParent = Split-Path -Parent $resolvedZip
if (-not [string]::IsNullOrEmpty($zipParent)) { [IO.Directory]::CreateDirectory($zipParent) | Out-Null }

$stagingParent = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '.diagnostics\haas-machine-package'))
[IO.Directory]::CreateDirectory($stagingParent) | Out-Null
$staging = Join-Path $stagingParent ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($staging) | Out-Null
try {
    & (Join-Path $PSScriptRoot 'new-haas-verification-commissioning-pack.ps1') `
        -ConfigPath $resolvedConfig -OutputDirectory $staging `
        -AcknowledgeQuarantinedAuditOnly

    $readme = @"
MEIMAD HAAS MACHINE-SPECIFIC VERIFICATION CANDIDATE
===================================================

MACHINE LABEL: $machineLabel
STATUS: QUARANTINED - PHYSICAL TIMEOUT FAILURE - DO NOT LOAD

This ZIP contains a sensitive derived Machine key inside the protected verify
program. It does not contain the Server verification secret or the local JSON.

Macro candidates v3-v5 failed physical Reset/timeout acceptance. Do not load this
ZIP. A reviewed replacement input/timer and sequence design is required before a
new candidate is issued. Verification must remain disabled on the Server. Store
or destroy this ZIP under the site's credential procedure.
"@
    [IO.File]::WriteAllText((Join-Path $staging 'README-MACHINE-CANDIDATE.txt'),
        $readme.Replace("`r`n", "`n").Replace("`r", "`n").Replace("`n", "`r`n"),
        [Text.Encoding]::ASCII)

    if (Test-Path -LiteralPath $resolvedZip) { Remove-Item -LiteralPath $resolvedZip -Force }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $resolvedZip `
        -CompressionLevel Optimal

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($resolvedZip)
    try {
        $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        $challengeName = 'O0' + ('{0:D4}' -f [int]$config.challengeProgramNumber) + '-CHALLENGE.CNC'
        $verifyName = 'O0' + ('{0:D4}' -f [int]$config.verifyProgramNumber) + '-VERIFY.CNC'
        $testOffsetName = 'O0' + ('{0:D4}' -f $testOffsetProgram) + '-TEST-OFFSET-LOADER.CNC'
        $testNcName = 'O0' + ('{0:D4}' -f $testNcProgram) + '-TEST-NC-PROGRAM.CNC'
        foreach ($required in @(
            $challengeName, $verifyName, $testOffsetName, $testNcName,
            'manifest.json', 'README-MACHINE-CANDIDATE.txt')) {
            if ($names -notcontains $required) { throw "Machine-specific ZIP is missing $required." }
        }
        if ($names | Where-Object { $_ -match '\.local\.json$' }) {
            throw 'Machine-specific ZIP must not contain the local configuration.'
        }
    }
    finally { $archive.Dispose() }

    $hash = (Get-FileHash -LiteralPath $resolvedZip -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashPath = "$resolvedZip.sha256"
    if ((Test-Path -LiteralPath $hashPath) -and -not $Force) {
        throw "Refusing to overwrite '$hashPath'. Use -Force after reviewing the target."
    }
    [IO.File]::WriteAllText($hashPath,
        "$hash *$([IO.Path]::GetFileName($resolvedZip))`r`n", [Text.Encoding]::ASCII)
}
finally {
    $resolvedStaging = [IO.Path]::GetFullPath($staging)
    $requiredPrefix = $stagingParent.TrimEnd('\') + '\'
    if ($resolvedStaging.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedStaging)) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}

Write-Host "Built Machine-specific commissioning candidate: $resolvedZip"
Write-Host "SHA-256: $resolvedZip.sha256"
Write-Warning 'This ZIP is sensitive and QUARANTINED after a physical timeout failure. Do not load it.'
