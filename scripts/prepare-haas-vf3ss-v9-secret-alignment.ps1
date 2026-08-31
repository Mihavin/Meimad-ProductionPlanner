[CmdletBinding()]
param(
    [string] $OutputPath,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Windows PowerShell 5.1 does not reliably populate $PSScriptRoot while it is
# evaluating default parameter expressions. Resolve the default after binding,
# when the automatic variable is available.
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $OutputPath = Join-Path $repositoryRoot '.diagnostics\vf3ss-verification-v9.local.json'
}

$random = [Security.Cryptography.RandomNumberGenerator]::Create()
$bytes = New-Object byte[] 32
try {
    $random.GetBytes($bytes)
    $plainSecret = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $secureSecret = ConvertTo-SecureString $plainSecret -AsPlainText -Force

    $arguments = @{
        MachineId = '5b332822830545d19950a43743779237'
        MachineLabel = 'HAAS-VF3SS'
        OutputPath = $OutputPath
        VerificationSecret = $secureSecret
        MacroVersion = 9
        SampleNcIdentity = 742915
        SampleOffsetReleaseToken = 782703
    }
    if ($Force) { $arguments.Force = $true }

    & (Join-Path $PSScriptRoot 'new-haas-verification-local-config.ps1') @arguments
    Set-Clipboard -Value $plainSecret

    Write-Host ''
    Write-Host 'NEW V9 SECRET COPIED TO THE WINDOWS CLIPBOARD.'
    Write-Host 'Paste it once into Machine 10 Server verification secret.'
    Write-Host 'Set expected macro version to 9, keep verification disabled, and Save.'
    Write-Host 'Then immediately clear the clipboard with: Set-Clipboard -Value ""'
    Write-Warning 'Do not paste the secret into chat, a command line, a document, or an NC file.'
}
finally {
    [Array]::Clear($bytes, 0, $bytes.Length)
    $random.Dispose()
}
