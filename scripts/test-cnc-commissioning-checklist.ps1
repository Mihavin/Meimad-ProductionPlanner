$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$algorithm = Get-Content -LiteralPath (Join-Path $repositoryRoot 'docs\haas-verification-response-algorithm.md') -Raw
$generator = Get-Content -LiteralPath (Join-Path $repositoryRoot 'scripts\new-haas-verification-v10-bench-pack.ps1') -Raw
$audit = Get-Content -LiteralPath (Join-Path $repositoryRoot 'docs\tasks-24-32-completion-audit.md') -Raw

foreach ($required in @(
    'no ARMED timeout',
    'first intended NC start',
    'Later starts',
    'new Offset Loader release supersedes',
    'sequence field is diagnostic evidence')) {
    if ($algorithm.IndexOf($required, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Verification contract is missing: $required"
    }
}
if ($generator.IndexOf('macroVersion $config.macroVersion 10 10', [StringComparison]::Ordinal) -lt 0) {
    throw 'V10-only generator guard is missing.'
}
if ($audit.IndexOf('Physical commissioning remains required', [StringComparison]::OrdinalIgnoreCase) -lt 0) {
    throw 'The audit must retain the physical commissioning gate.'
}

Write-Host 'CNC commissioning contract tests passed.'
