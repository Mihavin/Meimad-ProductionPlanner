[CmdletBinding()]
param(
    [ValidateRange(16, 5000)][int]$Operations = 200,
    [string]$OutputPath = 'artifacts/planner-mixed-profile.json'
)
$ErrorActionPreference = 'Stop'
if ($Operations % 4 -ne 0) { throw 'Shop workload operations must be a multiple of four.' }
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$destination = if ([IO.Path]::IsPathRooted($OutputPath)) { [IO.Path]::GetFullPath($OutputPath) } else { Join-Path $repositoryRoot $OutputPath }
$priorOutput = $env:MEIMAD_MIXED_PROFILE_OUTPUT
$priorOperations = $env:MEIMAD_MIXED_PROFILE_OPERATIONS
try {
    $env:MEIMAD_MIXED_PROFILE_OUTPUT = $destination
    $env:MEIMAD_MIXED_PROFILE_OPERATIONS = [string]$Operations
    & dotnet test (Join-Path $repositoryRoot 'tests/Meimad.Planner.Server.Tests') -c Release --filter 'FullyQualifiedName~PlannerMixedLoadProfile' --artifacts-path (Join-Path $repositoryRoot 'artifacts/mixed-profile-build')
    if ($LASTEXITCODE -ne 0) { throw 'Mixed workload failed; inspect the report and test log.' }
    Write-Output "Disposable mixed workload saved to $destination. Reported targets are observations, not factory acceptance."
}
finally { $env:MEIMAD_MIXED_PROFILE_OUTPUT = $priorOutput; $env:MEIMAD_MIXED_PROFILE_OPERATIONS = $priorOperations }
