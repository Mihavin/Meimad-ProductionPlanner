[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$installerRoot = Split-Path -Parent $PSCommandPath
$repositoryRoot = Split-Path -Parent $installerRoot
$resolvedInstallerRoot = [System.IO.Path]::GetFullPath($installerRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $installerRoot "artifacts"
}

$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$stageRoot = Join-Path $installerRoot "obj\publish"
$clientStage = Join-Path $stageRoot "client"
$clientHarvestStage = Join-Path $stageRoot "client-harvest"
$serverStage = Join-Path $stageRoot "server"
$serverHarvestStage = Join-Path $stageRoot "server-harvest"

function Reset-InstallerStage {
    param([Parameter(Mandatory)][string]$Path)

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($resolvedInstallerRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove staging path outside the installer directory: $resolvedPath"
    }

    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $resolvedPath -Force | Out-Null
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-StableWixId {
    param(
        [Parameter(Mandatory)][string]$Prefix,
        [Parameter(Mandatory)][string]$Value
    )

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())
        $hash = [System.BitConverter]::ToString($sha256.ComputeHash($bytes)).Replace("-", "")
        return "${Prefix}_$($hash.Substring(0, 24))"
    }
    finally {
        $sha256.Dispose()
    }
}

function New-WixPayloadAuthoring {
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string]$OutputFile,
        [Parameter(Mandatory)][string]$ComponentGroupId
    )

    $sourceRoot = [System.IO.Path]::GetFullPath($SourceDirectory).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $sourcePrefix = $sourceRoot + [System.IO.Path]::DirectorySeparatorChar
    $wixNamespace = "http://wixtoolset.org/schemas/v4/wxs"
    $document = [System.Xml.XmlDocument]::new()
    $wix = $document.CreateElement("Wix", $wixNamespace)
    $document.AppendChild($wix) | Out-Null

    $directoryFragment = $document.CreateElement("Fragment", $wixNamespace)
    $wix.AppendChild($directoryFragment) | Out-Null
    $directoryReference = $document.CreateElement("DirectoryRef", $wixNamespace)
    $directoryReference.SetAttribute("Id", "INSTALLFOLDER")
    $directoryFragment.AppendChild($directoryReference) | Out-Null

    $groupFragment = $document.CreateElement("Fragment", $wixNamespace)
    $wix.AppendChild($groupFragment) | Out-Null
    $componentGroup = $document.CreateElement("ComponentGroup", $wixNamespace)
    $componentGroup.SetAttribute("Id", $ComponentGroupId)
    $groupFragment.AppendChild($componentGroup) | Out-Null

    $directoryNodes = @{ "" = $directoryReference }
    $files = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Sort-Object FullName)
    $leafDirectories = @($files | ForEach-Object {
        if ($_.DirectoryName.Length -gt $sourceRoot.Length) {
            $_.DirectoryName.Substring($sourcePrefix.Length)
        }
    } | Sort-Object -Unique)
    $directorySet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($leafDirectory in $leafDirectories) {
        $candidate = $leafDirectory
        while (-not [string]::IsNullOrWhiteSpace($candidate) -and $candidate -ne ".") {
            $directorySet.Add($candidate) | Out-Null
            $candidate = [System.IO.Path]::GetDirectoryName($candidate)
        }
    }
    $relativeDirectories = @($directorySet | Sort-Object { ($_ -split '[\\/]').Count }, { $_ })

    foreach ($relativeDirectory in $relativeDirectories) {
        $parentRelative = [System.IO.Path]::GetDirectoryName($relativeDirectory)
        if ([string]::IsNullOrWhiteSpace($parentRelative) -or $parentRelative -eq ".") {
            $parentRelative = ""
        }

        $directory = $document.CreateElement("Directory", $wixNamespace)
        $directory.SetAttribute("Id", (Get-StableWixId -Prefix "D" -Value $relativeDirectory))
        $directory.SetAttribute("Name", ([System.IO.Path]::GetFileName($relativeDirectory)))
        $directoryNodes[$parentRelative].AppendChild($directory) | Out-Null
        $directoryNodes[$relativeDirectory] = $directory
    }

    foreach ($file in $files) {
        $relativePath = $file.FullName.Substring($sourcePrefix.Length)
        $relativeDirectory = [System.IO.Path]::GetDirectoryName($relativePath)
        if ([string]::IsNullOrWhiteSpace($relativeDirectory) -or $relativeDirectory -eq ".") {
            $relativeDirectory = ""
        }

        $componentId = Get-StableWixId -Prefix "C" -Value $relativePath
        $component = $document.CreateElement("Component", $wixNamespace)
        $component.SetAttribute("Id", $componentId)
        $component.SetAttribute("Guid", "*")

        $fileElement = $document.CreateElement("File", $wixNamespace)
        $fileElement.SetAttribute("Id", (Get-StableWixId -Prefix "F" -Value $relativePath))
        $fileElement.SetAttribute("Source", $file.FullName)
        $fileElement.SetAttribute("KeyPath", "yes")
        $component.AppendChild($fileElement) | Out-Null
        $directoryNodes[$relativeDirectory].AppendChild($component) | Out-Null

        $componentReference = $document.CreateElement("ComponentRef", $wixNamespace)
        $componentReference.SetAttribute("Id", $componentId)
        $componentGroup.AppendChild($componentReference) | Out-Null
    }

    $outputParent = Split-Path -Parent $OutputFile
    New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $writer = [System.Xml.XmlWriter]::Create($OutputFile, $settings)
    try {
        $document.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
}

