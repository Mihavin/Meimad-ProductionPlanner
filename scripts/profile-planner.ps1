[CmdletBinding()]
param(
    [int[]]$Operations = @(200, 1000, 5000),
    [ValidateRange(2, 100)][int]$Samples = 5,
    [ValidateSet('all', 'queue', 'board', 'timeline')][string]$Area = 'all',
    [ValidateSet('independent', 'shop')][string]$Workload = 'independent',
    [ValidateRange(0, 100000)][int]$HistoryEvents = 0,
    [string]$OutputPath = 'artifacts/planner-profile.json'
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (@($Operations | Where-Object { $_ -lt 16 -or $_ -gt 5000 }).Count -gt 0) {
    throw 'Operations must be between 16 and 5000.'
}
if ($Workload -eq 'shop' -and @($Operations | Where-Object { $_ % 4 -ne 0 }).Count -gt 0) {
    throw 'Shop workload operation counts must be multiples of four.'
}
$destination = if ([IO.Path]::IsPathRooted($OutputPath)) { [IO.Path]::GetFullPath($OutputPath) } else { [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputPath)) }
New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
$previous = @{}
foreach ($key in @('MEIMAD_PROFILE_OUTPUT', 'MEIMAD_PROFILE_SIZES', 'MEIMAD_PROFILE_SAMPLES', 'MEIMAD_PROFILE_ROUTE', 'MEIMAD_PROFILE_WORKLOAD', 'MEIMAD_PROFILE_HISTORY')) {
    $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}
try {
    $env:MEIMAD_PROFILE_OUTPUT = $destination
    $env:MEIMAD_PROFILE_SIZES = $Operations -join ','
    $env:MEIMAD_PROFILE_SAMPLES = [string]$Samples
    $env:MEIMAD_PROFILE_WORKLOAD = $Workload
    $env:MEIMAD_PROFILE_HISTORY = [string]$HistoryEvents
    $env:MEIMAD_PROFILE_ROUTE = switch ($Area) { 'queue' { 'preparation-queues' } 'board' { 'planning-board' } 'timeline' { 'timeline' } default { '' } }
    & dotnet test (Join-Path $repositoryRoot 'tests/Meimad.Planner.Server.Tests') -c Release --filter 'FullyQualifiedName~PlannerPerformanceProfile' --artifacts-path (Join-Path $repositoryRoot 'artifacts/performance-build')
    if ($LASTEXITCODE -ne 0) { throw 'Performance profile failed; inspect any completed dimensions in the JSON output.' }
    Write-Output "Profile saved to $destination"
}
finally {
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
}
