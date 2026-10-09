[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string]$ModelsPath
)
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.status -ne 'APPROVED' -or $manifest.runtime.executableSha256 -notmatch '^[a-fA-F0-9]{64}$') {
    throw 'Use a reviewed APPROVED manifest with the pinned executable SHA-256.'
}
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$models = (Resolve-Path -LiteralPath $ModelsPath).Path
if (-not (Test-Path -LiteralPath $models -PathType Container)) { throw 'The approved model directory is missing.' }
if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ine $manifest.runtime.executableSha256) {
    throw 'Runtime executable hash differs from the approved manifest.'
}
# The runtime stays a separate foreground child of its explicitly configured host/task.
# This script does not install a service, acquire models, modify firewall rules or enable Planner AI.
$variables = @{ OLLAMA_HOST = '127.0.0.1:11434'; OLLAMA_NO_CLOUD = '1'; OLLAMA_MODELS = $models; OLLAMA_NUM_PARALLEL = '1' }
$previous = @{}
foreach ($name in $variables.Keys) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    foreach ($name in $variables.Keys) { [Environment]::SetEnvironmentVariable($name, $variables[$name], 'Process') }
    & $executable serve
    if ($LASTEXITCODE -ne 0) { throw "Pinned runtime exited with code $LASTEXITCODE." }
}
finally { foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') } }
