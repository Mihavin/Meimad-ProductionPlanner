# Creates the "Cimatron to Meimad" shortcuts for the exporter beside this script: one on the desktop and one in the
# Start menu (Meimad folder). -Remove deletes them again. Run it as the CAM user, from a normal window.
param([switch]$Remove)

$exe = Join-Path $PSScriptRoot 'Meimad.CimatronExporter.exe'
if (-not (Test-Path $exe)) {
    Write-Error "Meimad.CimatronExporter.exe is not beside this script ($PSScriptRoot)."
    exit 1
}

$shell = New-Object -ComObject WScript.Shell
$folders = @(
    [Environment]::GetFolderPath('Desktop'),
    (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\Meimad')
)
foreach ($folder in $folders) {
    $link = Join-Path $folder 'Cimatron to Meimad.lnk'
    if ($Remove) {
        if (Test-Path $link) { Remove-Item $link; Write-Output "Removed: $link" }
        continue
    }
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $shortcut = $shell.CreateShortcut($link)
    $shortcut.TargetPath = $exe
    $shortcut.Arguments = '--ui'
    $shortcut.WorkingDirectory = $PSScriptRoot
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Description = 'Export the active Cimatron NC toolpath to Meimad'
    $shortcut.Save()
    Write-Output "Shortcut: $link"
}
