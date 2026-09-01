[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $InputPath,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [int] $Dpi = 144
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName ReachFramework

$input = (Resolve-Path -LiteralPath $InputPath).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
$document = [System.Windows.Xps.Packaging.XpsDocument]::new($input, [IO.FileAccess]::Read)
try {
    $paginator = $document.GetFixedDocumentSequence().DocumentPaginator
    for ($index = 0; $index -lt $paginator.PageCount; $index++) {
        $page = $paginator.GetPage($index)
        $width = [Math]::Ceiling($page.Size.Width * $Dpi / 96)
        $height = [Math]::Ceiling($page.Size.Height * $Dpi / 96)
        $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
            $width, $height, $Dpi, $Dpi, [System.Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($page.Visual)
        $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $path = Join-Path $output ('page-{0:D2}.png' -f ($index + 1))
        $stream = [IO.File]::Open($path, [IO.FileMode]::Create)
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
    }
    Write-Output $paginator.PageCount
}
finally {
    $document.Close()
}
