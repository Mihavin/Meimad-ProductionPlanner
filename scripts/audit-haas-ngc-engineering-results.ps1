[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ResultsPath,
    [switch] $RequirePass
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$resolved = (Resolve-Path -LiteralPath $ResultsPath).Path
$text = Get-Content -LiteralPath $resolved -Raw
$expectedTests = @(
    'Direct M109 fresh timer read',
    'Separate G65 finalizer fresh timer read',
    'Reset at M109 cannot return',
    'E-stop at M109 cannot return',
    'Single Block executes finalizer exactly once',
    'Block Delete ON cannot skip protection',
    'Mode-change behavior at M109',
    'Counter one-time initialization',
    'Counter consecutive increments',
    'Counter retained after Reset',
    'Counter retained after E-stop',
    'Counter retained after controller reboot',
    'First post-reboot increment is exact next value')

$rows = @{}
foreach ($line in ($text -split "`r?`n")) {
    if ($line -match '^\|\s*(?<test>[^|]+?)\s*\|\s*(?<result>PASS|FAIL|NOT_TESTED)\s*\|\s*(?<observed>[^|]*)\|\s*(?<evidence>[^|]*)\|\s*$') {
        $name = $Matches.test.Trim()
        if ($rows.ContainsKey($name)) { throw "Duplicate engineering-test row: $name" }
        $rows[$name] = [pscustomobject]@{
            result = $Matches.result
            observed = $Matches.observed.Trim()
            evidence = $Matches.evidence.Trim()
        }
    }
}
foreach ($name in $expectedTests) {
    if (-not $rows.ContainsKey($name)) { throw "Missing engineering-test row: $name" }
}
if ($rows.Count -ne $expectedTests.Count) {
    throw "Expected exactly $($expectedTests.Count) engineering-test rows; found $($rows.Count)."
}

$missingEvidence = @()
foreach ($name in $expectedTests) {
    $row = $rows[$name]
    if ($row.result -ne 'NOT_TESTED' -and
        ([string]::IsNullOrWhiteSpace($row.observed) -or [string]::IsNullOrWhiteSpace($row.evidence))) {
        $missingEvidence += $name
    }
}

$requiredHeaderPatterns = [ordered]@{
    'Controller / version' = '(?m)^- Controller / version:[ \t]*\S[^\r\n]*$'
    'Controller serial' = '(?m)^- Controller serial:[ \t]*\S[^\r\n]*$'
    'Test date / work order' = '(?m)^- Test date / work order:[ \t]*\S[^\r\n]*$'
    'Observer' = '(?m)^- Observer:[ \t]*\S[^\r\n]*$'
    'O-numbers confirmed free' = '(?m)^- O-numbers confirmed free:[ \t]*\S[^\r\n]*$'
    'Response-variable collision approval' = '(?m)^- Response variable / collision approval:[ \t]*#[0-9]+[ \t]*/[ \t]*\S[^\r\n]*$'
    'Persistent-counter collision approval' = '(?m)^- Persistent counter / collision approval:[ \t]*#[0-9]+[ \t]*/[ \t]*\S[^\r\n]*$'
    'Pack manifest SHA-256' = '(?m)^- Pack manifest SHA-256:[ \t]*[0-9A-Fa-f]{64}[ \t]*$'
}
$missingHeaders = @()
foreach ($entry in $requiredHeaderPatterns.GetEnumerator()) {
    if ($text -notmatch $entry.Value) { $missingHeaders += $entry.Key }
}

$passed = @($expectedTests | Where-Object { $rows[$_].result -eq 'PASS' }).Count
$failed = @($expectedTests | Where-Object { $rows[$_].result -eq 'FAIL' }).Count
$notTested = @($expectedTests | Where-Object { $rows[$_].result -eq 'NOT_TESTED' }).Count
$declaredReady = $text -match '(?m)^Decision:\s*READY\s*$'
$ready = $passed -eq $expectedTests.Count -and $failed -eq 0 -and $notTested -eq 0 -and
    $missingEvidence.Count -eq 0 -and $missingHeaders.Count -eq 0
$declarationConsistent = $declaredReady -eq $ready

$result = [ordered]@{
    results = $resolved
    status = if ($ready) { 'READY_FOR_V6_RETEST_DESK_REVIEW' } else { 'NOT_READY' }
    declaredReady = $declaredReady
    declarationConsistent = $declarationConsistent
    checks = [ordered]@{
        total = $expectedTests.Count
        passed = $passed
        failed = $failed
        notTested = $notTested
        missingEvidence = $missingEvidence
    }
    missingHeaders = $missingHeaders
}
$result | ConvertTo-Json -Depth 6

if ($RequirePass -and (-not $ready -or -not $declarationConsistent)) {
    throw 'Physical NGC engineering evidence is incomplete or inconsistent.'
}
