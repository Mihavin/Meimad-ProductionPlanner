[CmdletBinding()]
param(
    [string]$MsiPath,
    [string]$ChecksumPath,
    [string]$ExpectedVersion,
    [string]$BaseUri = 'http://127.0.0.1:5080',
    [string]$ServiceName = 'Meimad Planner Server',
    [string]$LogPath,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installerRoot = Split-Path -Parent $PSCommandPath
if ([string]::IsNullOrWhiteSpace($MsiPath)) {
    $MsiPath = Join-Path $installerRoot 'artifacts\Meimad-Planner-Server-Setup.msi'
}
if ([string]::IsNullOrWhiteSpace($ChecksumPath)) {
    $ChecksumPath = Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($MsiPath))) 'SHA256SUMS.txt'
}
if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    $packageSource = Join-Path $installerRoot 'server\Package.wxs'
    if (-not (Test-Path -LiteralPath $packageSource)) {
        throw 'ExpectedVersion is required when installer source is unavailable.'
    }
    $ExpectedVersion = ([xml](Get-Content -LiteralPath $packageSource -Raw)).Wix.Package.Version
}

$resolvedMsi = (Resolve-Path -LiteralPath $MsiPath).Path
$resolvedChecksums = (Resolve-Path -LiteralPath $ChecksumPath).Path
if ([IO.Path]::GetFileName($resolvedMsi) -cne 'Meimad-Planner-Server-Setup.msi') {
    throw 'MsiPath must identify Meimad-Planner-Server-Setup.msi.'
}

function Get-MsiProperty {
    param([string]$Path, [string]$Property)
    $windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
    $database = $windowsInstaller.GetType().InvokeMember(
        'OpenDatabase', 'InvokeMethod', $null, $windowsInstaller, @($Path, 0))
    $view = $database.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$Property'")
    [void]$view.Execute()
    $record = $view.Fetch()
    if ($null -eq $record) { throw "MSI property '$Property' is missing." }
    $value = $record.StringData(1) |
        Where-Object { $null -ne $_ } |
        Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace([string]$value)) {
        throw "MSI property '$Property' is empty."
    }
    return [string]$value
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Read-VerificationGate {
    param([string]$Uri)
    $machineResponse = Invoke-RestMethod -Uri "$Uri/api/v1/machines" -Method Get -TimeoutSec 10
    $machines = if ($null -ne $machineResponse.items) { @($machineResponse.items) } else { @($machineResponse) }
    $configured = 0
    $enabled = [Collections.Generic.List[string]]::new()
    foreach ($machine in $machines) {
        $machineId = [string]$machine.machineId
        if ([string]::IsNullOrWhiteSpace($machineId)) { continue }
        try {
            $settings = Invoke-RestMethod `
                -Uri "$Uri/api/v1/machines/$machineId/verification-configuration" `
                -Method Get -TimeoutSec 10
            $configured++
            if ([bool]$settings.enabled) { $enabled.Add($machineId) }
        }
        catch {
            $status = if ($null -ne $_.Exception.Response) {
                [int]$_.Exception.Response.StatusCode
            } else { 0 }
            if ($status -ne 404) { throw }
        }
    }
    return [pscustomobject]@{
        ConfiguredMachines = $configured
        EnabledMachineIds = @($enabled)
    }
}

$checksumEntries = @{}
foreach ($line in [IO.File]::ReadAllLines($resolvedChecksums)) {
    if ($line -notmatch '^(?<hash>[0-9a-fA-F]{64}) \*(?<name>[^\\/]+)$') {
        throw "Invalid SHA256SUMS line: $line"
    }
    if ($checksumEntries.ContainsKey($Matches.name)) {
        throw "Duplicate SHA256SUMS entry: $($Matches.name)"
    }
    $checksumEntries[$Matches.name] = $Matches.hash.ToUpperInvariant()
}
$msiName = [IO.Path]::GetFileName($resolvedMsi)
if (-not $checksumEntries.ContainsKey($msiName)) {
    throw "SHA256SUMS is missing $msiName."
}
$actualHash = (Get-FileHash -LiteralPath $resolvedMsi -Algorithm SHA256).Hash
if ($actualHash -cne $checksumEntries[$msiName]) {
    throw 'Server MSI SHA-256 does not match SHA256SUMS.txt.'
}
$msiVersion = Get-MsiProperty -Path $resolvedMsi -Property 'ProductVersion'
if ($msiVersion -cne $ExpectedVersion) {
    throw "Expected Server MSI version $ExpectedVersion but found $msiVersion."
}

