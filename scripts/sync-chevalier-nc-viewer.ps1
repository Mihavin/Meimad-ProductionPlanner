[CmdletBinding()]
param(
    # The upstream "fanuc-toolpath-preview" folder of the ChevalierGcode project (with node_modules installed).
    [Parameter(Mandatory)][string]$Source
)

# Copies the parts of the Chevalier NC viewer that Meimad Planner uses into
# third_party/chevalier-nc-viewer, unmodified. See third_party/chevalier-nc-viewer/UPSTREAM.md.

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$target = Join-Path $repositoryRoot "third_party\chevalier-nc-viewer"
$source = (Resolve-Path -LiteralPath $Source).Path

$files = @(
    @{ From = "src\parser.js"; To = "src\parser.js" },
    @{ From = "src\macro.js"; To = "src\macro.js" },
    @{ From = "src\machine-parameters.js"; To = "src\machine-parameters.js" },
    @{ From = "src\haas-mill.js"; To = "src\haas-mill.js" },
    @{ From = "src\machines.js"; To = "src\machines.js" },
    @{ From = "src\program-model.js"; To = "src\program-model.js" },
    @{ From = "src\subprograms.js"; To = "src\subprograms.js" },
    @{ From = "src\tool-table.js"; To = "src\tool-table.js" },
    @{ From = "src\stroke-font.js"; To = "src\stroke-font.js" },
    @{ From = "media\preview.js"; To = "media\preview.js" },
    @{ From = "media\preview.css"; To = "media\preview.css" },
    @{ From = "media\viewer3d.js"; To = "media\viewer3d.js" },
    @{ From = "media\kinematics.js"; To = "media\kinematics.js" },
    @{ From = "media\model-transport.js"; To = "media\model-transport.js" },
    @{ From = "desktop\renderer.js"; To = "desktop\renderer.js" },
    @{ From = "desktop\desktop.css"; To = "desktop\desktop.css" },
    @{ From = "desktop\fanuc-mode.js"; To = "desktop\fanuc-mode.js" },
    @{ From = "CNC-PARA.TXT"; To = "CNC-PARA.TXT" },
    @{ From = "node_modules\codemirror\LICENSE"; To = "vendor\codemirror\LICENSE" },
    @{ From = "node_modules\codemirror\lib\codemirror.js"; To = "vendor\codemirror\lib\codemirror.js" },
    @{ From = "node_modules\codemirror\lib\codemirror.css"; To = "vendor\codemirror\lib\codemirror.css" },
    @{ From = "node_modules\codemirror\addon\dialog\dialog.js"; To = "vendor\codemirror\addon\dialog\dialog.js" },
    @{ From = "node_modules\codemirror\addon\dialog\dialog.css"; To = "vendor\codemirror\addon\dialog\dialog.css" },
    @{ From = "node_modules\codemirror\addon\search\searchcursor.js"; To = "vendor\codemirror\addon\search\searchcursor.js" },
    @{ From = "node_modules\codemirror\addon\search\search.js"; To = "vendor\codemirror\addon\search\search.js" },
    @{ From = "node_modules\three\LICENSE"; To = "vendor\three\LICENSE" },
    @{ From = "node_modules\three\build\three.module.js"; To = "vendor\three\three.module.js" },
    @{ From = "node_modules\three\build\three.core.js"; To = "vendor\three\three.core.js" }
)

foreach ($file in $files) {
    $from = Join-Path $source $file.From
    if (-not (Test-Path -LiteralPath $from)) {
        throw "Upstream file not found: $from (run npm install in the upstream folder first)."
    }
    $to = Join-Path $target $file.To
    New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force | Out-Null
    Copy-Item -LiteralPath $from -Destination $to -Force
}

foreach ($folder in @("machines", "controls")) {
    $destination = Join-Path $target $folder
    if (Test-Path -LiteralPath $destination) {
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -Path (Join-Path $source "$folder\*.json") -Destination $destination -Force
}

$package = Get-Content -LiteralPath (Join-Path $source "package.json") -Raw | ConvertFrom-Json
$commit = & git -C $source rev-parse HEAD 2>$null
Write-Host "Synchronized Chevalier NC viewer $($package.version) (commit $commit) into $target."
Write-Host "Update UPSTREAM.md and NcEngineInfo.UpstreamVersion, then run the Server and client tests."