Reset-InstallerStage -Path $clientStage
Reset-InstallerStage -Path $clientHarvestStage
Reset-InstallerStage -Path $serverStage
Reset-InstallerStage -Path $serverHarvestStage
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

$clientProject = Join-Path $repositoryRoot "client-windows\Meimad.Planner.Client.Windows\Meimad.Planner.Client.Windows.csproj"
$serverProject = Join-Path $repositoryRoot "server\Meimad.Planner.Server\Meimad.Planner.Server.csproj"
$clientInstaller = Join-Path $installerRoot "client\Meimad.Planner.Client.Windows.Installer.wixproj"
$serverInstaller = Join-Path $installerRoot "server\Meimad.Planner.Server.Installer.wixproj"
$clientPackage = Join-Path $installerRoot "client\Package.wxs"
$serverPackage = Join-Path $installerRoot "server\Package.wxs"
$clientPayloadAuthoring = Join-Path $installerRoot "obj\generated\ClientPayload.wxs"
$serverPayloadAuthoring = Join-Path $installerRoot "obj\generated\ServerPayload.wxs"

$clientApplicationVersion = ([xml](Get-Content -LiteralPath $clientProject -Raw)).Project.PropertyGroup.Version
$serverApplicationVersion = ([xml](Get-Content -LiteralPath $serverProject -Raw)).Project.PropertyGroup.Version
$clientPackageVersion = ([xml](Get-Content -LiteralPath $clientPackage -Raw)).Wix.Package.Version
$serverPackageVersion = ([xml](Get-Content -LiteralPath $serverPackage -Raw)).Wix.Package.Version
$versions = @($clientApplicationVersion, $serverApplicationVersion, $clientPackageVersion, $serverPackageVersion)
if (@($versions | Sort-Object -Unique).Count -ne 1) {
    throw "Client, Server, and MSI versions must match. Found: $($versions -join ', ')."
}

Write-Host "Building Meimad Production Planner version $clientPackageVersion..."

Write-Host "Publishing self-contained Windows client..."
Invoke-DotNet -Arguments @(
    "publish", $clientProject,
    "-c", $Configuration,
    "-r", $RuntimeIdentifier,
    "--self-contained", "true",
    "-o", $clientStage
)

Write-Host "Publishing self-contained Windows Server..."
Invoke-DotNet -Arguments @(
    "publish", $serverProject,
    "-c", $Configuration,
    "-r", $RuntimeIdentifier,
    "--self-contained", "true",
    "-o", $serverStage
)

# The client executable is authored explicitly so it can own an advertised
# all-users Start Menu shortcut without mixing per-user and per-machine key paths.
Copy-Item -Path (Join-Path $clientStage "*") -Destination $clientHarvestStage -Recurse -Force
$harvestedClientExecutable = Join-Path $clientHarvestStage "Meimad.Planner.Client.Windows.exe"
if (Test-Path -LiteralPath $harvestedClientExecutable) {
    Remove-Item -LiteralPath $harvestedClientExecutable -Force
}

# The Server executable is authored explicitly as the Windows Service key path.
# Harvest every other published file from a separate, installer-owned staging tree.
Copy-Item -Path (Join-Path $serverStage "*") -Destination $serverHarvestStage -Recurse -Force
$harvestedServerExecutable = Join-Path $serverHarvestStage "Meimad.Planner.Server.exe"
if (Test-Path -LiteralPath $harvestedServerExecutable) {
    Remove-Item -LiteralPath $harvestedServerExecutable -Force
}
$harvestedServerSettings = Join-Path $serverHarvestStage "appsettings.json"
if (Test-Path -LiteralPath $harvestedServerSettings) {
    # appsettings.json is authored explicitly as a permanent, never-overwrite
    # component so a Server upgrade cannot discard site configuration.
    Remove-Item -LiteralPath $harvestedServerSettings -Force
}

New-WixPayloadAuthoring -SourceDirectory $clientHarvestStage -OutputFile $clientPayloadAuthoring -ComponentGroupId "ClientPayloadComponents"
New-WixPayloadAuthoring -SourceDirectory $serverHarvestStage -OutputFile $serverPayloadAuthoring -ComponentGroupId "ServerPayloadComponents"

Write-Host "Building client MSI..."
Invoke-DotNet -Arguments @(
    "build", $clientInstaller,
    "-c", $Configuration,
    "-p:GeneratedPayloadFile=$clientPayloadAuthoring",
    "-p:ClientExecutableDir=$clientStage",
    "-p:InstallerOutputPath=$outputPath"
)

Write-Host "Building Server MSI..."
Invoke-DotNet -Arguments @(
    "build", $serverInstaller,
    "-c", $Configuration,
    "-p:GeneratedPayloadFile=$serverPayloadAuthoring",
    "-p:ServerExecutableDir=$serverStage",
    "-p:InstallerOutputPath=$outputPath"
)

$packages = Get-ChildItem -LiteralPath $outputPath -Filter "*.msi" | Sort-Object Name
if ($packages.Count -ne 2) {
    throw "Expected exactly two MSI packages in $outputPath, found $($packages.Count)."
}

$checksumPath = Join-Path $outputPath "SHA256SUMS.txt"
$checksumLines = @($packages | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$($_.Name)"
})
[System.IO.File]::WriteAllLines(
    $checksumPath,
    $checksumLines,
    [System.Text.UTF8Encoding]::new($false))

Write-Host "Installers created:"
$packages | ForEach-Object { Write-Host "  $($_.FullName)" }
Write-Host "Checksums: $checksumPath"
