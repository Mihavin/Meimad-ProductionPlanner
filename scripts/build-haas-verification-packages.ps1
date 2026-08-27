[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\haas-verification'
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$allowedOutput = ([IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))).TrimEnd('\') + '\'
if (-not $outputRoot.StartsWith($allowedOutput, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package output must stay below the repository artifacts directory.'
}
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null

$benchZip = Join-Path $outputRoot 'Meimad-Haas-BENCH-ONLY-v3.zip'
$toolkitZip = Join-Path $outputRoot 'Meimad-Haas-COMMISSIONING-TOOLKIT-v3.zip'
$checksumsPath = Join-Path $outputRoot 'SHA256SUMS.txt'
foreach ($target in @($benchZip, $toolkitZip, $checksumsPath)) {
    if ((Test-Path -LiteralPath $target) -and -not $Force) {
        throw "Refusing to overwrite '$target'. Use -Force after reviewing the target."
    }
}

$stagingParent = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '.diagnostics\haas-package-build'))
[IO.Directory]::CreateDirectory($stagingParent) | Out-Null
$staging = Join-Path $stagingParent ([Guid]::NewGuid().ToString('N'))
$benchRoot = Join-Path $staging 'bench'
$toolkitRoot = Join-Path $staging 'toolkit'
[IO.Directory]::CreateDirectory($benchRoot) | Out-Null
[IO.Directory]::CreateDirectory($toolkitRoot) | Out-Null

function Write-Ascii {
    param([string] $Path, [string] $Contents)
    [IO.File]::WriteAllText($Path,
        $Contents.Replace("`r`n", "`n").Replace("`r", "`n").Replace("`n", "`r`n"),
        [Text.Encoding]::ASCII)
}

try {
    & (Join-Path $PSScriptRoot 'new-haas-verification-commissioning-pack.ps1') `
        -ConfigPath (Join-Path $PSScriptRoot 'haas-verification-commissioning.example.json') `
        -OutputDirectory $benchRoot

    $machineOutput = Join-Path $benchRoot 'MACHINE-OUTPUT-TRANSCRIPT.txt'
    & dotnet run --project (Join-Path $repositoryRoot 'tools\Meimad.Planner.CncSimulator') -- `
        --scenario (Join-Path $repositoryRoot 'tools\Meimad.Planner.CncSimulator\scenario.verification-commissioning.json') `
        --output $machineOutput
    if ($LASTEXITCODE -ne 0) { throw 'CNC Machine-output simulator failed while building the package.' }

    Write-Ascii (Join-Path $benchRoot 'README-BENCH-ONLY.txt') @'
MEIMAD HAAS VERIFICATION BENCH PACKAGE
======================================

STATUS: QUARANTINED - PHYSICAL TIMEOUT FAILURE - DO NOT LOAD

This package contains no-motion O9001 challenge and O9002 verification
commissioning candidates, a matched O1991 test Offset Loader and O1990 test NC,
insertion snippets, and a simulated strict DPRNT transcript. It must not be
installed as a production protected-macro package.

Macro v3 failed Reset-during-input acceptance. Macro v5 then accepted a correct
response after at least 130 seconds at M109 despite its textual post-input timer
check. Versions 3-5 and bench packages v1-v3 are quarantined. Do not reload them.
A reviewed input/timer execution barrier and reboot/wrap sequence design are
required before another physical test.

Required before any controller load:
1. Haas Factory Outlet or qualified CNC engineer review.
2. Confirm O9001/O9002 and variables #10500-#10503 do not collide.
3. Isolate the Machine with spindle/feed disabled for the no-motion bench test.
4. Confirm Setting 23 protection and M109 behavior on the exact NGC version.
5. Complete every row and both sign-offs in the commissioning checklist.

Bench execution order after review:
1. Load/protect O9001 and O9002.
2. Run O1991. It performs no offset writes and creates the challenge.
3. With an isolated development Server context containing the matching public
   release token 483920 and NC identity 654321, confirm the tablet response.
   Without that exact context, STALE OFFSET LOADER is the expected safe result.
4. Run O1990 and enter the response one digit at a time.
5. Confirm success returns to the no-motion test and emits MEIMADSPIKE only.
6. Repeat with a wrong digit and prove alarm-before-return.

The NC hook must be the first executable block. The Offset Loader call belongs
only after every offset write and readback succeeds. Do not copy the public test
key into a production package.
'@

    $toolkitScripts = @(
        'new-haas-verification-local-config.ps1',
        'new-haas-verification-commissioning-pack.ps1',
        'new-haas-machine-specific-package.ps1',
        'haas-verification-commissioning.example.json',
        'test-haas-verification-commissioning-pack.ps1',
        'test-cnc-machine-output-simulator.ps1',
        'invoke-haas-verification-live-bench.ps1',
        'audit-cnc-commissioning-checklist.ps1',
        'test-cnc-commissioning-checklist.ps1'
    )
    $scriptFolder = Join-Path $toolkitRoot 'scripts'
    $docFolder = Join-Path $toolkitRoot 'docs'
    $simulatorFolder = Join-Path $toolkitRoot 'simulator'
    [IO.Directory]::CreateDirectory($scriptFolder) | Out-Null
    [IO.Directory]::CreateDirectory($docFolder) | Out-Null
    [IO.Directory]::CreateDirectory($simulatorFolder) | Out-Null
    foreach ($name in $toolkitScripts) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $scriptFolder
    }
    foreach ($name in @(
        'cnc-commissioning-checklist.md',
        'haas-verification-response-algorithm.md',
        'haas-protected-verification-spike.md')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot "docs\$name") -Destination $docFolder
    }
    foreach ($name in @(
        'Meimad.Planner.CncSimulator.csproj',
        'Program.cs',
        'README.md',
        'scenario.full.json',
        'scenario.verification-commissioning.json')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot "tools\Meimad.Planner.CncSimulator\$name") `
            -Destination $simulatorFolder
    }
    Write-Ascii (Join-Path $toolkitRoot 'README-COMMISSIONING.txt') @'
MEIMAD HAAS MACHINE-SPECIFIC COMMISSIONING TOOLKIT
==================================================

STATUS: QUARANTINED GENERATION TOOLKIT - DO NOT LOAD GENERATED MACROS

This ZIP contains no Server secret and no production Machine key. Generate a
Machine-specific local configuration from a secure interactive prompt:

powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-haas-verification-local-config.ps1 `
  -MachineId <stable-server-machine-id> -MachineLabel <UPPERCASE-LABEL> `
  -SampleNcIdentity <server-issued-six-digit-nc-id> `
  -SampleOffsetReleaseToken <current-server-issued-six-digit-token> `
  -OutputPath .\machine.local.json

Then generate the reviewed candidate macros:

powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-haas-verification-commissioning-pack.ps1 `
  -ConfigPath .\machine.local.json -OutputDirectory .\generated