$preflight = Read-VerificationGate -Uri $BaseUri.TrimEnd('/')
if ($preflight.EnabledMachineIds.Count -gt 0) {
    throw "Refusing Server upgrade while CNC verification is enabled for Machine(s): $($preflight.EnabledMachineIds -join ', ')."
}

if ($ValidateOnly) {
    [pscustomobject]@{
        status = 'READY_FOR_ELEVATED_INSTALL'
        msiVersion = $msiVersion
        sha256 = $actualHash
        configuredVerificationMachines = $preflight.ConfiguredMachines
        enabledVerificationMachines = $preflight.EnabledMachineIds.Count
        administratorRequired = $true
    } | ConvertTo-Json
    exit 0
}

if (-not (Test-Administrator)) {
    throw 'Administrator elevation is required. Re-run this script from an elevated PowerShell window.'
}
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path (Split-Path -Parent $resolvedMsi) 'Meimad-Planner-Server-Setup.install.log'
}
$resolvedLog = [IO.Path]::GetFullPath($LogPath)
$logParent = Split-Path -Parent $resolvedLog
if (-not [string]::IsNullOrWhiteSpace($logParent)) {
    [IO.Directory]::CreateDirectory($logParent) | Out-Null
}

$install = Start-Process -FilePath 'msiexec.exe' -ArgumentList @(
    '/i', $resolvedMsi, '/qn', '/norestart', '/l*v', $resolvedLog
) -Wait -PassThru -WindowStyle Hidden
if ($install.ExitCode -ne 0) {
    throw "Server MSI upgrade failed with exit code $($install.ExitCode). See $resolvedLog"
}

$deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
do {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.Status -eq 'Running') { break }
    Start-Sleep -Seconds 1
} while ([DateTimeOffset]::UtcNow -lt $deadline)
if ($null -eq $service -or $service.Status -ne 'Running') {
    throw "Service '$ServiceName' is not Running after the upgrade."
}
if ([string]$service.StartType -ne 'Automatic') {
    throw "Service '$ServiceName' is not Automatic after the upgrade."
}

$failureText = (& sc.exe qfailure $ServiceName 2>&1 | Out-String)
$restartActions = @([regex]::Matches(
    $failureText,
    'RESTART[^\r\n]*Delay\s*=\s*(?<delay>\d+)',
    [Text.RegularExpressions.RegexOptions]::IgnoreCase))
$incorrectRestartDelay = @($restartActions | Where-Object {
    $_.Groups['delay'].Value -ne '60000'
}).Count -gt 0
$hasUnexpectedFailureAction = $failureText -match '(?im)^\s*(REBOOT|RUN COMMAND)\b'
if ($LASTEXITCODE -ne 0 -or
    $failureText -notmatch 'RESET_PERIOD[^\r\n]*86400' -or
    $restartActions.Count -ne 2 -or
    $incorrectRestartDelay -or
    $hasUnexpectedFailureAction) {
    throw "Installed service recovery policy does not match the bounded 60s/60s/none policy.`n$failureText"
}

$postflight = Read-VerificationGate -Uri $BaseUri.TrimEnd('/')
if ($postflight.EnabledMachineIds.Count -gt 0) {
    throw "Post-upgrade safety check found enabled CNC verification for Machine(s): $($postflight.EnabledMachineIds -join ', ')."
}

[pscustomobject]@{
    status = 'INSTALLED_AND_VERIFIED'
    msiVersion = $msiVersion
    sha256 = $actualHash
    serviceStatus = [string]$service.Status
    serviceStartType = [string]$service.StartType
    recoveryPolicy = 'restart 60s; restart 60s; none; reset 1d'
    configuredVerificationMachines = $postflight.ConfiguredMachines
    enabledVerificationMachines = $postflight.EnabledMachineIds.Count
    logPath = $resolvedLog
} | ConvertTo-Json
