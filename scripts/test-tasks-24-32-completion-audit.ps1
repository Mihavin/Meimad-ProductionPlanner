$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$audit = Get-Content -LiteralPath (Join-Path $repositoryRoot 'docs\tasks-24-32-completion-audit.md') -Raw
$rules = Get-Content -LiteralPath (Join-Path $repositoryRoot 'AGENTS.md') -Raw

foreach ($required in @(
    'MachineID`, fixed controller IP, and controller MAC',
    'OFFSET_LOADER_COMPLETED -> ARMED -> PENDING -> SUCCEEDED',
    'ARMED has no timeout',
    'Sequence is retained as duplicate/gap/reset/wrap/out-of-order evidence only',
    'No Machine credential is present',
    'Physical commissioning remains required')) {
    if ($audit.IndexOf($required, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Current CNC audit is missing: $required"
    }
}
foreach ($required in @(
    'CNC Machine identity consists only',
    'CNC event sequence numbers are evidence only',
    'OFFSET_LOADER_COMPLETED -> ARMED -> PENDING -> SUCCEEDED')) {
    if ($rules.IndexOf($required, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Repository rules are missing: $required"
    }
}

Write-Host 'Current CNC identity and verification audit tests passed.'