Or build a checksummed Machine-specific ZIP directly:

powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-haas-machine-specific-package.ps1 `
  -ConfigPath .\machine.local.json -OutputZip .\MEIMAD-<MACHINE>-CANDIDATE.zip

The secure prompt secret is never written. The local JSON and generated O9000
program contain the derived Machine key and are sensitive. Apply site access
control. Do not email or commit them. Delete or archive them under the credential
procedure after controlled installation.

The current M109 candidate failed physical timeout acceptance and its #3001-only
sequence is not monotonic across reboot/wrap. Generated macros are quarantined and
must not be loaded. A reviewed replacement design, new automated evidence, bounded
physical retest, and both checklist sign-offs are required before verification is
enabled on the Server.
'@

    foreach ($zip in @($benchZip, $toolkitZip)) {
        if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    }
    Compress-Archive -Path (Join-Path $benchRoot '*') -DestinationPath $benchZip `
        -CompressionLevel Optimal
    Compress-Archive -Path (Join-Path $toolkitRoot '*') -DestinationPath $toolkitZip `
        -CompressionLevel Optimal

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $benchArchive = [IO.Compression.ZipFile]::OpenRead($benchZip)
    try {
        $benchNames = @($benchArchive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($required in @(
            'O09001-CHALLENGE.CNC', 'O09002-VERIFY.CNC', 'manifest.json',
            'O01991-TEST-OFFSET-LOADER.CNC', 'O01990-TEST-NC-PROGRAM.CNC',
            'README-BENCH-ONLY.txt', 'MACHINE-OUTPUT-TRANSCRIPT.txt')) {
            if ($benchNames -notcontains $required) { throw "Bench ZIP is missing $required." }
        }
    }
    finally { $benchArchive.Dispose() }

    $toolkitArchive = [IO.Compression.ZipFile]::OpenRead($toolkitZip)
    try {
        $toolkitNames = @($toolkitArchive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($required in @(
            'README-COMMISSIONING.txt',
            'scripts/new-haas-verification-local-config.ps1',
            'scripts/new-haas-verification-commissioning-pack.ps1',
            'scripts/new-haas-machine-specific-package.ps1',
            'scripts/invoke-haas-verification-live-bench.ps1',
            'scripts/audit-cnc-commissioning-checklist.ps1',
            'docs/cnc-commissioning-checklist.md',
            'simulator/scenario.verification-commissioning.json')) {
            if ($toolkitNames -notcontains $required) { throw "Toolkit ZIP is missing $required." }
        }
    }
    finally { $toolkitArchive.Dispose() }

    $checksumLines = foreach ($zip in @($benchZip, $toolkitZip)) {
        $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash *$([IO.Path]::GetFileName($zip))"
    }
    Write-Ascii $checksumsPath (($checksumLines -join "`n") + "`n")
}
finally {
    $resolvedStaging = [IO.Path]::GetFullPath($staging)
    $requiredPrefix = $stagingParent.TrimEnd('\') + '\'
    if ($resolvedStaging.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedStaging)) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}

Write-Host "Built Haas bench package: $benchZip"
Write-Host "Built Haas commissioning toolkit: $toolkitZip"
Write-Host "SHA-256 file: $checksumsPath"
Write-Warning 'The bench ZIP uses public test key 271828 and must never be deployed as the production protected-macro package.'
