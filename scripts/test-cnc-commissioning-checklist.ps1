$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$auditScript = Join-Path $PSScriptRoot 'audit-cnc-commissioning-checklist.ps1'
$checklist = Join-Path $PSScriptRoot '..\docs\cnc-commissioning-checklist.md'
$boundedRetest = Join-Path $PSScriptRoot '..\docs\haas-bounded-retest-after-hfo-approval.md'
$engineeringReview = Join-Path $PSScriptRoot '..\docs\haas-hfo-review-request-2026-08-27.md'
$audit = ((& $auditScript -ChecklistPath $checklist) | ConvertFrom-Json)
if ($audit.status -ne 'NOT_READY' -or $audit.declaredReady -or
    -not $audit.declarationConsistent -or $audit.checks.total -ne 14 -or
    $audit.checks.passed -ne 4 -or $audit.checks.failed -ne 1 -or
    $audit.checks.notTested -ne 9 -or
    $audit.unrecordedMachineFields.Count -ne 5 -or
    $audit.unsignedRoles.Count -ne 2) {
    throw 'The current partial physical commissioning record was not graded accurately.'
}

$rejected = $false
try { & $auditScript -ChecklistPath $checklist -RequireReady | Out-Null }
catch { $rejected = $true }
if (-not $rejected) { throw 'RequireReady unexpectedly accepted incomplete physical evidence.' }

$retestText = Get-Content -LiteralPath $boundedRetest -Raw
foreach ($required in @(
    '**DO NOT RUN YET.**',
    '30 minutes maximum at the CNC',
    'written internal engineering decision record is completed',
    'Macro v6',
    'BENCH_ONLY_INTERNAL_REVIEW_REQUIRED',
    'Challenge / verify / finalizer program numbers',
    'Late correct response must fail closed',
    'Timely correct response and sequence adjacency',
    'Reset during input',
    'Controller reboot sequence contract',
    'Disable verification again regardless of the result',
    'No source edit, MDI workaround')) {
    if ($retestText.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "Bounded retest plan is missing the required fail-closed marker: $required"
    }
}
if ($retestText -match '(?im)^\s*(?:G0?[0123]|M0?3|M0?4|M0?6|M0?8|T\d+)\b') {
    throw 'Bounded retest plan must not embed executable motion/spindle/tool/coolant blocks.'
}

$engineeringText = Get-Content -LiteralPath $engineeringReview -Raw
foreach ($required in @(
    'Written response and decision record',
    'External HFO/vendor approval is explicitly',
    'Supported post-M109 fresh-read pattern',
    'Protected persistent evidence counter supported',
    '`PERSISTENT_COUNTER`',
    '`EXPLICIT_EPOCH`',
    'Only one choice may be selected')) {
    if ($engineeringText.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "Engineering review record is missing the required decision marker: $required"
    }
}
if ($engineeringText.IndexOf('scripts/new-haas-verification-v6-bench-pack.ps1',
        [StringComparison]::Ordinal) -lt 0) {
    throw 'Engineering review record does not identify the separate v6 candidate source.'
}

Write-Host 'CNC commissioning-checklist gate tests passed.'
