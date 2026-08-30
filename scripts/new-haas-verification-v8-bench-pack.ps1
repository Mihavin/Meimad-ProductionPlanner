[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ConfigPath,
    [string] $OutputDirectory,
    [switch] $AcknowledgeBenchOnlyCandidate,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$arguments = @{
    ConfigPath = $ConfigPath
    CandidateMacroVersion = 8
    FailureDprntDwellSeconds = 1
}
if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $arguments.OutputDirectory = $OutputDirectory
}
if ($AcknowledgeBenchOnlyCandidate) {
    $arguments.AcknowledgeBenchOnlyCandidate = $true
}
if ($Force) {
    $arguments.Force = $true
}

& (Join-Path $PSScriptRoot 'new-haas-verification-v7-bench-pack.ps1') @arguments
