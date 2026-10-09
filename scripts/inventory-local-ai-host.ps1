[CmdletBinding()]
param([string]$OutputPath = 'artifacts/local-ai/host-inventory.json')
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$destination = if ([IO.Path]::IsPathRooted($OutputPath)) { [IO.Path]::GetFullPath($OutputPath) } else { Join-Path $repositoryRoot $OutputPath }
$machine = Get-CimInstance Win32_ComputerSystem
$os = Get-CimInstance Win32_OperatingSystem
$report = [ordered]@{
    capturedAt = [DateTimeOffset]::UtcNow.ToString('O')
    status = 'INVENTORY_ONLY_NOT_DEPLOYMENT_ACCEPTANCE'
    hostName = $env:COMPUTERNAME
    operatingSystem = $os.Caption
    operatingSystemVersion = $os.Version
    ramBytes = [long]$machine.TotalPhysicalMemory
    processors = @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
    videoAdapters = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion)
    gpuMemory = 'Not established by WMI; record vendor-reported dedicated VRAM on the selected host.'
    localDisks = @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | Select-Object DeviceID, Size, FreeSpace)
    serviceIdentity = $null
    expectedConcurrentUse = $null
    targetHostApproved = $false
}
New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $destination -Encoding UTF8
Write-Output "Read-only host inventory saved to $destination. No runtime, model or service was installed."
