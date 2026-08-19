[CmdletBinding()]
param(
    [string]$ExpectedVersion
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$installerRoot = Split-Path -Parent $PSCommandPath
$packageSource = Join-Path $installerRoot "client\Package.wxs"
if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    $ExpectedVersion = ([xml](Get-Content -LiteralPath $packageSource -Raw)).Wix.Package.Version
}
$installerPrefix = [System.IO.Path]::GetFullPath($installerRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$extractRoot = [System.IO.Path]::GetFullPath((Join-Path $installerRoot "obj\extract"))
if (-not $extractRoot.StartsWith($installerPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe extraction root: $extractRoot"
}

if (Test-Path -LiteralPath $extractRoot) {
    Remove-Item -LiteralPath $extractRoot -Recurse -Force
}

$clientTarget = Join-Path $extractRoot "client"
$serverTarget = Join-Path $extractRoot "server"
New-Item -ItemType Directory -Path $clientTarget, $serverTarget -Force | Out-Null

function Get-MsiProperty {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Property
    )

    $windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
    $database = $windowsInstaller.GetType().InvokeMember(
        "OpenDatabase",
        "InvokeMethod",
        $null,
        $windowsInstaller,
        @($Path, 0))
    $view = $database.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$Property'")
    $view.Execute()
    $record = $view.Fetch()
    if ($null -eq $record) {
        throw "MSI property '$Property' is missing from $Path."
    }

    return $record.StringData(1)
}

try {
    $clientMsi = (Resolve-Path (Join-Path $installerRoot "artifacts\Meimad-Planner-Client-Setup.msi")).Path
    $serverMsi = (Resolve-Path (Join-Path $installerRoot "artifacts\Meimad-Planner-Server-Setup.msi")).Path

    $clientVersion = Get-MsiProperty -Path $clientMsi -Property "ProductVersion"
    $serverVersion = Get-MsiProperty -Path $serverMsi -Property "ProductVersion"
    if ($clientVersion -ne $ExpectedVersion -or $serverVersion -ne $ExpectedVersion) {
        throw "Expected MSI version $ExpectedVersion; found client $clientVersion and Server $serverVersion."
    }

    $clientExtraction = Start-Process msiexec.exe -ArgumentList @(
        "/a", "`"$clientMsi`"", "/qn", "TARGETDIR=`"$clientTarget`""
    ) -Wait -PassThru -WindowStyle Hidden
    if ($clientExtraction.ExitCode -ne 0) {
        throw "Client administrative extraction failed with exit code $($clientExtraction.ExitCode)."
    }

    $serverExtraction = Start-Process msiexec.exe -ArgumentList @(
        "/a", "`"$serverMsi`"", "/qn", "TARGETDIR=`"$serverTarget`""
    ) -Wait -PassThru -WindowStyle Hidden
    if ($serverExtraction.ExitCode -ne 0) {
        throw "Server administrative extraction failed with exit code $($serverExtraction.ExitCode)."
    }

    $clientExecutables = @(Get-ChildItem -LiteralPath $clientTarget -Recurse -Filter "Meimad.Planner.Client.Windows.exe")
    $nestedOcctKernels = @(Get-ChildItem -LiteralPath $clientTarget -Recurse -Filter "TKernel.dll" |
        Where-Object { $_.FullName -match "runtimes\\win-x64\\native" })
    $serverExecutables = @(Get-ChildItem -LiteralPath $serverTarget -Recurse -Filter "Meimad.Planner.Server.exe")

    if ($clientExecutables.Count -ne 1) {
        throw "Expected one packaged client executable; found $($clientExecutables.Count)."
    }
    if ($nestedOcctKernels.Count -ne 1) {
        throw "Expected one nested runtimes\win-x64\native\TKernel.dll; found $($nestedOcctKernels.Count)."
    }
    if ($serverExecutables.Count -ne 1) {
        throw "Expected one packaged Server executable; found $($serverExecutables.Count)."
    }

    [pscustomobject]@{
        ProductVersion = $ExpectedVersion
        ClientExtractedFiles = @(Get-ChildItem -LiteralPath $clientTarget -Recurse -File).Count
        NestedOcctKernel = $nestedOcctKernels[0].FullName.Substring($clientTarget.Length).TrimStart("\")
        ServerExtractedFiles = @(Get-ChildItem -LiteralPath $serverTarget -Recurse -File).Count
    } | Format-List
}
finally {
    if (Test-Path -LiteralPath $extractRoot) {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force
    }
}
