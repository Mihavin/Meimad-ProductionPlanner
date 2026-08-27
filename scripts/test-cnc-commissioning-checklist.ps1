$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$auditScript = Join-Path $PSScriptRoot 'audit-cnc-commissioning-checklist.ps1'
$checklist = Join-Path $PSScriptRoot '..\docs\cnc-commissioning-checklist.md'
$audit = ((& $auditScript -ChecklistPath $checklist) | ConvertFrom-Json)
if ($audit.status -ne 'NOT_READY' -or $audit.declaredReady -or
    -not $audit.declarationConsistent -or $audit.checks.total -ne 14 -or
    $audit.checks.passed -ne 1 -or $audit.checks.failed -ne 0 -or
    $audit.checks.notTested -ne 13 -or $audit.unsignedRoles.Count -ne 2) {
    throw 'The current partial physical commissioning record was not graded accurately.'
}

$rejected = $false
try { & $auditScript -ChecklistPath $checklist -RequireReady | Out-Null }
catch { $rejected = $true }
if (-not $rejected) { throw 'RequireReady unexpectedly accepted incomplete physical evidence.' }

Write-Host 'CNC commissioning-checklist gate tests passed.'
