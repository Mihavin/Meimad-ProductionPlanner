[CmdletBinding()]
param(
    [string]$ExpectedVersion
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$installerRoot = Split-Path -Parent $PSCommandPath
$packageSource = Join-Path $installerRoot "client\Package.wxs"
$serverPackageSource = Join-Path $installerRoot "server\Package.wxs"
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
    [void]$view.Execute()
    $record = $view.Fetch()
    if ($null -eq $record) {
        throw "MSI property '$Property' is missing from $Path."
    }

    $value = $record.StringData(1) |
        Where-Object { $null -ne $_ } |
        Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace([string]$value)) {
        throw "MSI property '$Property' is empty in $Path."
    }
    return [string]$value
}

function Test-MsiTable {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Table
    )

    $windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
    $database = $windowsInstaller.GetType().InvokeMember(
        "OpenDatabase", "InvokeMethod", $null, $windowsInstaller, @($Path, 0))
    $view = $database.OpenView("SELECT ``Name`` FROM ``_Tables`` WHERE ``Name`` = '$Table'")
    [void]$view.Execute()
    return $null -ne $view.Fetch()
}

try {
    $clientMsi = (Resolve-Path (Join-Path $installerRoot "artifacts\Meimad-Planner-Client-Setup.msi")).Path
    $serverMsi = (Resolve-Path (Join-Path $installerRoot "artifacts\Meimad-Planner-Server-Setup.msi")).Path
    $checksumPath = (Resolve-Path (Join-Path $installerRoot "artifacts\SHA256SUMS.txt")).Path

    $expectedHashes = @{}
    foreach ($line in [IO.File]::ReadAllLines($checksumPath)) {
        if ($line -notmatch '^(?<hash>[0-9a-fA-F]{64}) \*(?<name>[^\\/]+)$') {
            throw "Invalid SHA256SUMS line: $line"
        }
        if ($expectedHashes.ContainsKey($Matches.name)) {
            throw "Duplicate SHA256SUMS entry: $($Matches.name)"
        }
        $expectedHashes[$Matches.name] = $Matches.hash.ToUpperInvariant()
    }
    foreach ($msi in @($clientMsi, $serverMsi)) {
        $name = [IO.Path]::GetFileName($msi)
        if (-not $expectedHashes.ContainsKey($name)) {
            throw "SHA256SUMS is missing $name."
        }
        $actual = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash
        if ($actual -cne $expectedHashes[$name]) {
            throw "SHA-256 mismatch for $name."
        }
    }
    if ($expectedHashes.Count -ne 2) {
        throw "Expected exactly two SHA256SUMS entries; found $($expectedHashes.Count)."
    }

    $clientVersion = Get-MsiProperty -Path $clientMsi -Property "ProductVersion"
    $serverVersion = Get-MsiProperty -Path $serverMsi -Property "ProductVersion"
    if ($clientVersion -ne $ExpectedVersion -or $serverVersion -ne $ExpectedVersion) {
        throw "Expected MSI version $ExpectedVersion; found client $clientVersion and Server $serverVersion."
    }

    $serverAuthoring = Get-Content -LiteralPath $serverPackageSource -Raw
    foreach ($marker in @(
        'FirstFailureActionType="restart"',
        'SecondFailureActionType="restart"',
        'ThirdFailureActionType="none"',
        'ResetPeriodInDays="1"',
        'RestartServiceDelayInSeconds="60"')) {
        if ($serverAuthoring.IndexOf($marker, [StringComparison]::Ordinal) -lt 0) {
            throw "Server MSI authoring is missing bounded service-recovery marker: $marker"
        }
    }
    if (-not (Test-MsiTable -Path $serverMsi -Table 'Wix4ServiceConfig')) {
        throw 'Server MSI does not contain the compiled WiX utility service-configuration table.'
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
    $clientAssemblies = @(Get-ChildItem -LiteralPath $clientTarget -Recurse -Filter "Meimad.Planner.Client.Windows.dll")
    $nestedOcctKernels = @(Get-ChildItem -LiteralPath $clientTarget -Recurse -Filter "TKernel.dll" |
        Where-Object { $_.FullName -match "runtimes\\win-x64\\native" })
    $serverExecutables = @(Get-ChildItem -LiteralPath $serverTarget -Recurse -Filter "Meimad.Planner.Server.exe")
    $serverSimulatorHtml = @(Get-ChildItem -LiteralPath $serverTarget -Recurse -Filter "index.html" |
        Where-Object { $_.FullName -match "wwwroot\\eink-simulator\\index\.html$" })
    $serverSimulatorScript = @(Get-ChildItem -LiteralPath $serverTarget -Recurse -Filter "app.js" |
        Where-Object { $_.FullName -match "wwwroot\\eink-simulator\\app\.js$" })
    $serverSimulatorPowerPolicy = @(Get-ChildItem -LiteralPath $serverTarget -Recurse -Filter "power-policy.js" |
        Where-Object { $_.FullName -match "wwwroot\\eink-simulator\\power-policy\.js$" })
    $serverSimulatorStyles = @(Get-ChildItem -LiteralPath $serverTarget -Recurse -Filter "styles.css" |
        Where-Object { $_.FullName -match "wwwroot\\eink-simulator\\styles\.css$" })

    if ($clientExecutables.Count -ne 1) {
        throw "Expected one packaged client executable; found $($clientExecutables.Count)."
    }
    if ($clientAssemblies.Count -ne 1) {
        throw "Expected one packaged client assembly; found $($clientAssemblies.Count)."
    }
    if ($nestedOcctKernels.Count -ne 1) {
        throw "Expected one nested runtimes\win-x64\native\TKernel.dll; found $($nestedOcctKernels.Count)."
    }
    if ($serverExecutables.Count -ne 1) {
        throw "Expected one packaged Server executable; found $($serverExecutables.Count)."
    }
    if ($serverSimulatorHtml.Count -ne 1 -or
        $serverSimulatorScript.Count -ne 1 -or
        $serverSimulatorPowerPolicy.Count -ne 1 -or
        $serverSimulatorStyles.Count -ne 1) {
        throw "Expected one complete packaged E-Ink simulator; found HTML $($serverSimulatorHtml.Count), script $($serverSimulatorScript.Count), power policy $($serverSimulatorPowerPolicy.Count), styles $($serverSimulatorStyles.Count)."
    }

    # NC viewer and NC engine (third_party/chevalier-nc-viewer hosted by shared/Meimad.Planner.NcEngine).
    # The client needs the viewer page, its bridge, the vendored UI and three.js, the engine files,
    # the V8 native library and the WebView2 loader; the Server needs the engine for cycle times.
    function Assert-PayloadFiles {
        param(
            [Parameter(Mandatory)][string]$Root,
            [Parameter(Mandatory)][string]$Label,
            [Parameter(Mandatory)][string[]]$RelativeSuffixes
        )
        $files = @(Get-ChildItem -LiteralPath $Root -Recurse -File)
        foreach ($suffix in $RelativeSuffixes) {
            $pattern = '\\' + [regex]::Escape($suffix) + '$'
            # At least one: the WebView2 package ships its loader both at the root and under runtimes\.
            if (@($files | Where-Object { $_.FullName -match $pattern }).Count -lt 1) {
                throw "$Label is missing $suffix."
            }
        }
    }
    Assert-PayloadFiles -Root $clientTarget -Label "Packaged Windows Client" -RelativeSuffixes @(
        'NcViewer\desktop\meimad-viewer.html',
        'NcViewer\desktop\meimad-bridge.js',
        'NcViewer\desktop\meimad-viewer.js',
        'NcViewer\desktop\renderer.js',
        'NcViewer\media\preview.js',
        'NcViewer\media\viewer3d.js',
        'NcViewer\node_modules\codemirror\lib\codemirror.js',
        'NcViewer\node_modules\three\build\three.module.js',
        'NcViewer\node_modules\three\build\three.core.js',
        'nc-engine\meimad\bootstrap.js',
        'nc-engine\meimad\meimad-nc-engine.js',
        'nc-engine\meimad\meimad-dialects.js',
        'nc-engine\meimad\meimad-subprograms.js',
        'nc-engine\meimad\machines\mazak-variaxis-i-500.json',
        'nc-engine\meimad\machines\okuma-genos-l200e-m.json',
        'nc-engine\meimad\machines\haas-st-25y.json',
        'nc-engine\meimad\machines\haas-vf-3ss.json',
        'nc-engine\meimad\machines\fanuc-0i-mc-vmc-3axis.json',
        'nc-engine\meimad\machines\fanuc-0i-mc-vmc-4axis-a.json',
        'nc-engine\meimad\controls\okuma-osp-p200l-lathe.json',
        'nc-engine\meimad\controls\mazak-matrix2-mill.json',
        'nc-engine\src\parser.js',
        'nc-engine\src\haas-mill.js',
        'nc-engine\machines\haas-umc-500.json',
        'nc-engine\CNC-PARA.TXT',
        'ClearScriptV8.win-x64.dll',
        'WebView2Loader.dll')
    Assert-PayloadFiles -Root $serverTarget -Label "Packaged Server" -RelativeSuffixes @(
        'nc-engine\meimad\bootstrap.js',
        'nc-engine\meimad\meimad-nc-engine.js',
        'nc-engine\meimad\meimad-dialects.js',
        'nc-engine\meimad\meimad-subprograms.js',
        'nc-engine\meimad\machines\mazak-variaxis-i-500.json',
        'nc-engine\meimad\machines\okuma-genos-l200e-m.json',
        'nc-engine\meimad\machines\haas-st-25y.json',
        'nc-engine\meimad\machines\haas-vf-3ss.json',
        'nc-engine\meimad\machines\fanuc-0i-mc-vmc-3axis.json',
        'nc-engine\meimad\machines\fanuc-0i-mc-vmc-4axis-a.json',
        'nc-engine\meimad\controls\okuma-osp-p200l-lathe.json',
        'nc-engine\meimad\controls\haas-classic-lathe.json',
        'nc-engine\src\parser.js',
        'nc-engine\src\haas-mill.js',
        'nc-engine\machines\doosan-dvf-5000.json',
        'nc-engine\controls\fanuc-31i-b-plus-mill.json',
        'nc-engine\CNC-PARA.TXT',
        'ClearScriptV8.win-x64.dll')

    $clientAssemblyText = [Text.Encoding]::UTF8.GetString(
        [IO.File]::ReadAllBytes($clientAssemblies[0].FullName))
    foreach ($marker in @(
        'Finalizer program (O9xxx)',
        'Persistent event-sequence variable',
        'VerificationFinalizeProgram',
        'VerificationEventSequenceVariable')) {
        if ($clientAssemblyText.IndexOf($marker, [StringComparison]::Ordinal) -lt 0) {
            throw "Packaged Windows Client is missing protected-verification UI marker: $marker"
        }
    }

    $simulatorHtml = Get-Content -LiteralPath $serverSimulatorHtml[0].FullName -Raw
    foreach ($marker in @(
        '800 x 480 monochrome',
        'id="panel-canvas"',
        'width="800" height="480"',
        'id="button-d1"',
        'id="button-d2"',
        'id="button-d4"',
        'id="button-reset"',
        'HOLD: SERVICE / DEBUG',
        'HOLD: SEND_TO_QC')) {
        if ($simulatorHtml.IndexOf($marker, [StringComparison]::Ordinal) -lt 0) {
            throw "Packaged E-Ink simulator HTML is missing marker: $marker"
        }
    }

    $simulatorScript = Get-Content -LiteralPath $serverSimulatorScript[0].FullName -Raw
    foreach ($marker in @(
        'const HOLD_MILLISECONDS = 1200',
        'const GLCD_FONT = new Uint8Array',
        'function drawBitmapText',
        'drawProductionCanvas(model)',
        '/api/tablet/ping?hardwareId=',
        'requestStatus("before-SEND_TO_QC")',
        '{ event_type: "SEND_TO_QC" }',
        'requestStatus("after-SEND_TO_QC")')) {
        if ($simulatorScript.IndexOf($marker, [StringComparison]::Ordinal) -lt 0) {
            throw "Packaged E-Ink simulator script is missing marker: $marker"
        }
    }
    $fontBlock = [regex]::Match(
        $simulatorScript,
        'const GLCD_FONT = new Uint8Array\(\[(?<data>[\s\S]*?)\]\);').Groups['data'].Value
    $glyphByteCount = [regex]::Matches($fontBlock, '0x[0-9A-Fa-f]{2}').Count
    if ($glyphByteCount -ne 475) {
        throw "Packaged E-Ink simulator must contain 475 classic GLCD glyph bytes; found $glyphByteCount."
    }

    $simulatorPowerPolicy = Get-Content -LiteralPath $serverSimulatorPowerPolicy[0].FullName -Raw
    foreach ($marker in @(
        'READY_FOR_SETUP: Object.freeze({ sleepMode: "STAY_AWAKE"',
        'buttonRefresh: "WAIT_FOR_IN_SETUP_OR_TIMEOUT"',
        'READY_FOR_PRODUCTION: Object.freeze({ sleepMode: "DEEP_SLEEP"',
        'periodicRefreshSeconds: 60',
        'IN_PRODUCTION: Object.freeze({ sleepMode: "DEEP_SLEEP"')) {
        if ($simulatorPowerPolicy.IndexOf($marker, [StringComparison]::Ordinal) -lt 0) {
            throw "Packaged E-Ink simulator power policy is missing marker: $marker"
        }
    }
    if ($simulatorPowerPolicy.IndexOf('periodicRefreshSeconds: 15', [StringComparison]::Ordinal) -ge 0) {
        throw 'Packaged E-Ink simulator must not retain recurring 15-second setup polling.'
    }

    $simulatorStyles = Get-Content -LiteralPath $serverSimulatorStyles[0].FullName -Raw
    if ($simulatorStyles.IndexOf('filter: grayscale(1)', [StringComparison]::Ordinal) -lt 0 -or
        $simulatorStyles.IndexOf('aspect-ratio: 5 / 3', [StringComparison]::Ordinal) -lt 0 -or
        $simulatorStyles.IndexOf('image-rendering: pixelated', [StringComparison]::Ordinal) -lt 0) {
        throw 'Packaged E-Ink simulator styles do not prove the monochrome 800x480 profile.'
    }

    $bundledClientMsi = @(Get-ChildItem -LiteralPath $serverTarget -Recurse -Filter "Meimad-Planner-Client-Setup.msi" |
        Where-Object { $_.FullName -match "client-installer\\Meimad-Planner-Client-Setup\.msi$" })
    $bundledClientManifestFile = @(Get-ChildItem -LiteralPath $serverTarget -Recurse -Filter "Meimad-Planner-Client-Setup.json" |
        Where-Object { $_.FullName -match "client-installer\\Meimad-Planner-Client-Setup\.json$" })
    if ($bundledClientMsi.Count -ne 1 -or $bundledClientManifestFile.Count -ne 1) {
        throw "Expected the Server MSI to bundle client-installer\Meimad-Planner-Client-Setup.msi with its JSON manifest; found MSI $($bundledClientMsi.Count), manifest $($bundledClientManifestFile.Count)."
    }
    $distributedClientHash = (Get-FileHash -LiteralPath $clientMsi -Algorithm SHA256).Hash.ToLowerInvariant()
    $bundledClientHash = (Get-FileHash -LiteralPath $bundledClientMsi[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($bundledClientHash -ne $distributedClientHash) {
        throw "The client MSI bundled in the Server MSI does not match the distributed client MSI."
    }
    $bundledClientManifest = Get-Content -LiteralPath $bundledClientManifestFile[0].FullName -Raw | ConvertFrom-Json
    if ($bundledClientManifest.version -ne $ExpectedVersion -or
        $bundledClientManifest.sha256 -ne $distributedClientHash -or
        [long]$bundledClientManifest.byteLength -ne (Get-Item -LiteralPath $clientMsi).Length) {
        throw "The bundled client installer manifest does not describe the distributed client MSI $ExpectedVersion."
    }

    [pscustomobject]@{
        ProductVersion = $ExpectedVersion
        ClientExtractedFiles = @(Get-ChildItem -LiteralPath $clientTarget -Recurse -File).Count
        NestedOcctKernel = $nestedOcctKernels[0].FullName.Substring($clientTarget.Length).TrimStart("\")
        ClientVerificationUi = 'O9003 finalizer + persistent sequence fields present'
        NcViewerAndEngine = 'client NcViewer page + nc-engine + V8 + WebView2 loader; Server nc-engine + V8'
        ServerExtractedFiles = @(Get-ChildItem -LiteralPath $serverTarget -Recurse -File).Count
        EInkSimulatorProfile = '800x480 TFT bitmap; explicit status power policy; D1/D2/D4/reset; guarded SEND_TO_QC'
        ServiceRecoveryPolicy = 'restart 60s; restart 60s; none; reset 1d'
        ChecksumsVerified = $expectedHashes.Count
        BundledClientInstaller = "client-installer\Meimad-Planner-Client-Setup.msi $ExpectedVersion; manifest and hash verified"
    } | Format-List
}
finally {
    if (Test-Path -LiteralPath $extractRoot) {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force
    }
}
