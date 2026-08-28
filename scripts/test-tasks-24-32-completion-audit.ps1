$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$auditPath = Join-Path $root 'docs\tasks-24-32-completion-audit.md'
$audit = Get-Content -LiteralPath $auditPath -Raw

foreach ($task in 24..32) {
    $heading = "## Task $task -"
    if ([regex]::Matches($audit, [regex]::Escape($heading)).Count -ne 1) {
        throw "Expected exactly one Task $task heading in $auditPath."
    }
}

$anomalyTypes = @(
    'wrong_nc_program', 'active_nc_identity_unavailable',
    'stale_offset_loader', 'offset_loader_not_executed', 'offset_loader_interrupted',
    'verification_failed', 'verification_expired', 'verification_macro_version_mismatch',
    'cycle_started_before_qc_pass', 'cycle_end_without_start', 'cycle_interrupted',
    'cnc_event_sequence_gap', 'duplicate_cnc_event',
    'unknown_production_run', 'ambiguous_production_run',
    'tablet_offline', 'tablet_credential_revoked'
)
foreach ($type in $anomalyTypes) {
    if ($audit.IndexOf($type, [StringComparison]::Ordinal) -lt 0) {
        throw "Task 24 audit is missing anomaly type $type."
    }
}

$requiredRecovery = @(
    'Invalidate current verification session',
    'Revoke current Offset Loader release',
    'Generate a new Offset Loader release',
    'Reassign replacement tablet',
    'Rotate tablet credential',
    'Retry QC workflow after failure',
    'BYPASS VERIFICATION'
)
foreach ($text in $requiredRecovery) {
    if ($audit.IndexOf($text, [StringComparison]::Ordinal) -lt 0) {
        throw "Task 25 audit is missing recovery evidence: $text."
    }
}

foreach ($required in @(
    'CNC VERIFICATION MACRO UPDATE REQUIRED',
    'PHYSICAL_NOT_READY',
    '4 `PASS`',
    '1 `FAIL`',
    '9 `NOT_TESTED`',
    '5 incomplete Machine/controller identity fields',
    'both commissioning sign-offs missing',
    'Persistent CNC workflow mode variable: REMOVED',
    'Protected temporary setup verification variables: SUPPORTED',
    'new-haas-verification-v6-bench-pack.ps1',
    'distinct protected finalizer',
    'fails closed instead of wrapping'
)) {
    if ($audit.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "Completion audit is missing required statement: $required."
    }
}

$task32Files = @(
    'AGENTS.md',
    'docs\functional-spec.md',
    'docs\architecture.md',
    'docs\data-model.md',
    'docs\api-contract.md',
    'docs\implementation-plan.md',
    'docs\production-run-architecture.md',
    'docs\esp32-eink-work-tablet.md',
    'docs\haas-active-program-header.md',
    'firmware\esp32-eink-mvp\README.md'
)
foreach ($relativePath in $task32Files) {
    $path = Join-Path $root $relativePath
    $content = Get-Content -LiteralPath $path -Raw
    $normalized = (($content -replace '\*', '') -replace '\s+', ' ')
    if ($normalized.IndexOf('Persistent CNC workflow mode variable: REMOVED', [StringComparison]::Ordinal) -lt 0 -or
        $normalized.IndexOf('Protected temporary setup verification variables: SUPPORTED', [StringComparison]::Ordinal) -lt 0) {
        throw "Task 32 boundary wording is missing from $relativePath."
    }
}

Write-Host 'Tasks 24-32 completion audit checks passed.'
