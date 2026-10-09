[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [string]$OutputPath = 'artifacts/local-ai/candidate-evaluation.json'
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifest = (Resolve-Path -LiteralPath $ManifestPath).Path
$destination = if ([IO.Path]::IsPathRooted($OutputPath)) { [IO.Path]::GetFullPath($OutputPath) } else { Join-Path $repositoryRoot $OutputPath }
$priorManifest = $env:MEIMAD_AI_EVALUATION_MANIFEST
$priorOutput = $env:MEIMAD_AI_EVALUATION_OUTPUT
try {
    $env:MEIMAD_AI_EVALUATION_MANIFEST = $manifest
    $env:MEIMAD_AI_EVALUATION_OUTPUT = $destination
    & dotnet test (Join-Path $repositoryRoot 'tests/Meimad.Planner.Server.Tests') -c Release --filter 'FullyQualifiedName~LocalAiEvaluationTests' --artifacts-path (Join-Path $repositoryRoot 'artifacts/ai-evaluation-build')
    if ($LASTEXITCODE -ne 0) { throw 'Candidate evaluation failed. Inspect the report and test output; inference must remain disabled.' }
    Write-Output "Synthetic adapter evaluation saved to $destination. Human usefulness and deployment acceptance remain separate."
}
finally {
    $env:MEIMAD_AI_EVALUATION_MANIFEST = $priorManifest
    $env:MEIMAD_AI_EVALUATION_OUTPUT = $priorOutput
}
