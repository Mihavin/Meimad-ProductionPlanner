[CmdletBinding()]
param(
    [string] $ChecklistPath,
    [switch] $RequireReady
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($ChecklistPath)) {
    $ChecklistPath = Join-Path $PSScriptRoot '..\docs\cnc-commissioning-checklist.md'
}

$resolved = (Resolve-Path -LiteralPath $ChecklistPath).Path
$lines = [IO.File]::ReadAllLines($resolved)
$text = [IO.File]::ReadAllText($resolved)
$rowPattern = '^\|\s*(?<number>\d{1,2})\s*\|(?<criterion>.*?)\|\s*(?<result>PASS|FAIL|NOT_TESTED)\s*\|(?<observation>.*?)\|(?<evidence>.*?)\|\s*$'
$rows = [Collections.Generic.List[object]]::new()
foreach ($line in $lines) {
    $match = [regex]::Match($line, $rowPattern,
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) { continue }
    $rows.Add([ordered]@{
        number = [int]$match.Groups['number'].Value
        result = $match.Groups['result'].Value
        observation = $match.Groups['observation'].Value.Trim()
        evidence = $match.Groups['evidence'].Value.Trim()
    })
}
if ($rows.Count -ne 14 -or (@($rows.number | Sort-Object) -join ',') -ne (1..14 -join ',')) {
    throw 'The commissioning record must contain exactly numbered checks 1 through 14.'
}

$placeholderPattern = '^(?:NOT RECORDED|NOT TESTED|(?:-|\p{Pd})+)$'
$machineFields = [ordered]@{}
$inMachineTable = $false
foreach ($line in $lines) {
    if ($line -eq '## Machine and controller identity') { $inMachineTable = $true; continue }
    if ($inMachineTable -and $line.StartsWith('## ')) { break }
    if (-not $inMachineTable) { continue }
    $match = [regex]::Match($line, '^\|\s*(?<field>[^|]+?)\s*\|\s*(?<value>[^|]+?)\s*\|\s*$')
    if (-not $match.Success -or $match.Groups['field'].Value.Trim() -eq 'Field' -or
        $match.Groups['field'].Value.Trim().StartsWith('---')) { continue }
    $field = $match.Groups['field'].Value.Trim()
    $value = $match.Groups['value'].Value.Trim()
    $machineFields[$field] = [ordered]@{
        value = $value
        recorded = -not [regex]::IsMatch($value,
            '(?:\bNOT RECORDED\b|\bNOT TESTED\b|^(?:-|\p{Pd})+$)',
            [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
}

$signoffs = [Collections.Generic.List[object]]::new()
$inSignoffTable = $false
foreach ($line in $lines) {
    if ($line -eq '## Decision and sign-off') { $inSignoffTable = $true; continue }
    if (-not $inSignoffTable) { continue }
    $match = [regex]::Match($line,
        '^\|\s*(?<role>[^|]+?)\s*\|\s*(?<name>[^|]+?)\s*\|\s*(?<date>[^|]+?)\s*\|\s*(?<decision>[^|]+?)\s*\|\s*$')
    if (-not $match.Success -or $match.Groups['role'].Value.Trim() -eq 'Role' -or
        $match.Groups['role'].Value.Trim().StartsWith('---')) { continue }
    $name = $match.Groups['name'].Value.Trim()
    $date = $match.Groups['date'].Value.Trim()
    $decision = $match.Groups['decision'].Value.Trim()
    $signoffPlaceholder = '^(?:NOT RECORDED|NOT TESTED|N/?A|(?:-|\p{Pd})+)$'
    $signed = -not [regex]::IsMatch($name, $signoffPlaceholder,
        [Text.RegularExpressions.RegexOptions]::IgnoreCase) -and
        -not [regex]::IsMatch($date, $signoffPlaceholder,
            [Text.RegularExpressions.RegexOptions]::IgnoreCase) -and
        -not [regex]::IsMatch($decision, $signoffPlaceholder,
            [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $signoffs.Add([ordered]@{
        role = $match.Groups['role'].Value.Trim()
        name = $name
        date = $date
        decision = $decision
        signed = $signed
    })
}
if ($signoffs.Count -ne 2) { throw 'The commissioning record must contain exactly two sign-off rows.' }

$passes = @($rows | Where-Object result -eq 'PASS').Count
$failures = @($rows | Where-Object result -eq 'FAIL').Count
$notTested = @($rows | Where-Object result -eq 'NOT_TESTED').Count
$missingEvidence = @($rows | Where-Object {
    [regex]::IsMatch($_.evidence, $placeholderPattern,
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
} | ForEach-Object number)
$unrecordedMachineFields = @($machineFields.GetEnumerator() | Where-Object {
    -not $_.Value.recorded
} | ForEach-Object Key)
$unsignedRoles = @($signoffs | Where-Object { -not $_.signed } | ForEach-Object role)
$declaredReady = [regex]::IsMatch($text,
    'Current commissioning decision:\s*\*\*READY\b',
    [Text.RegularExpressions.RegexOptions]::IgnoreCase)
$computedReady = $passes -eq 14 -and $failures -eq 0 -and $notTested -eq 0 -and
    $missingEvidence.Count -eq 0 -and $unrecordedMachineFields.Count -eq 0 -and
    $unsignedRoles.Count -eq 0
$declarationConsistent = $declaredReady -eq $computedReady

$result = [ordered]@{
    checklist = $resolved
    status = if ($computedReady) { 'READY' } else { 'NOT_READY' }
    declaredReady = $declaredReady
    declarationConsistent = $declarationConsistent
    checks = [ordered]@{
        total = $rows.Count
        passed = $passes
        failed = $failures
        notTested = $notTested
        missingEvidence = $missingEvidence
    }
    unrecordedMachineFields = $unrecordedMachineFields
    unsignedRoles = $unsignedRoles
}
$result | ConvertTo-Json -Depth 6

if (-not $declarationConsistent) {
    throw 'The declared commissioning decision does not match the checklist evidence.'
}
if ($RequireReady -and -not $computedReady) {
    throw 'CNC setup verification is not physically commissioned.'
}
