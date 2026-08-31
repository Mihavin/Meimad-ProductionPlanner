[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ProductionRunId,
    [string] $ServerBaseUrl = 'http://127.0.0.1:5080',
    [ValidateRange(10, 600)]
    [int] $TimeoutSeconds = 120,
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Read-Run {
    $encoded = [Uri]::EscapeDataString($ProductionRunId.Trim())
    Invoke-RestMethod -Method Get -Uri ($ServerBaseUrl.TrimEnd('/') + '/api/v1/production-runs/' + $encoded) -TimeoutSec 10
}

$before = Read-Run
if ($before.status -notin @('PLANNED', 'IN_PROGRESS')) {
    throw "Production Run must be PLANNED/READY or IN_PROGRESS before the cycle test. Current status: $($before.status)."
}
$expectedProgramStatus = if ($before.status -ceq 'PLANNED') { 'PLANNED' } else { 'ACTIVE' }
$programs = @($before.programs | Where-Object status -CEQ $expectedProgramStatus)
if ($programs.Count -ne 1) {
    throw "Expected exactly one $expectedProgramStatus Production Run Program; found $($programs.Count)."
}
$programId = [string]$programs[0].productionRunProgramId
$baselineCycles = [int64]$programs[0].completedCycleCount
$baselineOutputs = @($programs[0].outputs | ForEach-Object {
    [ordered]@{ id = [string]$_.productionRunOutputId; produced = [int64]$_.producedQuantity }
})

Write-Host "RECORDER READY - baseline cycle count: $baselineCycles"
if ($before.status -ceq 'PLANNED') {
    Write-Host 'The first valid CST will automatically start connected-CNC production.'
}
Write-Host 'Run O01992 exactly once now. It contains no motion. Do not press manual Start.'

$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$currentCycles = $baselineCycles
do {
    Start-Sleep -Seconds 2
    $after = Read-Run
    $program = @($after.programs | Where-Object productionRunProgramId -CEQ $programId)
    if ($program.Count -ne 1) { throw 'The active Production Run Program disappeared during the test.' }
    $currentCycles = [int64]$program[0].completedCycleCount
    if ($currentCycles -gt $baselineCycles) { break }
} while ([DateTimeOffset]::UtcNow -lt $deadline)

if ($currentCycles -ne $baselineCycles + 1) {
    throw "Expected exactly one completed cycle. Baseline=$baselineCycles current=$currentCycles."
}
foreach ($baseline in $baselineOutputs) {
    $output = @($program[0].outputs | Where-Object productionRunOutputId -CEQ $baseline.id)
    if ($output.Count -ne 1) { throw "Output '$($baseline.id)' disappeared during the test." }
    $quantityPerCycle = [int64]$output[0].quantityPerCycle
    $expected = $baseline.produced + $quantityPerCycle
    if ([int64]$output[0].producedQuantity -ne $expected) {
        throw "Output '$($baseline.id)' expected produced quantity $expected; observed $($output[0].producedQuantity)."
    }
}

$evidence = [ordered]@{
    result = 'PASS'
    productionRunId = $ProductionRunId
    productionRunProgramId = $programId
    baselineCompletedCycleCount = $baselineCycles
    completedCycleCount = $currentCycles
    capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    outputs = @($program[0].outputs)
}
$json = $evidence | ConvertTo-Json -Depth 8
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolved = [IO.Path]::GetFullPath($OutputPath)
    $parent = Split-Path -Parent $resolved
    if (-not [string]::IsNullOrEmpty($parent)) { [IO.Directory]::CreateDirectory($parent) | Out-Null }
    [IO.File]::WriteAllText($resolved, $json + "`r`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Evidence saved: $resolved"
}
Write-Output $json
Write-Host 'CYCLE COUNT PASS - one START/END pair completed exactly one cycle.'
